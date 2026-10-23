param([Parameter(Mandatory = $true)][string]$ParamFile)
$ErrorActionPreference = "Stop"
$p = Get-Content -Raw -LiteralPath $ParamFile | ConvertFrom-Json
$t = ([string]$p.thumbprint).ToUpper()
$prm = $p.parameters
$result = [ordered]@{ ok = $false; previousThumbprint = $null; message = "" }
try {
    Import-Module RemoteDesktopServices
    $item = "RDS:\GatewayServer\SSLCertificate\Thumbprint"
    $prev = (Get-Item -Path $item).CurrentValue
    if ($prev) { $result.previousThumbprint = [string]$prev }
    Set-Item -Path $item -Value $t
    Restart-Service TSGateway -Force
    $result.message = "RD Gateway certificate set to $t; TSGateway restarted."
    $result.ok = $true
} catch {
    $result.message = $_.Exception.Message
}
Write-Output ("##AUTOMATECERT##" + ($result | ConvertTo-Json -Compress))
