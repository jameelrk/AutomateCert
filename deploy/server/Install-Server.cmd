@echo off
rem AutomateCert server installer. Right-click > Run as administrator.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-AutomateCertServer.ps1" %*
set RC=%ERRORLEVEL%
if "%~1"=="" pause
exit /b %RC%
