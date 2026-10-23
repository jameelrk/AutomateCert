param([Parameter(Mandatory = $true)][string]$ParamFile)
$ErrorActionPreference = "Stop"
$p = Get-Content -Raw -LiteralPath $ParamFile | ConvertFrom-Json
$result = [ordered]@{ ok = $false; previousThumbprint = $null; message = "" }
try {
    Import-Module DnsServer -ErrorAction Stop
    $cn = if ([string]$p.server) { [string]$p.server } else { $env:COMPUTERNAME }
    $fqdn = ([string]$p.name).TrimEnd(".")
    $zone = [string]$p.zone
    if (-not $zone) {
        $zones = Get-DnsServerZone -ComputerName $cn | Where-Object { -not $_.IsReverseLookupZone -and $_.ZoneType -ne "Forwarder" } | Select-Object -ExpandProperty ZoneName
        $zone = $zones | Where-Object { $fqdn -eq $_ -or $fqdn.EndsWith("." + $_) } | Sort-Object Length -Descending | Select-Object -First 1
        if (-not $zone) { throw "No zone on $cn hosts $fqdn." }
    }
    $rel = if ($fqdn -eq $zone) { "@" } else { $fqdn.Substring(0, $fqdn.Length - $zone.Length - 1) }
    if ([string]$p.action -eq "add") {
        Add-DnsServerResourceRecord -ComputerName $cn -ZoneName $zone -Name $rel -Txt -DescriptiveText ([string]$p.value) -TimeToLive (New-TimeSpan -Seconds 60)
        $result.message = "TXT $fqdn added in zone $zone on $cn."
    } else {
        Get-DnsServerResourceRecord -ComputerName $cn -ZoneName $zone -Name $rel -RRType Txt -ErrorAction SilentlyContinue |
            Where-Object { $_.RecordData.DescriptiveText -eq [string]$p.value } | Remove-DnsServerResourceRecord -ComputerName $cn -ZoneName $zone -Force
        $result.message = "TXT $fqdn removed from zone $zone on $cn."
    }
    $result.ok = $true
} catch {
    $result.message = $_.Exception.Message
}
Write-Output ("##AUTOMATECERT##" + ($result | ConvertTo-Json -Compress))
