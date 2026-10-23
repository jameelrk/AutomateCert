<#
.SYNOPSIS
  Builds the customer installers into .\dist (requires Inno Setup 6: winget install JRSoftware.InnoSetup):
    AutomateCertServer-Setup-<ver>.exe   wizard: host name, certificate, SQL (or install SQL Express), admin, SMTP, options
    AutomateCertAgent-Setup-<ver>.exe    wizard or silent: /VERYSILENT /SERVERURL="https://..." /TOKEN="..." [/PIN=1]
  Optional code signing: set $env:AUTOMATECERT_SIGN_THUMBPRINT to your code-signing certificate thumbprint (signtool in PATH).
#>
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe", "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 not found. Install it (winget install JRSoftware.InnoSetup) and run again." }
[xml]$props = Get-Content (Join-Path $root "Directory.Build.props")
$version = $props.Project.PropertyGroup.Version
$dist = Join-Path $root "dist"
New-Item -ItemType Directory -Force -Path $dist | Out-Null

function Sign([string]$Path) {
    if (-not $env:AUTOMATECERT_SIGN_THUMBPRINT) { return }
    & signtool.exe sign /sha1 $env:AUTOMATECERT_SIGN_THUMBPRINT /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 $Path
    if ($LASTEXITCODE -ne 0) { throw "Signing failed for $Path" }
}

& (Join-Path $root "deploy\build.ps1")
Sign "$root\artifacts\agent\AutomateCert.Agent.exe"; Sign "$root\artifacts\server\AutomateCert.Server.exe"
if ($env:AUTOMATECERT_SIGN_THUMBPRINT) {
    # Re-package the agent so the console's download and one-line installer serve the signed binary.
    Compress-Archive -Path "$root\artifacts\agent\*" -DestinationPath "$root\artifacts\AutomateCert.Agent.zip" -Force
    Copy-Item "$root\artifacts\AutomateCert.Agent.zip" "$root\artifacts\server\agent\" -Force
}
& $iscc "/DAppVersion=$version" "/O$dist" (Join-Path $PSScriptRoot "AutomateCertAgent.iss")
if ($LASTEXITCODE -ne 0) { throw "Agent installer build failed." }
Sign "$dist\AutomateCertAgent-Setup-$version.exe"
Copy-Item "$dist\AutomateCertAgent-Setup-$version.exe" "$dist\AutomateCertAgent-Setup.exe" -Force
Copy-Item "$dist\AutomateCertAgent-Setup.exe" "$root\artifacts\server\agent\" -Force

& $iscc "/DAppVersion=$version" "/O$dist" (Join-Path $PSScriptRoot "AutomateCertServer.iss")
if ($LASTEXITCODE -ne 0) { throw "Server installer build failed." }
Sign "$dist\AutomateCertServer-Setup-$version.exe"
Write-Host ""
Write-Host "Installers in $dist" -ForegroundColor Green
Get-ChildItem $dist -Filter *.exe | ForEach-Object { Write-Host ("  {0}  ({1:N1} MB)" -f $_.Name, ($_.Length / 1MB)) }
