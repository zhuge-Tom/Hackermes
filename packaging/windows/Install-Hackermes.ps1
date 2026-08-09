[CmdletBinding()]
param(
    [string]$Destination = (Join-Path $env:LOCALAPPDATA 'Programs\Hackermes')
)

$ErrorActionPreference = 'Stop'
$source = Join-Path $PSScriptRoot 'app'
$executable = Join-Path $source 'Hackermes.App.exe'
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
    throw "Hackermes.App.exe was not found beside this installer: $executable"
}

$destinationPath = [IO.Path]::GetFullPath($Destination)
$programsRoot = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Programs'))
if (-not $destinationPath.StartsWith($programsRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "For safety, the installer only writes below $programsRoot"
}

Get-Process Hackermes.App -ErrorAction SilentlyContinue | Stop-Process
if (Test-Path -LiteralPath $destinationPath) {
    Remove-Item -LiteralPath $destinationPath -Recurse -Force
}
New-Item -ItemType Directory -Path $destinationPath -Force | Out-Null
Copy-Item -Path (Join-Path $source '*') -Destination $destinationPath -Recurse -Force

$shell = New-Object -ComObject WScript.Shell
$menuDirectory = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Hackermes'
New-Item -ItemType Directory -Path $menuDirectory -Force | Out-Null
$shortcut = $shell.CreateShortcut((Join-Path $menuDirectory 'Hackermes.lnk'))
$shortcut.TargetPath = Join-Path $destinationPath 'Hackermes.App.exe'
$shortcut.WorkingDirectory = $destinationPath
$shortcut.IconLocation = (Join-Path $destinationPath 'Hackermes.App.exe') + ',0'
$shortcut.Save()

Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Uninstall-Hackermes.ps1') -Destination $destinationPath -Force
Write-Host "Hackermes installed to $destinationPath"
Write-Host 'Open it from the Start menu or run Hackermes.App.exe.'
