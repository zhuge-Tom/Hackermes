[CmdletBinding()]
param(
    [string]$Destination = (Join-Path $env:LOCALAPPDATA 'Programs\Hackermes'),
    [string]$AllowedProgramsRoot = (Join-Path $env:LOCALAPPDATA 'Programs'),
    [string]$MenuRoot = (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'),
    [switch]$PurgeUserData
)

$ErrorActionPreference = 'Stop'
$destinationPath = [IO.Path]::GetFullPath($Destination)
$programsRoot = [IO.Path]::GetFullPath($AllowedProgramsRoot)
if (-not $destinationPath.StartsWith($programsRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "For safety, the uninstaller only removes paths below $programsRoot"
}

Get-Process Hackermes.App -ErrorAction SilentlyContinue | Stop-Process -Force
$menuDirectory = Join-Path $MenuRoot 'Hackermes'
if (Test-Path -LiteralPath $menuDirectory) { Remove-Item -LiteralPath $menuDirectory -Recurse -Force }
foreach ($path in @($destinationPath, $destinationPath + '.previous')) {
    if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force }
}
if ($PurgeUserData) {
    $userData = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Hackermes'))
    if (Test-Path -LiteralPath $userData) { Remove-Item -LiteralPath $userData -Recurse -Force }
    Write-Host 'Hackermes and its user data were removed.'
}
else {
    Write-Host 'Hackermes was removed. User settings under LocalAppData\Hackermes were preserved.'
}
