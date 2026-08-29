param([Parameter(Mandatory = $true)][string]$SdrSharpDir)

$ErrorActionPreference = 'Stop'
$package = [IO.Path]::GetFullPath($PSScriptRoot)
$sdr = [IO.Path]::GetFullPath($SdrSharpDir)
$executables = @('SDRSharp.dotnet9.exe', 'SDRSharp.dotnet8.exe', 'SDRSharp.exe')
$exe = $executables | ForEach-Object { Join-Path $sdr $_ } | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $exe) {
    throw "No se encontro SDRSharp.dotnet9.exe, SDRSharp.dotnet8.exe ni SDRSharp.exe en $sdr"
}

$stream = [IO.File]::OpenRead($exe)
try {
    $reader = [IO.BinaryReader]::new($stream)
    $stream.Position = 0x3c
    $pe = $reader.ReadInt32()
    $stream.Position = $pe + 4
    $machine = $reader.ReadUInt16()
} finally { $stream.Dispose() }
if ($machine -eq 0x014c) {
    throw 'Esta compilacion usa libnrsc5 Win64 y requiere SDR# x64. Instale el paquete oficial SDR# x64 en una carpeta separada.'
}
if ($machine -ne 0x8664) {
    throw ('Arquitectura de SDR# no compatible: 0x{0:X4}' -f $machine)
}

if (-not (Test-Path -LiteralPath (Join-Path $package 'SDRSharp.NRSC5.dll')) -or
    -not (Test-Path -LiteralPath (Join-Path $package 'NRSC5Runtime\libnrsc5.dll'))) {
    throw 'El paquete no contiene el plugin o el runtime NRSC-5.'
}

$target = Join-Path $sdr 'Plugins\SDRSharp-NRSC5-Plugin'
$runtimeTarget = Join-Path $sdr 'NRSC5Runtime'
$runtimeSource = Join-Path $package 'NRSC5Runtime'
New-Item -ItemType Directory -Force -Path $target, $runtimeTarget | Out-Null

# SDR# examina recursivamente Plugins. Mantener las DLL nativas fuera de ese
# arbol evita que intente abrirlas como ensamblados administrados.
Get-ChildItem -LiteralPath $package -File | Copy-Item -Destination $target -Force
Get-ChildItem -LiteralPath $runtimeSource -Filter '*.dll' -File | Copy-Item -Destination $runtimeTarget -Force

$legacyRuntime = Join-Path $target 'NRSC5Runtime'
if (Test-Path -LiteralPath $legacyRuntime) {
    Remove-Item -LiteralPath $legacyRuntime -Recurse -Force
}

Write-Host "[OK] Plugin x64 instalado en $target" -ForegroundColor Green
Write-Host "[OK] Runtime NRSC5 instalado fuera de Plugins en $runtimeTarget" -ForegroundColor Green
Write-Host 'Inicie SDRSharp.dotnet9.exe y abra Digital Radio > NRSC-5 HD Radio.' -ForegroundColor Cyan
