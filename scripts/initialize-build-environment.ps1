[CmdletBinding()]
param(
    [string]$Root = 'G:\HackermesBuild',
    [switch]$PersistUserEnvironment
)

$resolvedRoot = [IO.Path]::GetFullPath($Root)
if (-not [IO.Path]::IsPathRooted($Root) -or $resolvedRoot -eq [IO.Path]::GetPathRoot($resolvedRoot)) {
    throw "Build environment root must be an absolute non-root directory: $resolvedRoot"
}
if (-not $resolvedRoot.StartsWith('G:\', [StringComparison]::OrdinalIgnoreCase)) {
    throw "Hackermes build environment must stay on G: $resolvedRoot"
}

$sharedRoot = Join-Path $resolvedRoot 'shared'
$directories = [ordered]@{
    Build = Join-Path $resolvedRoot 'workspace'
    NuGetPackages = Join-Path $sharedRoot 'nuget-packages'
    NuGetHttpCache = Join-Path $sharedRoot 'nuget-http-cache'
    NuGetPluginsCache = Join-Path $sharedRoot 'nuget-plugins-cache'
    DotnetCliHome = Join-Path $sharedRoot 'dotnet-cli-home'
    Temp = Join-Path $sharedRoot 'temp'
    NpmPrefix = Join-Path $sharedRoot 'npm-prefix'
    PythonCache = Join-Path $sharedRoot 'python-cache'
    XdgCache = Join-Path $sharedRoot 'xdg-cache'
}

foreach ($directory in $directories.Values) {
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
}

# These variables are process-scoped. They affect this script and every child
# dotnet/npm/python process without changing the user's global environment.
$env:NUGET_PACKAGES = $directories.NuGetPackages
$env:NUGET_HTTP_CACHE_PATH = $directories.NuGetHttpCache
$env:NUGET_PLUGINS_CACHE_PATH = $directories.NuGetPluginsCache
$env:DOTNET_CLI_HOME = $directories.DotnetCliHome
$env:TEMP = $directories.Temp
$env:TMP = $directories.Temp
$env:TMPDIR = $directories.Temp
$env:npm_config_cache = $null
$env:npm_config_prefix = $directories.NpmPrefix
$env:PYTHONPYCACHEPREFIX = $directories.PythonCache
$env:XDG_CACHE_HOME = $directories.XdgCache
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

if ($PersistUserEnvironment) {
    $persistentEnvironment = [ordered]@{
        HACKERMES_BUILD_ROOT = $directories.Build
        NUGET_PACKAGES = $directories.NuGetPackages
        NUGET_HTTP_CACHE_PATH = $directories.NuGetHttpCache
        NUGET_PLUGINS_CACHE_PATH = $directories.NuGetPluginsCache
        DOTNET_CLI_HOME = $directories.DotnetCliHome
        npm_config_prefix = $directories.NpmPrefix
        DOTNET_CLI_TELEMETRY_OPTOUT = '1'
        DOTNET_NOLOGO = '1'
    }

    foreach ($entry in $persistentEnvironment.GetEnumerator()) {
        [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, 'User')
    }

    # Older versions of this script forced every npm process to share one cache.
    # Remove that persisted override so npm can use its normal per-user/default
    # cache and avoid cross-project lock contention.
    [Environment]::SetEnvironmentVariable('npm_config_cache', $null, 'User')

    # Older versions persisted process-temporary paths for the whole Windows
    # user, causing unrelated applications to write into the Hackermes build
    # tree. Keep these isolated only for this script and its child processes.
    $legacyProcessOnlyEnvironment = [ordered]@{
        TEMP = $directories.Temp
        TMP = $directories.Temp
        TMPDIR = $directories.Temp
        PYTHONPYCACHEPREFIX = $directories.PythonCache
        XDG_CACHE_HOME = $directories.XdgCache
    }
    foreach ($entry in $legacyProcessOnlyEnvironment.GetEnumerator()) {
        if ([Environment]::GetEnvironmentVariable($entry.Key, 'User') -eq $entry.Value) {
            [Environment]::SetEnvironmentVariable($entry.Key, $null, 'User')
        }
    }
}

[pscustomobject]@{
    Root = $resolvedRoot
    DefaultBuildRoot = $directories.Build
    Directories = $directories
    UserEnvironmentPersisted = [bool]$PersistUserEnvironment
}
