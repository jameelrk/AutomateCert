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
    $prev = Get-AdfsSslCertificate | Select-Object -First 1
    if ($prev) { $result.previousThumbprint = [string]$prev.CertificateHash }
    Grant-KeyRead -Thumbprint $t -Account $svc.StartName
    Set-AdfsSslCertificate -Thumbprint $t
    Set-AdfsCertificate -CertificateType Service-Communications -Thumbprint $t
    Restart-Service adfssrv -Force
    $result.message = "AD FS SSL and service-communications certificate set to $t on $env:COMPUTERNAME; adfssrv restarted (account $($svc.StartName) granted key read)."
    $result.ok = $true
} catch {
    $result.message = $_.Exception.Message
}
Write-Output ("##AUTOMATECERT##" + ($result | ConvertTo-Json -Compress))
