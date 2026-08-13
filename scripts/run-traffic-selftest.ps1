param(
    [int]$Port = 18765,
    [int]$TimeoutSeconds = 80,
    [switch]$NoBuild,
    [string]$BuildRoot,
    [string]$EvidenceRoot,
    [string]$ProfileRoot
)

$ErrorActionPreference = 'Stop'
$buildEnvironment = & (Join-Path $PSScriptRoot 'initialize-build-environment.ps1')
$utf8 = [System.Text.UTF8Encoding]::new($false)
[Console]::InputEncoding = $utf8
[Console]::OutputEncoding = $utf8
$OutputEncoding = $utf8
chcp 65001 | Out-Null
$repoRoot = Split-Path -Parent $PSScriptRoot
$pagePath = Join-Path $PSScriptRoot 'selftest-page.html'
$serverPath = Join-Path $PSScriptRoot 'selftest-server.py'
$appProject = Join-Path $repoRoot 'src\Hackermes.App\Hackermes.App.csproj'
if ([string]::IsNullOrWhiteSpace($BuildRoot)) {
    $BuildRoot = $buildEnvironment.DefaultBuildRoot
}
$buildRoot = [IO.Path]::GetFullPath($BuildRoot)
$appExe = Join-Path $buildRoot 'bin\Hackermes.App\TrafficSelfTest\net10.0\Hackermes.App.exe'
if ([string]::IsNullOrWhiteSpace($EvidenceRoot)) {
    $EvidenceRoot = Join-Path $buildEnvironment.Root 'evidence\traffic-selftest'
}
$evidenceRoot = [IO.Path]::GetFullPath($EvidenceRoot)
if ([IO.Path]::GetPathRoot($buildRoot) -ne 'G:\' -or
    [IO.Path]::GetPathRoot($evidenceRoot) -ne 'G:\') {
    throw "BuildRoot and EvidenceRoot must stay on G: $buildRoot ; $evidenceRoot"
}
if ([string]::IsNullOrWhiteSpace($ProfileRoot)) {
    $ProfileRoot = Join-Path $evidenceRoot 'webview2-profile'
}
$profileRoot = [IO.Path]::GetFullPath($ProfileRoot)
if (-not $profileRoot.StartsWith(
    $evidenceRoot + [IO.Path]::DirectorySeparatorChar,
    [StringComparison]::OrdinalIgnoreCase)) {
    throw "ProfileRoot must be contained by EvidenceRoot: $profileRoot"
}
if (Get-Process -Name 'Hackermes.App' -ErrorAction SilentlyContinue) {
    throw 'Hackermes.App is already running. Close it before the desktop self-test.'
}
$isolatedDataRoot = Join-Path $evidenceRoot 'app-data'
$logPath = Join-Path $isolatedDataRoot 'logs\latest.log'
$prefix = "http://127.0.0.1:$Port/"
$buildOrder = @(
    'Hackermes.Base', 'Hackermes.PageAgent', 'Hackermes.Platform', 'Hackermes.Dock',
    'Hackermes.Cdp', 'Hackermes.Traffic', 'Hackermes.Browser', 'Hackermes.Inspector',
    'Hackermes.Automation', 'Hackermes.Terminal', 'Hackermes.AiPanel',
    'Hackermes.Assessment', 'Hackermes.ToolHost', 'Hackermes.App'
)

function Wait-SelfTestAssembly {
    param([Parameter(Mandatory)] [string]$Path, [int]$TimeoutSeconds = 30)
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        try {
            $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
            $stream.Dispose()
            return
        }
        catch { Start-Sleep -Milliseconds 250 }
    }
    throw "Self-test build output stayed locked: $Path"
}

function Wait-ProcessGone {
    param([Parameter(Mandatory)] [int]$Id, [int]$TimeoutSeconds = 30)
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        if (-not (Get-Process -Id $Id -ErrorAction SilentlyContinue)) { return $true }
        Start-Sleep -Milliseconds 100
    }
    return -not (Get-Process -Id $Id -ErrorAction SilentlyContinue)
}

function Build-SelfTestProject {
    param([Parameter(Mandatory)] [string]$Name)
    $projectPath = Join-Path $repoRoot "src\$Name\$Name.csproj"
    $assemblyPath = Join-Path $buildRoot "bin\$Name\TrafficSelfTest\net10.0\$Name.dll"
    $maximumAttempts = if ($Name -eq 'Hackermes.App') { 20 } else { 8 }
    for ($attempt = 1; $attempt -le $maximumAttempts; $attempt++) {
        $arguments = @(
            'build', $projectPath, '--configuration', 'TrafficSelfTest',
            '--no-restore', '--no-dependencies', '--disable-build-servers',
            '-m:1', '-p:UseSharedCompilation=false', "-p:HackermesBuildRoot=$buildRoot"
        )
        if ($Name -eq 'Hackermes.App') { $arguments += '--no-incremental' }
        $output = & dotnet @arguments 2>&1
        $exitCode = $LASTEXITCODE
        $output | Out-Host
        if ($exitCode -eq 0) {
            Wait-SelfTestAssembly -Path $assemblyPath
            return
        }
        $failure = [string]::Join([Environment]::NewLine, [string[]]$output)
        if ($failure -notmatch '(?i)being used by another process|process cannot access the file') {
            throw "$Name has a source or configuration error."
        }
        if ($attempt -lt $maximumAttempts) { Start-Sleep -Seconds 2 }
    }
    throw "$Name remained locked after $maximumAttempts build attempts."
}

if (-not $NoBuild) {
    dotnet build-server shutdown | Out-Host
    dotnet restore $appProject --disable-parallel "-p:HackermesBuildRoot=$buildRoot" | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "App restore failed with exit code $LASTEXITCODE." }
    $buildOrder | ForEach-Object { Build-SelfTestProject -Name $_ }
}
if (-not (Test-Path -LiteralPath $appExe)) { throw "Self-test executable not found: $appExe" }

$runtimeDirectory = Split-Path -Parent $appExe
$runtimeLibraries = $buildOrder | Where-Object { $_ -notin @('Hackermes.App', 'Hackermes.ToolHost') }
Write-Host 'Waiting for security scanning to release self-test runtime files...'
foreach ($name in $runtimeLibraries) {
    Wait-SelfTestAssembly -Path (Join-Path $runtimeDirectory "$name.dll") -TimeoutSeconds 60
}
Wait-SelfTestAssembly -Path (Join-Path $runtimeDirectory 'Hackermes.App.dll') -TimeoutSeconds 60
Wait-SelfTestAssembly -Path $appExe -TimeoutSeconds 45

$python = Get-Command python -ErrorAction Stop
$server = Start-Process -FilePath $python.Source -ArgumentList @($serverPath, '--port', $Port, '--page', $pagePath) -PassThru -WindowStyle Hidden

$oldSelftest = $env:HACKERMES_SELFTEST
$oldExit = $env:HACKERMES_SELFTEST_EXIT
$oldUrl = $env:HACKERMES_AUTOOPEN_URL
$oldProfileRoot = $env:HACKERMES_BROWSER_PROFILE_ROOT
$oldDataRoot = $env:HACKERMES_DATA_ROOT
$process = $null
$selfTestProcesses = @()
try {
    $serverDeadline = [DateTime]::UtcNow.AddSeconds(10)
    while ([DateTime]::UtcNow -lt $serverDeadline) {
        try {
            if ((Invoke-WebRequest -UseBasicParsing -Uri "${prefix}health" -TimeoutSec 1).StatusCode -eq 200) { break }
        }
        catch { Start-Sleep -Milliseconds 100 }
    }
    if ($server.HasExited) { throw "Local HTTP server exited with code $($server.ExitCode)." }
    try { $null = Invoke-WebRequest -UseBasicParsing -Uri "${prefix}health" -TimeoutSec 1 } catch { throw "Local HTTP server did not become ready." }
    $env:HACKERMES_SELFTEST = '1'
    $env:HACKERMES_SELFTEST_EXIT = '1'
    $env:HACKERMES_AUTOOPEN_URL = "${prefix}selftest-page.html"
    $env:HACKERMES_BROWSER_PROFILE_ROOT = $profileRoot
    $env:HACKERMES_DATA_ROOT = $isolatedDataRoot
    New-Item -ItemType Directory -Path $evidenceRoot, $profileRoot, $isolatedDataRoot -Force | Out-Null
    if (Test-Path -LiteralPath $logPath) { Remove-Item -LiteralPath $logPath -Force }
    $launchedAt = [DateTime]::UtcNow
    $process = Start-Process -FilePath $appExe -WorkingDirectory $runtimeDirectory -PassThru -WindowStyle Hidden

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $resultLine = $null
    while ([DateTime]::UtcNow -lt $deadline) {
        $selfTestProcesses = @(Get-Process -Name 'Hackermes.App' -ErrorAction SilentlyContinue)
        if ((Test-Path -LiteralPath $logPath) -and (Get-Item -LiteralPath $logPath).LastWriteTimeUtc -ge $launchedAt) {
            $resultLine = Get-Content -LiteralPath $logPath | Where-Object { $_ -match 'TRAFFIC_SELFTEST RESULT' } | Select-Object -Last 1
            if ($resultLine) { break }
        }
        Start-Sleep -Milliseconds 250
    }

    if (-not $resultLine) {
        $tail = if (Test-Path -LiteralPath $logPath) { (Get-Content -LiteralPath $logPath -Tail 40) -join [Environment]::NewLine } else { '(log missing)' }
        throw "Timed out or App exited before a traffic result marker.`n$tail"
    }
    if ($resultLine -notmatch 'RESULT 5/5 PASS') { throw "Traffic acceptance failed: $resultLine" }
    Write-Host $resultLine
    Get-Content -LiteralPath $logPath | Where-Object { $_ -match 'TRAFFIC_SELFTEST (DPAPI_KEY|PASS|RESULT)' }
    Copy-Item -LiteralPath $logPath -Destination (Join-Path $evidenceRoot 'desktop-loopback.log') -Force
}
finally {
    $selfTestProcesses = @($selfTestProcesses + @(Get-Process -Name 'Hackermes.App' -ErrorAction SilentlyContinue) |
        Sort-Object Id -Unique)
    foreach ($candidate in $selfTestProcesses) {
        if (-not $candidate.HasExited) { Stop-Process -Id $candidate.Id -Force -ErrorAction SilentlyContinue }
    }
    foreach ($candidate in $selfTestProcesses) {
        Wait-Process -Id $candidate.Id -Timeout 10 -ErrorAction SilentlyContinue
    }
    if ($server -and -not $server.HasExited) { Stop-Process -Id $server.Id -Force; Wait-Process -Id $server.Id -Timeout 10 -ErrorAction SilentlyContinue }
    $appGone = $true
    foreach ($candidate in $selfTestProcesses) {
        if (-not (Wait-ProcessGone -Id $candidate.Id)) { $appGone = $false }
    }
    $serverGone = -not $server -or (Wait-ProcessGone -Id $server.Id)
    $env:HACKERMES_SELFTEST = $oldSelftest
    $env:HACKERMES_SELFTEST_EXIT = $oldExit
    $env:HACKERMES_AUTOOPEN_URL = $oldUrl
    $env:HACKERMES_BROWSER_PROFILE_ROOT = $oldProfileRoot
    $env:HACKERMES_DATA_ROOT = $oldDataRoot

    if (-not $appGone) {
        throw "Hackermes self-test process was not reaped: PID $($process.Id)."
    }
    if (-not $serverGone) {
        throw "Loopback server process was not reaped: PID $($server.Id)."
    }
    $profileProcessDeadline = [DateTime]::UtcNow.AddSeconds(10)
    do {
        $profileProcesses = @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | Where-Object {
            $_.ProcessId -ne $PID -and
            -not [string]::IsNullOrWhiteSpace($_.CommandLine) -and
            $_.CommandLine.IndexOf($profileRoot, [StringComparison]::OrdinalIgnoreCase) -ge 0
        })
        if ($profileProcesses.Count -eq 0) { break }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $profileProcessDeadline)
    if ($profileProcesses.Count -gt 0) {
        throw "WebView2 profile process was not reaped: PID $($profileProcesses.ProcessId -join ', ')."
    }
    $listener = Get-NetTCPConnection -LocalAddress '127.0.0.1' -LocalPort $Port -State Listen -ErrorAction SilentlyContinue
    if ($listener) { throw "Loopback listener remained on 127.0.0.1:$Port after self-test cleanup." }
}
