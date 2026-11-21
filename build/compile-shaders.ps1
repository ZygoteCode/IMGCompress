<#
.SYNOPSIS
  Compiles src/Carbonado.UI/Effects/*.fx to WPF pixel-shader bytecode (*.ps, ps_2_0) with fxc.exe.
.DESCRIPTION
  ps_2_0 is used on purpose: WPF's software renderer (RenderTargetBitmap, remote desktop, tier-0 GPUs)
  only executes ps_2_0 shaders. The compiled .ps files are committed, so this script is only needed
  after editing an .fx file.
#>
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$effects = Join-Path $root 'src/Carbonado.UI/Effects'
$fxc = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Recurse -Filter fxc.exe -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -match '\\x64\\' } | Sort-Object FullName -Descending | Select-Object -First 1
if (-not $fxc) { throw 'fxc.exe not found (install the Windows SDK).' }
foreach ($fx in Get-ChildItem $effects -Filter *.fx) {
    $out = [IO.Path]::ChangeExtension($fx.FullName, '.ps')
    & $fxc.FullName /nologo /T ps_2_0 /E main /O3 /Fo $out $fx.FullName
    if ($LASTEXITCODE -ne 0) { throw "fxc failed for $($fx.Name)" }
    Write-Host "compiled $($fx.Name) -> $([IO.Path]::GetFileName($out))"
}
