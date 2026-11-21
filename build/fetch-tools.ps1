<#
.SYNOPSIS
  Downloads Carbonado's encoder binaries from their official release pages into tools/ and verifies SHA-256.

.DESCRIPTION
  Reads tools/manifest.json. For every package with an archive URL: downloads the archive (unless present),
  checks the archive SHA-256, extracts the listed files into tools/<package>/ and checks each file's SHA-256.
  Packages marked "manual" (ffmpeg, pngquant) are only verified.

.PARAMETER Verify
  Only verify files already present; never download.

.EXAMPLE
  pwsh build/fetch-tools.ps1
  pwsh build/fetch-tools.ps1 -Verify
#>
[CmdletBinding()]
param([switch]$Verify)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$tools = Join-Path $root 'tools'
$manifest = Get-Content (Join-Path $tools 'manifest.json') -Raw | ConvertFrom-Json
$cache = Join-Path ([IO.Path]::GetTempPath()) 'carbonado-tool-cache'
New-Item -ItemType Directory -Force $cache | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Get-Sha256([string]$path) { (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash.ToLowerInvariant() }

$failures = 0
foreach ($pkg in $manifest.packages) {
    $files = $pkg.files.PSObject.Properties
    $missing = @($files | Where-Object { -not (Test-Path (Join-Path $tools $_.Name)) })
    if ($missing.Count -gt 0 -and -not $Verify -and $pkg.archiveSha256) {
        $archive = Join-Path $cache ([IO.Path]::GetFileName($pkg.source))
        if (-not (Test-Path $archive) -or (Get-Sha256 $archive) -ne $pkg.archiveSha256) {
            Write-Host "Downloading $($pkg.name) $($pkg.version) ..."
            Invoke-WebRequest -Uri $pkg.source -OutFile $archive -UseBasicParsing
        }
        $actual = Get-Sha256 $archive
        if ($actual -ne $pkg.archiveSha256) { throw "Archive hash mismatch for $($pkg.name): $actual" }
        $zip = [IO.Compression.ZipFile]::OpenRead($archive)
        try {
            foreach ($f in $files) {
                $entryName = $pkg.archiveRoot + [IO.Path]::GetFileName($f.Name)
                $entry = $zip.Entries | Where-Object { $_.FullName -eq $entryName } | Select-Object -First 1
                if (-not $entry) { throw "Entry $entryName not found in $($pkg.source)" }
                $dest = Join-Path $tools $f.Name
                New-Item -ItemType Directory -Force (Split-Path $dest) | Out-Null
                [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $dest, $true)
            }
        } finally { $zip.Dispose() }
    }
    foreach ($f in $files) {
        $path = Join-Path $tools $f.Name
        if (-not (Test-Path $path)) {
            Write-Warning "missing  $($f.Name)  ($($pkg.source))"
            $failures++
            continue
        }
        $hash = Get-Sha256 $path
        if ($hash -eq $f.Value) { Write-Host "verified $($f.Name)" }
        else { Write-Warning "MISMATCH $($f.Name)  expected $($f.Value) got $hash"; $failures++ }
    }
}

if ($failures -gt 0) { Write-Warning "$failures tool file(s) missing or modified."; exit 1 }
Write-Host 'all tools verified'
