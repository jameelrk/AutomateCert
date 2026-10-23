# Developer setup and test plan (1.5)

## 1. Dev environment
1. Visual Studio 2026 (or the .NET 10 SDK) and SQL Server (Developer or Express).
2. `Get-ChildItem <repo> -Recurse | Unblock-File`, then `dotnet dev-certs https --trust`.
3. Start **AutomateCert.Server** (F5) → `https://localhost:7443`. `AutomateCert_Dev` is created or upgraded automatically, and existing data is kept. Expected log line: `Database schema verified/upgraded`.
4. SMTP credentials for dev go in user secrets:
   ```powershell
   dotnet user-secrets set "Smtp:UseAuthentication" "true" --project src\AutomateCert.Server
   dotnet user-secrets set "Smtp:Username" "<user>" --project src\AutomateCert.Server
   dotnet user-secrets set "Smtp:Password" "<password>" --project src\AutomateCert.Server
   ```
5. Agent on the dev machine: `deploy\build.ps1`, then **Servers → Connect servers** → run the one-liner elevated. `localhost` works only for an agent on the same machine.

## 2. Build release artifacts
```powershell
powershell -ExecutionPolicy Bypass -File .\deploy\build.ps1               # artifacts\
winget install --id JRSoftware.InnoSetup -e                                # once
powershell -ExecutionPolicy Bypass -File .\installer\build-installers.ps1  # dist\AutomateCertServer-Setup-1.5.0.exe, AutomateCertAgent-Setup-1.5.0.exe
```

## 3. Test plan
Use a clean Windows Server VM for installer tests, and a real public domain (for example `test.example.com`) for public-CA tests.

| # | Area | Test | Expected |
|---|---|---|---|
| 1 | Branding | Sign-in page, sidebar, favicon, light/dark | Logo, favicon and theme colours; no layout breaks at 1280 px and on mobile |
| 2 | Auth | 5 wrong passwords | Locked 15 min; audit `auth.locked` |
| 3 | Auth | Authenticator 2-step, sign out/in, recovery code | Code works; recovery code works once |
| 4 | Users | Admin adds an Operator with a temporary password | First sign-in forces a password change |
| 5 | Users | Change a role while the user is signed in | Takes effect on the next request |
| 6 | Roles | Viewer opens Automation; Approver approves | Viewer sees no action buttons; Approver can approve but not edit |
| 7 | Approvals | Requester tries to approve own job; second user rejects | Blocked; Reject cancels the job (audited) |
| 8 | Alerts | Send test email; Send summary now | Both arrive; summary shows coverage, expiries, failures |
| 9 | Issuers | Let's Encrypt prod and staging present after install | Yes, without manual input |
| 10 | Issuance | LE staging + Cloudflare + IIS target | Succeeded; binding updated; success email |
| 11 | Internal names | `x.local` with a public CA | Blocked with an AD CS suggestion |
| 12 | Adoption | Adopt an IIS cert → approve renewal | Every binding of the old thumbprint replaced; log lists each |
| 13 | Rollback | Wrong verify host | Failed, rolled back, critical email, Diagnose explains |
| 14 | DNS | Azure DNS / Route 53 / Windows DNS provider **Test** | Test record created and deleted |
| 15 | CA accounts | DigiCert API key → Sync | Balance and currency shown; low-balance alert below threshold |
| 16 | Coverage | Issuer coverage end in 25 days | Warning email once (30-day step) |
| 17 | Endpoints | Discover a /28 with 443; add `expired.badssl.com` | TLS services added; expired / untrusted flagged |
| 18 | CT | Add your domain in Settings → CT sync | Unknown certificates listed; Acknowledge works |
| 19 | Compliance | Self-signed SHA-1 or 1024-bit test cert on a server | Flagged with severity |
| 20 | CRM & AD FS | Lab AD FS + CRM: CRM pack, then token pack in stage mode | Claims cert set, iisreset, RP refreshed; token cert secondary, then promoted on schedule |
| 21 | Autopilot / AI | Autopilot actions; Copilot "what expires in 30 days" | Actions run with the user's role; answers match inventory |
| 22 | Reports | CSV exports | Open in Excel; no formula injection (cells starting with = are prefixed) |
| 23 | Installer | Fresh VM with "Install SQL Express" and "agent on this server" | Console reachable; local agent online; web first-run setup closed |
| 24 | Installer | Port 443 already used by IIS | Setup stops early with a clear message |
| 25 | Upgrade | Run Setup again | Pages skipped; data and keys kept |
| 26 | Security page | Settings → Security | Score and checks reflect the configuration |
