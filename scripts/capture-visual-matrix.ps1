[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$AppExe,
    [Parameter(Mandatory)] [string]$EvidenceRoot,
    [int]$BasePort = 18780,
    [switch]$Resume,
    [string]$ReuseViewport = ''
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
$viewports = @(
    [ordered]@{ id = 'wide'; width = 1492; height = 997; purpose = 'Default desktop working area' },
    [ordered]@{ id = 'medium'; width = 1250; height = 820; purpose = 'Constrained responsive working area above the physical minimum' },
    [ordered]@{ id = 'minimum'; width = 880; height = 560; purpose = 'MainWindow declared minimum size' })
$lastPort = $BasePort + ($viewports.Count * 2) - 1
if ($BasePort -lt 1 -or $lastPort -gt 65535) { throw 'The visual matrix port range must remain inside 1..65535.' }
if (Get-Process -Name 'Hackermes.App' -ErrorAction SilentlyContinue) {
    throw 'Hackermes.App is already running. Close it before responsive visual matrix acceptance.'
}

if (-not ('HackermesVisualMatrixNative' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class HackermesVisualMatrixNative {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
  [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Auto)] public struct MONITORINFO {
    public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint flags;
  }
  [DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(int x, int y, uint flags);
  [DllImport("user32.dll", CharSet=CharSet.Auto)] public static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
  [DllImport("shcore.dll")] public static extern int GetScaleFactorForMonitor(IntPtr monitor, out int scale);
}
'@
}

function Assert-Artifact {
    param([Parameter(Mandatory)][string]$Path, [long]$MinimumBytes = 1)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Required visual artifact is missing: $Path" }
    if ((Get-Item -LiteralPath $Path).Length -lt $MinimumBytes) { throw "Visual artifact is empty or truncated: $Path" }
}

function Assert-Metadata {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Workspace,
        [Parameter(Mandatory)][int]$Width,
        [Parameter(Mandatory)][int]$Height,
        [Parameter(Mandatory)][int]$HostScale,
        [Parameter(Mandatory)][int]$MinimumSentinels
    )
    Assert-Artifact $Path 256
    $metadata = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    if ($metadata.schemaVersion -ne 2 -or $metadata.workspace -ne $Workspace -or
        $metadata.validation.mode -ne 'responsiveViewport' -or
        $metadata.validation.realSystemDpiChanged -ne $false -or
        $metadata.validation.hostScalePercent -ne $HostScale -or
        $metadata.window.requestedWidth -ne $Width -or $metadata.window.requestedHeight -ne $Height -or
        $metadata.window.captured.width -lt $Width -or $metadata.window.captured.height -lt $Height -or
        $metadata.window.captured.width -gt [Math]::Max($Width + 256, 1136) -or
        $metadata.window.captured.height -gt [Math]::Max($Height + 192, 752) -or
        @($metadata.layoutSentinels).Count -lt $MinimumSentinels) {
        throw "Visual matrix metadata does not prove the requested responsive viewport and layout sentinels: $Path"
    }
    return $metadata
}

$monitor = [HackermesVisualMatrixNative]::MonitorFromPoint(0, 0, 1)
if ($monitor -eq [IntPtr]::Zero) { throw 'Could not resolve the primary monitor for responsive visual acceptance.' }
$hostScalePercent = 0
if ([HackermesVisualMatrixNative]::GetScaleFactorForMonitor($monitor, [ref]$hostScalePercent) -ne 0 -or
    $hostScalePercent -lt 50 -or $hostScalePercent -gt 400) {
    throw 'GetScaleFactorForMonitor did not return a usable host scale.'
}
$monitorInfo = New-Object HackermesVisualMatrixNative+MONITORINFO
$monitorInfo.cbSize = [Runtime.InteropServices.Marshal]::SizeOf($monitorInfo)
if (-not [HackermesVisualMatrixNative]::GetMonitorInfo($monitor, [ref]$monitorInfo)) {
    throw 'GetMonitorInfo failed for the responsive visual matrix host monitor.'
}

$ports = @($BasePort..$lastPort)
foreach ($port in $ports) {
    if (Get-NetTCPConnection -LocalAddress '127.0.0.1' -LocalPort $port -State Listen -ErrorAction SilentlyContinue) {
        throw "Responsive visual matrix port is already in use: 127.0.0.1:$port"
    }
}

New-Item -ItemType Directory -Path $evidenceRootPath -Force | Out-Null
$entries = @()
$artifactPaths = @()
for ($index = 0; $index -lt $viewports.Count; $index++) {
    $viewport = $viewports[$index]
    $assessmentPort = $BasePort + ($index * 2)
    $trafficPort = $assessmentPort + 1
    $reuseViewportIds = @($ReuseViewport.Split(',', [StringSplitOptions]::RemoveEmptyEntries) |
        ForEach-Object { $_.Trim() })
    $reuseThisViewport = $reuseViewportIds -contains $viewport.id
    if ($reuseViewportIds.Count -gt 0 -and -not $reuseThisViewport) {
        $assessmentRoot = Join-Path $evidenceRootPath "assessment\$($viewport.id)-v2"
        $trafficRoot = Join-Path $evidenceRootPath "traffic\$($viewport.id)-v2"
    }
    else {
        $assessmentRoot = Join-Path $evidenceRootPath "assessment\$($viewport.id)"
        $trafficRoot = Join-Path $evidenceRootPath "traffic\$($viewport.id)"
    }
    $assessmentMetadataPath = Join-Path $assessmentRoot 'visual-metadata.json'
    $trafficMetadataPath = Join-Path $trafficRoot 'visual-metadata.json'
    $entryExists = (Test-Path -LiteralPath $assessmentMetadataPath -PathType Leaf) -and
        (Test-Path -LiteralPath $trafficMetadataPath -PathType Leaf)
    if (-not $Resume -and $reuseViewportIds.Count -eq 0 -and -not $reuseThisViewport -and $entryExists) {
        throw "Refusing to overwrite responsive matrix entry $($viewport.id) under $evidenceRootPath"
    }
    if (($Resume -or $reuseThisViewport) -and $entryExists) {
        Write-Host "[$($index + 1)/$($viewports.Count)] Revalidating existing responsive viewport $($viewport.id)..."
    }
    else {
        if (($Resume -or $reuseThisViewport) -and ((Test-Path -LiteralPath $assessmentMetadataPath) -or (Test-Path -LiteralPath $trafficMetadataPath))) {
            throw "Responsive matrix entry $($viewport.id) is only partially present; refusing an ambiguous resume."
        }
        Write-Host "[$($index + 1)/$($viewports.Count)] Assessment responsive viewport $($viewport.id) ($($viewport.width)x$($viewport.height))..."
        & (Join-Path $PSScriptRoot 'capture-assessment-visual.ps1') -AppExe $appExePath `
            -EvidenceRoot $assessmentRoot -Port $assessmentPort -Width $viewport.width -Height $viewport.height `
            -ValidationMode responsiveViewport
        if (-not $?) { throw "Assessment responsive capture failed for $($viewport.id)." }

        Write-Host "[$($index + 1)/$($viewports.Count)] Traffic responsive viewport $($viewport.id) ($($viewport.width)x$($viewport.height))..."
        & (Join-Path $PSScriptRoot 'capture-traffic-visual.ps1') -AppExe $appExePath `
            -EvidenceRoot $trafficRoot -Port $trafficPort -Width $viewport.width -Height $viewport.height `
            -ValidationMode responsiveViewport
        if (-not $?) { throw "Traffic responsive capture failed for $($viewport.id)." }
    }
    $assessmentMetadata = Assert-Metadata $assessmentMetadataPath 'authorized-assessment' `
        $viewport.width $viewport.height $hostScalePercent 5
    $trafficMetadata = Assert-Metadata $trafficMetadataPath 'traffic-workbench' `
        $viewport.width $viewport.height $hostScalePercent 4
    if ($assessmentMetadata.dpi -ne $trafficMetadata.dpi) {
        throw "Assessment and Traffic captures used different real host DPI values for viewport $($viewport.id)."
    }

    $scaleArtifacts = @(
        (Join-Path $assessmentRoot 'authorized-assessment-light.png'),
        (Join-Path $assessmentRoot 'authorized-assessment-dark.png'),
        $assessmentMetadataPath,
        (Join-Path $trafficRoot 'traffic-workbench-light.png'),
        (Join-Path $trafficRoot 'traffic-workbench-dark.png'),
        $trafficMetadataPath)
    foreach ($artifact in $scaleArtifacts) {
        Assert-Artifact $artifact $(if ($artifact.EndsWith('.png')) { 10KB } else { 256 })
    }
    $artifactPaths += $scaleArtifacts
    $entries += [ordered]@{
        id = $viewport.id; purpose = $viewport.purpose
        requestedPhysicalWindow = [ordered]@{ width = $viewport.width; height = $viewport.height }
        capturedPhysicalWindow = [ordered]@{
            width = $assessmentMetadata.window.captured.width
            height = $assessmentMetadata.window.captured.height
        }
        clampedByWindowMinimumOrNonClientFrame =
            $assessmentMetadata.window.captured.width -ne $viewport.width -or
            $assessmentMetadata.window.captured.height -ne $viewport.height
        actualHostDpi = $assessmentMetadata.dpi
        assessmentMetadata = $assessmentMetadataPath
        trafficMetadata = $trafficMetadataPath
    }
}

$deadline = [DateTime]::UtcNow.AddSeconds(10)
do {
    $residualApps = @(Get-Process -Name 'Hackermes.App' -ErrorAction SilentlyContinue)
    $residualProfileProcesses = @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | Where-Object {
        $_.ProcessId -ne $PID -and -not [string]::IsNullOrWhiteSpace($_.CommandLine) -and
        $_.CommandLine.IndexOf($evidenceRootPath, [StringComparison]::OrdinalIgnoreCase) -ge 0
    })
    if ($residualApps.Count -eq 0 -and $residualProfileProcesses.Count -eq 0) { break }
    Start-Sleep -Milliseconds 100
} while ([DateTime]::UtcNow -lt $deadline)
if ($residualApps.Count -gt 0) { throw "Residual Hackermes.App process detected: $($residualApps.Id -join ', ')." }
if ($residualProfileProcesses.Count -gt 0) {
    throw "Residual responsive visual profile process detected: $($residualProfileProcesses.ProcessId -join ', ')."
}
foreach ($port in $ports) {
    if (Get-NetTCPConnection -LocalAddress '127.0.0.1' -LocalPort $port -State Listen -ErrorAction SilentlyContinue) {
        throw "Responsive visual matrix listener remained on 127.0.0.1:$port."
    }
}

$capturedSizes = @($entries | ForEach-Object {
    "$($_.capturedPhysicalWindow.width)x$($_.capturedPhysicalWindow.height)"
} | Select-Object -Unique)
if ($capturedSizes.Count -ne $viewports.Count) {
    throw "Responsive matrix did not produce three distinct captured window sizes: $($capturedSizes -join ', ')"
}

$manifestPath = Join-Path $evidenceRootPath 'visual-matrix.json'
[ordered]@{
    schemaVersion = 1
    capturedAtUtc = [DateTime]::UtcNow.ToString('o')
    mode = 'responsiveViewport'
    realSystemDpiChanged = $false
    claim = 'Wide/medium/minimum are real window-size captures at the unchanged host DPI. This artifact does not claim alternate 100/150/200 operating-system DPI coverage.'
    hostScaleDetection = 'GetScaleFactorForMonitor(primary monitor)'
    hostScalePercent = $hostScalePercent
    actualMonitorPixels = [ordered]@{
        width = $monitorInfo.rcMonitor.Right - $monitorInfo.rcMonitor.Left
        height = $monitorInfo.rcMonitor.Bottom - $monitorInfo.rcMonitor.Top
    }
    actualWorkAreaPixels = [ordered]@{
        width = $monitorInfo.rcWork.Right - $monitorInfo.rcWork.Left
        height = $monitorInfo.rcWork.Bottom - $monitorInfo.rcWork.Top
    }
    workspaces = @('main-shell/authorized-assessment', 'main-shell/traffic-workbench')
    entries = $entries
    artifacts = @($artifactPaths | ForEach-Object {
        $item = Get-Item -LiteralPath $_
        [ordered]@{ path = $item.FullName; bytes = $item.Length; sha256 = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
    })
} | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath $manifestPath -Encoding UTF8
Assert-Artifact $manifestPath 1024
Write-Host "RESPONSIVE VISUAL MATRIX PASS: $manifestPath"
