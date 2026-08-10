[CmdletBinding()]
param(
    [string]$Destination = (Join-Path $env:LOCALAPPDATA 'Programs\Hackermes'),
    [string]$AllowedProgramsRoot = (Join-Path $env:LOCALAPPDATA 'Programs'),
    [string]$MenuRoot = (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'),
    [switch]$RestorePrevious
)

$ErrorActionPreference = 'Stop'
$source = Join-Path $PSScriptRoot 'app'
$manifestPath = Join-Path $PSScriptRoot 'release-manifest.json'
$destinationPath = [IO.Path]::GetFullPath($Destination)
$programsRoot = [IO.Path]::GetFullPath($AllowedProgramsRoot)
$backupPath = $destinationPath + '.previous'
$stagingPath = $destinationPath + '.staging-' + [Guid]::NewGuid().ToString('N')

if (-not $destinationPath.StartsWith($programsRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "For safety, the installer only writes below $programsRoot"
}

function Stop-Hackermes {
    Get-Process Hackermes.App -ErrorAction SilentlyContinue | Stop-Process -Force
}

function Install-Shortcut {
    param([string]$InstallRoot)
    $shell = New-Object -ComObject WScript.Shell
    $menuDirectory = Join-Path $MenuRoot 'Hackermes'
    New-Item -ItemType Directory -Path $menuDirectory -Force | Out-Null
    $shortcut = $shell.CreateShortcut((Join-Path $menuDirectory 'Hackermes.lnk'))
    $shortcut.TargetPath = Join-Path $InstallRoot 'Hackermes.App.exe'
    $shortcut.WorkingDirectory = $InstallRoot
    $shortcut.IconLocation = (Join-Path $InstallRoot 'Hackermes.App.exe') + ',0'
    $shortcut.Save()
}

if ($RestorePrevious) {
    if (-not (Test-Path -LiteralPath (Join-Path $backupPath 'Hackermes.App.exe') -PathType Leaf)) {
        throw "No recoverable previous installation exists at $backupPath"
    }
    Stop-Hackermes
    $failedPath = $destinationPath + '.failed-' + (Get-Date -Format 'yyyyMMddHHmmss')
    if (Test-Path -LiteralPath $destinationPath) { Move-Item -LiteralPath $destinationPath -Destination $failedPath }
    Move-Item -LiteralPath $backupPath -Destination $destinationPath
    Install-Shortcut $destinationPath
    Write-Host "Hackermes was restored from $backupPath. The replaced version is at $failedPath"
    exit 0
}

$executable = Join-Path $source 'Hackermes.App.exe'
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
    throw "Hackermes.App.exe was not found beside this installer: $executable"
}
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    throw "release-manifest.json is missing; package integrity cannot be verified."
}

$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
foreach ($file in $manifest.files) {
    $relative = [string]$file.path
    if ([string]::IsNullOrWhiteSpace($relative) -or [IO.Path]::IsPathRooted($relative) -or $relative.Contains('..')) {
        throw "Unsafe path in release manifest: $relative"
    }
    $candidate = Join-Path $source ($relative.Replace('/', [IO.Path]::DirectorySeparatorChar))
    if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) { throw "Package file is missing: $relative" }
    $actual = (Get-FileHash -LiteralPath $candidate -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne ([string]$file.sha256).ToLowerInvariant()) { throw "Package hash mismatch: $relative" }
}

try {
    New-Item -ItemType Directory -Path $stagingPath -Force | Out-Null
    Copy-Item -Path (Join-Path $source '*') -Destination $stagingPath -Recurse -Force
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Uninstall-Hackermes.ps1') -Destination $stagingPath -Force
    Copy-Item -LiteralPath $manifestPath -Destination $stagingPath -Force
    if (-not (Test-Path -LiteralPath (Join-Path $stagingPath 'Hackermes.App.exe') -PathType Leaf)) {
        throw 'Staged application validation failed.'
    }

    Stop-Hackermes
    if (Test-Path -LiteralPath $backupPath) { Remove-Item -LiteralPath $backupPath -Recurse -Force }
    if (Test-Path -LiteralPath $destinationPath) { Move-Item -LiteralPath $destinationPath -Destination $backupPath }
    Move-Item -LiteralPath $stagingPath -Destination $destinationPath
    Install-Shortcut $destinationPath
}
catch {
    if (Test-Path -LiteralPath $stagingPath) { Remove-Item -LiteralPath $stagingPath -Recurse -Force }
    if (-not (Test-Path -LiteralPath $destinationPath) -and (Test-Path -LiteralPath $backupPath)) {
        Move-Item -LiteralPath $backupPath -Destination $destinationPath
    }
    throw
}

Write-Host "Hackermes $($manifest.version) installed to $destinationPath"
if (Test-Path -LiteralPath $backupPath) { Write-Host "Previous version retained for recovery: $backupPath" }
Write-Host 'User settings under LocalAppData\Hackermes were preserved.'
