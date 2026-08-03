@echo off
setlocal
if "%~1"=="" (
  echo Indique la carpeta donde esta SDRSharp.exe:
  set /p "SDRSHARP_DIR=Carpeta de SDRSharp: "
) else (
  set "SDRSHARP_DIR=%~1"
)
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-Package.ps1" -SdrSharpDir "%SDRSHARP_DIR%"
set "SDR_NRSC5_EXIT=%ERRORLEVEL%"
pause
exit /b %SDR_NRSC5_EXIT%
