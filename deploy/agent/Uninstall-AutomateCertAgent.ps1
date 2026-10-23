[CmdletBinding()]
param([switch]$RemoveData)
$ErrorActionPreference = "Continue"
if (Get-Service AutomateCertAgent -ErrorAction SilentlyContinue) { Stop-Service AutomateCertAgent -Force -ErrorAction SilentlyContinue; & sc.exe delete AutomateCertAgent | Out-Null }
Remove-Item "$env:ProgramFiles\AutomateCert\Agent" -Recurse -Force -ErrorAction SilentlyContinue
if ($RemoveData) { Remove-Item "$env:ProgramData\AutomateCert\Agent" -Recurse -Force -ErrorAction SilentlyContinue }
Write-Host "AutomateCert agent removed. Installed certificates were NOT removed. Revoke the server in the console." -ForegroundColor Green
