param(
    [string]$Nrsc5Runtime = '',
    [switch]$SkipDotNet
)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ([string]::IsNullOrWhiteSpace($Nrsc5Runtime)) {
    $Nrsc5Runtime = Join-Path (Split-Path $root -Parent) 'FM-DX-Windows-Portable\runtime\nrsc5'
}
$cache = Join-Path $root '.cache'
$sdkZip = Join-Path $cache 'SDRSharp.PluginSDK.zip'
$sdkExtract = Join-Path $cache 'SDRSharp.PluginSDK'
$sdkVendor = Join-Path $root 'vendor\SDRSharpSDK'
$nrsc5Vendor = Join-Path $root 'vendor\nrsc5\win-x64'
$dotnetDir = Join-Path $root '.tools\dotnet'

New-Item -ItemType Directory -Force -Path $cache, $sdkVendor, (Join-Path $sdkVendor 'lib'), $nrsc5Vendor | Out-Null

if (-not (Test-Path -LiteralPath $sdkZip)) {
    Write-Host 'Descargando SDK oficial de SDR#...' -ForegroundColor Cyan
    Invoke-WebRequest -UseBasicParsing -Uri 'https://airspy.com/?ddownload=5944' -OutFile $sdkZip
}
if (-not (Test-Path -LiteralPath (Join-Path $sdkExtract 'sdrplugins\lib\SDRSharp.Common.dll'))) {
    if (Test-Path -LiteralPath $sdkExtract) { Remove-Item -LiteralPath $sdkExtract -Recurse -Force }
    Expand-Archive -LiteralPath $sdkZip -DestinationPath $sdkExtract -Force
}

$sdkRoot = Join-Path $sdkExtract 'sdrplugins'
foreach ($name in 'SDRSharp.Common.dll','SDRSharp.PanView.dll','SDRSharp.Radio.dll') {
    Copy-Item -LiteralPath (Join-Path $sdkRoot "lib\$name") -Destination (Join-Path $sdkVendor "lib\$name") -Force
}
Copy-Item -LiteralPath (Join-Path $sdkRoot 'LICENSE.txt') -Destination (Join-Path $sdkVendor 'LICENSE.txt') -Force

$nrsc5Dlls = @(
    'libnrsc5.dll',
    'libfftw3f-3.dll',
    'libgcc_s_seh-1.dll',
    'libwinpthread-1.dll',
    'librtlsdr.dll',
    'libusb-1.0.dll'
)
foreach ($name in $nrsc5Dlls) {
    if (-not (Test-Path -LiteralPath (Join-Path $Nrsc5Runtime $name))) {
        throw "No se encontro $name. Indique -Nrsc5Runtime con la carpeta del runtime Win64 completo de nrsc5."
    }
}

# No copie todo el entorno MSYS2: DLL ajenas como libnghttp3 pueden ser
# bloqueadas por Smart App Control aunque nrsc5 nunca las utilice.
Get-ChildItem -LiteralPath $nrsc5Vendor -Filter '*.dll' -File -ErrorAction SilentlyContinue |
    Remove-Item -Force
foreach ($name in $nrsc5Dlls) {
    Copy-Item -LiteralPath (Join-Path $Nrsc5Runtime $name) -Destination $nrsc5Vendor -Force
}
$licenseSource = Join-Path $Nrsc5Runtime 'licenses\nrsc5-GPL-3.0.txt'
if (Test-Path -LiteralPath $licenseSource) {
    New-Item -ItemType Directory -Force -Path (Join-Path $root 'vendor\nrsc5\licenses') | Out-Null
    Copy-Item -LiteralPath $licenseSource -Destination (Join-Path $root 'vendor\nrsc5\licenses') -Force
}

if (-not $SkipDotNet -and -not (Test-Path -LiteralPath (Join-Path $dotnetDir 'dotnet.exe'))) {
    $installer = Join-Path $cache 'dotnet-install.ps1'
    if (-not (Test-Path -LiteralPath $installer)) {
        Write-Host 'Descargando instalador oficial de .NET...' -ForegroundColor Cyan
        Invoke-WebRequest -UseBasicParsing -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installer
    }
    Write-Host 'Instalando SDK .NET 9 local al proyecto...' -ForegroundColor Cyan
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $installer -Channel '9.0' -Quality 'GA' -Architecture 'x64' -InstallDir $dotnetDir
    if ($LASTEXITCODE -ne 0) { throw 'No se pudo instalar el SDK .NET 9 local.' }
}

Write-Host '[OK] Dependencias preparadas.' -ForegroundColor Green
