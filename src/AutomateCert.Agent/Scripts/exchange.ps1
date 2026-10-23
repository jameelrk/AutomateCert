param([Parameter(Mandatory = $true)][string]$ParamFile)
$ErrorActionPreference = "Stop"
$p = Get-Content -Raw -LiteralPath $ParamFile | ConvertFrom-Json
$t = ([string]$p.thumbprint).ToUpper()
$prm = $p.parameters
$result = [ordered]@{ ok = $false; previousThumbprint = $null; message = "" }
try {
    Add-PSSnapin Microsoft.Exchange.Management.PowerShell.SnapIn -ErrorAction Stop
    $services = if ($prm.services) { [string]$prm.services } else { "IIS,SMTP" }
    $prev = Get-ExchangeCertificate | Where-Object { $_.Services -match "IIS" -and $_.Thumbprint -ne $t } | Sort-Object NotAfter -Descending | Select-Object -First 1
    if ($prev) { $result.previousThumbprint = [string]$prev.Thumbprint }
    Enable-ExchangeCertificate -Thumbprint $t -Services $services -Force -Confirm:$false
    $result.message = "Exchange certificate $t enabled for $services."
    $result.ok = $true
} catch {
    $result.message = $_.Exception.Message
}
Write-Output ("##AUTOMATECERT##" + ($result | ConvertTo-Json -Compress))
