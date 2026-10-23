@echo off
rem Runs the installer without changing the machine's PowerShell execution policy.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-AutomateCertAgent.ps1" %*
exit /b %ERRORLEVEL%
