param([Parameter(Mandatory = $true)][string]$ParamFile)
$ErrorActionPreference = "Stop"
$p = Get-Content -Raw -LiteralPath $ParamFile | ConvertFrom-Json
$t = ([string]$p.thumbprint).ToUpper()
$prm = $p.parameters
$result = [ordered]@{ ok = $false; previousThumbprint = $null; message = "" }

function Grant-KeyRead([string]$Thumbprint, [string]$Account) {
    $cert = Get-Item -LiteralPath "Cert:\LocalMachine\My\$Thumbprint"
    $key = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($cert)
    if ($null -eq $key) { $key = [System.Security.Cryptography.X509Certificates.ECDsaCertificateExtensions]::GetECDsaPrivateKey($cert) }
    if ($null -eq $key) { throw "Certificate $Thumbprint has no accessible private key." }
    if ($key -is [System.Security.Cryptography.RSACng] -or $key -is [System.Security.Cryptography.ECDsaCng]) {
        $path = Join-Path $env:ProgramData ("Microsoft\Crypto\Keys\" + $key.Key.UniqueName)
        if (-not (Test-Path -LiteralPath $path)) { $path = Join-Path $env:ProgramData ("Microsoft\Crypto\RSA\MachineKeys\" + $key.Key.UniqueName) }
    } else {
        $path = Join-Path $env:ProgramData ("Microsoft\Crypto\RSA\MachineKeys\" + $key.CspKeyContainerInfo.UniqueKeyContainerName)
    }
    $acl = Get-Acl -LiteralPath $path
    $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($Account, "Read", "Allow")))
    Set-Acl -LiteralPath $path -AclObject $acl
}
try {
    Import-Module WebAdministration
    $appPool = if ($prm.appPool) { [string]$prm.appPool } else { "CRMAppPool" }
    $pool = Get-Item "IIS:\AppPools\$appPool" -ErrorAction Stop
    $account = switch ([string]$pool.processModel.identityType) {
        "SpecificUser" { [string]$pool.processModel.userName }
        "NetworkService" { "NT AUTHORITY\NETWORK SERVICE" }
        "LocalSystem" { "NT AUTHORITY\SYSTEM" }
        "ApplicationPoolIdentity" { "IIS APPPOOL\$appPool" }
        default { "NT AUTHORITY\NETWORK SERVICE" }
    }
    Grant-KeyRead -Thumbprint $t -Account $account
    $cert = Get-Item -LiteralPath "Cert:\LocalMachine\My\$t"
    $msg = @("Granted $account read access to the new key.")
    if ([string]$prm.updateClaimsSetting -ne "false") {
        Add-PSSnapin Microsoft.Crm.PowerShell -ErrorAction Stop
        $claims = Get-CrmSetting -SettingType ClaimsSettings
        if (-not $claims.Enabled) { $msg += "Claims-based authentication is not enabled in CRM; claims setting left unchanged." }
        else {
            $old = [string]$claims.EncryptionCertificate
            if ($old -eq $cert.Subject) {
                $msg += "CRM claims encryption certificate subject unchanged ('$old'); no CRM setting change needed."
            } else {
                $claims.EncryptionCertificate = $cert.Subject
                try { Set-CrmSetting -Setting $claims }
                catch { throw "Set-CrmSetting failed: $($_.Exception.Message). The agent runs as the computer account ($env:USERDOMAIN\$env:COMPUTERNAME`$); add it as a CRM Deployment Administrator, or set the encryption certificate once in Deployment Manager and use 'Update claims encryption setting' = false." }
                $msg += "CRM claims encryption certificate set to '$($cert.Subject)' (was '$old')."
            }
            $same = @(Get-ChildItem Cert:\LocalMachine\My | Where-Object { $_.Subject -eq $cert.Subject -and $_.Thumbprint -ne $t -and $_.HasPrivateKey })
            if ($same.Count -gt 0) { $msg += "Note: $($same.Count) older certificate(s) with the same subject remain in the store; remove them after verifying sign-in so CRM cannot select an old one." }
        }
    }
    if ([string]$prm.iisReset -ne "false") { & iisreset.exe /noforce | Out-Null; $msg += "IIS reset." }
    $result.message = $msg -join " "
    $result.ok = $true
} catch {
    $result.message = $_.Exception.Message
}
Write-Output ("##AUTOMATECERT##" + ($result | ConvertTo-Json -Compress))
