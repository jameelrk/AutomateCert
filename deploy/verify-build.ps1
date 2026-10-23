<#
.SYNOPSIS
  Restores and builds the whole solution in Release and prints an error/warning summary.
  Usage:  powershell -ExecutionPolicy Bypass -File .\deploy\verify-build.ps1
#>
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw "The .NET 10 SDK is required (dotnet not found)." }
Push-Location $root
try {
    $log = Join-Path $env:TEMP "automatecert-build.log"
    dotnet restore AutomateCert.sln --nologo | Out-Null
    dotnet build AutomateCert.sln -c Release --no-restore --nologo 2>&1 | Tee-Object -FilePath $log | Out-Null
    $code = $LASTEXITCODE
    $errors = @(Select-String -Path $log -Pattern ": error " | ForEach-Object { $_.Line.Trim() } | Sort-Object -Unique)
    $warnings = @(Select-String -Path $log -Pattern ": warning " | ForEach-Object { $_.Line.Trim() } | Sort-Object -Unique)
    Write-Host ""
    Write-Host ("Errors: {0}   Warnings: {1}" -f $errors.Count, $warnings.Count) -ForegroundColor $(if ($errors.Count) { "Red" } else { "Green" })
    $errors | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    $warnings | Select-Object -First 20 | ForEach-Object { Write-Host "  $_" -ForegroundColor Yellow }
    Write-Host "Full log: $log"
    exit $code
} finally { Pop-Location }
