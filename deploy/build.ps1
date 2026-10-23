<#
.SYNOPSIS
  Builds release artifacts into .\artifacts:
    artifacts\agent, artifacts\AutomateCert.Agent.zip         agent (self-contained single file) + installer scripts
    artifacts\server                                       self-contained server (no .NET runtime or IIS needed)
    artifacts\AutomateCert-Server-<ver>.zip                    script-based server package (server\ + Install-Server.cmd)
  Usage:  powershell -ExecutionPolicy Bypass -File .\deploy\build.ps1
#>
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root "artifacts"
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw "The .NET 10 SDK is required (dotnet not found)." }
[xml]$props = Get-Content (Join-Path $root "Directory.Build.props")
$version = $props.Project.PropertyGroup.Version
Write-Host "==> Cleaning $out" -ForegroundColor Cyan
Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue

Write-Host "==> Publishing agent $version" -ForegroundColor Cyan
dotnet publish "$root\src\AutomateCert.Agent\AutomateCert.Agent.csproj" -c Release -o "$out\agent" --nologo
if ($LASTEXITCODE -ne 0) { throw "Agent publish failed." }
Copy-Item "$PSScriptRoot\agent\*.ps1", "$PSScriptRoot\agent\*.cmd" "$out\agent"
Remove-Item "$out\agent\*.pdb" -ErrorAction SilentlyContinue
Copy-Item "$root\THIRD-PARTY-NOTICES.txt" "$out\agent\"
Compress-Archive -Path "$out\agent\*" -DestinationPath "$out\AutomateCert.Agent.zip" -Force

Write-Host "==> Publishing server $version (self-contained win-x64)" -ForegroundColor Cyan
dotnet publish "$root\src\AutomateCert.Server\AutomateCert.Server.csproj" -c Release -r win-x64 --self-contained true -o "$out\server" --nologo
if ($LASTEXITCODE -ne 0) { throw "Server publish failed." }
Remove-Item "$out\server\*.pdb", "$out\server\appsettings.Development.json" -ErrorAction SilentlyContinue
Copy-Item "$root\THIRD-PARTY-NOTICES.txt" "$out\server\"
New-Item -ItemType Directory -Force -Path "$out\server\agent" | Out-Null
Copy-Item "$out\AutomateCert.Agent.zip" "$out\server\agent\"
if (Test-Path "$root\dist\AutomateCertAgent-Setup.exe") { Copy-Item "$root\dist\AutomateCertAgent-Setup.exe" "$out\server\agent\" }

Write-Host "==> Packaging server" -ForegroundColor Cyan
$pkg = Join-Path $out "server-package"
New-Item -ItemType Directory -Force -Path $pkg | Out-Null
Copy-Item "$out\server" "$pkg\server" -Recurse
Copy-Item "$PSScriptRoot\server\Install-AutomateCertServer.ps1", "$PSScriptRoot\server\Uninstall-AutomateCertServer.ps1", "$PSScriptRoot\server\Install-Server.cmd" $pkg
Compress-Archive -Path "$pkg\*" -DestinationPath "$out\AutomateCert-Server-$version.zip" -Force
Write-Host ""
Write-Host "Done. Server package: $out\AutomateCert-Server-$version.zip   Agent: $out\AutomateCert.Agent.zip" -ForegroundColor Green
Write-Host "Setup.exe installers: installer\build-installers.ps1 (needs Inno Setup 6)"
