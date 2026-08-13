[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$AppExe,
    [Parameter(Mandatory)] [string]$EvidenceRoot,
    [int]$Port = 18768,
    [int]$Width = 1492,
    [int]$Height = 997,
    [ValidateSet('actualDpi', 'responsiveViewport')] [string]$ValidationMode = 'actualDpi',
    [int]$TargetScalePercent = 0,
    [int]$ReferencePixelWidth = 0,
    [int]$ReferencePixelHeight = 0
)

$ErrorActionPreference = 'Stop'
$buildEnvironment = & (Join-Path $PSScriptRoot 'initialize-build-environment.ps1')
$appExePath = [IO.Path]::GetFullPath($AppExe)
$evidenceRootPath = [IO.Path]::GetFullPath($EvidenceRoot)
if ([IO.Path]::GetPathRoot($appExePath) -ne 'G:\' -or
    [IO.Path]::GetPathRoot($evidenceRootPath) -ne 'G:\') {
    throw "AppExe and EvidenceRoot must stay on G: $appExePath ; $evidenceRootPath"
}
if (-not [IO.Path]::IsPathRooted($AppExe) -or -not (Test-Path -LiteralPath $appExePath -PathType Leaf)) {
    throw "AppExe must be an existing absolute file: $appExePath"
}
if (-not [IO.Path]::IsPathRooted($EvidenceRoot) -or $evidenceRootPath -eq [IO.Path]::GetPathRoot($evidenceRootPath)) {
    throw "EvidenceRoot must be an absolute non-root directory: $evidenceRootPath"
}
if ($Port -lt 1 -or $Port -gt 65535) { throw 'Port must be in the range 1..65535.' }
if ($Width -lt 800 -or $Height -lt 500) { throw 'Assessment visual acceptance requires at least an 800x500 physical application window.' }
if ($TargetScalePercent -ne 0 -or $ReferencePixelWidth -ne 0 -or $ReferencePixelHeight -ne 0) {
    throw 'TargetScalePercent and reference dimensions are no longer accepted; use the responsive window-size matrix instead of simulated DPI claims.'
}
if (Get-Process -Name 'Hackermes.App' -ErrorAction SilentlyContinue) {
    throw 'Hackermes.App is already running. Close it before visual acceptance.'
}
if (Get-NetTCPConnection -LocalAddress '127.0.0.1' -LocalPort $Port -State Listen -ErrorAction SilentlyContinue) {
    throw "Visual acceptance port is already in use: 127.0.0.1:$Port"
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$serverPath = Join-Path $PSScriptRoot 'selftest-server.py'
$pagePath = Join-Path $PSScriptRoot 'selftest-page.html'
$profileRoot = Join-Path $evidenceRootPath 'webview2-profile'
$dataRoot = Join-Path $evidenceRootPath 'app-data'
$lightPath = Join-Path $evidenceRootPath 'authorized-assessment-light.png'
$darkPath = Join-Path $evidenceRootPath 'authorized-assessment-dark.png'
$metadataPath = Join-Path $evidenceRootPath 'visual-metadata.json'
$server = $null
$serverOwnerId = $null
$app = $null

New-Item -ItemType Directory -Path $evidenceRootPath, $profileRoot, $dataRoot -Force | Out-Null
[IO.File]::WriteAllText((Join-Path $dataRoot 'settings.json'), '{"general":{"isDarkMode":false}}')

if (-not ('HackermesVisualNative' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class HackermesVisualNative {
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int command);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
  [DllImport("user32.dll")] public static extern bool ScreenToClient(IntPtr hwnd, ref POINT point);
  [DllImport("user32.dll")] public static extern IntPtr PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
  [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hwnd);
}
'@
}
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing

function Send-ElementClick {
    param([Parameter(Mandatory)]$Element, [Parameter(Mandatory)][IntPtr]$WindowHandle)
    $bounds = $Element.Current.BoundingRectangle
    $point = New-Object HackermesVisualNative+POINT
    $point.X = [int]($bounds.Left + $bounds.Width / 2)
    $point.Y = [int]($bounds.Top + $bounds.Height / 2)
    if (-not [HackermesVisualNative]::ScreenToClient($WindowHandle, [ref]$point)) {
        throw 'ScreenToClient failed for an application-owned element.'
    }
    $parameter = [IntPtr](($point.Y -shl 16) -bor ($point.X -band 0xffff))
    [void][HackermesVisualNative]::PostMessage($WindowHandle, 0x0201, [IntPtr]1, $parameter)
    [void][HackermesVisualNative]::PostMessage($WindowHandle, 0x0202, [IntPtr]0, $parameter)
}

function Save-ApplicationWindow {
    param([Parameter(Mandatory)][IntPtr]$WindowHandle, [Parameter(Mandatory)][string]$Path)
    $rect = New-Object HackermesVisualNative+RECT
    if (-not [HackermesVisualNative]::GetWindowRect($WindowHandle, [ref]$rect)) { throw 'GetWindowRect failed.' }
    $captureWidth = $rect.Right - $rect.Left
    $captureHeight = $rect.Bottom - $rect.Top
    if ($captureWidth -lt 800 -or $captureHeight -lt 500) {
        throw "Application window is not restored: ${captureWidth}x${captureHeight}."
    }
    $bitmap = New-Object Drawing.Bitmap($captureWidth, $captureHeight)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    try {
        $device = $graphics.GetHdc()
        try {
            if (-not [HackermesVisualNative]::PrintWindow($WindowHandle, $device, 2)) { throw 'PrintWindow failed.' }
        }
        finally { $graphics.ReleaseHdc($device) }
        $bitmap.Save($Path, [Drawing.Imaging.ImageFormat]::Png)
    }
    finally { $graphics.Dispose(); $bitmap.Dispose() }
    if ((Get-Item -LiteralPath $Path).Length -lt 10KB) { throw "Visual artifact is empty or incomplete: $Path" }
    return [ordered]@{ width = $captureWidth; height = $captureHeight }
}

function Assert-NamedElementsInsideWindow {
    param(
        [Parameter(Mandatory)]$Root,
        [Parameter(Mandatory)]$Elements,
        [Parameter(Mandatory)][string[]]$Names
    )
    $windowBounds = $Root.Current.BoundingRectangle
    foreach ($name in $Names) {
        $matches = @($Elements | Where-Object {
            try {
                $bounds = $_.Current.BoundingRectangle
                $_.Current.Name -eq $name -and $bounds.Width -gt 0 -and $bounds.Height -gt 0 -and
                $bounds.Left -ge ($windowBounds.Left - 2) -and $bounds.Right -le ($windowBounds.Right + 2)
            }
            catch { $false }
        })
        if ($matches.Count -lt 1) {
            $candidates = @($Elements | Where-Object { try { $_.Current.Name -eq $name } catch { $false } } |
                ForEach-Object { try { $_.Current.BoundingRectangle.ToString() } catch { '<unavailable>' } })
            throw "Required Assessment layout sentinel is missing or clipped outside the application window: $name; window=$windowBounds; candidates=$($candidates -join ', ')"
        }
    }
}

function Wait-ProcessGone {
    param([Parameter(Mandatory)][int]$Id, [int]$TimeoutSeconds = 10)
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (-not (Get-Process -Id $Id -ErrorAction SilentlyContinue)) { return $true }
        Start-Sleep -Milliseconds 100
    }
    return -not (Get-Process -Id $Id -ErrorAction SilentlyContinue)
}

function Wait-ApplicationWindow {
    param([Parameter(Mandatory)]$Process, [int]$TimeoutSeconds = 30)
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $processCondition = New-Object Windows.Automation.PropertyCondition(
        [Windows.Automation.AutomationElement]::ProcessIdProperty, $Process.Id)
    while ([DateTime]::UtcNow -lt $deadline) {
        $Process.Refresh()
        if ($Process.HasExited) { break }
        $windows = ([Windows.Automation.AutomationElement]::RootElement).FindAll(
            [Windows.Automation.TreeScope]::Children, $processCondition)
        foreach ($window in $windows) {
            try {
                $handle = [IntPtr]$window.Current.NativeWindowHandle
                if ($handle -ne [IntPtr]::Zero) {
                    [void][HackermesVisualNative]::ShowWindow($handle, 9)
                    [void][HackermesVisualNative]::SetWindowPos($handle, [IntPtr]0, 40, 40, $Width, $Height, 0x0040)
                    Start-Sleep -Milliseconds 150
                    return $handle
                }
            }
            catch { }
        }
        Start-Sleep -Milliseconds 100
    }
    throw 'Hackermes did not expose a restored application window.'
}

$oldProfile = $env:HACKERMES_BROWSER_PROFILE_ROOT
$oldData = $env:HACKERMES_DATA_ROOT
$oldUrl = $env:HACKERMES_AUTOOPEN_URL
try {
    $python = Get-Command python -ErrorAction Stop
    $server = Start-Process -FilePath $python.Source -ArgumentList @($serverPath, '--port', $Port, '--page', $pagePath) -PassThru -WindowStyle Hidden
    $health = "http://127.0.0.1:$Port/health"
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    do {
        try { $ready = (Invoke-WebRequest -UseBasicParsing -Uri $health -TimeoutSec 1).StatusCode -eq 200 } catch { $ready = $false }
        if (-not $ready) { Start-Sleep -Milliseconds 100 }
    } until ($ready -or [DateTime]::UtcNow -ge $deadline)
    if (-not $ready) { throw 'Visual loopback server did not become ready.' }
    $listener = Get-NetTCPConnection -LocalAddress '127.0.0.1' -LocalPort $Port -State Listen -ErrorAction Stop |
        Select-Object -First 1
    $serverOwnerId = [int]$listener.OwningProcess

    $env:HACKERMES_BROWSER_PROFILE_ROOT = $profileRoot
    $env:HACKERMES_DATA_ROOT = $dataRoot
    $env:HACKERMES_AUTOOPEN_URL = "http://127.0.0.1:$Port/selftest-page.html"
    $app = Start-Process -FilePath $appExePath -WorkingDirectory (Split-Path -Parent $appExePath) -PassThru
    $windowHandle = Wait-ApplicationWindow $app
    Start-Sleep -Seconds 8
    $windowHandle = Wait-ApplicationWindow $app
    $root = [Windows.Automation.AutomationElement]::FromHandle($windowHandle)
    $all = $root.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition)
    $assessmentName = -join ([char[]](0x6388,0x6743,0x8bc4,0x4f30))
    $assessment = @($all | Where-Object {
        try { $_.Current.ControlType -eq [Windows.Automation.ControlType]::Text -and $_.Current.Name -eq $assessmentName } catch { $false }
    }) | Select-Object -First 1
    if (-not $assessment) { throw 'UI Automation could not locate the authorized-assessment tab.' }
    Send-ElementClick $assessment $windowHandle
    Start-Sleep -Seconds 3

    $windowHandle = Wait-ApplicationWindow $app
    $root = [Windows.Automation.AutomationElement]::FromHandle($windowHandle)
    $rendered = $root.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition)
    $createScopeName = -join ([char[]](0x521b,0x5efa,0x6388,0x6743,0x8303,0x56f4))
    if (-not @($rendered | Where-Object { try { $_.Current.Name -eq $createScopeName } catch { $false } })) {
        throw 'The assessment workspace did not become active.'
    }
    $assessmentTabName = -join ([char[]](0x6388,0x6743,0x8bc4,0x4f30))
    $scopeStageName = -join ([char[]](0x31,0x20,0x20,0x6388,0x6743,0x8303,0x56f4))
    $planStageName = -join ([char[]](0x32,0x20,0x20,0x56fa,0x5b9a,0x8ba1,0x5212))
    $approvalStageName = -join ([char[]](0x33,0x20,0x20,0x5ba1,0x6279,0x4e0e,0x6267,0x884c))
    Assert-NamedElementsInsideWindow $root $rendered @(
        $assessmentTabName, $createScopeName, $scopeStageName, $planStageName, $approvalStageName)
    $windowBounds = $root.Current.BoundingRectangle
    $theme = @($rendered | Where-Object {
        try {
            $bounds = $_.Current.BoundingRectangle
            $_.Current.ControlType -eq [Windows.Automation.ControlType]::Button -and
            $bounds.Top -lt ($windowBounds.Top + 120) -and $bounds.Left -gt ($windowBounds.Right - 100) -and
            $bounds.Width -ge 30 -and $bounds.Width -le 55
        } catch { $false }
    }) | Sort-Object { $_.Current.BoundingRectangle.Left } -Descending | Select-Object -First 1
    if (-not $theme) { throw 'UI Automation could not locate the application theme button.' }

    $windowHandle = Wait-ApplicationWindow $app
    $lightCapture = Save-ApplicationWindow $windowHandle $lightPath
    Send-ElementClick $theme $windowHandle
    Start-Sleep -Seconds 3
    $windowHandle = Wait-ApplicationWindow $app
    $darkCapture = Save-ApplicationWindow $windowHandle $darkPath

    $dpi = [HackermesVisualNative]::GetDpiForWindow($windowHandle)
    $hostScalePercent = [Math]::Round($dpi / 96 * 100)
    if ($lightCapture.width -ne $darkCapture.width -or $lightCapture.height -ne $darkCapture.height) {
        throw 'Assessment light/dark captures do not use the same physical viewport.'
    }
    $validation = if ($ValidationMode -eq 'responsiveViewport') {
        [ordered]@{
            mode = 'responsiveViewport'
            realSystemDpiChanged = $false
            hostDpi = $dpi
            hostScalePercent = $hostScalePercent
            requestedPhysicalWindow = [ordered]@{ width = $Width; height = $Height }
            capturedPhysicalWindow = $lightCapture
            claim = 'Responsive window-size capture at the real host DPI; this is not an operating-system DPI change or alternate-DPI simulation.'
        }
    }
    else {
        [ordered]@{
            mode = 'actualDpi'
            realSystemDpiChanged = $false
            hostDpi = $dpi
            hostScalePercent = $hostScalePercent
            claim = 'Real window capture at the host monitor DPI; no alternate DPI is claimed.'
        }
    }
    [ordered]@{
        schemaVersion = 2; workspace = 'authorized-assessment'; capturedAtUtc = [DateTime]::UtcNow.ToString('o')
        loopbackUrl = "http://127.0.0.1:$Port/"
        dpi = $dpi; scalePercent = $hostScalePercent
        validation = $validation
        window = [ordered]@{ requestedWidth = $Width; requestedHeight = $Height; captured = $lightCapture }
        layoutSentinels = @($assessmentTabName, $createScopeName, $scopeStageName, $planStageName, $approvalStageName)
        layoutSentinelPolicy = 'All lifecycle sentinels have non-zero bounds and remain horizontally inside the main window; vertical lifecycle overflow is intentionally reachable through its ScrollViewer.'
        browserProfileRoot = $profileRoot; dataRoot = $dataRoot
        light = [ordered]@{ path = $lightPath; sha256 = (Get-FileHash $lightPath -Algorithm SHA256).Hash.ToLowerInvariant() }
        dark = [ordered]@{ path = $darkPath; sha256 = (Get-FileHash $darkPath -Algorithm SHA256).Hash.ToLowerInvariant() }
    } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $metadataPath -Encoding UTF8
}
finally {
    if ($app -and -not $app.HasExited) { Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue }
    if ($server -and -not $server.HasExited) { Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue }
    if ($serverOwnerId -and (-not $server -or $serverOwnerId -ne $server.Id)) {
        Stop-Process -Id $serverOwnerId -Force -ErrorAction SilentlyContinue
    }
    $appGone = -not $app -or (Wait-ProcessGone $app.Id)
    $launcherGone = -not $server -or (Wait-ProcessGone $server.Id)
    $ownerGone = -not $serverOwnerId -or (Wait-ProcessGone $serverOwnerId)
    $env:HACKERMES_BROWSER_PROFILE_ROOT = $oldProfile
    $env:HACKERMES_DATA_ROOT = $oldData
    $env:HACKERMES_AUTOOPEN_URL = $oldUrl
    if (-not $appGone) { throw "Visual Hackermes process was not reaped: PID $($app.Id)." }
    if (-not $launcherGone) { throw "Visual loopback launcher was not reaped: PID $($server.Id)." }
    if (-not $ownerGone) { throw "Visual loopback server was not reaped: PID $serverOwnerId." }
    if (Get-NetTCPConnection -LocalAddress '127.0.0.1' -LocalPort $Port -State Listen -ErrorAction SilentlyContinue) {
        throw "Visual loopback listener remained on 127.0.0.1:$Port."
    }
}

Write-Host "ASSESSMENT VISUAL PASS: $metadataPath"
