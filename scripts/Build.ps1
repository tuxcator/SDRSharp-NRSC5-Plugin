param(
    [string]$Nrsc5Runtime = '',
    [switch]$SkipDependencies
)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $SkipDependencies) {
    & (Join-Path $PSScriptRoot 'Get-Dependencies.ps1') -Nrsc5Runtime $Nrsc5Runtime
}

$dotnet = Join-Path $root '.tools\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) {
    throw 'Falta .tools\dotnet\dotnet.exe. Ejecute scripts\Get-Dependencies.ps1.'
}

$project = Join-Path $root 'src\SDRSharp.NRSC5\SDRSharp.NRSC5.csproj'
$build = Join-Path $root 'src\SDRSharp.NRSC5\bin\Release\net9.0-windows'
$dist = Join-Path $root 'dist\SDRSharp-NRSC5-Plugin'
$runtimeOut = Join-Path $dist 'NRSC5Runtime'

& $dotnet restore $project --ignore-failed-sources
if ($LASTEXITCODE -ne 0) { throw 'dotnet restore fallo.' }
& $dotnet build $project -c Release --no-restore
if ($LASTEXITCODE -ne 0) { throw 'dotnet build fallo.' }

if (Test-Path -LiteralPath $dist) { Remove-Item -LiteralPath $dist -Recurse -Force }
New-Item -ItemType Directory -Force -Path $dist, $runtimeOut | Out-Null
Copy-Item -LiteralPath (Join-Path $build 'SDRSharp.NRSC5.dll') -Destination $dist -Force
if (Test-Path -LiteralPath (Join-Path $build 'SDRSharp.NRSC5.pdb')) {
    Copy-Item -LiteralPath (Join-Path $build 'SDRSharp.NRSC5.pdb') -Destination $dist -Force
}
Get-ChildItem -LiteralPath (Join-Path $root 'vendor\nrsc5\win-x64') -Filter '*.dll' -File |
    Copy-Item -Destination $runtimeOut -Force
Copy-Item -LiteralPath (Join-Path $root 'Plugin.xml.fragment') -Destination $dist -Force
Copy-Item -LiteralPath (Join-Path $root 'README.md') -Destination $dist -Force
Copy-Item -LiteralPath (Join-Path $root 'README.es.md') -Destination $dist -Force
Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination $dist -Force
Copy-Item -LiteralPath (Join-Path $root 'THIRD_PARTY_NOTICES.md') -Destination $dist -Force
Copy-Item -LiteralPath (Join-Path $root 'packaging\Install-Package.ps1') -Destination (Join-Path $dist 'Install-Package.ps1') -Force
Copy-Item -LiteralPath (Join-Path $root 'packaging\Instalar.cmd') -Destination (Join-Path $dist 'Instalar.cmd') -Force

& (Join-Path $root 'tests\Test-Project.ps1') -Distribution $dist
& $dotnet run --project (Join-Path $root 'tests\NativeSmoke\NativeSmoke.csproj') -- $runtimeOut
if ($LASTEXITCODE -ne 0) { throw 'La prueba nativa de libnrsc5 fallo.' }
$zip = Join-Path $root 'dist\SDRSharp-NRSC5-Plugin-v0.1.0-win-x64.zip'
if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
Compress-Archive -Path (Join-Path $dist '*') -DestinationPath $zip -CompressionLevel Optimal
Write-Host "[OK] Paquete creado: $dist" -ForegroundColor Green
