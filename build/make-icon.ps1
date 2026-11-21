<#
.SYNOPSIS
  Regenerates src\Carbonado.App\Assets\Carbonado.ico from the brand DiamondMark (16-256 px).
.PARAMETER Preview
  Optional folder that also receives one PNG per size, for review.
#>
[CmdletBinding()]
param([string]$Preview)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $PSScriptRoot 'icongen\IconGen.csproj'
$icon = Join-Path $root 'src\Carbonado.App\Assets\Carbonado.ico'
$runArgs = @($icon)
if ($Preview) { $runArgs += $Preview }
dotnet run --project $project -c Release -- @runArgs
if ($LASTEXITCODE -ne 0) { throw 'Icon generation failed.' }
