param([Parameter(Mandatory = $true)][string]$ParamFile)
$ErrorActionPreference = "Stop"
$p = Get-Content -Raw -LiteralPath $ParamFile | ConvertFrom-Json
$t = ([string]$p.thumbprint).ToUpper()
$prm = $p.parameters
$result = [ordered]@{ ok = $false; previousThumbprint = $null; message = "" }
try {
    $name = [string]$prm.serviceName
    $svc = Get-Service -Name $name -ErrorAction SilentlyContinue
    if (-not $svc) { throw "Service '$name' not found." }
    Restart-Service -Name $name -Force
    $result.message = "Service '$name' restarted on $env:COMPUTERNAME."
    $result.ok = $true
} catch {
    $result.message = $_.Exception.Message
}
Write-Output ("##AUTOMATECERT##" + ($result | ConvertTo-Json -Compress))
