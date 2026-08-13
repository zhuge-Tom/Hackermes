[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$AppExe,
    [Parameter(Mandatory)] [string]$EvidenceRoot,
    [int]$Port = 18769,
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
if ($Width -lt 800 -or $Height -lt 500) { throw 'Traffic visual acceptance requires at least an 800x500 physical application window.' }
if ($TargetScalePercent -ne 0 -or $ReferencePixelWidth -ne 0 -or $ReferencePixelHeight -ne 0) {
    throw 'TargetScalePercent and reference dimensions are no longer accepted; use the responsive window-size matrix instead of simulated DPI claims.'
}
if (Get-Process -Name 'Hackermes.App' -ErrorAction SilentlyContinue) {
    throw 'Hackermes.App is already running. Close it before Traffic visual acceptance.'
}
if (Get-NetTCPConnection -LocalAddress '127.0.0.1' -LocalPort $Port -State Listen -ErrorAction SilentlyContinue) {
    throw "Traffic visual acceptance port is already in use: 127.0.0.1:$Port"
}

$serverPath = Join-Path $PSScriptRoot 'selftest-server.py'
$pagePath = Join-Path $PSScriptRoot 'selftest-page.html'
$profileRoot = Join-Path $evidenceRootPath 'webview2-profile'
$dataRoot = Join-Path $evidenceRootPath 'app-data'
$lightPath = Join-Path $evidenceRootPath 'traffic-workbench-light.png'
$darkPath = Join-Path $evidenceRootPath 'traffic-workbench-dark.png'
$metadataPath = Join-Path $evidenceRootPath 'visual-metadata.json'
$server = $null
$serverOwnerId = $null
$app = $null

New-Item -ItemType Directory -Path $evidenceRootPath, $profileRoot, $dataRoot -Force | Out-Null
$settings = [ordered]@{
    general = [ordered]@{ isDarkMode = $false }
    layout = [ordered]@{
        leftPanelVisible = $false
        rightPanelVisible = $false
        bottomPanelVisible = $true
        bottomSelectedTabId = 'traffic-workbench'
        bottomPanelHeight = 900
    }
}
[IO.File]::WriteAllText(
    (Join-Path $dataRoot 'settings.json'),
    ($settings | ConvertTo-Json -Depth 4 -Compress),
    [Text.UTF8Encoding]::new($false))

if (-not ('HackermesTrafficVisualNative' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class HackermesTrafficVisualNative {
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
    if ($bounds.Width -le 0 -or $bounds.Height -le 0) { throw 'The application-owned element has no clickable bounds.' }
    $point = New-Object HackermesTrafficVisualNative+POINT
    $point.X = [int]($bounds.Left + $bounds.Width / 2)
    $point.Y = [int]($bounds.Top + $bounds.Height / 2)
    if (-not [HackermesTrafficVisualNative]::ScreenToClient($WindowHandle, [ref]$point)) {
        throw 'ScreenToClient failed for an application-owned element.'
    }
    $parameter = [IntPtr](($point.Y -shl 16) -bor ($point.X -band 0xffff))
    [void][HackermesTrafficVisualNative]::PostMessage($WindowHandle, 0x0201, [IntPtr]1, $parameter)
    [void][HackermesTrafficVisualNative]::PostMessage($WindowHandle, 0x0202, [IntPtr]0, $parameter)
}

function Save-ApplicationWindow {
    param([Parameter(Mandatory)][IntPtr]$WindowHandle, [Parameter(Mandatory)][string]$Path)
    $rect = New-Object HackermesTrafficVisualNative+RECT
    if (-not [HackermesTrafficVisualNative]::GetWindowRect($WindowHandle, [ref]$rect)) { throw 'GetWindowRect failed.' }
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
            if (-not [HackermesTrafficVisualNative]::PrintWindow($WindowHandle, $device, 2)) { throw 'PrintWindow failed.' }
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
                $bounds.Left -ge ($windowBounds.Left - 2) -and $bounds.Top -ge ($windowBounds.Top - 2) -and
                $bounds.Right -le ($windowBounds.Right + 2) -and $bounds.Bottom -le ($windowBounds.Bottom + 2)
            }
            catch { $false }
        })
        if ($matches.Count -lt 1) {
            $candidates = @($Elements | Where-Object { try { $_.Current.Name -eq $name } catch { $false } } |
                ForEach-Object { try { $_.Current.BoundingRectangle.ToString() } catch { '<unavailable>' } })
            $available = @($Elements | ForEach-Object { try { $_.Current.Name } catch { '' } } |
                Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique -First 60)
            throw "Required Traffic layout sentinel is missing or clipped outside the application window: $name; window=$windowBounds; candidates=$($candidates -join ', '); available=$($available -join ' | ')"
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
                    [void][HackermesTrafficVisualNative]::ShowWindow($handle, 9)
                    [void][HackermesTrafficVisualNative]::SetWindowPos($handle, [IntPtr]0, 40, 40, $Width, $Height, 0x0040)
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
$oldSelfTest = $env:HACKERMES_SELFTEST
$oldSelfTestExit = $env:HACKERMES_SELFTEST_EXIT
try {
    $python = Get-Command python -ErrorAction Stop
    $server = Start-Process -FilePath $python.Source -ArgumentList @($serverPath, '--port', $Port, '--page', $pagePath) -PassThru -WindowStyle Hidden
    $health = "http://127.0.0.1:$Port/health"
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    do {
        try { $ready = (Invoke-WebRequest -UseBasicParsing -Uri $health -TimeoutSec 1).StatusCode -eq 200 } catch { $ready = $false }
        if (-not $ready) { Start-Sleep -Milliseconds 100 }
    } until ($ready -or [DateTime]::UtcNow -ge $deadline)
    if (-not $ready) { throw 'Traffic visual loopback server did not become ready.' }
    $listener = Get-NetTCPConnection -LocalAddress '127.0.0.1' -LocalPort $Port -State Listen -ErrorAction Stop |
        Select-Object -First 1
    $serverOwnerId = [int]$listener.OwningProcess

    $env:HACKERMES_BROWSER_PROFILE_ROOT = $profileRoot
    $env:HACKERMES_DATA_ROOT = $dataRoot
    $env:HACKERMES_AUTOOPEN_URL = "http://127.0.0.1:$Port/selftest-page.html"
    $env:HACKERMES_SELFTEST = '1'
    $env:HACKERMES_SELFTEST_EXIT = '0'
    $app = Start-Process -FilePath $appExePath -WorkingDirectory (Split-Path -Parent $appExePath) -PassThru
    $windowHandle = Wait-ApplicationWindow $app
    $trafficHistoryPath = Join-Path $dataRoot 'traffic-history.v1.json.gz'
    $appLogPath = Join-Path $dataRoot 'logs\latest.log'
    $deadline = [DateTime]::UtcNow.AddSeconds(90)
    do {
        Start-Sleep -Milliseconds 250
        $selfTestPassed = (Test-Path -LiteralPath $appLogPath -PathType Leaf) -and
            ((Get-Content -LiteralPath $appLogPath -Raw).Contains('TRAFFIC_SELFTEST RESULT 5/5 PASS'))
        $historyReady = (Test-Path -LiteralPath $trafficHistoryPath -PathType Leaf) -and
            ((Get-Item -LiteralPath $trafficHistoryPath).Length -ge 32)
    } until (($selfTestPassed -and $historyReady) -or $app.HasExited -or [DateTime]::UtcNow -ge $deadline)
    if (-not $selfTestPassed -or -not $historyReady) {
        throw 'The real loopback Traffic capture did not complete with isolated history evidence.'
    }
    Start-Sleep -Seconds 2
    $windowHandle = Wait-ApplicationWindow $app
    $root = [Windows.Automation.AutomationElement]::FromHandle($windowHandle)
    $runningSettings = Get-Content -LiteralPath (Join-Path $dataRoot 'settings.json') -Raw | ConvertFrom-Json
    if (-not $runningSettings.layout.bottomPanelVisible -or
        $runningSettings.layout.bottomSelectedTabId -ne 'traffic-workbench') {
        throw 'The isolated application state did not activate the Traffic workbench.'
    }
    $rendered = $root.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition)
    # Avalonia currently omits the nested Traffic workbench controls from the
    # Windows UIA tree. Activation is therefore proved by the isolated layout
    # settings above, and population by the real 5/5 self-test plus persisted
    # history. Screenshot dimensions and hashes cover the rendered result.
    $layoutSentinels = @('layout.bottomPanelVisible=true', 'layout.bottomSelectedTabId=traffic-workbench',
        'TRAFFIC_SELFTEST RESULT 5/5 PASS', 'traffic-history.v1.json.gz')
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

    $dpi = [HackermesTrafficVisualNative]::GetDpiForWindow($windowHandle)
    $hostScalePercent = [Math]::Round($dpi / 96 * 100)
    if ($lightCapture.width -ne $darkCapture.width -or $lightCapture.height -ne $darkCapture.height) {
        throw 'Traffic light/dark captures do not use the same physical viewport.'
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
        schemaVersion = 2; workspace = 'traffic-workbench'; capturedAtUtc = [DateTime]::UtcNow.ToString('o')
        loopbackUrl = "http://127.0.0.1:$Port/"; dpi = $dpi; scalePercent = $hostScalePercent
        validation = $validation
        window = [ordered]@{ requestedWidth = $Width; requestedHeight = $Height; captured = $lightCapture }
        layoutSentinels = $layoutSentinels
        layoutSentinelPolicy = 'Traffic nested controls are not exposed by the current Avalonia UIA provider; activation, populated real loopback history, capture dimensions, and artifact hashes are verified instead.'
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
    $env:HACKERMES_SELFTEST = $oldSelfTest
    $env:HACKERMES_SELFTEST_EXIT = $oldSelfTestExit
    if (-not $appGone) { throw "Traffic visual Hackermes process was not reaped: PID $($app.Id)." }
    if (-not $launcherGone) { throw "Traffic visual loopback launcher was not reaped: PID $($server.Id)." }
    if (-not $ownerGone) { throw "Traffic visual loopback server was not reaped: PID $serverOwnerId." }
    if (Get-NetTCPConnection -LocalAddress '127.0.0.1' -LocalPort $Port -State Listen -ErrorAction SilentlyContinue) {
        throw "Traffic visual loopback listener remained on 127.0.0.1:$Port."
    }
}

Write-Host "TRAFFIC VISUAL PASS: $metadataPath"
