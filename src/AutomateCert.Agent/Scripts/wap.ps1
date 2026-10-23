param([Parameter(Mandatory = $true)][string]$ParamFile)
$ErrorActionPreference = "Stop"
$p = Get-Content -Raw -LiteralPath $ParamFile | ConvertFrom-Json
$t = ([string]$p.thumbprint).ToUpper()
$prm = $p.parameters
$result = [ordered]@{ ok = $false; previousThumbprint = $null; message = "" }
try {
    $prev = $null
    try { $prev = [string](Get-WebApplicationProxySslCertificate | Select-Object -First 1).CertificateHash } catch { }
    if ($prev) { $result.previousThumbprint = $prev }
    Set-WebApplicationProxySslCertificate -Thumbprint $t
    $moved = 0
    if ($prev) {
        Get-WebApplicationProxyApplication | Where-Object { $_.ExternalCertificateThumbprint -eq $prev } | ForEach-Object { Set-WebApplicationProxyApplication -ID $_.ID -ExternalCertificateThumbprint $t; $moved++ }
    }
    $result.message = "WAP certificate set to $t; $moved published application(s) moved from previous certificate."
    $result.ok = $true
} catch {
    $result.message = $_.Exception.Message
}
Write-Output ("##AUTOMATECERT##" + ($result | ConvertTo-Json -Compress))
