# AutomateCert — administrator guide

## 1. Pages at a glance
| Page | Use it for |
|---|---|
| **Overview** | Health grade, security score, Autopilot top actions, vendors, coverage, expiry forecast, renewals, next expiries, AI briefing |
| **Autopilot** | Every recommendation, highest impact first, with one-click actions (adopt, diagnose, approve, open) |
| **Certificates** | Inventory with vendor, server, bindings, key, compliance issues; filter and export CSV; **Adopt**; free certificate |
| **Endpoints** | Agentless TLS monitoring; paste host names or **discover** a network (/22 max) |
| **Transparency** | Certificates issued for your domains in public CT logs; unknown ones are flagged |
| **Compliance** | Weak keys and signatures, long lifetimes, self-signed, wildcards, missing SAN, untrusted chains, legacy TLS |
| **Automation** | Profiles: names, CA, DNS, server, key, threshold, approval, maintenance window, deployment steps, next renewal |
| **CRM & AD FS** | Farm, WAP and CRM servers, guided packs, active identity automations, manual runbook |
| **Jobs & approvals** | Every renewal, deployment, staged step and follow-up; approve, reject, DNS ready, run now, cancel, log, ✦ Diagnose |
| **Servers** | Agents, roles, versions, online state; connect (one server or fleet); discover; revoke |
| **CAs & DNS** | Certificate authorities (coverage dates), CA accounts and balances, DNS providers (test, CNAME check) |
| **Logs & reports** | Audit trail, system log (admins), CSV exports |
| **Settings** | Security centre, organisation alerts, CT domains, session length, require two-step, AI assistant, users and roles, my account |
| **About & legal** | Version, notices, disclaimers, support |

Press **Ctrl+K** anywhere to jump to a page, run an action or find a certificate.

## 2. Users and roles
| Role | Can |
|---|---|
| **Admin** | Everything: users, CAs, DNS, CA accounts, servers, security, AI |
| **Operator** | Create/adopt automations, renew, deploy, approve, endpoints, discovery, DNS tests |
| **Approver** | Read everything; approve or reject changes (change board, security team) |
| **Viewer** | Read-only dashboards, inventory and reports (auditors, management) |

**Add a user** (Settings → Users & roles → Add user). AutomateCert generates a temporary password and shows it once. Share it over a separate channel. An invitation email (without the password) is sent if SMTP works. The user must choose a new password at first sign-in, and must set up two-step verification if the organisation requires it.

**Safeguards:** you can't remove the last active Admin or your own Admin role. The requester of a change can't approve it. Disabled users are signed out on their next request.

## 3. Alerts and reports
- **Expiry ladder:** default 30/14/7/3/1 days, one email per certificate per threshold. Covers server certificates and endpoints.
- **Operational:** failed issuance or deployment (with rollback status), manual DNS needed, approval waiting, agent offline over 15 minutes, CA subscription coverage ending (60/30/7 days), low CA balance, unknown CT certificates.
- **Channels:** email (HTML) and Teams/Slack webhook.
- **Reports:** CSV of certificates, endpoints and audit (Logs & reports). These open safely in Excel because formula injection is neutralised.

## 4. Automation recipes
| Scenario | Profile |
|---|---|
| Public website on IIS | Let's Encrypt or commercial CA · DNS provider · step *IIS HTTPS binding* (site + host name) with TLS verify |
| Existing certificate, zero risk | **Adopt** → *Replace everywhere* → approval on |
| Exchange | RSA, CAPI storage on older builds · *Exchange Server* (IIS,SMTP) |
| SQL Server TLS (internal name) | **AD CS** · *SQL Server TLS* · maintenance window |
| RDP / RD Gateway | AD CS or public · *RDP listener* / *RD Gateway* |
| AD FS / CRM | Use the guided packs (see `CRM-ADFS.md`) |
| Application loading the cert at start-up | Add *Restart a Windows service* |

**Renewal timing:** set as a percentage of the remaining lifetime. 33% ≈ 33 days before expiry on a 100-day certificate. Disruptive steps start only inside the maintenance window, unless the certificate expires within 3 days.

## 5. When something fails
1. Open **Jobs** → **✦ Diagnose**. Built-in rules explain the common causes, and the AI adds a root-cause analysis if configured.
2. The previous certificate is still bound (rollback), and retries back off from 30 minutes up to 24 hours.
3. Fix the cause (DNS record, CAA, EAB, rights), then click **Renew now** on the profile.

## 6. AI assistant
Settings → AI assistant: Azure OpenAI (v1 endpoint + deployment name), OpenAI, or any OpenAI-compatible endpoint (Ollama, vLLM, LiteLLM). The AI only reads tenant-scoped data and **proposes** actions as buttons; it never executes anything. Logs are redacted before they are sent. Verify AI output before acting on it.
