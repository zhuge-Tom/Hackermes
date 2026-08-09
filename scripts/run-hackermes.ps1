[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$buildRoot = Join-Path $env:LOCALAPPDATA 'Hackermes\Build'
$executable = Join-Path $buildRoot 'bin\Hackermes.App\Debug\net10.0\Hackermes.App.exe'

$existing = Get-Process -Name 'Hackermes.App' -ErrorAction SilentlyContinue | Select-Object -First 1
if ($existing) {
    Write-Host "Hackermes is already running (PID $($existing.Id))."
    exit 0
}

if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
    throw "Hackermes has not been built yet. Run .\scripts\build-hackermes.ps1 once, then run this startup script again."
}

$process = Start-Process -FilePath $executable -WorkingDirectory (Split-Path -Parent $executable) -PassThru
if ($process.WaitForExit(1000)) {
    throw "Hackermes exited during startup with code $($process.ExitCode)."
}

Write-Host "Hackermes started (PID $($process.Id))."
