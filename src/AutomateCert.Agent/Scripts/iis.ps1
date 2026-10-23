param([Parameter(Mandatory = $true)][string]$ParamFile)
$ErrorActionPreference = "Stop"
$p = Get-Content -Raw -LiteralPath $ParamFile | ConvertFrom-Json
$t = ([string]$p.thumbprint).ToUpper()
$prm = $p.parameters
$result = [ordered]@{ ok = $false; previousThumbprint = $null; message = "" }
try {
    Import-Module WebAdministration
    $site = [string]$prm.siteName
    if (-not (Get-Website -Name $site)) { throw "IIS site '$site' not found." }
    $hostName = if ($prm.hostName) { [string]$prm.hostName } else { "" }
    $port = if ($prm.port) { [int]$prm.port } else { 443 }
    $ip = if ($prm.ipAddress) { [string]$prm.ipAddress } else { "*" }
    $info = "{0}:{1}:{2}" -f $ip, $port, $hostName
    $binding = Get-WebBinding -Name $site -Protocol https | Where-Object { $_.bindingInformation -eq $info }
    if (-not $binding) {
        $sslFlags = if ($hostName) { 1 } else { 0 }
        New-WebBinding -Name $site -Protocol https -Port $port -IPAddress $ip -HostHeader $hostName -SslFlags $sslFlags
        $binding = Get-WebBinding -Name $site -Protocol https | Where-Object { $_.bindingInformation -eq $info }
    } elseif ($binding.certificateHash) { $result.previousThumbprint = [string]$binding.certificateHash }
    $binding.AddSslCertificate($t, "My")
    $result.message = "Bound $t to https $info on site '$site'."
    $result.ok = $true
} catch {
    $result.message = $_.Exception.Message
}
Write-Output ("##AUTOMATECERT##" + ($result | ConvertTo-Json -Compress))
