param([Parameter(Mandatory = $true)][string]$ParamFile)
$ErrorActionPreference = "Stop"
$result = [ordered]@{ ok = $false; previousThumbprint = $null; message = "" }
$found = New-Object System.Collections.Generic.List[object]
$roles = New-Object System.Collections.Generic.List[string]
function Add-Binding($thumb, $text) { if ($thumb) { $found.Add([ordered]@{ thumbprint = ([string]$thumb).ToUpper(); binding = $text }) } }
try {
    if (Get-Service W3SVC -ErrorAction SilentlyContinue) { $roles.Add("IIS") }
    if (Get-Module -ListAvailable -Name WebAdministration) {
        try { Import-Module WebAdministration; foreach ($site in Get-Website) { foreach ($b in @(Get-WebBinding -Name $site.Name -Protocol https)) { Add-Binding $b.certificateHash ("IIS '{0}' {1}" -f $site.Name, $b.bindingInformation) } } } catch { }
    }
    if (Get-Service adfssrv -ErrorAction SilentlyContinue) {
        $roles.Add("ADFS")
        try { if ((Get-AdfsProperties).AutoCertificateRollover) { $roles.Add("ADFS-AutoRollover") } } catch { }
        try { Get-AdfsSslCertificate | ForEach-Object { Add-Binding $_.CertificateHash ("AD FS SSL {0}:{1}" -f $_.HostName, $_.PortNumber) } } catch { }
        foreach ($ct in @("Service-Communications", "Token-Signing", "Token-Decrypting")) {
            try { Get-AdfsCertificate -CertificateType $ct | ForEach-Object { Add-Binding $_.Thumbprint ("AD FS {0} ({1})" -f $ct.ToLower(), $(if ($_.IsPrimary) { "primary" } else { "secondary" })) } } catch { }
        }
    }
    if (Get-Service appproxysvc -ErrorAction SilentlyContinue) {
        $roles.Add("WAP")
        try { Add-Binding (Get-WebApplicationProxySslCertificate | Select-Object -First 1).CertificateHash "WAP SSL" } catch { }
        try { Get-WebApplicationProxyApplication | ForEach-Object { Add-Binding $_.ExternalCertificateThumbprint ("WAP app " + $_.Name) } } catch { }
    }
    if (Test-Path "HKLM:\SOFTWARE\Microsoft\MSCRM") {
        $roles.Add("CRM")
        try {
            Add-PSSnapin Microsoft.Crm.PowerShell -ErrorAction Stop
            $claims = Get-CrmSetting -SettingType ClaimsSettings
            if ($claims.Enabled -and $claims.EncryptionCertificate) {
                Get-ChildItem Cert:\LocalMachine\My | Where-Object { $_.Subject -eq [string]$claims.EncryptionCertificate } | ForEach-Object { Add-Binding $_.Thumbprint "CRM claims encryption" }
            }
        } catch { }
    }
    try { $ts = Get-CimInstance -Namespace root\cimv2\TerminalServices -ClassName Win32_TSGeneralSetting -Filter "TerminalName='RDP-tcp'" -ErrorAction Stop; Add-Binding $ts.SSLCertificateSHA1Hash "RDP listener" } catch { }
    if (Get-Service TSGateway -ErrorAction SilentlyContinue) { $roles.Add("RDGateway"); try { Import-Module RemoteDesktopServices; Add-Binding (Get-Item RDS:\GatewayServer\SSLCertificate\Thumbprint).CurrentValue "RD Gateway" } catch { } }
    try {
        $names = Get-ItemProperty "HKLM:\SOFTWARE\Microsoft\Microsoft SQL Server\Instance Names\SQL" -ErrorAction Stop
        $roles.Add("SQL")
        foreach ($prop in $names.PSObject.Properties | Where-Object { $_.Name -notlike "PS*" }) {
            Add-Binding (Get-ItemProperty "HKLM:\SOFTWARE\Microsoft\Microsoft SQL Server\$($prop.Value)\MSSQLServer\SuperSocketNetLib").Certificate ("SQL Server " + $prop.Name)
        }
    } catch { }
    if (Get-Service MSExchangeServiceHost -ErrorAction SilentlyContinue) {
        $roles.Add("Exchange")
        try { Add-PSSnapin Microsoft.Exchange.Management.PowerShell.SnapIn -ErrorAction Stop; Get-ExchangeCertificate | Where-Object { [string]$_.Services -ne "None" } | ForEach-Object { Add-Binding $_.Thumbprint ("Exchange " + ([string]$_.Services)) } } catch { }
    }
    if (Get-Service DNS -ErrorAction SilentlyContinue) { $roles.Add("DNS") }
    if (Get-Service AutomateCertServer -ErrorAction SilentlyContinue) { $roles.Add("AutomateCert") }
    $result.message = ConvertTo-Json -InputObject ([ordered]@{ bindings = @($found.ToArray()); roles = @($roles.ToArray()) }) -Compress -Depth 4
    $result.ok = $true
} catch { $result.message = $_.Exception.Message }
Write-Output ("##AUTOMATECERT##" + ($result | ConvertTo-Json -Compress))
