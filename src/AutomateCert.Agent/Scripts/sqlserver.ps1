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
    $instance = if ($prm.instanceName) { [string]$prm.instanceName } else { "MSSQLSERVER" }
    $instId = (Get-ItemProperty "HKLM:\SOFTWARE\Microsoft\Microsoft SQL Server\Instance Names\SQL").$instance
    if (-not $instId) { throw "SQL Server instance '$instance' not found." }
    $reg = "HKLM:\SOFTWARE\Microsoft\Microsoft SQL Server\$instId\MSSQLServer\SuperSocketNetLib"
    $prev = (Get-ItemProperty -Path $reg).Certificate
    if ($prev) { $result.previousThumbprint = ([string]$prev).ToUpper() }
    if ($instance -eq "MSSQLSERVER") { $svcName = "MSSQLSERVER"; $agentName = "SQLSERVERAGENT" } else { $svcName = "MSSQL`$$instance"; $agentName = "SQLAgent`$$instance" }
    $svc = Get-CimInstance Win32_Service -Filter "Name='$svcName'"
    if (-not $svc) { throw "Service $svcName not found." }
    Grant-KeyRead -Thumbprint $t -Account $svc.StartName
    Set-ItemProperty -Path $reg -Name Certificate -Value $t.ToLower()
    if ($prm.forceEncryption -eq "true") { Set-ItemProperty -Path $reg -Name ForceEncryption -Value 1 }
    $agentWasRunning = (Get-Service -Name $agentName -ErrorAction SilentlyContinue).Status -eq "Running"
    Restart-Service -Name $svcName -Force
    if ($agentWasRunning) { Start-Service -Name $agentName }
    $result.message = "SQL Server $instance now uses $t; service restarted (account $($svc.StartName) granted key read)."
    $result.ok = $true
} catch {
    $result.message = $_.Exception.Message
}
Write-Output ("##AUTOMATECERT##" + ($result | ConvertTo-Json -Compress))
