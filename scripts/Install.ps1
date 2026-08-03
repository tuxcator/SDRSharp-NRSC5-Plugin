param(
    [Parameter(Mandatory = $true)]
    [string]$SdrSharpDir,
    [switch]$Build
)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$dist = Join-Path $root 'dist\SDRSharp-NRSC5-Plugin'
if ($Build -or -not (Test-Path -LiteralPath (Join-Path $dist 'SDRSharp.NRSC5.dll'))) {
    & (Join-Path $PSScriptRoot 'Build.ps1')
    if ($LASTEXITCODE -ne 0) { throw 'No se pudo compilar el paquete.' }
}
& (Join-Path $dist 'Install-Package.ps1') -SdrSharpDir $SdrSharpDir
