@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Build.ps1" %*
set "SDR_NRSC5_EXIT=%ERRORLEVEL%"
pause
exit /b %SDR_NRSC5_EXIT%
