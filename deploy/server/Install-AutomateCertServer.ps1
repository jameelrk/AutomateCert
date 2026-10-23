<#
.SYNOPSIS
  Installs or upgrades AutomateCert server as a self-contained Windows service. Nothing else to install by hand:
  no IIS, no .NET runtime, optional SQL Server Express, the console certificate, database, administrator, free Let's Encrypt
  issuers, firewall rule and the agent on this server are all handled here.

.DESCRIPTION
  Interactive:   .\Install-Server.cmd                (prompts for anything missing)
  Unattended:    .\Install-AutomateCertServer.ps1 -PublicHostName automatecert.contoso.com -InstallSqlExpress -OrganizationName "Contoso" `
                    -AdminEmail admin@contoso.com -AdminPassword '...' -OpenFirewall
  Answer file:   .\Install-AutomateCertServer.ps1 -AnswerFile C:\temp\answers.json   (used by AutomateCertServer-Setup.exe; deleted after reading)
  Upgrade:       .\Install-AutomateCertServer.ps1 -Upgrade
#>
[CmdletBinding()]
param(
    [string]$AnswerFile,
    [switch]$Upgrade,
    [string]$PublicHostName,
    [int]$HttpsPort = 443,
    [string]$CertificateThumbprint,
    [switch]$SelfSignedCertificate,
    [string]$SqlServer = ".",
    [string]$SqlDatabase = "AutomateCert",
    [ValidateSet("Windows", "Sql")][string]$SqlAuth = "Windows",
    [string]$SqlUser,
    [string]$SqlPassword,
    [switch]$InstallSqlExpress,
    [string]$SqlExpressUrl = "https://download.microsoft.com/download/5/1/4/5145fe04-4d30-4b85-b0d1-39533663a2f1/SQL2022-SSEI-Expr.exe",
    [string]$SqlExpressInstaller,
    [string]$OrganizationName,
    [string]$AdminName,
    [string]$AdminEmail,
    [string]$AdminPassword,
    [string]$SmtpHost,
    [int]$SmtpPort = 587,
    [bool]$SmtpStartTls = $true,
    [string]$SmtpUser,
    [string]$SmtpPassword,
    [string]$SmtpFrom,
    [string]$SmtpFromName = "AutomateCert",
    [string]$AlertEmails,
    [switch]$RequireMfa,
    [switch]$OpenFirewall,
    [bool]$InstallLocalAgent = $true,
    [string]$InstallDir = "$env:ProgramFiles\AutomateCert\Server",
    [string]$DataDir = "$env:ProgramData\AutomateCert\Server",
    [string]$PackageDir = $PSScriptRoot
)
$ErrorActionPreference = "Stop"
$AllowedAnswers = @($MyInvocation.MyCommand.Parameters.Keys | Where-Object { $_ -notin @("AnswerFile", "PackageDir") })
$ServiceName = "AutomateCertServer"
$ServiceAccount = "NT SERVICE\$ServiceName"

New-Item -ItemType Directory -Force -Path $DataDir | Out-Null
$logFile = Join-Path $DataDir ("install-{0:yyyyMMdd-HHmmss}.log" -f (Get-Date))
function Log([string]$Text, [string]$Color = "Gray") { Write-Host $Text -ForegroundColor $Color; Add-Content -LiteralPath $logFile -Value ("[{0:HH:mm:ss}] {1}" -f (Get-Date), $Text) }
function Step([string]$Text) { Log "==> $Text" "Cyan" }
function Fail([string]$Text) { Log "ERROR: $Text" "Red"; Log "Log file: $logFile" "Yellow"; exit 1 }

try {
# ---------------------------------------------------------------- 0. Inputs
$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { Fail "Run as Administrator." }
if ([Environment]::OSVersion.Version.Major -lt 10) { Fail "Windows Server 2016 or later is required." }

if ($AnswerFile) {
    if (-not (Test-Path -LiteralPath $AnswerFile)) { Fail "Answer file not found: $AnswerFile" }
    $a = Get-Content -LiteralPath $AnswerFile -Raw -Encoding UTF8 | ConvertFrom-Json
    Remove-Item -LiteralPath $AnswerFile -Force -ErrorAction SilentlyContinue   # contains passwords
    foreach ($p in $a.PSObject.Properties) {
        if ($null -eq $p.Value -or "$($p.Value)" -eq "") { continue }
        if ($AllowedAnswers -notcontains $p.Name) { Log "    Ignoring unknown answer '$($p.Name)'" "Yellow"; continue }
        switch ($p.Name) {
            { $_ -in @("Upgrade", "SelfSignedCertificate", "RequireMfa", "OpenFirewall", "InstallSqlExpress") } { Set-Variable -Name $p.Name -Value ([System.Management.Automation.SwitchParameter][bool]$p.Value); continue }
            { $_ -in @("SmtpStartTls", "InstallLocalAgent") } { Set-Variable -Name $p.Name -Value ([bool]$p.Value); continue }
            { $_ -in @("HttpsPort", "SmtpPort") } { Set-Variable -Name $p.Name -Value ([int]$p.Value); continue }
            default { Set-Variable -Name $p.Name -Value ([string]$p.Value) }
        }
    }
}

$settingsPath = Join-Path $InstallDir "appsettings.Production.json"
if (-not $Upgrade -and -not $PublicHostName -and (Test-Path $settingsPath)) { $Upgrade = $true }
$interactive = [Environment]::UserInteractive -and -not $AnswerFile
function Ask([string]$Prompt, [string]$Current, [switch]$Secret, [switch]$Optional) {
    if ($Current) { return $Current }
    if (-not $interactive) { if ($Optional) { return "" }; Fail "Missing required value: $Prompt" }
    if ($Secret) { $s = Read-Host -Prompt $Prompt -AsSecureString; return [Runtime.InteropServices.Marshal]::PtrToStringAuto([Runtime.InteropServices.Marshal]::SecureStringToBSTR($s)) }
    $v = Read-Host -Prompt $Prompt
    if (-not $v -and -not $Optional) { Fail "$Prompt is required." }
    return $v
}
if (-not $Upgrade) {
    $PublicHostName = (Ask "Console host name (e.g. automatecert.contoso.com)" $PublicHostName).Trim().ToLowerInvariant()
    if ($PublicHostName -notmatch '^[a-z0-9]([a-z0-9\-\.]{0,251}[a-z0-9])?$') { Fail "Invalid host name '$PublicHostName'." }
    if ($SqlDatabase -notmatch '^[A-Za-z0-9_]{1,100}$') { Fail "Database name may contain letters, digits and underscore only." }
    if ($SqlAuth -eq "Sql") { $SqlUser = Ask "SQL login" $SqlUser; $SqlPassword = Ask "SQL password" $SqlPassword -Secret }
    $OrganizationName = Ask "Organization name" $OrganizationName
    $AdminName = Ask "Administrator name" $AdminName -Optional
    $AdminEmail = Ask "Administrator email" $AdminEmail
    if ($AdminEmail -notmatch '^[^@\s]+@[^@\s]+\.[^@\s]+$') { Fail "Invalid administrator email." }
    $AdminPassword = Ask "Administrator password (min 12 chars)" $AdminPassword -Secret
    if ($AdminPassword.Length -lt 12) { Fail "Administrator password must be at least 12 characters." }
}

# ---------------------------------------------------------------- 1. Package
# Pre-flight: the HTTPS port must be free (IIS or another web server often owns 443).
if (-not $Upgrade) {
    $listener = Get-NetTCPConnection -LocalPort $HttpsPort -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($listener) {
        $owner = (Get-Process -Id $listener.OwningProcess -ErrorAction SilentlyContinue).ProcessName
        if ($owner -ne "AutomateCert.Server") {
            $hint = if ($owner -eq "System") { "IIS or another HTTP.sys application" } else { "process '$owner'" }
            Fail "Port $HttpsPort is already used by $hint. Run Setup again and choose another HTTPS port (for example 8443), or move the other site."
        }
    }
}

Step "Locating package"
$src = if (Test-Path (Join-Path $PackageDir "server\AutomateCert.Server.exe")) { Join-Path $PackageDir "server" } elseif (Test-Path (Join-Path $PackageDir "AutomateCert.Server.exe")) { $PackageDir } else { $null }
if (-not $src) { Fail "AutomateCert.Server.exe not found under $PackageDir. Run deploy\build.ps1 first or use AutomateCertServer-Setup.exe." }
Log "    Package: $src"

# ---------------------------------------------------------------- 2. SQL Server Express (optional)
function Test-SqlInstance([string]$Instance) {
    $b = New-Object System.Data.SqlClient.SqlConnectionStringBuilder
    $b["Data Source"] = $Instance; $b["Integrated Security"] = $true; $b["Connect Timeout"] = 5; $b["TrustServerCertificate"] = $true; $b["Encrypt"] = $true
    $c = New-Object System.Data.SqlClient.SqlConnection $b.ConnectionString
    try { $c.Open(); return $true } catch { return $false } finally { $c.Dispose() }
}
if (-not $Upgrade -and $InstallSqlExpress -and $SqlAuth -eq "Windows") {
    if (Test-SqlInstance $SqlServer) { Log "    SQL Server '$SqlServer' is reachable; skipping SQL Express installation." }
    elseif (Test-SqlInstance ".\SQLEXPRESS") { $SqlServer = ".\SQLEXPRESS"; Log "    Using existing SQL Server Express (.\SQLEXPRESS)." }
    else {
        Step "Installing SQL Server 2022 Express (5-10 minutes)"
        $work = Join-Path $env:TEMP ("AutomateCertSql-" + [guid]::NewGuid().ToString("N"))
        New-Item -ItemType Directory -Force -Path $work | Out-Null
        try {
            $media = $SqlExpressInstaller
            if (-not $media) {
                $ssei = Join-Path $work "SQL2022-SSEI-Expr.exe"
                [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
                Invoke-WebRequest -Uri $SqlExpressUrl -OutFile $ssei -UseBasicParsing
                $p = Start-Process -FilePath $ssei -ArgumentList "/ACTION=Download", "/MEDIAPATH=$work", "/MEDIATYPE=Core", "/QUIET" -Wait -PassThru
                if ($p.ExitCode -ne 0) { Fail "SQL Express media download failed (exit $($p.ExitCode)). Install SQL Server manually and re-run with -SqlServer." }
                $media = (Get-ChildItem $work -Filter "SQLEXPR*.exe" | Select-Object -First 1).FullName
            }
            if (-not $media) { Fail "SQL Express media not found." }
            $x = Join-Path $work "x"
            $p = Start-Process -FilePath $media -ArgumentList "/q", "/x:$x" -Wait -PassThru
            if ($p.ExitCode -ne 0) { Fail "SQL Express extraction failed (exit $($p.ExitCode))." }
            $p = Start-Process -FilePath (Join-Path $x "SETUP.EXE") -ArgumentList "/Q", "/IACCEPTSQLSERVERLICENSETERMS", "/ACTION=Install", "/FEATURES=SQLENGINE", "/INSTANCENAME=SQLEXPRESS", "/SQLSYSADMINACCOUNTS=BUILTIN\Administrators", "/UPDATEENABLED=0", "/TCPENABLED=1" -Wait -PassThru
            if ($p.ExitCode -ne 0 -and $p.ExitCode -ne 3010) { Fail "SQL Express setup failed (exit $($p.ExitCode)). See %ProgramFiles%\Microsoft SQL Server\160\Setup Bootstrap\Log." }
            $SqlServer = ".\SQLEXPRESS"
            Log "    SQL Server 2022 Express installed (.\SQLEXPRESS)."
        } finally { Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue }
    }
}

# ---------------------------------------------------------------- 3. Stop + copy
$svc = Get-Service $ServiceName -ErrorAction SilentlyContinue
if ($svc -and $svc.Status -ne "Stopped") { Step "Stopping existing service"; Stop-Service $ServiceName -Force; (Get-Service $ServiceName).WaitForStatus("Stopped", [TimeSpan]::FromSeconds(60)) }
Step "Copying application to $InstallDir"
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
& robocopy.exe $src $InstallDir /MIR /XF appsettings.Production.json Install-AutomateCertServer.ps1 Uninstall-AutomateCertServer.ps1 unins*.* /R:2 /W:2 /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { Fail "File copy failed (robocopy exit code $LASTEXITCODE)." }
foreach ($f in @("Install-AutomateCertServer.ps1", "Uninstall-AutomateCertServer.ps1")) { $p = Join-Path $PSScriptRoot $f; if (Test-Path $p) { Copy-Item $p $InstallDir -Force } }
Get-ChildItem -LiteralPath $InstallDir -Recurse -File | Unblock-File
$exe = Join-Path $InstallDir "AutomateCert.Server.exe"

# ---------------------------------------------------------------- 4. Service (virtual account)
if (-not (Get-Service $ServiceName -ErrorAction SilentlyContinue)) {
    Step "Creating Windows service $ServiceName ($ServiceAccount)"
    New-Service -Name $ServiceName -BinaryPathName "`"$exe`"" -DisplayName "AutomateCert Server" -Description "AutomateCert certificate lifecycle management" -StartupType Automatic | Out-Null
    & sc.exe config $ServiceName obj= "$ServiceAccount" | Out-Null
    if ($LASTEXITCODE -ne 0) { Fail "Could not configure the service account." }
} else { & sc.exe config $ServiceName binPath= "`"$exe`"" start= auto | Out-Null }
& sc.exe failure $ServiceName reset= 86400 actions= restart/60000/restart/60000/restart/300000 | Out-Null
if (-not [Diagnostics.EventLog]::SourceExists("AutomateCert.Server")) { New-EventLog -LogName Application -Source "AutomateCert.Server" }

Step "Securing data folders"
$keysDir = Join-Path $DataDir "keys"
New-Item -ItemType Directory -Force -Path $keysDir | Out-Null
& icacls.exe $DataDir /inheritance:r /grant:r "*S-1-5-18:(OI)(CI)F" "*S-1-5-32-544:(OI)(CI)F" "${ServiceAccount}:(OI)(CI)M" | Out-Null
if ($LASTEXITCODE -ne 0) { Fail "Could not set permissions on $DataDir." }

function Grant-KeyRead([string]$Thumbprint, [string]$Account) {
    $cert = Get-Item -LiteralPath "Cert:\LocalMachine\My\$Thumbprint"
    $key = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($cert)
    if ($null -eq $key) { $key = [System.Security.Cryptography.X509Certificates.ECDsaCertificateExtensions]::GetECDsaPrivateKey($cert) }
    if ($null -eq $key) { throw "Certificate $Thumbprint has no accessible private key." }
    if ($key -is [System.Security.Cryptography.RSACng] -or $key -is [System.Security.Cryptography.ECDsaCng]) {
        $path = Join-Path $env:ProgramData ("Microsoft\Crypto\Keys\" + $key.Key.UniqueName)
        if (-not (Test-Path -LiteralPath $path)) { $path = Join-Path $env:ProgramData ("Microsoft\Crypto\RSA\MachineKeys\" + $key.Key.UniqueName) }
    } else { $path = Join-Path $env:ProgramData ("Microsoft\Crypto\RSA\MachineKeys\" + $key.CspKeyContainerInfo.UniqueKeyContainerName) }
    $acl = Get-Acl -LiteralPath $path
    $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($Account, "Read", "Allow")))
    Set-Acl -LiteralPath $path -AclObject $acl
}

if (-not $Upgrade) {
    # ------------------------------------------------------------ 5. Certificate
    Step "Preparing HTTPS certificate for $PublicHostName"
    $now = Get-Date
    if ($CertificateThumbprint) {
        $CertificateThumbprint = ($CertificateThumbprint -replace '[^0-9A-Fa-f]', '').ToUpper()
        $cert = Get-Item -LiteralPath "Cert:\LocalMachine\My\$CertificateThumbprint" -ErrorAction SilentlyContinue
        if (-not $cert -or -not $cert.HasPrivateKey) { Fail "Certificate $CertificateThumbprint not found in LocalMachine\My with a private key." }
    } else {
        $cert = Get-ChildItem Cert:\LocalMachine\My | Where-Object {
            $_.HasPrivateKey -and $_.NotAfter -gt $now -and $_.NotBefore -le $now -and
            (@($_.DnsNameList | ForEach-Object { $_.Unicode }) | Where-Object { $_ -eq $PublicHostName -or ($_.StartsWith("*.") -and $PublicHostName.EndsWith($_.Substring(1))) })
        } | Sort-Object NotAfter -Descending | Select-Object -First 1
        if ($cert) { Log "    Found existing certificate $($cert.Thumbprint) ($($cert.Subject), expires $($cert.NotAfter.ToString('d')))" }
        elseif ($SelfSignedCertificate -or $interactive) {
            Log "    No certificate for $PublicHostName found - creating a self-signed certificate (agents pin it automatically; AutomateCert can replace it later)." "Yellow"
            $cert = New-SelfSignedCertificate -DnsName $PublicHostName -CertStoreLocation Cert:\LocalMachine\My -NotAfter $now.AddYears(2) -KeyAlgorithm RSA -KeyLength 2048 -KeyExportPolicy NonExportable -FriendlyName "AutomateCert console (self-signed)"
        } else { Fail "No certificate for $PublicHostName in LocalMachine\My. Import one, pass -CertificateThumbprint, or use -SelfSignedCertificate." }
        $CertificateThumbprint = $cert.Thumbprint
    }
    Grant-KeyRead -Thumbprint $CertificateThumbprint -Account $ServiceAccount
    Log "    Using certificate $CertificateThumbprint; service account granted key read."

    # ------------------------------------------------------------ 6. SQL
    Step "Preparing database [$SqlDatabase] on $SqlServer"
    $serverHost = ($SqlServer -split '[\\,]')[0].Trim()
    $sqlIsLocal = @(".", "(local)", "localhost", "127.0.0.1", $env:COMPUTERNAME.ToLowerInvariant()) -contains $serverHost.ToLowerInvariant()
    function New-Cs([string]$Database, [bool]$Integrated, [int]$Timeout = 15) {
        $b = New-Object System.Data.SqlClient.SqlConnectionStringBuilder
        $b["Data Source"] = $SqlServer; $b["Initial Catalog"] = $Database
        if ($Integrated) { $b["Integrated Security"] = $true } else { $b["User ID"] = $SqlUser; $b["Password"] = $SqlPassword }
        $b["Encrypt"] = $true; $b["TrustServerCertificate"] = $true; $b["Connect Timeout"] = $Timeout
        return $b.ConnectionString
    }
    $integrated = $SqlAuth -eq "Windows"
    $adminCs = New-Cs "master" $integrated
    $serviceCs = New-Cs $SqlDatabase $integrated 30
    $sqlLogin = $null
    if ($integrated) {
        if ($sqlIsLocal) { $sqlLogin = $ServiceAccount }
        else {
            $domain = (Get-CimInstance Win32_NTDomain | Where-Object { $_.DnsForestName } | Select-Object -First 1).DomainName
            if (-not $domain) { $domain = $env:USERDOMAIN }
            $sqlLogin = "$domain\$($env:COMPUTERNAME)$"
            Log "    Remote SQL with Windows authentication: granting access to computer account $sqlLogin" "Yellow"
        }
    }
    $conn = New-Object System.Data.SqlClient.SqlConnection $adminCs
    try { $conn.Open() } catch { Fail "Cannot connect to SQL Server '$SqlServer': $($_.Exception.Message)" }
    try {
        $db = $SqlDatabase.Replace("]", "]]")
        $sql = "IF DB_ID(N'$SqlDatabase') IS NULL CREATE DATABASE [$db];"
        if ($sqlLogin) {
            $l = $sqlLogin.Replace("]", "]]"); $ln = $sqlLogin.Replace("'", "''")
            $sql += "`nIF SUSER_ID(N'$ln') IS NULL CREATE LOGIN [$l] FROM WINDOWS;"
            $sql += "`nEXEC('USE [$db]; IF USER_ID(N''$($ln.Replace("'", "''"))'') IS NULL CREATE USER [$l] FOR LOGIN [$l]; ALTER ROLE db_datareader ADD MEMBER [$l]; ALTER ROLE db_datawriter ADD MEMBER [$l];');"
        }
        $cmd = $conn.CreateCommand(); $cmd.CommandText = $sql; $cmd.CommandTimeout = 120
        [void]$cmd.ExecuteNonQuery()
    } catch { Fail "Database preparation failed (the installing account needs sysadmin, or dbcreator + securityadmin): $($_.Exception.Message)" }
    finally { $conn.Dispose() }
    Log "    Database ready; service login: $(if ($sqlLogin) { "$sqlLogin (db_datareader, db_datawriter)" } else { "SQL login $SqlUser" })"

    # ------------------------------------------------------------ 7. Configuration
    Step "Writing configuration"
    $publicUrl = if ($HttpsPort -eq 443) { "https://$PublicHostName" } else { "https://${PublicHostName}:$HttpsPort" }
    $config = [ordered]@{
        ConnectionStrings = [ordered]@{ AutomateCert = $serviceCs }
        App = [ordered]@{ PublicUrl = $publicUrl }
        Server = [ordered]@{ HttpsPort = $HttpsPort; CertificateThumbprint = $CertificateThumbprint; CertificateHost = $PublicHostName }
        Database = [ordered]@{ AutoCreate = $false }
        DataProtection = [ordered]@{ KeysDirectory = $keysDir }
        Smtp = [ordered]@{ Host = "$SmtpHost"; Port = $SmtpPort; StartTls = $SmtpStartTls; UseAuthentication = [bool]$SmtpUser; Username = "$SmtpUser"; Password = "$SmtpPassword"; FromAddress = "$SmtpFrom"; FromName = $SmtpFromName }
    }
    $config | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $settingsPath -Encoding UTF8
    & icacls.exe $settingsPath /inheritance:r /grant:r "*S-1-5-18:F" "*S-1-5-32-544:F" "${ServiceAccount}:R" | Out-Null
    $initCs = $serviceCs
} else {
    Step "Upgrade: keeping existing configuration and keys"
    if (-not (Test-Path $settingsPath)) { Fail "No existing configuration at $settingsPath - run a full install instead." }
    $existingCfg = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
    $initCs = [string]$existingCfg.ConnectionStrings.AutomateCert
    $publicUrl = [string]$existingCfg.App.PublicUrl
    $HttpsPort = [int]$existingCfg.Server.HttpsPort
    $InstallLocalAgent = $false
}

# ---------------------------------------------------------------- 8. Schema, administrator, defaults, local agent token
Step "Creating/upgrading database schema$(if (-not $Upgrade) { ', administrator and free Let''s Encrypt issuers' })"
$agentToken = $null
Push-Location $InstallDir
try {
    $env:AUTOMATECERT_INIT_PASSWORD = $AdminPassword
    $env:ConnectionStrings__AutomateCert = $initCs
    $initArgs = @("--init", "--Init:Org=$OrganizationName", "--Init:AdminEmail=$AdminEmail", "--Init:AdminName=$AdminName")
    if ($AlertEmails) { $initArgs += "--Init:AlertEmails=$AlertEmails" }
    if ($RequireMfa) { $initArgs += "--Init:RequireMfa=true" }
    if ($InstallLocalAgent) { $initArgs += "--Init:AgentToken=true" }
    $out = & $exe @initArgs 2>&1
    $code = $LASTEXITCODE
    foreach ($line in $out) {
        $s = "$line"
        if ($s -like "AUTOMATECERT-AGENT-TOKEN:*") { $agentToken = $s.Substring("AUTOMATECERT-AGENT-TOKEN:".Length).Trim(); continue }
        Add-Content -LiteralPath $logFile -Value "    $s"
        if ($s -like "AUTOMATECERT-INIT*") { Log "    $s" }
    }
    if ($code -ne 0) { Fail "Initialization failed (exit code $code). Details in the log file." }
} finally {
    Remove-Item Env:\AUTOMATECERT_INIT_PASSWORD -ErrorAction SilentlyContinue
    Remove-Item Env:\ConnectionStrings__AutomateCert -ErrorAction SilentlyContinue
    Pop-Location
}

# ---------------------------------------------------------------- 9. Firewall + start
if ($OpenFirewall) {
    Step "Opening firewall port $HttpsPort"
    Get-NetFirewallRule -DisplayName "AutomateCert Server (HTTPS)" -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    New-NetFirewallRule -DisplayName "AutomateCert Server (HTTPS)" -Direction Inbound -Protocol TCP -LocalPort $HttpsPort -Action Allow -Profile Domain, Private | Out-Null
}
Step "Starting service"
Start-Service $ServiceName
(Get-Service $ServiceName).WaitForStatus("Running", [TimeSpan]::FromSeconds(60))
Step "Verifying https://localhost:$HttpsPort/healthz"
$ok = $false
$prev = [System.Net.ServicePointManager]::ServerCertificateValidationCallback
[System.Net.ServicePointManager]::ServerCertificateValidationCallback = { $true }
[System.Net.ServicePointManager]::SecurityProtocol = [System.Net.ServicePointManager]::SecurityProtocol -bor [System.Net.SecurityProtocolType]::Tls12
for ($i = 0; $i -lt 30 -and -not $ok; $i++) { try { $r = Invoke-WebRequest -Uri "https://localhost:$HttpsPort/healthz" -UseBasicParsing -TimeoutSec 5; $ok = $r.StatusCode -eq 200 } catch { Start-Sleep -Seconds 2 } }
[System.Net.ServicePointManager]::ServerCertificateValidationCallback = $prev
if (-not $ok) { Fail "The service started but did not answer on port $HttpsPort. Check Event Viewer > Application (source AutomateCert.Server)." }

# ---------------------------------------------------------------- 10. Agent on this server
if ($InstallLocalAgent -and $agentToken) {
    Step "Installing the AutomateCert agent on this server (manages the console's own certificate and local services)"
    $zip = Join-Path $InstallDir "agent\AutomateCert.Agent.zip"
    if (Test-Path $zip) {
        $tmp = Join-Path $env:TEMP ("AutomateCertAgent-" + [guid]::NewGuid().ToString("N"))
        Expand-Archive -LiteralPath $zip -DestinationPath $tmp -Force
        Get-ChildItem $tmp -Recurse | Unblock-File
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $tmp "Install-AutomateCertAgent.ps1") -ServerUrl "https://localhost:$HttpsPort" -EnrollmentToken $agentToken -PinServerCertificate | ForEach-Object { Add-Content -LiteralPath $logFile -Value "    $_" }
        if ($LASTEXITCODE -eq 0) { Log "    Local agent installed and enrolled." } else { Log "    Local agent installation did not complete (exit $LASTEXITCODE). Use Servers > Connect servers later." "Yellow" }
        Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
    } else { Log "    Agent package not found in $InstallDir\agent; skipping." "Yellow" }
}

Log ""
Log "AutomateCert is running." "Green"
Log "  Console:        $publicUrl" "Green"
if (-not $Upgrade) { Log "  Administrator:  $AdminEmail" "Green" }
Log "  Service:        $ServiceName ($ServiceAccount)"
Log "  Configuration:  $settingsPath"
Log "  Keys (back up with the database!): $keysDir"
Log "  Log file:       $logFile"
exit 0
}
catch { Fail $_.Exception.Message }
