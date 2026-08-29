[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [string]$BuildRoot,
    [switch]$KeepIntermediates
)

$ErrorActionPreference = 'Stop'
$buildEnvironment = & (Join-Path $PSScriptRoot 'initialize-build-environment.ps1')
# dotnet writes localized output as UTF-8. Align the PowerShell host and child
# process output so compiler diagnostics do not become mojibake on Windows.
$utf8 = [System.Text.UTF8Encoding]::new($false)
[Console]::InputEncoding = $utf8
[Console]::OutputEncoding = $utf8
$OutputEncoding = $utf8
chcp 65001 | Out-Null

$projectRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $projectRoot 'src\Hackermes.App\Hackermes.App.csproj'
if ([string]::IsNullOrWhiteSpace($BuildRoot)) {
    $BuildRoot = $buildEnvironment.DefaultBuildRoot
}
$buildRoot = [System.IO.Path]::GetFullPath($BuildRoot)
# 产物必须远离被监控的源码树（G:\Hackmes 里的新 DLL 会被监控软件锁定），
# 且不得落在源码树内部；盘符本身不限（G: 为 exFAT U 盘时会丢已构建文件，推荐本地 SSD）。
$projectRootResolved = [System.IO.Path]::GetFullPath($projectRoot)
if ($buildRoot.StartsWith($projectRootResolved + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "BuildRoot must stay outside the watched source tree: $buildRoot"
}
$executable = Join-Path $buildRoot "bin\Hackermes.App\$Configuration\net10.0\Hackermes.App.exe"
$buildStamp = Join-Path $buildRoot 'source.fingerprint'

$existing = Get-Process -Name 'Hackermes.App' -ErrorAction SilentlyContinue
if ($existing) {
    throw "Hackermes is already running (PID $($existing[0].Id)). Close it before rebuilding."
}

function Wait-HackermesAssembly {
    param(
        [Parameter(Mandatory)] [string]$Path,
        [int]$TimeoutSeconds = 30
    )

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline) {
        try {
            $stream = [System.IO.File]::Open(
                $Path,
                [System.IO.FileMode]::Open,
                [System.IO.FileAccess]::Read,
                [System.IO.FileShare]::ReadWrite)
            $stream.Dispose()
            return
        }
        catch {
            Start-Sleep -Milliseconds 250
        }
    }

    throw "Build output stayed locked for more than $TimeoutSeconds seconds: $Path"
}

function Invoke-HackermesDotnet {
    param(
        [Parameter(Mandatory)] [string[]]$Arguments,
        [Parameter(Mandatory)] [string]$Description,
        [int]$TimeoutSeconds = 120
    )

    # Do not invoke dotnet through the current PowerShell pipeline.  A blocked
    # compiler then blocks the whole script with no way to distinguish a lock
    # from a real compile.  Capturing the child process gives every project a
    # deterministic timeout and preserves its exact diagnostic output.
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = 'dotnet'
    $startInfo.WorkingDirectory = $projectRoot
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    # ArgumentList is unexpectedly null in some Windows PowerShell/.NET host
    # combinations.  Use the long-standing Arguments property instead, with
    # the only required quoting for this script's project paths.
    $quotedArguments = foreach ($argument in $Arguments) {
        if ($argument -match '[\s"]') { '"' + $argument.Replace('"', '\"') + '"' }
        else { $argument }
    }
    $startInfo.Arguments = [string]::Join(' ', [string[]]$quotedArguments)

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    if (-not $process.Start()) { throw "Could not start dotnet for $Description." }
    $standardOutput = $process.StandardOutput.ReadToEndAsync()
    $standardError = $process.StandardError.ReadToEndAsync()

    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
        try { $process.Kill($true) } catch { try { $process.Kill() } catch {} }
        $process.WaitForExit()
        throw "$Description did not finish within $TimeoutSeconds seconds. The build was stopped; retry after the security scanner has released files."
    }

    $stdoutText = $standardOutput.GetAwaiter().GetResult()
    $stderrText = $standardError.GetAwaiter().GetResult()
    $output = (([string]$stdoutText) + ([string]$stderrText)).TrimEnd()
    [pscustomobject]@{ ExitCode = $process.ExitCode; Output = $output }
}

function Get-HackermesSourceFingerprint {
    $inputs = @(
        Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src') -Recurse -File |
            Where-Object {
                $_.FullName -notmatch '[\\/](bin|obj)[\\/]' -and
                $_.Extension -in @('.cs', '.csproj', '.axaml', '.props', '.targets', '.resx')
            }
        Get-Item -LiteralPath (Join-Path $projectRoot 'Directory.Build.props') -ErrorAction SilentlyContinue
        Get-Item -LiteralPath (Join-Path $projectRoot 'Directory.Packages.props') -ErrorAction SilentlyContinue
        Get-Item -LiteralPath (Join-Path $projectRoot 'global.json') -ErrorAction SilentlyContinue
    ) | Where-Object { $_ -ne $null } | Sort-Object FullName

    $description = [System.Text.StringBuilder]::new()
    foreach ($inputFile in $inputs) {
        $relativePath = $inputFile.FullName.Substring($projectRoot.Length).Replace('\', '/')
        [void]$description.Append($relativePath).Append('|').Append($inputFile.Length).Append('|').Append($inputFile.LastWriteTimeUtc.Ticks).AppendLine()
    }

    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($description.ToString())
        return [System.BitConverter]::ToString($sha.ComputeHash($bytes)).Replace('-', '')
    }
    finally { $sha.Dispose() }
}

function Start-HackermesApplication {
    Write-Host 'Starting Hackermes...'
    $process = Start-Process -FilePath $executable -WorkingDirectory (Split-Path -Parent $executable) -PassThru
    if ($process.WaitForExit(3000)) {
        throw "Hackermes exited during startup with code $($process.ExitCode). Check the application log for the startup exception."
    }
    Write-Host "Hackermes started successfully (PID $($process.Id))."
}

function Clear-HackermesBuildIntermediates {
    # Keep only the self-contained application runtime after a successful
    # build. Project-specific bins and MSBuild obj files are compiler
    # intermediates; restore/build will recreate them on the next explicit
    # build. Resolve every candidate under Build before deleting it so a bad
    # environment value cannot expand the cleanup scope.
    $resolvedBuildRoot = [System.IO.Path]::GetFullPath($buildRoot)
    $resolvedRuntimeRoot = [System.IO.Path]::GetFullPath(
        (Join-Path $buildRoot 'bin\Hackermes.App'))
    $candidates = @((Join-Path $buildRoot 'obj'))
    $binRoot = Join-Path $buildRoot 'bin'

    if (Test-Path -LiteralPath $binRoot) {
        $candidates += Get-ChildItem -LiteralPath $binRoot -Directory -Force |
            Where-Object {
                [System.IO.Path]::GetFullPath($_.FullName) -ne $resolvedRuntimeRoot
            } |
            ForEach-Object { $_.FullName }
    }

    foreach ($candidate in $candidates) {
        $resolvedCandidate = [System.IO.Path]::GetFullPath($candidate)
        $insideBuildRoot = $resolvedCandidate.StartsWith(
            $resolvedBuildRoot + [System.IO.Path]::DirectorySeparatorChar,
            [System.StringComparison]::OrdinalIgnoreCase)
        $overlapsRuntime =
            $resolvedCandidate -eq $resolvedRuntimeRoot -or
            $resolvedCandidate.StartsWith(
                $resolvedRuntimeRoot + [System.IO.Path]::DirectorySeparatorChar,
                [System.StringComparison]::OrdinalIgnoreCase)

        if (-not $insideBuildRoot -or $overlapsRuntime) {
            throw "Refusing to clean an unexpected build path: $resolvedCandidate"
        }

        if (Test-Path -LiteralPath $resolvedCandidate) {
            Remove-Item -LiteralPath $resolvedCandidate -Recurse -Force
        }
    }
}

function Limit-HackermesRuntimePlatforms {
    param([Parameter(Mandatory)] [string]$RuntimeDirectory)

    # Development/runtime artifacts intentionally support only 64-bit Windows
    # and the two common 64-bit Linux environments.  NuGet packages may carry
    # native assets for mobile, macOS, WASM and other CPU architectures; leaving
    # all of them beside the app previously added more than 450 MiB.
    $allowed = @('win-x64', 'linux-x64', 'linux-musl-x64')
    $runtimes = Join-Path $RuntimeDirectory 'runtimes'
    if (-not (Test-Path -LiteralPath $runtimes -PathType Container)) { return }

    $resolvedRuntimeDirectory = [System.IO.Path]::GetFullPath($RuntimeDirectory)
    foreach ($directory in Get-ChildItem -LiteralPath $runtimes -Directory -Force) {
        if ($directory.Name -in $allowed) { continue }
        $resolved = [System.IO.Path]::GetFullPath($directory.FullName)
        if (-not $resolved.StartsWith(
            $resolvedRuntimeDirectory + [System.IO.Path]::DirectorySeparatorChar,
            [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to prune an unexpected runtime path: $resolved"
        }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}

function Build-HackermesProject {
    param(
        [Parameter(Mandatory)] [string]$Name
    )

    $projectPath = Join-Path $projectRoot "src\$Name\$Name.csproj"
    $assemblyPath = Join-Path $buildRoot "bin\$Name\$Configuration\net10.0\$Name.dll"
    $maximumAttempts = if ($Name -eq 'Hackermes.App') { 20 } else { 8 }

    for ($attempt = 1; $attempt -le $maximumAttempts; $attempt++) {
        $buildArguments = @(
            'build', $projectPath,
            '--configuration', $Configuration,
            '--no-restore',
            '--no-dependencies',
            '--disable-build-servers',
            '-m:1',
            '-p:UseSharedCompilation=false',
            "-p:HackermesBuildRoot=$buildRoot"
        )
        if ($Name -eq 'Hackermes.App') {
            # An interrupted Avalonia compile can leave an invalid incremental
            # result that contains resources but no precompiled App XAML.
            $buildArguments += '--no-incremental'
        }

        Write-Host "  Running compiler (attempt $attempt/$maximumAttempts; timeout 120 seconds)..."
        $buildResult = Invoke-HackermesDotnet -Arguments $buildArguments -Description "$Name build" -TimeoutSeconds 120
        $buildOutput = $buildResult.Output
        $buildExitCode = $buildResult.ExitCode
        if (-not [string]::IsNullOrWhiteSpace($buildOutput)) { $buildOutput | Out-Host }

        if ($buildExitCode -eq 0) {
            Wait-HackermesAssembly -Path $assemblyPath
            return
        }

        $failureText = [string]$buildOutput
        if ($failureText -notmatch '(?i)being used by another process|process cannot access the file') {
            throw "$Name has a source or configuration error; resolve the compiler diagnostic above."
        }

        if ($attempt -lt $maximumAttempts) {
            Write-Warning "$Name build output is temporarily locked; retrying in 2 seconds ($attempt/$maximumAttempts)..."
            Start-Sleep -Seconds 2
        }
    }

    throw "$Name could not be built because a security or indexing process kept its dependencies locked."
}

Write-Host 'Building Hackermes source...'

# Release stale MSBuild/Roslyn workers before Avalonia regenerates compiled resources.
dotnet build-server shutdown | Out-Host
dotnet restore $project --disable-parallel "-p:HackermesBuildRoot=$buildRoot" | Out-Host
if ($LASTEXITCODE -ne 0) { throw "App restore failed with exit code $LASTEXITCODE." }

# Build one dependency layer at a time.  A security scanner on this machine
# briefly opens every new DLL exclusively; waiting at each boundary prevents
# the next compiler from racing that scanner.
$buildOrder = @(
    'Hackermes.Base',
    'Hackermes.PageAgent',
    'Hackermes.Platform',
    'Hackermes.Dock',
    'Hackermes.Cdp',
    'Hackermes.Traffic',
    'Hackermes.Browser',
    'Hackermes.Inspector',
    'Hackermes.Automation',
    'Hackermes.Terminal',
    'Hackermes.AiPanel',
    'Hackermes.Assessment',
    'Hackermes.ToolHost',
    'Hackermes.App'
)

foreach ($name in $buildOrder) {
    Write-Host "Building $name..."
    Build-HackermesProject -Name $name
}

$runtimeDirectory = Split-Path -Parent $executable
$runtimeLibraries = $buildOrder | Where-Object { $_ -notin @('Hackermes.App', 'Hackermes.ToolHost') }

# Stage redistributable third-party tools beside the application. The catalog
# resolves this application-relative directory before any legacy E:/F: roots.
$bundledToolsSource = Join-Path $projectRoot 'third_party\tools'
$bundledToolsDestination = Join-Path $runtimeDirectory 'tools'
if (Test-Path -LiteralPath $bundledToolsSource -PathType Container) {
    $manifestPath = Join-Path $bundledToolsSource 'manifest.json'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw "Bundled tools manifest is missing: $manifestPath"
    }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $requiredAssets = @($manifest.runtime.path)
    foreach ($runtimeLicense in @($manifest.runtime.licenseFiles) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }) {
        $requiredAssets += $runtimeLicense
    }
    foreach ($bundledTool in $manifest.tools) {
        $requiredAssets += Join-Path $bundledTool.id $bundledTool.entryPoint
        foreach ($licenseFile in @($bundledTool.licenseFiles) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }) {
            $requiredAssets += Join-Path $bundledTool.id $licenseFile
        }
    }
    $resolvedBundledSource = [System.IO.Path]::GetFullPath($bundledToolsSource)
    foreach ($relativeAsset in $requiredAssets) {
        $asset = [System.IO.Path]::GetFullPath((Join-Path $resolvedBundledSource $relativeAsset))
        if (-not $asset.StartsWith(
            $resolvedBundledSource + [System.IO.Path]::DirectorySeparatorChar,
            [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Bundled manifest points outside its source directory: $relativeAsset"
        }
        if (-not (Test-Path -LiteralPath $asset -PathType Leaf)) {
            throw "Bundled manifest asset is missing: $relativeAsset"
        }
    }
    $resolvedRuntimeDirectory = [System.IO.Path]::GetFullPath($runtimeDirectory)
    $resolvedToolsDestination = [System.IO.Path]::GetFullPath($bundledToolsDestination)
    if (-not $resolvedToolsDestination.StartsWith(
        $resolvedRuntimeDirectory + [System.IO.Path]::DirectorySeparatorChar,
        [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to stage bundled tools outside the runtime directory: $resolvedToolsDestination"
    }
    if (Test-Path -LiteralPath $resolvedToolsDestination) {
        Remove-Item -LiteralPath $resolvedToolsDestination -Recurse -Force
    }
    New-Item -ItemType Directory -Path $resolvedToolsDestination -Force | Out-Null
    Copy-Item -Path (Join-Path $bundledToolsSource '*') -Destination $resolvedToolsDestination -Recurse -Force
}

if (-not (Test-Path -LiteralPath $executable)) {
    throw "Build completed but executable was not found: $executable"
}

Limit-HackermesRuntimePlatforms -RuntimeDirectory $runtimeDirectory

Write-Host 'All projects built. Checking whether runtime files are ready...'
foreach ($name in $runtimeLibraries) {
    Wait-HackermesAssembly -Path (Join-Path $runtimeDirectory "$name.dll") -TimeoutSeconds 60
}
Wait-HackermesAssembly -Path (Join-Path $runtimeDirectory 'Hackermes.App.dll') -TimeoutSeconds 60
Wait-HackermesAssembly -Path $executable -TimeoutSeconds 60
[System.IO.Directory]::CreateDirectory($buildRoot) | Out-Null
if (-not $KeepIntermediates) {
    Clear-HackermesBuildIntermediates
}
Write-Host 'Hackermes build completed. Run scripts\run-hackermes.ps1 to start the application.'
