<#
.SYNOPSIS
    Publishes a new StandMate version as a GitHub release that installed copies can update to.
.DESCRIPTION
    1. Builds dist\StandMate-Setup.exe (build.ps1), unless -SkipBuild
    2. Writes dist\latest.json (version, download link, SHA-256, size, notes)
    3. Signs it with the release key -> dist\latest.json.sig
    4. Creates the GitHub release vX.Y.Z with those three files (needs the GitHub CLI, signed in)

    Before releasing: bump <Version> in src\StandMate\StandMate.csproj.
      - Patch/minor (1.2.0 -> 1.2.1 / 1.3.0): updating keeps everyone's running timer.
      - Major (1.x -> 2.0.0): updating starts the timer fresh. Settings and mates are always kept.

    -Local writes the files for a test server instead (no GitHub), e.g.
      release.ps1 -Local -BaseUrl http://127.0.0.1:8765
      python -m http.server 8765 --directory dist
      StandMate.exe --update-url http://127.0.0.1:8765/latest.json
.EXAMPLE
    powershell -ExecutionPolicy Bypass -File release.ps1 -Notes "Reminders now show in games."
#>
param(
    [string]$Notes = '',
    [switch]$SkipBuild,
    [switch]$Local,
    [string]$BaseUrl = '',
    [string]$KeyPath = (Join-Path $env:USERPROFILE '.standmate\release-key.xml')
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$dist = Join-Path $root 'dist'

$csproj = [xml](Get-Content -Raw (Join-Path $root 'src\StandMate\StandMate.csproj'))
$props = $csproj.Project.PropertyGroup
$version = $props.Version | Where-Object { $_ } | Select-Object -First 1
$repo = $props.UpdateRepo | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw 'No <Version> in the csproj.' }

if (-not $Local) {
    if (-not $repo -or $repo -eq 'OWNER/stand-mate') {
        throw 'Set <UpdateRepo> in src\StandMate\StandMate.csproj to your GitHub "owner/repo" first.'
    }
    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
        throw 'The GitHub CLI is needed: winget install GitHub.cli, then: gh auth login'
    }
    & gh release view "v$version" --repo $repo *> $null
    if ($LASTEXITCODE -eq 0) { throw "Release v$version already exists. Bump <Version> in the csproj first." }
    $BaseUrl = "https://github.com/$repo/releases/download/v$version"
} elseif (-not $BaseUrl) {
    throw '-Local needs -BaseUrl (e.g. http://127.0.0.1:8765).'
}
if (-not (Test-Path $KeyPath)) { throw "Release key not found at $KeyPath (see tools\new-release-key.ps1)." }

if (-not $SkipBuild) {
    & (Join-Path $root 'build.ps1')
    if ($LASTEXITCODE -and $LASTEXITCODE -ne 0) { throw 'Build failed.' }
}

$setup = Join-Path $dist 'StandMate-Setup.exe'
if (-not (Test-Path $setup)) { throw "Missing $setup" }
$size = (Get-Item $setup).Length
$sha = (Get-FileHash -Algorithm SHA256 $setup).Hash.ToLowerInvariant()

$manifest = [ordered]@{
    version = $version
    url     = "$($BaseUrl.TrimEnd('/'))/StandMate-Setup.exe"
    sha256  = $sha
    size    = $size
    notes   = $Notes
}
$json = $manifest | ConvertTo-Json
$bytes = (New-Object System.Text.UTF8Encoding $false).GetBytes($json)
[IO.File]::WriteAllBytes((Join-Path $dist 'latest.json'), $bytes)

# Sign the exact bytes the app will download.
$rsa = New-Object System.Security.Cryptography.RSACryptoServiceProvider
$rsa.PersistKeyInCsp = $false
$rsa.FromXmlString((Get-Content -Raw $KeyPath))
$sig = $rsa.SignData($bytes, [System.Security.Cryptography.HashAlgorithmName]::SHA256,
    [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
[IO.File]::WriteAllText((Join-Path $dist 'latest.json.sig'), [Convert]::ToBase64String($sig))
Write-Host "Signed dist\latest.json for $version ($([math]::Round($size / 1MB, 1)) MB, sha256 $sha)"

if ($Local) { Write-Host "Local release files are in $dist"; return }

$notesFile = Join-Path $env:TEMP 'standmate-release-notes.txt'
[IO.File]::WriteAllText($notesFile, $(if ($Notes) { $Notes } else { "StandMate $version" }))
& gh release create "v$version" $setup (Join-Path $dist 'latest.json') (Join-Path $dist 'latest.json.sig') `
    --repo $repo --title "StandMate $version" --notes-file $notesFile
if ($LASTEXITCODE -ne 0) { throw 'gh release create failed.' }
Write-Host "Published https://github.com/$repo/releases/tag/v$version"
