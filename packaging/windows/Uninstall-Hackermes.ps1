[CmdletBinding()]
param(
    [string]$Destination = (Join-Path $env:LOCALAPPDATA 'Programs\Hackermes')
)

$ErrorActionPreference = 'Stop'
$destinationPath = [IO.Path]::GetFullPath($Destination)
$programsRoot = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Programs'))
if (-not $destinationPath.StartsWith($programsRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "For safety, the uninstaller only removes paths below $programsRoot"
}

Get-Process Hackermes.App -ErrorAction SilentlyContinue | Stop-Process
$menuDirectory = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Hackermes'
if (Test-Path -LiteralPath $menuDirectory) {
    Remove-Item -LiteralPath $menuDirectory -Recurse -Force
}
if (Test-Path -LiteralPath $destinationPath) {
    Remove-Item -LiteralPath $destinationPath -Recurse -Force
}
Write-Host 'Hackermes was removed. User settings under LocalAppData were preserved.'
