<#
.SYNOPSIS
  Publishes a portable Carbonado build (ReadyToRun for fast startup) into artifacts\Carbonado.
.DESCRIPTION
  Framework-dependent (.NET 11 Desktop Runtime required), win-x64, ReadyToRun + TieredPGO. Copies the verified
  encoder binaries (tools\), licenses and docs next to Carbonado.exe and carbonado-cli.exe.
.PARAMETER Output
  Destination folder (default: artifacts\Carbonado).
.PARAMETER Portable
  Also writes portable.flag so settings/history live in .\data next to the executable.
#>
[CmdletBinding()]
param(
    [string]$Output,
    [switch]$Portable
)

$ErrorActionPreference = 'Stop'
# $PSScriptRoot is empty inside param() defaults on Windows PowerShell 5.1: resolve paths here.
$root = Split-Path -Parent $PSScriptRoot
if (-not $Output) { $Output = Join-Path $root 'artifacts\Carbonado' }
Push-Location $root
try {
    & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'fetch-tools.ps1') -Verify
    if ($LASTEXITCODE -ne 0) { throw 'Tool verification failed (run build\fetch-tools.ps1).' }

    if (Test-Path $Output) { Remove-Item -Recurse -Force $Output }
    $common = @('-c', 'Release', '-r', 'win-x64', '--self-contained', 'false', '-p:PublishReadyToRun=true', '-p:DebugType=none', '-nologo', '-v', 'q', '-o', $Output)
    dotnet publish src/Carbonado.App/Carbonado.App.csproj @common
    if ($LASTEXITCODE -ne 0) { throw 'App publish failed.' }
    dotnet publish src/Carbonado.Cli/Carbonado.Cli.csproj @common
    if ($LASTEXITCODE -ne 0) { throw 'CLI publish failed.' }

    $manifest = Get-Content tools/manifest.json -Raw | ConvertFrom-Json
    foreach ($pkg in $manifest.packages) {
        foreach ($f in $pkg.files.PSObject.Properties) {
            $dest = Join-Path $Output (Join-Path 'tools' $f.Name)
            New-Item -ItemType Directory -Force (Split-Path $dest) | Out-Null
            Copy-Item (Join-Path 'tools' $f.Name) $dest
        }
    }
    Copy-Item tools/manifest.json (Join-Path $Output 'tools/manifest.json')
    Copy-Item -Recurse tools/libjxl/licenses (Join-Path $Output 'tools/libjxl/licenses')
    Copy-Item tools/oxipng/LICENSE.txt (Join-Path $Output 'tools/oxipng/LICENSE.txt')
    Copy-Item LICENSE, THIRD-PARTY-NOTICES.md $Output
    if (Test-Path README.md) { Copy-Item README.md $Output }
    if ($Portable) { New-Item -ItemType File -Force (Join-Path $Output 'portable.flag') | Out-Null }

    $size = (Get-ChildItem $Output -Recurse -File | Measure-Object Length -Sum).Sum / 1MB
    Write-Host ("Published to {0} ({1:N0} MB)" -f $Output, $size)
} finally {
    Pop-Location
}
