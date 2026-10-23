<#
  Removes the AutomateCert server service and program files. Configuration is backed up; database, encryption keys
  (unless -RemoveData) and certificates are kept so a reinstall can reuse them.
#>
[CmdletBinding()]
param([switch]$RemoveData)
$ErrorActionPreference = "Continue"
if (Get-Service AutomateCertServer -ErrorAction SilentlyContinue) { Stop-Service AutomateCertServer -Force -ErrorAction SilentlyContinue; & sc.exe delete AutomateCertServer | Out-Null }
Get-NetFirewallRule -DisplayName "AutomateCert Server (HTTPS)" -ErrorAction SilentlyContinue | Remove-NetFirewallRule
$installDir = "$env:ProgramFiles\AutomateCert\Server"
$cfg = Join-Path $installDir "appsettings.Production.json"
if (Test-Path $cfg) {
    $backup = Join-Path "$env:ProgramData\AutomateCert\Server" ("appsettings.Production.{0:yyyyMMddHHmmss}.json" -f (Get-Date))
    Copy-Item $cfg $backup -ErrorAction SilentlyContinue
    Write-Host "Configuration backed up to $backup"
}
Get-ChildItem $installDir -Exclude "Uninstall-AutomateCertServer.ps1", "unins*.*" -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
if ($RemoveData) { Remove-Item "$env:ProgramData\AutomateCert\Server" -Recurse -Force -ErrorAction SilentlyContinue }
Write-Host "AutomateCert server removed. Database and certificates were not touched." -ForegroundColor Green
