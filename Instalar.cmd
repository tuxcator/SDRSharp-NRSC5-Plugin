@echo off
setlocal
if "%~1"=="" (
  echo Arrastre la carpeta de SDRSharp sobre este archivo o indique su ruta:
  set /p "SDRSHARP_DIR=Carpeta de SDRSharp: "
) else (
  set "SDRSHARP_DIR=%~1"
)
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Install.ps1" -SdrSharpDir "%SDRSHARP_DIR%"
set "SDR_NRSC5_EXIT=%ERRORLEVEL%"
pause
exit /b %SDR_NRSC5_EXIT%
