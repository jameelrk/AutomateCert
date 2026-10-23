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
    $old = ([string]$prm.oldThumbprint).ToUpper()
    $result.previousThumbprint = $old
    $changed = New-Object System.Collections.Generic.List[string]
    if (-not $old -or $old -eq $t) { $result.message = "No previous certificate tracked; nothing to replace." }
    else {
        if (Get-Module -ListAvailable -Name WebAdministration) {
            Import-Module WebAdministration
            foreach ($site in Get-Website) {
                foreach ($b in @(Get-WebBinding -Name $site.Name -Protocol https)) {
                    if (([string]$b.certificateHash).ToUpper() -eq $old) { $b.AddSslCertificate($t, "My"); $changed.Add("IIS '$($site.Name)' $($b.bindingInformation)") }
                }
            }
        }
        if (Get-Service adfssrv -ErrorAction SilentlyContinue) {
            $ssl = Get-AdfsSslCertificate | Select-Object -First 1
            if ($ssl -and ([string]$ssl.CertificateHash).ToUpper() -eq $old) {
                $svc = Get-CimInstance Win32_Service -Filter "Name='adfssrv'"
                Grant-KeyRead -Thumbprint $t -Account $svc.StartName
                Set-AdfsSslCertificate -Thumbprint $t
                $sc = Get-AdfsCertificate -CertificateType Service-Communications | Select-Object -First 1
                if ($sc -and ([string]$sc.Thumbprint).ToUpper() -eq $old) { Set-AdfsCertificate -CertificateType Service-Communications -Thumbprint $t }
                Restart-Service adfssrv -Force
                $changed.Add("AD FS (restarted)")
            }
        }
        if (Get-Service appproxysvc -ErrorAction SilentlyContinue) {
            try { $w = [string](Get-WebApplicationProxySslCertificate | Select-Object -First 1).CertificateHash; if ($w.ToUpper() -eq $old) { Set-WebApplicationProxySslCertificate -Thumbprint $t; $changed.Add("WAP SSL") } } catch { }
            Get-WebApplicationProxyApplication | Where-Object { ([string]$_.ExternalCertificateThumbprint).ToUpper() -eq $old } | ForEach-Object { Set-WebApplicationProxyApplication -ID $_.ID -ExternalCertificateThumbprint $t; $changed.Add("WAP app '$($_.Name)'") }
        }
        $ts = Get-CimInstance -Namespace root\cimv2\TerminalServices -ClassName Win32_TSGeneralSetting -Filter "TerminalName='RDP-tcp'" -ErrorAction SilentlyContinue
        if ($ts -and ([string]$ts.SSLCertificateSHA1Hash).ToUpper() -eq $old) { Grant-KeyRead -Thumbprint $t -Account "NT AUTHORITY\NETWORK SERVICE"; Set-CimInstance -InputObject $ts -Property @{ SSLCertificateSHA1Hash = $t }; $changed.Add("RDP listener") }
        if (Get-Service TSGateway -ErrorAction SilentlyContinue) {
            Import-Module RemoteDesktopServices
            $item = "RDS:\GatewayServer\SSLCertificate\Thumbprint"
            if (([string](Get-Item -Path $item).CurrentValue).ToUpper() -eq $old) { Set-Item -Path $item -Value $t; Restart-Service TSGateway -Force; $changed.Add("RD Gateway (restarted)") }
        }
        $names = Get-ItemProperty "HKLM:\SOFTWARE\Microsoft\Microsoft SQL Server\Instance Names\SQL" -ErrorAction SilentlyContinue
        if ($names) {
            foreach ($prop in $names.PSObject.Properties | Where-Object { $_.Name -notlike "PS*" }) {
                $reg = "HKLM:\SOFTWARE\Microsoft\Microsoft SQL Server\$($prop.Value)\MSSQLServer\SuperSocketNetLib"
                if (([string](Get-ItemProperty -Path $reg).Certificate).ToUpper() -eq $old) {
                    if ($prop.Name -eq "MSSQLSERVER") { $svcName = "MSSQLSERVER"; $agentName = "SQLSERVERAGENT" } else { $svcName = "MSSQL`$$($prop.Name)"; $agentName = "SQLAgent`$$($prop.Name)" }
                    $svc = Get-CimInstance Win32_Service -Filter "Name='$svcName'"
                    Grant-KeyRead -Thumbprint $t -Account $svc.StartName
                    Set-ItemProperty -Path $reg -Name Certificate -Value $t.ToLower()
                    $agentWasRunning = (Get-Service -Name $agentName -ErrorAction SilentlyContinue).Status -eq "Running"
                    Restart-Service -Name $svcName -Force
                    if ($agentWasRunning) { Start-Service -Name $agentName }
                    $changed.Add("SQL Server $($prop.Name) (restarted)")
                }
            }
        }
        if (Get-PSSnapin -Registered -Name Microsoft.Exchange.Management.PowerShell.SnapIn -ErrorAction SilentlyContinue) {
            Add-PSSnapin Microsoft.Exchange.Management.PowerShell.SnapIn -ErrorAction SilentlyContinue
            $ec = Get-ExchangeCertificate -Thumbprint $old -ErrorAction SilentlyContinue
            if ($ec -and [string]$ec.Services -ne "None") { $services = ([string]$ec.Services) -replace " ", ""; Enable-ExchangeCertificate -Thumbprint $t -Services $services -Force -Confirm:$false; $changed.Add("Exchange ($services)") }
        }
        if (Get-Service AutomateCertServer -ErrorAction SilentlyContinue) { Grant-KeyRead -Thumbprint $t -Account "NT SERVICE\AutomateCertServer" }
        $result.message = if ($changed.Count -gt 0) { "Replaced $old with $t on: " + ($changed -join "; ") } else { "Certificate $old was not bound to any supported service on this server; new certificate installed only." }
    }
    $result.ok = $true
} catch {
    $result.message = $_.Exception.Message
}
Write-Output ("##AUTOMATECERT##" + ($result | ConvertTo-Json -Compress))
