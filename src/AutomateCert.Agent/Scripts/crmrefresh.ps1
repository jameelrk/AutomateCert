param([Parameter(Mandatory = $true)][string]$ParamFile)
$ErrorActionPreference = "Stop"
$p = Get-Content -Raw -LiteralPath $ParamFile | ConvertFrom-Json
$t = ([string]$p.thumbprint).ToUpper()
$prm = $p.parameters
$result = [ordered]@{ ok = $false; previousThumbprint = $null; message = "" }
try {
    & iisreset.exe /noforce | Out-Null
    $msg = "IIS reset on $env:COMPUTERNAME so CRM reloads AD FS federation metadata."
    if ([string]$prm.restartAsync -eq "true") {
        Get-Service | Where-Object { $_.Name -like "MSCRMAsyncService*" -and $_.Status -eq "Running" } | Restart-Service -Force
        $msg += " CRM asynchronous services restarted."
    }
    $result.message = $msg
    $result.ok = $true
} catch {
    $result.message = $_.Exception.Message
}
Write-Output ("##AUTOMATECERT##" + ($result | ConvertTo-Json -Compress))
