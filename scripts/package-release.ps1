[CmdletBinding()]
param(
    [string]$Version = '0.11.0',
    [string]$OutputRoot,
    [string]$BuildRoot,
    [ValidateSet('all', 'windows', 'linux')]
    [string]$Platforms = 'all'
)

$ErrorActionPreference = 'Stop'
$buildEnvironment = & (Join-Path $PSScriptRoot 'initialize-build-environment.ps1')
$utf8 = [Text.UTF8Encoding]::new($false)
[Console]::InputEncoding = $utf8
[Console]::OutputEncoding = $utf8
$OutputEncoding = $utf8
if ($Version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') {
    throw "Invalid semantic version: $Version"
}

$projectRoot = Split-Path -Parent $PSScriptRoot
$appProject = Join-Path $projectRoot 'src\Hackermes.App\Hackermes.App.csproj'
$toolHostProject = Join-Path $projectRoot 'src\Hackermes.ToolHost\Hackermes.ToolHost.csproj'
$packagingRoot = Join-Path $projectRoot 'packaging'
if ([string]::IsNullOrWhiteSpace($BuildRoot)) {
    $BuildRoot = $buildEnvironment.DefaultBuildRoot
}
$resolvedBuildRoot = [IO.Path]::GetFullPath($BuildRoot)
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $buildEnvironment.Root 'artifacts\release'
}
$resolvedOutputRoot = [IO.Path]::GetFullPath($OutputRoot)
if ([IO.Path]::GetPathRoot($resolvedBuildRoot) -ne 'G:\' -or
    [IO.Path]::GetPathRoot($resolvedOutputRoot) -ne 'G:\') {
    throw "BuildRoot and OutputRoot must stay on G: $resolvedBuildRoot ; $resolvedOutputRoot"
}
$versionRoot = [IO.Path]::GetFullPath((Join-Path $resolvedOutputRoot $Version))
if (-not $versionRoot.StartsWith($resolvedOutputRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Release directory escapes the configured output root: $versionRoot"
}
if (Test-Path -LiteralPath $versionRoot) {
    Remove-Item -LiteralPath $versionRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $versionRoot -Force | Out-Null

function Invoke-Publish {
    param([string]$Project, [string]$Rid, [string]$Destination, [switch]$SingleFile)
    $arguments = @(
        'publish', $Project, '-c', 'Release', '-r', $Rid,
        '--self-contained', 'true', '-o', $Destination,
        '-p:TargetExt=.dll', "-p:Version=$Version",
        '-p:DebugType=None', '-p:DebugSymbols=false',
        '-p:PublishTrimmed=false', '-p:NuGetAudit=false',
        '--disable-build-servers', '-m:1'
        "-p:HackermesBuildRoot=$resolvedBuildRoot"
    )
    if ($SingleFile) {
        $arguments += '-p:PublishSingleFile=true'
        $arguments += '-p:IncludeNativeLibrariesForSelfExtract=true'
    }
    else {
        $arguments += '-p:PublishSingleFile=false'
    }
    & dotnet @arguments | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $Rid ($Project)" }
}

function Copy-PackageFiles {
    param([string]$PackageRoot, [string]$Rid)
    $readme = (Get-Content -LiteralPath (Join-Path $packagingRoot 'PACKAGE-README.md') -Raw -Encoding UTF8).
        Replace('@VERSION@', $Version).Replace('@RID@', $Rid)
    [IO.File]::WriteAllText((Join-Path $PackageRoot 'README.md'), $readme, $utf8)
    Copy-Item -LiteralPath (Join-Path $packagingRoot 'THIRD-PARTY-NOTICES.md') -Destination $PackageRoot
    $assetDirectory = Join-Path $PackageRoot 'app\Assets'
    New-Item -ItemType Directory -Path $assetDirectory -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $projectRoot 'src\Hackermes.App\Assets\hackermes-icon.png') -Destination $assetDirectory
}

function Copy-ToolDirectory {
    param([string]$ToolsSource, [string]$ToolsDestination, [string]$Name)
    $source = Join-Path $ToolsSource $Name
    if (Test-Path -LiteralPath $source -PathType Container) {
        Copy-Item -LiteralPath $source -Destination (Join-Path $ToolsDestination $Name) -Recurse -Force
    }
}

function Stage-Tools {
    param([string]$AppDirectory, [bool]$IncludeWindowsRuntime)
    $source = Join-Path $projectRoot 'third_party\tools'
    if (-not (Test-Path -LiteralPath (Join-Path $source 'manifest.json') -PathType Leaf)) { return }
    $destination = Join-Path $AppDirectory 'tools'
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $source 'manifest.json') -Destination $destination
    if ($IncludeWindowsRuntime) {
        Copy-ToolDirectory $source $destination '_runtime'
        Copy-ToolDirectory $source $destination 'recon.nmap.terminal'
    }
    foreach ($name in @('recon.dirsearch.terminal', 'detect.wafw00f.terminal', 'exploit.xss-fuzzer.terminal', 'exploit.sqlmap.terminal')) {
        Copy-ToolDirectory $source $destination $name
    }
    # Installer binaries copied into the original xssFuzz folder are not used
    # by its Python entry point and are intentionally excluded from releases.
    $unusedXssWindows = Join-Path $destination 'exploit.xss-fuzzer.terminal\windows'
    if (Test-Path -LiteralPath $unusedXssWindows) {
        Remove-Item -LiteralPath $unusedXssWindows -Recurse -Force
    }
}

function Build-Package {
    param([string]$Rid, [string]$PackageName, [string]$ToolHostName)
    $packageRoot = Join-Path $versionRoot $PackageName
    $appDirectory = Join-Path $packageRoot 'app'
    $toolHostDirectory = Join-Path $versionRoot ('.toolhost-' + $Rid)
    New-Item -ItemType Directory -Path $appDirectory -Force | Out-Null
    New-Item -ItemType Directory -Path $toolHostDirectory -Force | Out-Null

    Invoke-Publish $appProject $Rid $appDirectory
    Invoke-Publish $toolHostProject $Rid $toolHostDirectory -SingleFile
    foreach ($partial in Get-ChildItem -LiteralPath $appDirectory -Filter 'Hackermes.ToolHost*' -File -ErrorAction SilentlyContinue) {
        Remove-Item -LiteralPath $partial.FullName -Force
    }
    $toolHost = Join-Path $toolHostDirectory $ToolHostName
    if (-not (Test-Path -LiteralPath $toolHost -PathType Leaf)) {
        throw "Published ToolHost executable was not found: $toolHost"
    }
    Copy-Item -LiteralPath $toolHost -Destination (Join-Path $appDirectory $ToolHostName)
    Stage-Tools $appDirectory ($Rid -eq 'win-x64')
    Copy-PackageFiles $packageRoot $Rid
    return $packageRoot
}

function Write-ReleaseManifest {
    param([string]$PackageRoot, [string]$Rid)
    $appRoot = Join-Path $PackageRoot 'app'
    $files = @(Get-ChildItem -LiteralPath $appRoot -File -Recurse | Sort-Object FullName | ForEach-Object {
        [ordered]@{
            path = $_.FullName.Substring($appRoot.Length + 1).Replace('\', '/')
            size = $_.Length
            sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    })
    $manifest = [ordered]@{ schemaVersion = 1; version = $Version; rid = $Rid; files = $files }
    $manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $PackageRoot 'release-manifest.json') -Encoding UTF8
}

Write-Host "Publishing Hackermes $Version..."
$windowsName = "Hackermes-$Version-windows-x64"
$linuxName = "Hackermes-$Version-linux-x64"
$windowsRoot = $null
$linuxRoot = $null
if ($Platforms -in @('all', 'windows')) {
    $windowsRoot = Build-Package 'win-x64' $windowsName 'Hackermes.ToolHost.exe'
    Write-ReleaseManifest $windowsRoot 'win-x64'
    Copy-Item -LiteralPath (Join-Path $packagingRoot 'windows\Install-Hackermes.ps1') -Destination $windowsRoot
    Copy-Item -LiteralPath (Join-Path $packagingRoot 'windows\Uninstall-Hackermes.ps1') -Destination $windowsRoot
}
if ($Platforms -in @('all', 'linux')) {
    $linuxRoot = Build-Package 'linux-x64' $linuxName 'Hackermes.ToolHost'
    Write-ReleaseManifest $linuxRoot 'linux-x64'
    Copy-Item -LiteralPath (Join-Path $packagingRoot 'linux\install.sh') -Destination $linuxRoot
    Copy-Item -LiteralPath (Join-Path $packagingRoot 'linux\uninstall.sh') -Destination $linuxRoot
}

$python = Get-Command python -ErrorAction SilentlyContinue
if ($null -eq $python) { throw 'Python 3 is required to create deterministic release archives.' }
$archiveArguments = @((Join-Path $projectRoot 'scripts\create-release-archives.py'), '--output', $versionRoot, '--version', $Version)
if ($null -ne $windowsRoot) { $archiveArguments += @('--windows', $windowsRoot) }
if ($null -ne $linuxRoot) { $archiveArguments += @('--linux', $linuxRoot) }
& $python.Source @archiveArguments
if ($LASTEXITCODE -ne 0) { throw 'Creating release archives failed.' }

foreach ($stagingDirectory in Get-ChildItem -LiteralPath $versionRoot -Directory -Force -Filter '.toolhost-*') {
    $resolvedStaging = [IO.Path]::GetFullPath($stagingDirectory.FullName)
    if (-not $resolvedStaging.StartsWith(
        $versionRoot + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove an unexpected ToolHost staging directory: $resolvedStaging"
    }
    Remove-Item -LiteralPath $resolvedStaging -Recurse -Force
}

Get-ChildItem -LiteralPath $versionRoot -File | Select-Object Name, Length, LastWriteTime
Write-Host "Release packages are ready: $versionRoot"
