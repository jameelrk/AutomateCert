param([Parameter(Mandatory = $true)][string]$ParamFile)
$ErrorActionPreference = "Stop"
$p = Get-Content -Raw -LiteralPath $ParamFile | ConvertFrom-Json
$t = ([string]$p.thumbprint).ToUpper()
$prm = $p.parameters
$result = [ordered]@{ ok = $false; previousThumbprint = $null; message = "" }
try {
    $names = ([string]$prm.relyingParties).Split(",") | ForEach-Object { $_.Trim() } | Where-Object { $_ }
    if (-not $names) { throw "No relying party names given." }
    $done = @()
    foreach ($n in $names) {
        $rp = Get-AdfsRelyingPartyTrust -Name $n
        if (-not $rp) { throw "Relying party '$n' not found in AD FS." }
        if (-not $rp.MetadataUrl) { throw "Relying party '$n' has no federation metadata URL; update its encryption certificate manually." }
        Update-AdfsRelyingPartyTrust -TargetName $n
        $done += $n
    }
    $result.message = "Refreshed relying parties from federation metadata: " + ($done -join ", ")
    $result.ok = $true
} catch {
    $result.message = $_.Exception.Message
}
Write-Output ("##AUTOMATECERT##" + ($result | ConvertTo-Json -Compress))
