@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Uninstall-AutomateCertAgent.ps1" %*
exit /b %ERRORLEVEL%
