<#
.SYNOPSIS
  Fails if the repository contains anything that must never be committed: private keys, certificates,
  connection strings with passwords, filled-in passwords/API keys, or files that hold runtime secrets.
  Usage:  powershell -ExecutionPolicy Bypass -File .\deploy\check-secrets.ps1
#>
$root = Split-Path $PSScriptRoot -Parent
$patterns = @(
    '-----BEGIN [A-Z ]*PRIVATE KEY-----',
    'AKIA[0-9A-Z]{16}',
    'sk-[A-Za-z0-9]{20,}',
    'Password=[^;"\s<]{3,}',
    '"Password"\s*:\s*"[^"<]+"',
    '"ApiKey"\s*:\s*"[^"<]+"',
    '"Host"\s*:\s*"smtp\.[^"]+"'
)
$skip = '\\(bin|obj|artifacts|dist|out|\.git|\.vs)\\'
$files = Get-ChildItem $root -Recurse -File | Where-Object { $_.FullName -notmatch $skip -and $_.Extension -notin '.png', '.ico', '.bmp', '.exe', '.dll', '.zip' -and $_.Name -ne 'check-secrets.ps1' }
$hits = foreach ($f in $files) { Select-String -Path $f.FullName -Pattern $patterns -AllMatches | Where-Object { $_.Line -notmatch 'example\.com|contoso|placeholder|<password>' } }
$blocked = $files | Where-Object { $_.Extension -in '.pfx', '.p12', '.key', '.pem' -or $_.Name -in 'appsettings.Production.json', 'answers.json', 'secrets.json' }
if ($hits -or $blocked) {
    Write-Host "Possible secrets found:" -ForegroundColor Red
    $hits | ForEach-Object { Write-Host ("  {0}:{1}  {2}" -f $_.Path.Replace($root, '.'), $_.LineNumber, $_.Line.Trim()) -ForegroundColor Red }
    $blocked | ForEach-Object { Write-Host ("  {0}" -f $_.FullName.Replace($root, '.')) -ForegroundColor Red }
    exit 1
}
Write-Host "No secrets found." -ForegroundColor Green
