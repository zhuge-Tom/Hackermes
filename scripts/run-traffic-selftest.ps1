param(
    [int]$Port = 18765,
    [int]$TimeoutSeconds = 55,
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$pagePath = Join-Path $PSScriptRoot 'selftest-page.html'
$serverPath = Join-Path $PSScriptRoot 'selftest-server.py'
$appProject = Join-Path $repoRoot 'src\Hookmes.App\Hookmes.App.csproj'
$appExe = Join-Path $repoRoot 'src\Hookmes.App\bin\TrafficSelfTest\net10.0\Hookmes.App.exe'
$logPath = Join-Path $env:LOCALAPPDATA 'Hookmes\logs\latest.log'
$prefix = "http://127.0.0.1:$Port/"

if (-not $NoBuild) {
    dotnet build $appProject --configuration TrafficSelfTest
    if ($LASTEXITCODE -ne 0) { throw "App build failed with exit code $LASTEXITCODE." }
}
if (-not (Test-Path -LiteralPath $appExe)) { throw "Self-test executable not found: $appExe" }

$python = Get-Command python -ErrorAction Stop
$server = Start-Process -FilePath $python.Source -ArgumentList @($serverPath, '--port', $Port, '--page', $pagePath) -PassThru -WindowStyle Hidden

$oldSelftest = $env:HOOKMES_SELFTEST
$oldExit = $env:HOOKMES_SELFTEST_EXIT
$oldUrl = $env:HOOKMES_AUTOOPEN_URL
$process = $null
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
    $env:HOOKMES_SELFTEST = '1'
    $env:HOOKMES_SELFTEST_EXIT = '1'
    $env:HOOKMES_AUTOOPEN_URL = "${prefix}selftest-page.html"
    $launchedAt = [DateTime]::UtcNow
    $process = Start-Process -FilePath $appExe -PassThru -WindowStyle Hidden

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    $resultLine = $null
    while ([DateTime]::UtcNow -lt $deadline) {
        if ((Test-Path -LiteralPath $logPath) -and (Get-Item -LiteralPath $logPath).LastWriteTimeUtc -ge $launchedAt) {
            $resultLine = Get-Content -LiteralPath $logPath | Where-Object { $_ -match 'TRAFFIC_SELFTEST RESULT' } | Select-Object -Last 1
            if ($resultLine) { break }
        }
        if ($process.HasExited) { break }
        Start-Sleep -Milliseconds 250
    }

    if (-not $resultLine) {
        $tail = if (Test-Path -LiteralPath $logPath) { (Get-Content -LiteralPath $logPath -Tail 40) -join [Environment]::NewLine } else { '(log missing)' }
        throw "Timed out or App exited before a traffic result marker.`n$tail"
    }
    if ($resultLine -notmatch 'RESULT 3/3 PASS') { throw "Traffic acceptance failed: $resultLine" }
    Write-Host $resultLine
    Get-Content -LiteralPath $logPath | Where-Object { $_ -match 'TRAFFIC_SELFTEST (PASS|RESULT)' }
}
finally {
    if ($process -and -not $process.HasExited) { Stop-Process -Id $process.Id -Force }
    if ($server -and -not $server.HasExited) { Stop-Process -Id $server.Id -Force }
    $env:HOOKMES_SELFTEST = $oldSelftest
    $env:HOOKMES_SELFTEST_EXIT = $oldExit
    $env:HOOKMES_AUTOOPEN_URL = $oldUrl
}
