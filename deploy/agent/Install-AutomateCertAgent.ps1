<#
.SYNOPSIS
  Installs (or upgrades) the AutomateCert agent as a Windows service and enrolls it with the AutomateCert console.
  Works from the agent package / AutomateCertAgent-Setup.exe or from the source repository (builds the agent if needed).
  For https://localhost the console certificate is pinned automatically.
.EXAMPLE
  .\Install-Agent.cmd -ServerUrl "https://automatecert.contoso.com" -EnrollmentToken "<token>" [-PinServerCertificate]
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ServerUrl,
    [Parameter(Mandatory = $true)][string]$EnrollmentToken,
    [string]$ServerCertificateThumbprint = "",
    [switch]$PinServerCertificate,
    [string]$InstallDir = "$env:ProgramFiles\AutomateCert\Agent",
    [string]$DataDir = "$env:ProgramData\AutomateCert\Agent"
)
$ErrorActionPreference = "Stop"
$serviceName = "AutomateCertAgent"
function Write-Step([string]$Text) { Write-Host "==> $Text" -ForegroundColor Cyan }

$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw "Run this from an elevated PowerShell / Terminal (Run as administrator)." }
$ServerUrl = $ServerUrl.Trim().TrimEnd("/")
if (-not $ServerUrl.StartsWith("https://", [StringComparison]::OrdinalIgnoreCase)) { throw "ServerUrl must start with https://" }
$uri = [Uri]$ServerUrl
$EnrollmentToken = $EnrollmentToken.Trim()
if ($EnrollmentToken -notmatch '^[0-9a-fA-F]{64}$') { throw "EnrollmentToken looks wrong. Copy it again from Servers > Connect servers." }

function Find-AgentFolder {
    if (Test-Path (Join-Path $PSScriptRoot "AutomateCert.Agent.exe")) { return $PSScriptRoot }
    $repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\.."))
    $built = Join-Path $repo "artifacts\agent"
    if (Test-Path (Join-Path $built "AutomateCert.Agent.exe")) { return $built }
    $project = Join-Path $repo "src\AutomateCert.Agent\AutomateCert.Agent.csproj"
    if (-not (Test-Path $project)) { throw "AutomateCert.Agent.exe not found. Use AutomateCertAgent-Setup.exe or the one-line installer from the console." }
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw "The agent is not built and the .NET SDK was not found." }
    Write-Step "Agent not built yet - publishing it now"
    & dotnet publish $project -c Release -o $built --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit code $LASTEXITCODE)." }
    return $built
}
$sourceDir = Find-AgentFolder
Write-Step "Using agent from $sourceDir"

function Get-ServerThumbprint([Uri]$Target) {
    $port = if ($Target.Port -gt 0) { $Target.Port } else { 443 }
    $tcp = New-Object System.Net.Sockets.TcpClient
    try {
        $tcp.Connect($Target.Host, $port)
        $callback = [System.Net.Security.RemoteCertificateValidationCallback] { param($s, $c, $ch, $e) $true }
        $ssl = New-Object System.Net.Security.SslStream($tcp.GetStream(), $false, $callback)
        $ssl.AuthenticateAsClient($Target.Host, $null, [System.Security.Authentication.SslProtocols]::Tls12, $false)
        $cert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($ssl.RemoteCertificate)
        $ssl.Dispose()
        return $cert.Thumbprint
    } finally { $tcp.Close() }
}
Write-Step "Checking connection to $ServerUrl"
try { $presented = Get-ServerThumbprint $uri } catch { throw "Cannot reach $ServerUrl ($($_.Exception.Message)). Check DNS, firewall and that the AutomateCert server is running." }
$isLoopback = @("localhost", "127.0.0.1", "::1", "[::1]") -contains $uri.Host.ToLowerInvariant()
if (-not $ServerCertificateThumbprint -and ($PinServerCertificate -or $isLoopback)) { $ServerCertificateThumbprint = $presented; Write-Host "    Pinned console certificate $presented" -ForegroundColor DarkGray }

$existing = Get-Service $serviceName -ErrorAction SilentlyContinue
if ($existing -and $existing.Status -ne "Stopped") { Write-Step "Stopping existing service"; Stop-Service $serviceName -Force; (Get-Service $serviceName).WaitForStatus("Stopped", [TimeSpan]::FromSeconds(30)) }

function Set-RestrictedAcl([string]$Path, [bool]$IsDirectory) {
    if ($IsDirectory) { $acl = New-Object System.Security.AccessControl.DirectorySecurity; $inherit = "ContainerInherit,ObjectInherit" }
    else { $acl = New-Object System.Security.AccessControl.FileSecurity; $inherit = "None" }
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($id in @("NT AUTHORITY\SYSTEM", "BUILTIN\Administrators")) { $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($id, "FullControl", $inherit, "None", "Allow"))) }
    Set-Acl -LiteralPath $Path -AclObject $acl
}
Write-Step "Installing files to $InstallDir"
New-Item -ItemType Directory -Force -Path $InstallDir, $DataDir | Out-Null
Set-RestrictedAcl $DataDir $true
if ([IO.Path]::GetFullPath($sourceDir).TrimEnd("\") -ne [IO.Path]::GetFullPath($InstallDir).TrimEnd("\")) {
    Get-ChildItem -LiteralPath $sourceDir -File | Where-Object { $_.Name -ne "appsettings.json" } | Copy-Item -Destination $InstallDir -Force
}
Get-ChildItem -LiteralPath $InstallDir -Recurse -File | Unblock-File
if (-not [Diagnostics.EventLog]::SourceExists("AutomateCert.Agent")) { New-EventLog -LogName Application -Source "AutomateCert.Agent" }
$cred = Join-Path $DataDir "agent.cred"
if (Test-Path $cred) { Remove-Item $cred -Force; Write-Host "    Previous enrollment removed (revoke the old entry in the console if it remains)." -ForegroundColor DarkGray }
$config = [ordered]@{
    Agent = [ordered]@{ ServerUrl = $ServerUrl; EnrollmentToken = $EnrollmentToken; ServerCertificateThumbprint = $ServerCertificateThumbprint; PollSeconds = 30; InventoryHours = 6; DataDirectory = $DataDir }
    Logging = [ordered]@{ LogLevel = [ordered]@{ Default = "Information"; Microsoft = "Warning" } }
}
$configPath = Join-Path $InstallDir "appsettings.json"
$config | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $configPath -Encoding UTF8
Set-RestrictedAcl $configPath $false

$exePath = Join-Path $InstallDir "AutomateCert.Agent.exe"
if (-not (Get-Service $serviceName -ErrorAction SilentlyContinue)) {
    Write-Step "Creating Windows service $serviceName"
    New-Service -Name $serviceName -BinaryPathName "`"$exePath`"" -DisplayName "AutomateCert Agent" -Description "AutomateCert certificate automation agent" -StartupType Automatic | Out-Null
} else { & sc.exe config $serviceName binPath= "`"$exePath`"" start= auto | Out-Null }
& sc.exe failure $serviceName reset= 86400 actions= restart/60000/restart/60000/restart/300000 | Out-Null
Write-Step "Starting service"
Start-Service $serviceName
(Get-Service $serviceName).WaitForStatus("Running", [TimeSpan]::FromSeconds(30))
Write-Step "Waiting for enrollment"
$deadline = (Get-Date).AddSeconds(60); $enrolled = $false
while ((Get-Date) -lt $deadline) { if (Test-Path $cred) { $enrolled = $true; break }; Start-Sleep -Seconds 2 }
if ($enrolled) {
    $config.Agent.EnrollmentToken = ""
    $config | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $configPath -Encoding UTF8
    Write-Host ""; Write-Host "AutomateCert agent installed and enrolled. The server appears under Servers in the console." -ForegroundColor Green
    exit 0
}
Write-Host ""; Write-Host "Service is running but enrollment has not completed yet." -ForegroundColor Yellow
Write-Host "Check: Event Viewer > Windows Logs > Application (source AutomateCert.Agent)."
exit 2
