[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
# dotnet writes localized output as UTF-8. Align the PowerShell host and child
# process output so compiler diagnostics do not become mojibake on Windows.
$utf8 = [System.Text.UTF8Encoding]::new($false)
[Console]::InputEncoding = $utf8
[Console]::OutputEncoding = $utf8
$OutputEncoding = $utf8
chcp 65001 | Out-Null

$projectRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $projectRoot 'src\Hackermes.App\Hackermes.App.csproj'
$buildRoot = Join-Path $env:LOCALAPPDATA 'Hackermes\Build'
$executable = Join-Path $buildRoot 'bin\Hackermes.App\Debug\net10.0\Hackermes.App.exe'

$existing = Get-Process -Name 'Hackermes.App' -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "Hackermes is already running (PID $($existing[0].Id)). Close it before rebuilding."
    exit 0
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

function Build-HackermesProject {
    param(
        [Parameter(Mandatory)] [string]$Name
    )

    $projectPath = Join-Path $projectRoot "src\$Name\$Name.csproj"
    $extension = if ($Name -eq 'Hackermes.App') { '.dll' } else { '.bin' }
    $assemblyPath = Join-Path $buildRoot "bin\$Name\Debug\net10.0\$Name$extension"
    $maximumAttempts = if ($Name -eq 'Hackermes.App') { 20 } else { 8 }

    for ($attempt = 1; $attempt -le $maximumAttempts; $attempt++) {
        $buildArguments = @(
            'build', $projectPath,
            '--no-restore',
            '--no-dependencies',
            '--disable-build-servers',
            '-m:1',
            '-p:UseSharedCompilation=false'
        )
        if ($Name -eq 'Hackermes.App') {
            # An interrupted Avalonia compile can leave an invalid incremental
            # result that contains resources but no precompiled App XAML.
            $buildArguments += '--no-incremental'
        }

        $buildOutput = & dotnet @buildArguments 2>&1
        $buildExitCode = $LASTEXITCODE
        $buildOutput | Out-Host

        if ($buildExitCode -eq 0) {
            Wait-HackermesAssembly -Path $assemblyPath
            return
        }

        $failureText = [string]::Join([Environment]::NewLine, [string[]]$buildOutput)
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

# Release stale MSBuild/Roslyn workers before Avalonia regenerates compiled resources.
dotnet build-server shutdown | Out-Host
dotnet restore $project --disable-parallel | Out-Host

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
    'Hackermes.App'
)

foreach ($name in $buildOrder) {
    Build-HackermesProject -Name $name
}

# The compiler-facing project assemblies use .bin so 360rp.exe does not seize
# them between project builds.  CoreCLR requires managed runtime assets to use
# .dll/.exe extensions, so stage DLL-named copies only after compilation is
# complete and update the generated dependency manifest accordingly.
$runtimeDirectory = Split-Path -Parent $executable
$runtimeLibraries = $buildOrder | Where-Object { $_ -ne 'Hackermes.App' }
foreach ($name in $runtimeLibraries) {
    $source = Join-Path $runtimeDirectory "$name.bin"
    $destination = Join-Path $runtimeDirectory "$name.dll"
    $copied = $false

    for ($attempt = 1; $attempt -le 20; $attempt++) {
        try {
            Copy-Item -LiteralPath $source -Destination $destination -Force
            $copied = $true
            break
        }
        catch [System.IO.IOException] {
            if ($attempt -lt 20) { Start-Sleep -Seconds 2 }
        }
    }

    if (-not $copied) { throw "Could not stage runtime assembly: $destination" }
}

$depsPath = Join-Path $runtimeDirectory 'Hackermes.App.deps.json'
$deps = Get-Content -LiteralPath $depsPath -Raw
foreach ($name in $runtimeLibraries) {
    $deps = $deps.Replace("$name.bin", "$name.dll")
}
[System.IO.File]::WriteAllText($depsPath, $deps, [System.Text.UTF8Encoding]::new($false))

if (-not (Test-Path -LiteralPath $executable)) {
    throw "Build completed but executable was not found: $executable"
}

Write-Host 'Waiting for 360 security scanning to release runtime files...'
Start-Sleep -Seconds 45
foreach ($name in $runtimeLibraries) {
    Wait-HackermesAssembly -Path (Join-Path $runtimeDirectory "$name.dll") -TimeoutSeconds 60
}
Wait-HackermesAssembly -Path (Join-Path $runtimeDirectory 'Hackermes.App.dll') -TimeoutSeconds 60
Wait-HackermesAssembly -Path $executable -TimeoutSeconds 60
Start-Process -FilePath $executable -WorkingDirectory (Split-Path -Parent $executable) | Out-Null
Write-Host 'Hackermes started.'
