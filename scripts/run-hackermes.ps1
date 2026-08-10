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

$process = $null
$maximumStartAttempts = 20
for ($attempt = 1; $attempt -le $maximumStartAttempts; $attempt++) {
    try {
        $process = Start-Process -FilePath $executable -WorkingDirectory (Split-Path -Parent $executable) -PassThru
        break
    }
    catch [System.InvalidOperationException] {
        if ($attempt -eq $maximumStartAttempts) { throw }
        Write-Host "Hackermes runtime is temporarily locked; retrying startup ($attempt/$maximumStartAttempts)..."
        Start-Sleep -Milliseconds 750
    }
}

if (-not $process) {
    throw 'Hackermes could not be started.'
}
if ($process.WaitForExit(1000)) {
    throw "Hackermes exited during startup with code $($process.ExitCode)."
}

Write-Host "Hackermes started (PID $($process.Id))."
