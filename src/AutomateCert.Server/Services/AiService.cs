using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AutomateCert.Server.Data;
using AutomateCert.Server.Security;
using AutomateCert.Shared;
using Microsoft.EntityFrameworkCore;

namespace AutomateCert.Server.Services;

public static class AiProviders
{
    public const string AzureOpenAI = "AzureOpenAI";
    public const string OpenAI = "OpenAI";
    public const string Custom = "Custom";
    public static readonly string[] All = [AzureOpenAI, OpenAI, Custom];
}

public sealed record AiConfig(string Provider, string Endpoint, string Model, string? ApiKey);
public sealed record Finding(string Title, string Cause, string Fix, string Confidence);

/// <summary>A one-click action proposed by Autopilot or Copilot. Never executed without a human click.</summary>
public sealed record ProposedAction(string Type, string? Id, string Label, string? Route = null);
public sealed record Recommendation(int Priority, string Severity, string Category, string Title, string Detail, ProposedAction? Action);

public static partial class RuleDiagnostics
{
    private sealed record Rule(Regex Pattern, string Title, string Cause, string Fix);
    private static Rule R(string pattern, string title, string cause, string fix) =>
        new(new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled), title, cause, fix);

    private static readonly Rule[] Rules =
    [
        R(@"rejectedIdentifier|public suffix|Public CAs only issue for public domain|internal suffix|single-label", "Internal name sent to a public CA",
            "Public CAs can only issue for registered public domains. Names like server.local, host.corp or single-label names are rejected by CA/Browser Forum rules.",
            "Switch the profile's issuer to your Microsoft AD CS, or use a public DNS name you own (e.g. app.yourdomain.com)."),
        R(@"rateLimited|too many (certificates|failed authorizations|new orders)", "CA rate limit reached",
            "The certificate authority is throttling this account or domain set.",
            "Wait for the rate-limit window (Let's Encrypt: up to 7 days for duplicate certificates). Test with STAGING; AutomateCert backs off automatically."),
        R(@"\bcaa\b", "CAA record blocks this CA", "The domain publishes a CAA record that does not authorise this CA.",
            "Add a CAA record, e.g. 0 issue \"letsencrypt.org\" / \"digicert.com\" / \"sectigo.com\" / \"globalsign.com\"."),
        R(@"externalAccountRequired|external account binding|\beab\b|unauthorized.*account", "External Account Binding / account problem",
            "Commercial ACME CAs require valid EAB credentials tied to an active product subscription.",
            "Recreate the issuer with current EAB Key ID and HMAC from the CA portal. Check the subscription has not ended (set 'Coverage ends' on the issuer)."),
        R(@"incorrect txt|no txt record|nxdomain|dns problem|unauthorized.*dns|DNS-01", "DNS validation failed",
            "The CA could not see the expected _acme-challenge TXT record on the domain's authoritative DNS.",
            "Use DNS providers → Test. With split-horizon or registrar DNS without API (e.g. Dynadot), use CNAME delegation to Cloudflare/Azure DNS."),
        R(@"should be a CNAME to", "CNAME delegation missing", "The provider uses alias mode but the _acme-challenge CNAME does not exist yet.",
            "Create the CNAME shown in the job log at your registrar/DNS host once; renewals are automatic afterwards."),
        R(@"Cloudflare|Azure DNS|Route 53|GoDaddy|REFUSED|NOTAUTH|Windows DNS|Webhook .* failed", "DNS provider API error",
            "The DNS provider rejected the change (credentials, permissions, zone not found or TSIG mismatch).",
            "Open CAs & DNS → DNS provider → Test to see the exact step that fails, then fix the credential/permission it names."),
        R(@"did not offer a DNS-01", "CA does not offer DNS-01", "This ACME directory offered other challenge types only.",
            "Pre-validate the domain in the CA portal or use a directory URL that supports DNS-01."),
        R(@"Timed out \(no progress", "Agent did not respond", "The agent was offline or stuck during the job.",
            "Check the AutomateCertAgent service (Get-Service AutomateCertAgent) and HTTPS reachability to the console, then Renew now."),
        R(@"Pending private key not found", "Private key lost between steps", "The agent was reinstalled or its data folder cleared after the CSR.",
            "Run Renew now again. Keep C:\\ProgramData\\AutomateCert\\Agent intact during upgrades."),
        R(@"did not present", "Post-deployment check failed (rolled back)", "The endpoint did not serve the new certificate after binding.",
            "Confirm the verify host resolves to this server (not a load balancer/WAF that terminates TLS) and the IIS binding host name/SNI."),
        R(@"keyset does not exist|access is denied|0x80090016|cannot find the private key", "Private key permission problem",
            "The service account cannot read the private key, or the application needs a legacy CAPI key.",
            "Set Key storage = CAPI for Exchange/older CRM builds. AutomateCert grants key read for AD FS, SQL, RDP, CRM and the console."),
        R(@"AutoCertificateRollover", "AD FS automatic rollover is on",
            "AD FS manages its own token-signing/decrypting certificates while AutoCertificateRollover is enabled.",
            "Either keep AD FS self-managed tokens (recommended) and remove the token target, or set 'Turn off AutoCertificateRollover' = true on the target."),
        R(@"Update-AdfsRelyingPartyTrust|relying party", "AD FS relying-party refresh failed",
            "AD FS could not read the relying party's federation metadata (CRM URL unreachable from AD FS, or wrong RP name).",
            "Check the RP names in AD FS Management and that https://<crm>/FederationMetadata/2007-06/FederationMetadata.xml opens from the AD FS server."),
        R(@"Microsoft\.Crm|Set-CrmSetting|ClaimsSettings|CRMAppPool", "Dynamics CRM claims step failed",
            "The CRM PowerShell snap-in or application pool was not found, or the account lacks CRM deployment-admin rights.",
            "Run the agent on a CRM Full/Front-end server; ensure the computer account (LocalSystem) is a CRM Deployment Administrator, or run the step manually from the runbook."),
        R(@"IIS site '.*' not found", "IIS site name is wrong", "The siteName parameter does not match an IIS site.",
            "Use the exact name from IIS Manager, or use 'Replace everywhere' which needs no parameters."),
        R(@"adfssrv\) not found", "AD FS is not installed here", "The AD FS template ran on a server without the AD FS role.",
            "Assign the profile to the agent on an AD FS server (one profile per farm node)."),
        R(@"Add-PSSnapin|Exchange\.Management", "Exchange tools not available", "The Exchange template must run on an Exchange server.",
            "Install the agent on the Exchange server and assign the profile to it."),
        R(@"certreq|denied by policy module|CA manager approval|CERTSRV_E", "AD CS enrollment failed", "The enterprise CA rejected or held the request.",
            "Grant the computer account Enroll on the template, allow 'Supply in the request', disable CA manager approval for automation."),
        R(@"missing SANs", "Certificate content mismatch", "The issued certificate lacks some requested names.",
            "Check each name is allowed by your CA product and validated; then renew again."),
        R(@"could not be established|remote certificate is invalid|SSL connection", "Agent cannot trust the console",
            "The agent (LocalSystem) does not trust the console's TLS certificate.",
            "Use a trusted console certificate or reinstall the agent with -PinServerCertificate (/PIN=1)."),
        R(@"Order did not become valid", "CA did not complete the order", "OV/EV orders can wait for organisation validation or manual approval at the CA.",
            "Check the order in CertCentral / SCM / Atlas and enable automatic approval for ACME requests."),
    ];

    public static List<Finding> Analyze(string? log, string? message)
    {
        var text = (message ?? "") + "\n" + (log ?? "");
        var list = Rules.Where(r => r.Pattern.IsMatch(text)).Select(r => new Finding(r.Title, r.Cause, r.Fix, "High")).ToList();
        if (list.Count == 0 && text.Contains("Failed", StringComparison.OrdinalIgnoreCase))
            list.Add(new Finding("No known signature matched", "This failure did not match a built-in rule.",
                "Read the last lines of the log. With the AI assistant enabled, the AI analysis explains it.", "Low"));
        return list;
    }

    [GeneratedRegex(@"(?i)(password|secret|token|hmac|api[-_ ]?key|authorization)\s*[:=]\s*\S+")]
    private static partial Regex SecretPattern();
    public static string Redact(string? text) => SecretPattern().Replace(text ?? "", "$1=[redacted]");
}

/// <summary>Autopilot: deterministic, explainable recommendations with one-click actions (human approves every action).</summary>
public sealed class AutopilotService(AppDb db)
{
    public async Task<List<Recommendation>> RecommendAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var list = new List<Recommendation>();
        var managed = (await db.Profiles.AsNoTracking().Where(p => p.CurrentThumbprint != null).Select(p => p.CurrentThumbprint!).ToListAsync(ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var agents = await db.Agents.AsNoTracking().Where(a => !a.Revoked).ToListAsync(ct);
        var agentNames = agents.ToDictionary(a => a.Id, a => a.Name);

        if (!await db.Issuers.AnyAsync(ct))
            list.Add(new(100, "warning", "Setup", "Add a certificate authority", "Start with free Let's Encrypt for public names, your commercial CA (DigiCert, Sectigo, GlobalSign) or internal AD CS.", new("open", null, "Add CA", "#/issuers")));
        if (agents.Count == 0)
            list.Add(new(99, "warning", "Setup", "Connect your first server", "Install the agent on a server with IIS, AD FS, Exchange, CRM or SQL to discover its certificates.", new("open", null, "Connect servers", "#/agents")));

        var certs = await db.Discovered.AsNoTracking().Where(d => d.HasPrivateKey && d.NotAfter > now.AddDays(-30)).ToListAsync(ct);
        foreach (var c in certs.Where(c => !managed.Contains(c.Thumbprint) && c.Bindings.Length > 0).OrderBy(c => c.NotAfter).Take(8))
        {
            var days = (int)Math.Floor((c.NotAfter - now).TotalDays);
            if (days > 45) continue;
            list.Add(new(days <= 0 ? 98 : days <= 7 ? 95 : days <= 14 ? 85 : 70, days <= 7 ? "critical" : "warning", "Expiry",
                days <= 0 ? $"Expired and still bound: {Cn(c.Subject)}" : $"{Cn(c.Subject)} expires in {days} days and is not automated",
                $"On {agentNames.GetValueOrDefault(c.AgentId, "?")} · {CertIntel.Vendor(c.Issuer, c.Subject)} · bound to {c.Bindings}. Adopt it: nothing changes now, and the renewal swaps it everywhere it is bound.",
                new("adopt", c.Id.ToString(), "Adopt")));
        }

        foreach (var j in (await db.Jobs.AsNoTracking().Where(j => j.Status == JobStatuses.Failed && j.UpdatedAt > now.AddDays(-7)).OrderByDescending(j => j.UpdatedAt).ToListAsync(ct))
                 .GroupBy(j => j.ProfileId).Select(g => g.First()).Take(5))
        {
            var f = RuleDiagnostics.Analyze(j.Log, j.Message).FirstOrDefault();
            list.Add(new(90, "critical", "Failure", $"{j.Type} failed{(f is null ? "" : ": " + f.Title)}", f?.Fix ?? j.Message ?? "", new("diagnose", j.Id.ToString(), "Diagnose")));
        }

        var approvals = await db.Jobs.AsNoTracking().CountAsync(j => j.Status == JobStatuses.AwaitingApproval, ct);
        if (approvals > 0) list.Add(new(88, "warning", "Approval", $"{approvals} change(s) waiting for approval", "Renewals with approval required will not run until approved.", new("open", null, "Review", "#/jobs?status=AwaitingApproval")));
        var manualDns = await db.Jobs.AsNoTracking().CountAsync(j => j.Status == JobStatuses.WaitingForManualDns, ct);
        if (manualDns > 0) list.Add(new(87, "warning", "DNS", $"{manualDns} renewal(s) waiting for manual DNS", "Create the TXT records shown on the job and click DNS ready — or add an automatic DNS provider.", new("open", null, "Open jobs", "#/jobs?status=WaitingForManualDns")));

        foreach (var a in agents.Where(a => a.LastSeenAt < now.AddMinutes(-15)).Take(5))
            list.Add(new(80, "warning", "Agents", $"Server offline: {a.Name}", $"Last seen {a.LastSeenAt:yyyy-MM-dd HH:mm} UTC. Renewals on this server are paused.", new("open", null, "Servers", "#/agents")));

        foreach (var e in await db.Endpoints.AsNoTracking().Where(e => (e.NotAfter != null && e.NotAfter < now.AddDays(21)) || e.ChainValid == false).Take(5).ToListAsync(ct))
            list.Add(new(e.NotAfter < now.AddDays(7) ? 86 : 65, e.NotAfter < now.AddDays(7) ? "critical" : "warning", "Endpoints",
                e.ChainValid == false ? $"Untrusted certificate on {e.Host}:{e.Port}" : $"{e.Host}:{e.Port} expires {e.NotAfter:yyyy-MM-dd}",
                "Network device or external site — renew on the device or move it behind a AutomateCert-managed server.", new("scan", e.Id.ToString(), "Rescan")));

        foreach (var c in certs.Where(c => managed.Contains(c.Thumbprint) || c.Bindings.Length > 0))
            foreach (var issue in CertIntel.Check(c.KeyInfo, c.SignatureAlgorithm, c.NotBefore, c.NotAfter, c.Issuer, c.Subject, c.DnsNames, c.Bindings.Length > 0, now)
                         .Where(i => i.Severity == "critical" && i.Rule != "Expired certificate").Take(3))
                list.Add(new(75, "critical", "Compliance", $"{issue.Rule}: {Cn(c.Subject)}", issue.Detail, new("open", null, "Compliance", "#/compliance")));

        var unknownCt = await db.CtCertificates.AsNoTracking().CountAsync(c => !c.Known && !c.Acknowledged && c.NotAfter > now, ct);
        if (unknownCt > 0) list.Add(new(72, "warning", "Transparency", $"{unknownCt} certificate(s) for your domains are not in the inventory", "Found in public Certificate Transparency logs. Confirm or investigate possible shadow IT.", new("open", null, "Review", "#/transparency")));

        foreach (var i in await db.Issuers.AsNoTracking().Where(i => i.CoverageEndsAt != null && i.CoverageEndsAt < now.AddDays(60)).ToListAsync(ct))
            list.Add(new(83, "warning", "Subscription", $"CA coverage ends {i.CoverageEndsAt:yyyy-MM-dd}: {i.Name}", "Renew the subscription or move automations to another CA before it ends.", new("open", null, "CAs", "#/issuers")));

        var admins = await db.Users.AsNoTracking().Where(u => !u.Disabled && (u.Role == Roles.Admin || u.Role == Roles.Operator) && !u.MfaEnabled).CountAsync(ct);
        if (admins > 0) list.Add(new(78, "warning", "Security", $"{admins} privileged user(s) without two-step verification", "Require two-step verification for the organization in Settings → Security.", new("open", null, "Security", "#/settings")));

        var profilesNoDns = await db.Profiles.AsNoTracking().Include(p => p.DnsProvider).CountAsync(p => p.DnsProvider != null && p.DnsProvider.Type == DnsProviderTypes.Manual, ct);
        if (profilesNoDns > 0) list.Add(new(55, "info", "DNS", $"{profilesNoDns} automation(s) use manual DNS", "Add Cloudflare, Azure DNS, Route 53, your own name servers (RFC 2136/Windows DNS) or CNAME delegation for fully hands-off renewals.", new("open", null, "DNS providers", "#/issuers")));

        return list.OrderByDescending(r => r.Priority).Take(25).ToList();
    }

    private static string Cn(string subject) => Regex.Match(subject, "CN=([^,]+)") is { Success: true } m ? m.Groups[1].Value : subject;
}

/// <summary>
/// AutomateCert Copilot. Bring-your-own model. READ-ONLY, tenant-scoped tools plus a propose_action tool whose output is
/// shown as buttons — the model can never execute anything itself.
/// </summary>
public sealed class AiService(AppDb db, TenantContext tenant, SecretProtector secrets, IHttpClientFactory http, AutopilotService autopilot, ILogger<AiService> log)
{
    private const int MaxToolRounds = 6;
    public List<ProposedAction> Proposals { get; } = [];

    public async Task<AiConfig?> GetConfigAsync(CancellationToken ct)
    {
        var t = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(x => x.Id == tenant.TenantId, ct);
        if (t is null || !t.AiEnabled || string.IsNullOrWhiteSpace(t.AiEndpoint) || string.IsNullOrWhiteSpace(t.AiModel)) return null;
        return new AiConfig(t.AiProvider ?? AiProviders.OpenAI, t.AiEndpoint.TrimEnd('/'), t.AiModel,
            t.AiApiKeyProtected is null ? null : secrets.Unprotect(t.AiApiKeyProtected));
    }

    public string SystemPrompt(string tenantName) => $"""
You are AutomateCert Copilot, the certificate-lifecycle agent inside AutomateCert for "{tenantName}".
Today is {DateTimeOffset.UtcNow:yyyy-MM-dd} (UTC).
Rules:
- Use the tools for every fact about certificates, endpoints, servers, CAs, profiles and jobs. Never invent data.
- You cannot change anything. When an action would help, call propose_action so the user gets a button to approve it.
- Be concise: short bullets, certificate names, vendors, dates, servers.
- Context: public TLS certificates are limited to 200 days since 15 March 2026, 100 days from 15 March 2027 and 47 days from 15 March 2029.
  Public CAs cannot issue for internal names (.local, .corp) — use AD CS. AD FS token-signing rotation must be staged (secondary → primary)
  and relying parties such as Dynamics CRM must reload metadata afterwards.
""";

    public async Task<string> ChatAsync(AiConfig cfg, JsonArray messages, bool useTools, CancellationToken ct)
    {
        for (var round = 0; round < MaxToolRounds; round++)
        {
            var body = new JsonObject { ["model"] = cfg.Model, ["messages"] = messages.DeepClone() };
            if (useTools) { body["tools"] = ToolDefinitions(); body["tool_choice"] = "auto"; }

            using var req = new HttpRequestMessage(HttpMethod.Post, cfg.Endpoint + "/chat/completions")
            { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
            if (!string.IsNullOrEmpty(cfg.ApiKey))
            {
                if (cfg.Provider == AiProviders.AzureOpenAI) req.Headers.TryAddWithoutValidation("api-key", cfg.ApiKey);
                else req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cfg.ApiKey);
            }
            using var resp = await http.CreateClient("ai").SendAsync(req, ct);
            var text = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"AI provider returned {(int)resp.StatusCode}: {(text.Length > 400 ? text[..400] : text)}");

            var message = JsonNode.Parse(text)?["choices"]?[0]?["message"] as JsonObject
                          ?? throw new InvalidOperationException("AI provider returned an unexpected response.");
            var calls = message["tool_calls"] as JsonArray;
            if (calls is null || calls.Count == 0) return message["content"]?.GetValue<string>() ?? "";

            messages.Add(message.DeepClone());
            foreach (var call in calls)
            {
                var id = call?["id"]?.GetValue<string>() ?? "";
                var name = call?["function"]?["name"]?.GetValue<string>() ?? "";
                var args = call?["function"]?["arguments"]?.GetValue<string>() ?? "{}";
                string result;
                try { result = await RunToolAsync(name, args, ct); }
                catch (Exception ex) { log.LogWarning(ex, "AI tool {Tool} failed", name); result = JsonSerializer.Serialize(new { error = ex.Message }); }
                messages.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = id, ["content"] = result });
            }
        }
        return "I needed too many steps to answer that. Please ask a more specific question.";
    }

    private static JsonNode ToolDefinitions() => JsonNode.Parse("""
[
 {"type":"function","function":{"name":"get_overview","description":"Counts of certificates, expiries, endpoints, agents, profiles and recent job results.","parameters":{"type":"object","properties":{}}}},
 {"type":"function","function":{"name":"get_recommendations","description":"Autopilot's prioritised list of risks and recommended actions.","parameters":{"type":"object","properties":{}}}},
 {"type":"function","function":{"name":"search_certificates","description":"Search certificates on servers by subject, DNS name, issuer, vendor or thumbprint; optionally only those expiring within N days.","parameters":{"type":"object","properties":{"query":{"type":"string"},"expiring_within_days":{"type":"integer"},"limit":{"type":"integer"}}}}},
 {"type":"function","function":{"name":"list_endpoints","description":"Monitored TLS endpoints with expiry, vendor, trust and name-match status.","parameters":{"type":"object","properties":{"expiring_within_days":{"type":"integer"}}}}},
 {"type":"function","function":{"name":"list_profiles","description":"Automation profiles with CA, server, targets, expiry and next renewal.","parameters":{"type":"object","properties":{}}}},
 {"type":"function","function":{"name":"list_jobs","description":"Recent jobs, optionally filtered by status.","parameters":{"type":"object","properties":{"status":{"type":"string"},"limit":{"type":"integer"}}}}},
 {"type":"function","function":{"name":"get_job","description":"Status, diagnostics and log tail of one job.","parameters":{"type":"object","properties":{"job_id":{"type":"string"}},"required":["job_id"]}}},
 {"type":"function","function":{"name":"list_agents","description":"Servers with online status, roles (IIS, ADFS, CRM, Exchange, SQL, DNS) and versions.","parameters":{"type":"object","properties":{}}}},
 {"type":"function","function":{"name":"list_ca_accounts","description":"Commercial CA accounts with balances and order counts, plus issuers and subscription coverage.","parameters":{"type":"object","properties":{}}}},
 {"type":"function","function":{"name":"propose_action","description":"Propose ONE action to the user as a button they must click. Types: adopt (certificate id), renew (profile id), approve (job id), diagnose (job id), scan (endpoint id), discover (agent id), open (route like #/issuers).","parameters":{"type":"object","properties":{"type":{"type":"string","enum":["adopt","renew","approve","diagnose","scan","discover","open"]},"id":{"type":"string"},"label":{"type":"string"},"route":{"type":"string"}},"required":["type","label"]}}}
]
""")!;

    private async Task<string> RunToolAsync(string name, string argsJson, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
        var a = doc.RootElement;
        string? Str(string p) => a.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        int? Int(string p) => a.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
        var now = DateTimeOffset.UtcNow;
        var agentNames = await db.Agents.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.Name, ct);

        object result;
        switch (name)
        {
            case "get_overview":
                result = new
                {
                    certificates = await db.Discovered.CountAsync(ct),
                    expired = await db.Discovered.CountAsync(d => d.NotAfter <= now, ct),
                    expiring30 = await db.Discovered.CountAsync(d => d.NotAfter > now && d.NotAfter <= now.AddDays(30), ct),
                    endpoints = await db.Endpoints.CountAsync(ct),
                    profiles = await db.Profiles.CountAsync(ct),
                    agents = await db.Agents.CountAsync(x => !x.Revoked, ct),
                    agentsOnline = await db.Agents.CountAsync(x => !x.Revoked && x.LastSeenAt > now.AddMinutes(-5), ct),
                    failedLast7Days = await db.Jobs.CountAsync(j => j.Status == JobStatuses.Failed && j.UpdatedAt > now.AddDays(-7), ct),
                    awaitingApproval = await db.Jobs.CountAsync(j => j.Status == JobStatuses.AwaitingApproval, ct)
                };
                break;
            case "get_recommendations":
                result = await autopilot.RecommendAsync(ct);
                break;
            case "search_certificates":
            {
                var q = db.Discovered.AsNoTracking();
                var text = Str("query");
                if (Int("expiring_within_days") is { } days) q = q.Where(d => d.NotAfter <= now.AddDays(days));
                var limit = Math.Clamp(Int("limit") ?? 25, 1, 50);
                result = (await q.OrderBy(d => d.NotAfter).Take(1500).ToListAsync(ct))
                    .Where(d => string.IsNullOrWhiteSpace(text) || $"{d.Subject} {d.DnsNames} {d.Issuer} {d.Thumbprint} {CertIntel.Vendor(d.Issuer, d.Subject)}".Contains(text, StringComparison.OrdinalIgnoreCase))
                    .Take(limit).Select(d => new
                    {
                        d.Id, d.Subject, d.DnsNames, vendor = CertIntel.Vendor(d.Issuer, d.Subject), notAfter = d.NotAfter.ToString("yyyy-MM-dd"),
                        daysLeft = (int)Math.Floor((d.NotAfter - now).TotalDays), server = agentNames.GetValueOrDefault(d.AgentId, "?"), d.Bindings, d.KeyInfo
                    });
                break;
            }
            case "list_endpoints":
            {
                var q = db.Endpoints.AsNoTracking();
                if (Int("expiring_within_days") is { } days) q = q.Where(e => e.NotAfter != null && e.NotAfter <= now.AddDays(days));
                result = (await q.OrderBy(e => e.NotAfter).Take(50).ToListAsync(ct)).Select(e => new
                {
                    e.Id, endpoint = $"{e.Host}:{e.Port}", e.Label, vendor = CertIntel.Vendor(e.Issuer, e.Subject),
                    notAfter = e.NotAfter?.ToString("yyyy-MM-dd"), e.ChainValid, e.NameMatches, e.TlsProtocol, e.LastError
                });
                break;
            }
            case "list_profiles":
                result = (await db.Profiles.AsNoTracking().Include(p => p.Targets).Include(p => p.Issuer).ToListAsync(ct)).Select(p => new
                {
                    p.Id, p.Name, p.Domains, issuer = p.Issuer?.Name, server = agentNames.GetValueOrDefault(p.AgentId, "?"), p.Enabled, p.RequireApproval,
                    targets = p.Targets.Select(t => t.Template), currentExpiry = p.CurrentNotAfter?.ToString("yyyy-MM-dd"),
                    nextRenewal = RenewalPlanner.NextRenewal(p)?.ToString("yyyy-MM-dd")
                });
                break;
            case "list_jobs":
            {
                var q = db.Jobs.AsNoTracking();
                if (Str("status") is { Length: > 0 } st) q = q.Where(j => j.Status == st);
                var profiles = await db.Profiles.AsNoTracking().ToDictionaryAsync(p => p.Id, p => p.Name, ct);
                result = (await q.OrderByDescending(j => j.CreatedAt).Take(Math.Clamp(Int("limit") ?? 15, 1, 40)).ToListAsync(ct)).Select(j => new
                {
                    j.Id, j.Type, j.Phase, j.Status, j.Message, profile = j.ProfileId is { } pid ? profiles.GetValueOrDefault(pid) : null,
                    server = agentNames.GetValueOrDefault(j.AgentId, "?"), created = j.CreatedAt.ToString("u")
                });
                break;
            }
            case "get_job":
            {
                if (!Guid.TryParse(Str("job_id"), out var id)) return JsonSerializer.Serialize(new { error = "job_id must be a GUID" });
                var j = await db.Jobs.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
                if (j is null) return JsonSerializer.Serialize(new { error = "job not found" });
                result = new { j.Id, j.Type, j.Status, j.Message, log = RuleDiagnostics.Redact(j.Log.Length > 6000 ? j.Log[^6000..] : j.Log), findings = RuleDiagnostics.Analyze(j.Log, j.Message) };
                break;
            }
            case "list_agents":
                result = (await db.Agents.AsNoTracking().ToListAsync(ct)).Select(x => new
                { x.Id, x.Name, x.Roles, x.Version, x.Revoked, online = !x.Revoked && x.LastSeenAt > now.AddMinutes(-5), lastSeen = x.LastSeenAt?.ToString("u") });
                break;
            case "list_ca_accounts":
                result = new
                {
                    accounts = (await db.CaAccounts.AsNoTracking().ToListAsync(ct)).Select(x => new { x.Name, x.Type, x.LastSyncAt, x.LastError, snapshot = x.SnapshotJson is null ? null : JsonNode.Parse(x.SnapshotJson) }),
                    issuers = (await db.Issuers.AsNoTracking().ToListAsync(ct)).Select(i => new { i.Name, i.Type, i.DirectoryUrl, coverageEnds = i.CoverageEndsAt?.ToString("yyyy-MM-dd") })
                };
                break;
            case "propose_action":
            {
                var type = Str("type") ?? "";
                var id = Str("id");
                var label = Str("label") ?? type;
                var ok = type switch
                {
                    "adopt" => Guid.TryParse(id, out var g1) && await db.Discovered.AnyAsync(d => d.Id == g1, ct),
                    "renew" => Guid.TryParse(id, out var g2) && await db.Profiles.AnyAsync(p => p.Id == g2, ct),
                    "approve" or "diagnose" => Guid.TryParse(id, out var g3) && await db.Jobs.AnyAsync(j => j.Id == g3, ct),
                    "scan" => Guid.TryParse(id, out var g4) && await db.Endpoints.AnyAsync(e => e.Id == g4, ct),
                    "discover" => Guid.TryParse(id, out var g5) && await db.Agents.AnyAsync(e => e.Id == g5, ct),
                    "open" => (Str("route") ?? "").StartsWith("#/", StringComparison.Ordinal),
                    _ => false
                };
                if (!ok) return JsonSerializer.Serialize(new { error = "invalid action or id; use ids returned by other tools" });
                if (Proposals.Count < 5) Proposals.Add(new ProposedAction(type, id, label.Length > 60 ? label[..60] : label, Str("route")));
                return JsonSerializer.Serialize(new { proposed = true, note = "The user will see a button and decide." });
            }
            default:
                return JsonSerializer.Serialize(new { error = $"unknown tool {name}" });
        }
        return JsonSerializer.Serialize(result);
    }
}
