param(
    [string]$FmDxRoot = '',
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ([string]::IsNullOrWhiteSpace($FmDxRoot)) {
    $FmDxRoot = Join-Path (Split-Path $root -Parent) 'FM-DX-Windows-Portable'
}
$msysRoot = Join-Path $FmDxRoot '.cache\msys2\msys64'
$bash = Join-Path $msysRoot 'usr\bin\bash.exe'
$source = Join-Path $FmDxRoot 'third_party\nrsc5'
$build = Join-Path $root '.cache\nrsc5-win32-build'
$vendor = Join-Path $root 'vendor\nrsc5\win-x86'
$required = Join-Path $vendor 'libnrsc5.dll'

if ((Test-Path -LiteralPath $required) -and -not $Force) {
    Write-Host "[OK] Runtime nrsc5 Win32 ya preparado: $vendor" -ForegroundColor Green
    exit 0
}
if (-not (Test-Path -LiteralPath $bash)) { throw "No se encontro MSYS2 en $msysRoot" }
if (-not (Test-Path -LiteralPath (Join-Path $source 'CMakeLists.txt'))) { throw "No se encontro el codigo fuente de nrsc5 en $source" }

$oldMsystem = $env:MSYSTEM
$oldChere = $env:CHERE_INVOKING
$oldSource = $env:SDRSHARP_NRSC5_SOURCE
$oldBuild = $env:SDRSHARP_NRSC5_BUILD
try {
    $env:MSYSTEM = 'MINGW32'
    $env:CHERE_INVOKING = '1'
    $env:SDRSHARP_NRSC5_SOURCE = $source
    $env:SDRSHARP_NRSC5_BUILD = $build

    Write-Host 'Instalando compilador y dependencias MinGW32...' -ForegroundColor Cyan
    & $bash -lc 'pacman -Sy --noconfirm'
    if ($LASTEXITCODE -ne 0) { throw 'pacman -Sy fallo.' }
    $packages = 'autoconf automake git make patch libtool mingw-w64-i686-cmake mingw-w64-i686-gcc'
    & $bash -lc "pacman -S --needed --noconfirm $packages"
    if ($LASTEXITCODE -ne 0) { throw 'No se pudieron instalar las dependencias MinGW32.' }

    if (Test-Path -LiteralPath $build) { Remove-Item -LiteralPath $build -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $build | Out-Null

    Push-Location $root
    try {
        & $bash 'scripts/build-nrsc5-win32-msys2.sh'
    } finally {
        Pop-Location
    }
    if ($LASTEXITCODE -ne 0) { throw 'La compilacion Win32 de nrsc5 fallo.' }
}
finally {
    $env:MSYSTEM = $oldMsystem
    $env:CHERE_INVOKING = $oldChere
    $env:SDRSHARP_NRSC5_SOURCE = $oldSource
    $env:SDRSHARP_NRSC5_BUILD = $oldBuild
}

$mingwBin = Join-Path $msysRoot 'mingw32\bin'
if (-not (Test-Path -LiteralPath (Join-Path $mingwBin 'libnrsc5.dll'))) {
    throw 'La compilacion no genero mingw32\bin\libnrsc5.dll.'
}
if (Test-Path -LiteralPath $vendor) { Remove-Item -LiteralPath $vendor -Recurse -Force }
New-Item -ItemType Directory -Force -Path $vendor | Out-Null
Get-ChildItem -LiteralPath $mingwBin -Filter '*.dll' -File | Copy-Item -Destination $vendor -Force

$lib = Join-Path $vendor 'libnrsc5.dll'
$stream = [IO.File]::OpenRead($lib)
try {
    $reader = [IO.BinaryReader]::new($stream)
    $stream.Position = 0x3c
    $pe = $reader.ReadInt32()
    $stream.Position = $pe + 4
    $machine = $reader.ReadUInt16()
} finally { $stream.Dispose() }
if ($machine -ne 0x014c) { throw ('libnrsc5.dll no es x86: 0x{0:X4}' -f $machine) }

Write-Host "[OK] Runtime nrsc5 Win32 preparado: $vendor" -ForegroundColor Green
