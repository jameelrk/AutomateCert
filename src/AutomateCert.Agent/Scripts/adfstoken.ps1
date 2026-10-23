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
    $svc = Get-CimInstance Win32_Service -Filter "Name='adfssrv'"
    if (-not $svc) { throw "AD FS service (adfssrv) not found on this server." }
    $type = if ($prm.certificateType) { [string]$prm.certificateType } else { "Token-Signing" }
    $phase = if ($prm.phase) { [string]$prm.phase } else { "stage" }
    $props = Get-AdfsProperties
    if ($props.AutoCertificateRollover) {
        if ([string]$prm.allowDisableAutoRollover -eq "true") { Set-AdfsProperties -AutoCertificateRollover $false }
        else { throw "AD FS AutoCertificateRollover is enabled, so AD FS manages its own $type certificates. Keep it (and remove this step) or set 'Turn off AutoCertificateRollover' = true." }
    }
    $primary = Get-AdfsCertificate -CertificateType $type | Where-Object { $_.IsPrimary } | Select-Object -First 1
    if ($primary) { $result.previousThumbprint = [string]$primary.Thumbprint }
    $present = Get-AdfsCertificate -CertificateType $type | Where-Object { $_.Thumbprint -eq $t }
    $msg = @()
    if (-not $present) {
        Grant-KeyRead -Thumbprint $t -Account $svc.StartName
        Add-AdfsCertificate -CertificateType $type -Thumbprint $t
        $msg += "Added $t as SECONDARY $type certificate (relying parties can now read it from federation metadata)."
    }
    if ($phase -eq "promote" -or $phase -eq "now") {
        Set-AdfsCertificate -CertificateType $type -Thumbprint $t -IsPrimary
        $msg += "Promoted $t to PRIMARY $type certificate. Previous primary remains as secondary until it expires."
    }
    $result.message = ($msg -join " ") + " Run on the primary AD FS node; farm members need the same certificate and key."
    $result.ok = $true
} catch {
    $result.message = $_.Exception.Message
}
Write-Output ("##AUTOMATECERT##" + ($result | ConvertTo-Json -Compress))
