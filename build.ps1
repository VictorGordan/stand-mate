<#
.SYNOPSIS
    Builds StandMate and its installer: dist\StandMate-Setup.exe
.DESCRIPTION
    1. Runs the unit tests
    2. Publishes the app to build\app
    3. Compiles the Inno Setup installer into dist\ (its version comes from the csproj)
    Needs the .NET SDK and Inno Setup 6 (see README).
.EXAMPLE
    powershell -ExecutionPolicy Bypass -File build.ps1
#>
param([switch]$SkipTests)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

function Find-Dotnet {
    # A runtime-only dotnet on PATH can't build, so pick the first one that actually has an SDK.
    $candidates = @()
    $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($cmd) { $candidates += $cmd.Source }
    $candidates += (Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe')
    $candidates += (Join-Path $env:ProgramFiles 'dotnet\dotnet.exe')
    foreach ($c in $candidates) {
        if ((Test-Path $c) -and (& $c --list-sdks 2>$null)) { return $c }
    }
    throw '.NET SDK not found. Install it with: winget install Microsoft.DotNet.SDK.10'
}

function Find-Iscc {
    $candidates = @(
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
    )
    foreach ($p in $candidates) { if ($p -and (Test-Path $p)) { return $p } }
    $cmd = Get-Command iscc -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    throw 'Inno Setup 6 not found. Install it with: winget install JRSoftware.InnoSetup'
}

$dotnet = Find-Dotnet
$iscc = Find-Iscc

if (-not $SkipTests) {
    Write-Host '==> Tests'
    & $dotnet test (Join-Path $root 'tests\StandMate.Tests\StandMate.Tests.csproj') -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
}

Write-Host '==> Publish'
$out = Join-Path $root 'build\app'
if (Test-Path $out) { Remove-Item -Recurse -Force $out }
& $dotnet publish (Join-Path $root 'src\StandMate\StandMate.csproj') -c Release -o $out
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }

Write-Host '==> Installer'
$version = ([xml](Get-Content -Raw (Join-Path $root 'src\StandMate\StandMate.csproj'))).Project.PropertyGroup.Version |
    Where-Object { $_ } | Select-Object -First 1
& $iscc /Q "/DAppVersion=$version" (Join-Path $root 'installer\StandMate.iss')
if ($LASTEXITCODE -ne 0) { throw 'Installer build failed.' }

$setup = Join-Path $root 'dist\StandMate-Setup.exe'
Write-Host ("Done: {0} ({1:N1} MB)" -f $setup, ((Get-Item $setup).Length / 1MB))
