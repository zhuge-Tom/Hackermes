[CmdletBinding()]
param(
    [string]$HackermesBuildRoot = 'G:\HackermesBuild\release-acceptance',
    [string]$EvidenceRoot,
    [int]$LoopbackPort = 18765,
    [int]$VisualPort = 0,
    [int]$TrafficVisualPort = 0,
    [switch]$RunResponsiveVisualMatrix,
    [int]$ResponsiveVisualBasePort = 0,
    [switch]$SkipLoopback,
    [switch]$SkipVisual,
    [switch]$SkipPackaging,
    [string]$Version = '0.13.0'
)

$ErrorActionPreference = 'Stop'
$buildEnvironment = & (Join-Path $PSScriptRoot 'initialize-build-environment.ps1')
$utf8 = [Text.UTF8Encoding]::new($false)
[Console]::InputEncoding = $utf8
[Console]::OutputEncoding = $utf8
$OutputEncoding = $utf8
chcp 65001 | Out-Null

$repoRoot = Split-Path -Parent $PSScriptRoot
$testProject = Join-Path $repoRoot 'tests\Hackermes.PacketTraffic.Tests\Hackermes.PacketTraffic.Tests.csproj'
$buildRoot = [IO.Path]::GetFullPath($HackermesBuildRoot)
if ([string]::IsNullOrWhiteSpace($EvidenceRoot)) {
    $EvidenceRoot = Join-Path $buildRoot 'release-evidence'
}
$evidenceRoot = [IO.Path]::GetFullPath($EvidenceRoot)
if ([IO.Path]::GetPathRoot($buildRoot) -ne 'G:\' -or
    [IO.Path]::GetPathRoot($evidenceRoot) -ne 'G:\') {
    throw "HackermesBuildRoot and EvidenceRoot must stay on G: $buildRoot ; $evidenceRoot"
}
$resultsRoot = Join-Path $evidenceRoot 'test-results'
$profileRoot = Join-Path $evidenceRoot 'webview2-profile'
$visualRoot = Join-Path $evidenceRoot 'visual'
$trafficVisualRoot = Join-Path $evidenceRoot 'traffic-visual'
$responsiveVisualRoot = Join-Path $evidenceRoot 'responsive-visual-matrix'
$trxPath = Join-Path $resultsRoot 'full-tests.trx'

if ($VisualPort -eq 0) { $VisualPort = $LoopbackPort + 1 }
if ($TrafficVisualPort -eq 0) { $TrafficVisualPort = $VisualPort + 1 }
if ($ResponsiveVisualBasePort -eq 0) { $ResponsiveVisualBasePort = $TrafficVisualPort + 1 }
if ($LoopbackPort -lt 1 -or $LoopbackPort -gt 65535 -or
    $VisualPort -lt 1 -or $VisualPort -gt 65535 -or
    $TrafficVisualPort -lt 1 -or $TrafficVisualPort -gt 65535) {
    throw 'LoopbackPort, VisualPort, and TrafficVisualPort must be in the range 1..65535.'
}
if ($RunResponsiveVisualMatrix -and
    ($ResponsiveVisualBasePort -lt 1 -or $ResponsiveVisualBasePort + 5 -gt 65535)) {
    throw 'ResponsiveVisualBasePort must reserve six ports inside 1..65535.'
}
if (-not $SkipVisual -and ($VisualPort -eq $TrafficVisualPort -or
    (-not $SkipLoopback -and ($LoopbackPort -eq $VisualPort -or $LoopbackPort -eq $TrafficVisualPort)))) {
    throw 'Enabled loopback and visual acceptance ports must be distinct.'
}

if (-not [IO.Path]::IsPathRooted($HackermesBuildRoot)) {
    throw 'HackermesBuildRoot must be an absolute path.'
}
if ($buildRoot -eq [IO.Path]::GetPathRoot($buildRoot)) {
    throw "Refusing to use a drive root as HackermesBuildRoot: $buildRoot"
}
if ($profileRoot -notlike "$evidenceRoot\*") {
    throw "The isolated browser profile escaped the evidence root: $profileRoot"
}
if (Get-Process -Name 'Hackermes.App' -ErrorAction SilentlyContinue) {
    throw 'Hackermes.App is already running. Close it before release acceptance.'
}
if (Get-NetTCPConnection -LocalAddress '127.0.0.1' -LocalPort $LoopbackPort -State Listen -ErrorAction SilentlyContinue) {
    throw "Loopback acceptance port is already in use: 127.0.0.1:$LoopbackPort"
}
if (-not $SkipVisual -and (Get-NetTCPConnection -LocalAddress '127.0.0.1' -LocalPort $VisualPort -State Listen -ErrorAction SilentlyContinue)) {
    throw "Visual acceptance port is already in use: 127.0.0.1:$VisualPort"
}
if (-not $SkipVisual -and (Get-NetTCPConnection -LocalAddress '127.0.0.1' -LocalPort $TrafficVisualPort -State Listen -ErrorAction SilentlyContinue)) {
    throw "Traffic visual acceptance port is already in use: 127.0.0.1:$TrafficVisualPort"
}
if ($RunResponsiveVisualMatrix) {
    foreach ($port in $ResponsiveVisualBasePort..($ResponsiveVisualBasePort + 5)) {
        if ($port -in @($LoopbackPort, $VisualPort, $TrafficVisualPort)) {
            throw "Responsive visual port overlaps another acceptance port: $port"
        }
        if (Get-NetTCPConnection -LocalAddress '127.0.0.1' -LocalPort $port -State Listen -ErrorAction SilentlyContinue) {
            throw "Responsive visual acceptance port is already in use: 127.0.0.1:$port"
        }
    }
}

New-Item -ItemType Directory -Path $buildRoot, $resultsRoot -Force | Out-Null
if (Test-Path -LiteralPath $trxPath) { Remove-Item -LiteralPath $trxPath -Force }

function Assert-LastExitCode([string]$Operation) {
    if ($LASTEXITCODE -ne 0) { throw "$Operation failed with exit code $LASTEXITCODE." }
}

function Assert-Artifact([string]$Path, [long]$MinimumBytes = 1) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Required artifact is missing: $Path" }
    if ((Get-Item -LiteralPath $Path).Length -lt $MinimumBytes) { throw "Required artifact is empty or truncated: $Path" }
}

Write-Host "Release acceptance build root: $buildRoot"
Write-Host '[1/5] Building all production projects (Release)...'
& (Join-Path $PSScriptRoot 'build-hackermes.ps1') -Configuration Release -BuildRoot $buildRoot -KeepIntermediates
if (-not $?) { throw 'Release build script failed.' }

$releaseAppRoot = Join-Path $buildRoot 'bin\Hackermes.App\Release\net10.0'
$releaseToolHostRoot = Join-Path $buildRoot 'bin\Hackermes.ToolHost\Release\net10.0'
Assert-Artifact (Join-Path $releaseAppRoot 'Hackermes.App.exe') 1024
Assert-Artifact (Join-Path $releaseAppRoot 'Hackermes.App.dll') 1024
Assert-Artifact (Join-Path $releaseToolHostRoot 'Hackermes.ToolHost.exe') 1024
Assert-Artifact (Join-Path $releaseToolHostRoot 'Hackermes.ToolHost.dll') 1024

Write-Host '[2/5] Running the complete test project and writing TRX evidence...'
& dotnet restore $testProject --disable-parallel "-p:HackermesBuildRoot=$buildRoot"
Assert-LastExitCode 'Test restore'
& dotnet test $testProject --configuration Release --no-restore --disable-build-servers -m:1 `
    "-p:HackermesBuildRoot=$buildRoot" --logger "trx;LogFileName=full-tests.trx" `
    --results-directory $resultsRoot
Assert-LastExitCode 'Full test suite'
Assert-Artifact $trxPath 256

[xml]$trx = Get-Content -LiteralPath $trxPath -Raw
$summary = $trx.SelectSingleNode("//*[local-name()='ResultSummary']")
$counters = $trx.SelectSingleNode("//*[local-name()='Counters']")
if ($null -eq $summary -or $null -eq $counters) { throw 'TRX result summary is missing.' }
if ($summary.outcome -ne 'Completed' -or [int]$counters.failed -ne 0 -or [int]$counters.error -ne 0 -or
    [int]$counters.timeout -ne 0 -or [int]$counters.aborted -ne 0 -or [int]$counters.notExecuted -ne 0 -or
    [int]$counters.total -lt 1 -or [int]$counters.executed -ne [int]$counters.total -or
    [int]$counters.passed -ne [int]$counters.total) {
    throw "TRX acceptance failed: outcome=$($summary.outcome), total=$($counters.total), executed=$($counters.executed), passed=$($counters.passed), failed=$($counters.failed)."
}

if ($SkipLoopback) {
    Write-Host '[3/5] Loopback desktop self-test explicitly skipped.'
}
else {
    Write-Host '[3/5] Running isolated WebView2/CDP loopback self-test...'
    & (Join-Path $PSScriptRoot 'run-traffic-selftest.ps1') -Port $LoopbackPort `
        -BuildRoot $buildRoot -EvidenceRoot $evidenceRoot -ProfileRoot $profileRoot
    if (-not $?) { throw 'Loopback desktop self-test failed.' }
    Assert-Artifact (Join-Path $evidenceRoot 'desktop-loopback.log') 32
    if (-not (Test-Path -LiteralPath $profileRoot -PathType Container)) {
        throw "The isolated WebView2 profile was not created: $profileRoot"
    }
}

if ($SkipVisual) {
    Write-Host '[4/5] Assessment and Traffic visual acceptance explicitly skipped.'
}
else {
    Write-Host '[4/5] Capturing isolated light/dark Assessment visual evidence...'
    & (Join-Path $PSScriptRoot 'capture-assessment-visual.ps1') `
        -AppExe (Join-Path $releaseAppRoot 'Hackermes.App.exe') -EvidenceRoot $visualRoot -Port $VisualPort
    if (-not $?) { throw 'Assessment visual acceptance failed.' }
    Assert-Artifact (Join-Path $visualRoot 'authorized-assessment-light.png') 10240
    Assert-Artifact (Join-Path $visualRoot 'authorized-assessment-dark.png') 10240
    Assert-Artifact (Join-Path $visualRoot 'visual-metadata.json') 128

    Write-Host 'Capturing isolated light/dark Traffic workbench evidence with real loopback packets...'
    & (Join-Path $PSScriptRoot 'capture-traffic-visual.ps1') `
        -AppExe (Join-Path $releaseAppRoot 'Hackermes.App.exe') -EvidenceRoot $trafficVisualRoot -Port $TrafficVisualPort
    if (-not $?) { throw 'Traffic visual acceptance failed.' }
    Assert-Artifact (Join-Path $trafficVisualRoot 'traffic-workbench-light.png') 10240
    Assert-Artifact (Join-Path $trafficVisualRoot 'traffic-workbench-dark.png') 10240
    Assert-Artifact (Join-Path $trafficVisualRoot 'visual-metadata.json') 128

    if ($RunResponsiveVisualMatrix) {
        Write-Host 'Capturing wide/medium/minimum responsive visual matrix at the real host DPI...'
        & (Join-Path $PSScriptRoot 'capture-visual-matrix.ps1') `
            -AppExe (Join-Path $releaseAppRoot 'Hackermes.App.exe') `
            -EvidenceRoot $responsiveVisualRoot -BasePort $ResponsiveVisualBasePort
        if (-not $?) { throw 'Responsive visual matrix acceptance failed.' }
        Assert-Artifact (Join-Path $responsiveVisualRoot 'visual-matrix.json') 1024
    }
}

Write-Host '[5/5] Checking cleanup, packaging, and writing acceptance manifest...'
$residualApps = @(Get-Process -Name 'Hackermes.App' -ErrorAction SilentlyContinue)
if ($residualApps.Count -gt 0) { throw "Residual Hackermes.App process detected: $($residualApps.Id -join ', ')." }
$residualListener = Get-NetTCPConnection -LocalAddress '127.0.0.1' -LocalPort $LoopbackPort -State Listen -ErrorAction SilentlyContinue
if ($residualListener) { throw "Residual loopback listener detected on 127.0.0.1:$LoopbackPort." }
$residualVisualListener = Get-NetTCPConnection -LocalAddress '127.0.0.1' -LocalPort $VisualPort -State Listen -ErrorAction SilentlyContinue
if (-not $SkipVisual -and $residualVisualListener) { throw "Residual visual listener detected on 127.0.0.1:$VisualPort." }
$residualTrafficVisualListener = Get-NetTCPConnection -LocalAddress '127.0.0.1' -LocalPort $TrafficVisualPort -State Listen -ErrorAction SilentlyContinue
if (-not $SkipVisual -and $residualTrafficVisualListener) { throw "Residual Traffic visual listener detected on 127.0.0.1:$TrafficVisualPort." }
if ($RunResponsiveVisualMatrix) {
    foreach ($port in $ResponsiveVisualBasePort..($ResponsiveVisualBasePort + 5)) {
        if (Get-NetTCPConnection -LocalAddress '127.0.0.1' -LocalPort $port -State Listen -ErrorAction SilentlyContinue) {
            throw "Residual responsive visual listener detected on 127.0.0.1:$port."
        }
    }
}
$isolatedProfileRoots = @($profileRoot)
if (-not $SkipVisual) {
    $isolatedProfileRoots += @(
        (Join-Path $visualRoot 'webview2-profile'),
        (Join-Path $trafficVisualRoot 'webview2-profile'))
}
$residualProfileProcesses = @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | Where-Object {
    $commandLine = $_.CommandLine
    $_.ProcessId -ne $PID -and
    -not [string]::IsNullOrWhiteSpace($commandLine) -and
    @($isolatedProfileRoots | Where-Object {
        $commandLine.IndexOf($_, [StringComparison]::OrdinalIgnoreCase) -ge 0
    }).Count -gt 0
})
if ($residualProfileProcesses.Count -gt 0) {
    throw "Residual isolated-profile process detected: PID $($residualProfileProcesses.ProcessId -join ', ')."
}

$artifacts = @($trxPath, (Join-Path $releaseAppRoot 'Hackermes.App.exe'), (Join-Path $releaseToolHostRoot 'Hackermes.ToolHost.exe'))
if (-not $SkipLoopback) { $artifacts += Join-Path $evidenceRoot 'desktop-loopback.log' }
if (-not $SkipVisual) {
    $artifacts += @(
        (Join-Path $visualRoot 'authorized-assessment-light.png'),
        (Join-Path $visualRoot 'authorized-assessment-dark.png'),
        (Join-Path $visualRoot 'visual-metadata.json'),
        (Join-Path $trafficVisualRoot 'traffic-workbench-light.png'),
        (Join-Path $trafficVisualRoot 'traffic-workbench-dark.png'),
        (Join-Path $trafficVisualRoot 'visual-metadata.json'))
    if ($RunResponsiveVisualMatrix) {
        $matrixManifestPath = Join-Path $responsiveVisualRoot 'visual-matrix.json'
        $matrixManifest = Get-Content -LiteralPath $matrixManifestPath -Raw | ConvertFrom-Json
        if ($matrixManifest.mode -ne 'responsiveViewport' -or $matrixManifest.realSystemDpiChanged -ne $false -or
            @($matrixManifest.entries).Count -ne 3 -or @($matrixManifest.artifacts).Count -lt 18) {
            throw "Responsive visual matrix manifest is incomplete: $matrixManifestPath"
        }
        $artifacts += $matrixManifestPath
        $artifacts += @($matrixManifest.artifacts | ForEach-Object { [string]$_.path })
    }
}

if ($SkipPackaging) {
    Write-Host 'Windows release packaging explicitly skipped.'
}
else {
    Write-Host 'Creating and validating the Windows release package...'
    $packageOutputRoot = Join-Path $evidenceRoot 'packages'
    & (Join-Path $PSScriptRoot 'package-release.ps1') -Version $Version -Platforms windows `
        -OutputRoot $packageOutputRoot -BuildRoot $buildRoot
    if (-not $?) { throw 'Windows release packaging failed.' }
    $versionRoot = Join-Path $packageOutputRoot $Version
    $packageRoot = Join-Path $versionRoot "Hackermes-$Version-windows-x64"
    $archivePath = Join-Path $versionRoot "Hackermes-$Version-windows-x64.zip"
    $releaseManifest = Join-Path $packageRoot 'release-manifest.json'
    $checksums = Join-Path $versionRoot 'SHA256SUMS.txt'
    Assert-Artifact $archivePath 1024
    Assert-Artifact $releaseManifest 128
    Assert-Artifact $checksums 32
    $packageAppRoot = [IO.Path]::GetFullPath((Join-Path $packageRoot 'app'))
    Assert-Artifact (Join-Path $packageAppRoot 'Hackermes.App.exe') 1024
    Assert-Artifact (Join-Path $packageAppRoot 'Hackermes.ToolHost.exe') 1024
    $releaseManifestObject = Get-Content -LiteralPath $releaseManifest -Raw | ConvertFrom-Json
    if ($releaseManifestObject.schemaVersion -ne 1 -or $releaseManifestObject.rid -ne 'win-x64' -or
        @($releaseManifestObject.files).Count -lt 1) {
        throw "Windows release manifest has an invalid schema, RID, or empty file list: $releaseManifest"
    }
    foreach ($entry in $releaseManifestObject.files) {
        $relativePath = ([string]$entry.path).Replace('/', [IO.Path]::DirectorySeparatorChar)
        $packagedFile = [IO.Path]::GetFullPath((Join-Path $packageAppRoot $relativePath))
        if (-not $packagedFile.StartsWith(
            $packageAppRoot + [IO.Path]::DirectorySeparatorChar,
            [StringComparison]::OrdinalIgnoreCase)) {
            throw "Release manifest path escaped the package app directory: $($entry.path)"
        }
        # Empty package files (for example Python namespace __init__.py files) are
        # legitimate when the signed manifest explicitly records size 0.
        Assert-Artifact $packagedFile 0
        $packagedItem = Get-Item -LiteralPath $packagedFile
        $packagedHash = (Get-FileHash -LiteralPath $packagedFile -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($packagedItem.Length -ne [long]$entry.size -or $packagedHash -ne [string]$entry.sha256) {
            throw "Release manifest verification failed for: $($entry.path)"
        }
    }
    if (Get-ChildItem -LiteralPath $versionRoot -Directory -Force -Filter '.toolhost-*') {
        throw "ToolHost staging directories remained in release artifacts: $versionRoot"
    }
    $expectedArchiveHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    $checksumText = Get-Content -LiteralPath $checksums -Raw
    if ($checksumText -notmatch [regex]::Escape($expectedArchiveHash)) {
        throw "SHA256SUMS.txt does not contain the Windows archive hash: $archivePath"
    }
    $artifacts += @($archivePath, $releaseManifest, $checksums)
}

$manifest = [ordered]@{
    schemaVersion = 1
    acceptedAtUtc = [DateTime]::UtcNow.ToString('o')
    buildRoot = $buildRoot
    evidenceRoot = $evidenceRoot
    loopback = if ($SkipLoopback) { 'skipped' } else { 'passed' }
    visual = if ($SkipVisual) { 'skipped' } else { 'passed' }
    trafficVisual = if ($SkipVisual) { 'skipped' } else { 'passed' }
    responsiveVisual = if (-not $RunResponsiveVisualMatrix) { 'not-requested' } elseif ($SkipVisual) { 'skipped' } else { 'passed' }
    packaging = if ($SkipPackaging) { 'skipped' } else { 'passed' }
    tests = [ordered]@{ total = [int]$counters.total; passed = [int]$counters.passed; failed = [int]$counters.failed }
    artifacts = @($artifacts | ForEach-Object {
        $item = Get-Item -LiteralPath $_
        if ([IO.Path]::GetPathRoot($item.FullName) -ne 'G:\') {
            throw "Acceptance artifact escaped G: $($item.FullName)"
        }
        [ordered]@{ path = $item.FullName; bytes = $item.Length; sha256 = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
    })
}
$manifestPath = Join-Path $evidenceRoot 'release-acceptance.json'
$manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $manifestPath -Encoding UTF8
Assert-Artifact $manifestPath 128
Write-Host "RELEASE ACCEPTANCE PASS: $manifestPath"
