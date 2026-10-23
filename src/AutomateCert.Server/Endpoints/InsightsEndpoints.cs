using AutomateCert.Server;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AutomateCert.Server.Data;
using AutomateCert.Server.Security;
using AutomateCert.Server.Services;
using AutomateCert.Shared;
using Microsoft.EntityFrameworkCore;

namespace AutomateCert.Server.Endpoints;

public sealed record SettingsInput(string? WebhookUrl, string? AlertEmails, string? AlertDays, bool AlertUnmanaged, bool NotifyOnSuccess,
    bool RequireMfa, int? SessionHours, string? CtDomains, bool? WeeklySummary = null);
public sealed record AiSettingsInput(bool Enabled, string? Provider, string? Endpoint, string? Model, string? ApiKey);
public sealed record ChatMessage(string Role, string Content);
public sealed record ChatRequest(ChatMessage[] Messages);

public sealed record SecurityCheck(string Name, bool Ok, string Detail);

public static class InsightsEndpoints
{
    private static IResult Bad(string error) => Results.BadRequest(new { error });

    public static void Map(WebApplication app)
    {
        var read = app.MapGroup("/api").RequireAuthorization();
        var op = app.MapGroup("/api").RequireAuthorization("Operator");
        var admin = app.MapGroup("/api").RequireAuthorization("Admin");

        // ---------------- Analytics ----------------
        read.MapGet("/analytics", async (AppDb db, CancellationToken ct) =>
        {
            var now = DateTimeOffset.UtcNow;
            var certs = await db.Discovered.AsNoTracking().Select(d => new { d.Thumbprint, d.Issuer, d.Subject, d.NotBefore, d.NotAfter, d.HasPrivateKey, d.KeyInfo, d.SignatureAlgorithm, d.DnsNames, d.Bindings }).ToListAsync(ct);
            var endpoints = await db.Endpoints.AsNoTracking().Select(e => new { e.Issuer, e.Subject, e.NotAfter, e.ChainValid, e.NameMatches }).ToListAsync(ct);
            var managed = (await db.Profiles.AsNoTracking().Where(p => p.CurrentThumbprint != null).Select(p => p.CurrentThumbprint!).ToListAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var jobs = await db.Jobs.AsNoTracking().Where(j => j.Type == JobTypes.Renew && j.CreatedAt > now.AddDays(-30)).Select(j => new { j.Status, j.CreatedAt, j.UpdatedAt }).ToListAsync(ct);
            var agents = await db.Agents.AsNoTracking().Where(a => !a.Revoked).Select(a => a.LastSeenAt).ToListAsync(ct);

            var allExpiries = certs.Select(c => c.NotAfter).Concat(endpoints.Where(e => e.NotAfter != null).Select(e => e.NotAfter!.Value)).ToList();
            var weeks = Enumerable.Range(0, 12).Select(w => new { week = now.AddDays(w * 7).ToString("MMM d"), count = allExpiries.Count(n => n > now.AddDays(w * 7) && n <= now.AddDays((w + 1) * 7)) }).ToList();
            var vendors = certs.Select(c => CertIntel.Vendor(c.Issuer, c.Subject)).Concat(endpoints.Select(e => CertIntel.Vendor(e.Issuer, e.Subject)))
                .GroupBy(v => v).Select(g => new { name = g.Key, count = g.Count(), free = CertIntel.FreeVendors.Contains(g.Key) }).OrderByDescending(x => x.count).Take(6).ToList();
            var days = Enumerable.Range(0, 30).Select(i => now.Date.AddDays(-29 + i)).Select(d => new
            {
                day = d.ToString("MMM d"),
                succeeded = jobs.Count(j => j.Status == JobStatuses.Succeeded && j.UpdatedAt.UtcDateTime.Date == d),
                failed = jobs.Count(j => j.Status == JobStatuses.Failed && j.UpdatedAt.UtcDateTime.Date == d)
            }).ToList();

            var done = jobs.Where(j => j.Status == JobStatuses.Succeeded).ToList();
            var withKey = certs.Where(c => c.HasPrivateKey && c.NotAfter > now).ToList();
            var coverage = withKey.Count == 0 ? 0 : (int)Math.Round(100.0 * withKey.Count(c => managed.Contains(c.Thumbprint)) / withKey.Count);
            var finished = jobs.Count(j => j.Status is JobStatuses.Succeeded or JobStatuses.Failed);
            var successRate = finished == 0 ? 100 : (int)Math.Round(100.0 * done.Count / finished);
            var expired = certs.Count(c => c.NotAfter <= now && c.NotAfter > now.AddDays(-30) && c.Bindings.Length > 0) + endpoints.Count(e => e.NotAfter <= now && e.NotAfter > now.AddDays(-30));
            var exp7 = certs.Count(c => c.NotAfter > now && c.NotAfter <= now.AddDays(7) && !managed.Contains(c.Thumbprint)) + endpoints.Count(e => e.NotAfter > now && e.NotAfter <= now.AddDays(7));
            var exp30 = certs.Count(c => c.NotAfter > now.AddDays(7) && c.NotAfter <= now.AddDays(30) && !managed.Contains(c.Thumbprint)) + endpoints.Count(e => e.NotAfter > now.AddDays(7) && e.NotAfter <= now.AddDays(30));
            var untrusted = endpoints.Count(e => e.ChainValid == false || e.NameMatches == false);
            var offline = agents.Count(s => s is null || s < now.AddMinutes(-15));
            var failed7 = jobs.Count(j => j.Status == JobStatuses.Failed && j.UpdatedAt > now.AddDays(-7));
            var criticalIssues = certs.Sum(c => CertIntel.Check(c.KeyInfo, c.SignatureAlgorithm, c.NotBefore, c.NotAfter, c.Issuer, c.Subject, c.DnsNames, c.Bindings.Length > 0, now).Count(i => i.Severity == "critical"));
            var risk = Math.Min(100, expired * 25 + exp7 * 12 + exp30 * 3 + untrusted * 4 + offline * 8 + failed7 * 6 + criticalIssues * 3 + (100 - coverage) / 5);
            var grade = risk switch { <= 10 => "A", <= 25 => "B", <= 45 => "C", <= 70 => "D", _ => "F" };
            var freeCount = certs.Count(c => c.NotAfter > now && CertIntel.FreeVendors.Contains(CertIntel.Vendor(c.Issuer, c.Subject)));
            return Results.Ok(new
            {
                riskScore = risk, grade, coverage, successRate,
                avgRenewMinutes = done.Count == 0 ? 0 : Math.Round(done.Average(j => (j.UpdatedAt - j.CreatedAt).TotalMinutes), 1),
                renewals30 = done.Count, failed30 = jobs.Count(j => j.Status == JobStatuses.Failed),
                hoursSaved30 = Math.Round(done.Count * 0.75, 1),
                unmanagedExpiring30 = exp7 + exp30, offlineAgents = offline, untrusted, criticalIssues, freeCount,
                longLived = certs.Count(c => c.NotAfter > now && (c.NotAfter - c.NotBefore).TotalDays > 200),
                vendorCount = vendors.Count, expiryWeeks = weeks, vendors, renewalsDaily = days
            });
        }).AddEndpointFilter(new ShortCache(20));

        read.MapGet("/autopilot", async (AutopilotService autopilot, CancellationToken ct) => Results.Ok(await autopilot.RecommendAsync(ct))).AddEndpointFilter(new ShortCache(20));

        read.MapGet("/compliance", async (AppDb db, CancellationToken ct) =>
        {
            var now = DateTimeOffset.UtcNow;
            var agents = await db.Agents.AsNoTracking().ToDictionaryAsync(a => a.Id, a => a.Name, ct);
            var items = new List<object>();
            var total = 0; var clean = 0;
            foreach (var c in await db.Discovered.AsNoTracking().ToListAsync(ct))
            {
                total++;
                var issues = CertIntel.Check(c.KeyInfo, c.SignatureAlgorithm, c.NotBefore, c.NotAfter, c.Issuer, c.Subject, c.DnsNames, c.Bindings.Length > 0, now);
                if (issues.Count == 0) { clean++; continue; }
                foreach (var i in issues) items.Add(new { source = "Server", c.Id, c.Subject, where = agents.GetValueOrDefault(c.AgentId, "?"), i.Rule, i.Severity, i.Detail, c.NotAfter });
            }
            foreach (var e in await db.Endpoints.AsNoTracking().Where(e => e.NotAfter != null).ToListAsync(ct))
            {
                total++;
                var issues = CertIntel.Check(e.KeyInfo, e.SignatureAlgorithm, e.NotBefore ?? now, e.NotAfter!.Value, e.Issuer ?? "", e.Subject ?? "", e.DnsNames ?? "", true, now);
                if (e.ChainValid == false) issues.Add(new("Untrusted chain", "critical", e.ChainStatus ?? ""));
                if (e.NameMatches == false) issues.Add(new("Host name mismatch", "critical", "The certificate does not cover the host name."));
                if (e.TlsProtocol is "Tls" or "Tls11" or "Ssl3") issues.Add(new("Legacy TLS protocol", "warning", $"Negotiated {e.TlsProtocol}; disable TLS 1.0/1.1."));
                if (issues.Count == 0) { clean++; continue; }
                foreach (var i in issues) items.Add(new { source = "Endpoint", e.Id, e.Subject, where = $"{e.Host}:{e.Port}", i.Rule, i.Severity, i.Detail, e.NotAfter });
            }
            return Results.Ok(new { score = total == 0 ? 100 : (int)Math.Round(100.0 * clean / total), total, clean, items });
        }).AddEndpointFilter(new ShortCache(20));

        read.MapGet("/transparency", async (AppDb db, TenantContext tc, CancellationToken ct) =>
        {
            var t = await db.Tenants.AsNoTracking().FirstAsync(x => x.Id == tc.TenantId, ct);
            var list = await db.CtCertificates.AsNoTracking().OrderByDescending(c => c.NotBefore).Take(500).ToListAsync(ct);
            return Results.Ok(new
            {
                domains = t.CtDomains,
                items = list.Select(c => new { c.Id, c.Domain, c.Names, c.Issuer, vendor = CertIntel.Vendor(c.Issuer, null), c.NotBefore, c.NotAfter, c.Known, c.Acknowledged, c.CrtShId })
            });
        });

        op.MapPost("/transparency/sync", async (AppDb db, CtMonitor ct, TenantContext tc, CancellationToken token) =>
        {
            var t = await db.Tenants.AsNoTracking().FirstAsync(x => x.Id == tc.TenantId, token);
            if (string.IsNullOrWhiteSpace(t.CtDomains)) return Bad("Add domains to watch first (Settings → Monitoring).");
            return Results.Ok(new { added = await ct.SyncTenantAsync(t, token) });
        }).RequireRateLimiting("ai");

        op.MapPost("/transparency/{id:long}/ack", async (long id, AppDb db, TenantContext t, AuditService audit) =>
        {
            var c = await db.CtCertificates.FirstOrDefaultAsync(x => x.Id == id);
            if (c is null) return Results.NotFound();
            c.Acknowledged = true;
            audit.Record(t.TenantId, t.Actor, "ct.acknowledged", c.Names);
            await db.SaveChangesAsync();
            return Results.Ok();
        });

        // ---------------- CRM & AD FS ----------------
        read.MapGet("/identity", async (AppDb db, CancellationToken ct) =>
        {
            var now = DateTimeOffset.UtcNow;
            var agents = await db.Agents.AsNoTracking().Where(a => !a.Revoked).ToListAsync(ct);
            bool Has(Agent a, string r) => (a.Roles ?? "").Split(',').Contains(r, StringComparer.OrdinalIgnoreCase);
            var ids = agents.Where(a => Has(a, "ADFS") || Has(a, "WAP") || Has(a, "CRM")).Select(a => a.Id).ToList();
            var certs = await db.Discovered.AsNoTracking().Where(d => ids.Contains(d.AgentId) && d.Bindings != "").ToListAsync(ct);
            var profiles = await db.Profiles.AsNoTracking().Include(p => p.Targets).ToListAsync(ct);
            var adfsTemplates = new[] { DeploymentTemplates.Adfs, DeploymentTemplates.AdfsToken, DeploymentTemplates.AdfsRpRefresh, DeploymentTemplates.Wap, DeploymentTemplates.CrmClaims, DeploymentTemplates.CrmRefresh };
            return Results.Ok(new
            {
                servers = agents.Where(a => ids.Contains(a.Id)).Select(a => new
                {
                    a.Id, a.Name, roles = (a.Roles ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries), online = a.LastSeenAt > now.AddMinutes(-5),
                    certificates = certs.Where(c => c.AgentId == a.Id).Select(c => new { c.Id, c.Subject, c.Bindings, c.NotAfter, c.Thumbprint, vendor = CertIntel.Vendor(c.Issuer, c.Subject) })
                }),
                profiles = profiles.Where(p => p.Targets.Any(t => adfsTemplates.Contains(t.Template)) || p.Scenario is not null)
                    .Select(p => new { p.Id, p.Name, p.Scenario, p.CurrentNotAfter, nextRenewal = RenewalPlanner.NextRenewal(p), targets = p.Targets.Select(t => t.Template) })
            });
        });

        // ---------------- Security center ----------------
        read.MapGet("/security", async (AppDb db, TenantContext t, EmailSender email, IConfiguration cfg, CancellationToken ct) =>
        {
            var tenant = await db.Tenants.AsNoTracking().FirstAsync(x => x.Id == t.TenantId, ct);
            var users = await db.Users.AsNoTracking().Where(u => !u.Disabled).ToListAsync(ct);
            var now = DateTimeOffset.UtcNow;
            var failed24 = await db.Audit.AsNoTracking().CountAsync(a => (a.Action == "auth.failed" || a.Action == "auth.mfa.failed") && a.At > now.AddDays(-1), ct);
            var locked24 = await db.Audit.AsNoTracking().CountAsync(a => a.Action == "auth.locked" && a.At > now.AddDays(-1), ct);
            var tokens = await db.EnrollmentTokens.AsNoTracking().CountAsync(x => x.ExpiresAt > now && x.UseCount < x.MaxUses, ct);
            var ai = tenant.AiEnabled ? tenant.AiEndpoint : null;
            var checks = new List<SecurityCheck>
            {
                new("Transport security", true, "HTTPS only · TLS 1.2 / 1.3 · HSTS · secure, HttpOnly, SameSite=Strict session cookie"),
                new("Content security policy", true, "Strict CSP (no inline script/style), frame-ancestors none, no-referrer, COOP/CORP"),
                new("Two-step verification enforced", tenant.RequireMfa, tenant.RequireMfa ? "Required for every user" : "Optional — turn on 'Require two-step verification'"),
                new("Privileged users with 2-step", users.Where(u => u.Role is Roles.Admin or Roles.Operator).All(u => u.MfaEnabled), $"{users.Count(u => u.Role is Roles.Admin or Roles.Operator && u.MfaEnabled)}/{users.Count(u => u.Role is Roles.Admin or Roles.Operator)} Admins/Operators protected"),
                new("Password policy", true, "12+ characters, common-word blocklist, PBKDF2-SHA256 600k iterations, lockout after 5 failures"),
                new("Secrets at rest", true, OperatingSystem.IsWindows() ? "AES-256 (Data Protection) with key ring protected by machine DPAPI" : "AES-256 (Data Protection)"),
                new("Private keys", true, "Generated on target servers; never sent to the console"),
                new("Session lifetime", tenant.SessionHours <= 12, $"{tenant.SessionHours} h"),
                new("SMTP authentication", !email.IsConfigured || email.UsesAuthentication, email.IsConfigured ? (email.UsesAuthentication ? "Authenticated SMTP with TLS" : "Unauthenticated relay") : "Not configured"),
                new("AI data boundary", ai is null || ai.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || ai.Contains("localhost"), ai is null ? "AI disabled" : $"Read-only tools; data sent only to {new Uri(ai).Host}"),
                new("Outstanding enrollment tokens", tokens <= 3, $"{tokens} active token(s)"),
                new("Sign-in failures (24 h)", locked24 == 0, $"{failed24} failed, {locked24} lockout(s)"),
            };
            var score = (int)Math.Round(100.0 * checks.Count(c => c.Ok) / checks.Count);
            return Results.Ok(new { score, checks });
        }).AddEndpointFilter(new ShortCache(20));

        // ---------------- Logs & reports ----------------
        admin.MapGet("/logs/system", (SystemLogBuffer buffer, string? level) =>
            Results.Ok(buffer.Snapshot().Where(e => string.IsNullOrEmpty(level) || e.Level.Equals(level, StringComparison.OrdinalIgnoreCase)).Take(500)));

        read.MapGet("/reports/{kind}.csv", async (string kind, AppDb db, CancellationToken ct) =>
        {
            var sb = new StringBuilder();
            static string Q(object? v) { var s = v?.ToString() ?? ""; if (s.Length > 0 && "=+-@".Contains(s[0])) s = "'" + s; return "\"" + s.Replace("\"", "\"\"") + "\""; }
            var agents = await db.Agents.AsNoTracking().ToDictionaryAsync(a => a.Id, a => a.Name, ct);
            switch (kind)
            {
                case "certificates":
                    sb.AppendLine("Subject,DNS names,Vendor,Issuer,Server,Bindings,Key,Signature,Not before,Not after,Days left,Thumbprint");
                    foreach (var c in await db.Discovered.AsNoTracking().OrderBy(c => c.NotAfter).ToListAsync(ct))
                        sb.AppendLine(string.Join(',', Q(c.Subject), Q(c.DnsNames), Q(CertIntel.Vendor(c.Issuer, c.Subject)), Q(c.Issuer), Q(agents.GetValueOrDefault(c.AgentId)), Q(c.Bindings), Q(c.KeyInfo), Q(c.SignatureAlgorithm),
                            Q(c.NotBefore.ToString("yyyy-MM-dd")), Q(c.NotAfter.ToString("yyyy-MM-dd")), Q((int)Math.Floor((c.NotAfter - DateTimeOffset.UtcNow).TotalDays)), Q(c.Thumbprint)));
                    break;
                case "endpoints":
                    sb.AppendLine("Host,Port,Label,Subject,Vendor,Not after,Trusted,Name matches,TLS,Last error");
                    foreach (var e in await db.Endpoints.AsNoTracking().ToListAsync(ct))
                        sb.AppendLine(string.Join(',', Q(e.Host), Q(e.Port), Q(e.Label), Q(e.Subject), Q(CertIntel.Vendor(e.Issuer, e.Subject)), Q(e.NotAfter?.ToString("yyyy-MM-dd")), Q(e.ChainValid), Q(e.NameMatches), Q(e.TlsProtocol), Q(e.LastError)));
                    break;
                case "audit":
                    sb.AppendLine("Time (UTC),Actor,Action,Target,Details");
                    foreach (var a in await db.Audit.AsNoTracking().OrderByDescending(a => a.At).Take(20000).ToListAsync(ct))
                        sb.AppendLine(string.Join(',', Q(a.At.ToString("u")), Q(a.Actor), Q(a.Action), Q(a.Target), Q(a.Details)));
                    break;
                default: return Results.NotFound();
            }
            return Results.File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray(), "text/csv", $"automatecert-{kind}-{DateTime.UtcNow:yyyyMMdd}.csv");
        });

        admin.MapPost("/reports/send-summary", async (AppDb db, TenantContext t, AlertEngine alerts, EmailSender email, AuditService audit, CancellationToken ct) =>
        {
            if (!email.IsConfigured) return Bad("SMTP is not configured. Set the Smtp section in appsettings and restart.");
            var tenant = await db.Tenants.AsNoTracking().FirstAsync(x => x.Id == t.TenantId, ct);
            await alerts.SendWeeklySummaryAsync(tenant, ct);
            audit.Record(t.TenantId, t.Actor, "report.summary.sent", tenant.Name);
            await db.SaveChangesAsync(ct);
            return Results.Ok();
        });

        // ---------------- Settings ----------------
        read.MapGet("/settings", async (AppDb db, TenantContext t, EmailSender email, IConfiguration cfg) =>
        {
            var tenant = await db.Tenants.AsNoTracking().FirstAsync(x => x.Id == t.TenantId);
            return Results.Ok(new
            {
                tenant.Name, tenant.WebhookUrl, tenant.AlertEmails, tenant.AlertDays, tenant.AlertUnmanaged, tenant.NotifyOnSuccess,
                tenant.RequireMfa, tenant.SessionHours, tenant.CtDomains, tenant.WeeklySummary, smtpConfigured = email.IsConfigured, publicUrl = cfg["App:PublicUrl"],
                ai = new { enabled = tenant.AiEnabled, provider = tenant.AiProvider, endpoint = tenant.AiEndpoint, model = tenant.AiModel, hasKey = tenant.AiApiKeyProtected != null }
            });
        });

        admin.MapPut("/settings", async (SettingsInput r, AppDb db, TenantContext t, AuditService audit) =>
        {
            if (!string.IsNullOrWhiteSpace(r.WebhookUrl) && (!Uri.TryCreate(r.WebhookUrl.Trim(), UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps))
                return Bad("Webhook URL must be https://");
            var days = AlertEngine.ParseDays(r.AlertDays);
            if (days.Length == 0) return Bad("Alert days must be numbers between 1 and 365, e.g. 30,14,7,3,1.");
            var emails = (r.AlertEmails ?? "").Split(new[] { ',', ';', ' ', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (emails.FirstOrDefault(e => !MimeKit.MailboxAddress.TryParse(e, out _) || !e.Contains('@')) is { } badEmail) return Bad($"Invalid email address '{badEmail}'.");
            var ctDomains = (r.CtDomains ?? "").Split(new[] { ',', ';', ' ', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(d => d.ToLowerInvariant().TrimStart('*', '.')).Distinct().ToList();
            if (ctDomains.FirstOrDefault(d => !d.Contains('.') || CertIntel.InternalNameReason(d) is not null) is { } badCt) return Bad($"'{badCt}' is not a public domain.");
            var tenant = await db.Tenants.FirstAsync(x => x.Id == t.TenantId);
            tenant.WebhookUrl = string.IsNullOrWhiteSpace(r.WebhookUrl) ? null : r.WebhookUrl.Trim();
            tenant.AlertEmails = emails.Length == 0 ? null : string.Join(", ", emails);
            tenant.AlertDays = string.Join(",", days);
            tenant.AlertUnmanaged = r.AlertUnmanaged;
            tenant.NotifyOnSuccess = r.NotifyOnSuccess;
            if (r.WeeklySummary is { } ws) tenant.WeeklySummary = ws;
            if (tenant.RequireMfa != r.RequireMfa) audit.Record(t.TenantId, t.Actor, r.RequireMfa ? "security.mfa.required" : "security.mfa.optional", tenant.Name);
            tenant.RequireMfa = r.RequireMfa;
            tenant.SessionHours = Math.Clamp(r.SessionHours ?? tenant.SessionHours, 1, 24);
            tenant.CtDomains = ctDomains.Count == 0 ? null : string.Join(", ", ctDomains);
            audit.Record(t.TenantId, t.Actor, "settings.updated", "organization settings");
            await db.SaveChangesAsync();
            return Results.Ok();
        });

        admin.MapPost("/settings/test-email", async (AppDb db, TenantContext t, EmailSender email, NotificationService notify, IConfiguration cfg, CancellationToken ct) =>
        {
            if (!email.IsConfigured) return Bad("SMTP is not configured. Set the Smtp section in appsettings and restart.");
            var tenant = await db.Tenants.AsNoTracking().FirstAsync(x => x.Id == t.TenantId, ct);
            var to = await notify.RecipientsAsync(tenant, ct);
            if (to.Count == 0) return Bad("No recipients. Add alert emails or enable email for an Admin/Operator.");
            try
            {
                var (html, text) = EmailTemplates.Render(new AlertMessage(Severity.Success, "Test alert", "Email alerts from AutomateCert are working.",
                    [new("Sent by", t.Actor), new("Recipients", string.Join(", ", to))], "#/settings"), cfg["App:PublicUrl"]);
                await email.SendAsync(to, "AutomateCert: test alert", html, text, ct);
            }
            catch (Exception ex) { return Bad("SMTP error: " + EmailSender.Explain(ex)); }
            return Results.Ok(new { sentTo = to });
        });

        admin.MapPut("/settings/ai", async (AiSettingsInput r, AppDb db, SecretProtector sp, TenantContext t, AuditService audit) =>
        {
            var tenant = await db.Tenants.FirstAsync(x => x.Id == t.TenantId);
            if (r.Enabled)
            {
                if (!AiProviders.All.Contains(r.Provider ?? "")) return Bad("Choose a provider.");
                if (!Uri.TryCreate(r.Endpoint?.Trim(), UriKind.Absolute, out var u)) return Bad("Endpoint must be an absolute URL.");
                if (u.Scheme != Uri.UriSchemeHttps && !(u.Scheme == Uri.UriSchemeHttp && u.IsLoopback)) return Bad("Endpoint must use https:// (http only for localhost models such as Ollama).");
                if (string.IsNullOrWhiteSpace(r.Model)) return Bad("Model / deployment name is required.");
                if (r.Provider != AiProviders.Custom && string.IsNullOrWhiteSpace(r.ApiKey) && tenant.AiApiKeyProtected is null) return Bad("API key is required.");
                tenant.AiProvider = r.Provider;
                tenant.AiEndpoint = u.ToString().TrimEnd('/');
                tenant.AiModel = r.Model.Trim();
                if (!string.IsNullOrWhiteSpace(r.ApiKey)) tenant.AiApiKeyProtected = sp.Protect(r.ApiKey.Trim());
            }
            tenant.AiEnabled = r.Enabled;
            audit.Record(t.TenantId, t.Actor, "settings.ai.updated", r.Enabled ? $"{r.Provider} {r.Model}" : "disabled");
            await db.SaveChangesAsync();
            return Results.Ok();
        });

        // ---------------- AI ----------------
        admin.MapPost("/ai/test", async (AiService ai, CancellationToken ct) =>
        {
            var cfg = await ai.GetConfigAsync(ct);
            if (cfg is null) return Bad("AI is not enabled. Save the AI settings first.");
            try { return Results.Ok(new { answer = await ai.ChatAsync(cfg, new JsonArray { new JsonObject { ["role"] = "user", ["content"] = "Reply with exactly: AutomateCert AI connection OK" } }, false, ct) }); }
            catch (Exception ex) { return Bad(ex.Message); }
        }).RequireRateLimiting("ai");

        read.MapPost("/ai/chat", async (ChatRequest r, AiService ai, AppDb db, TenantContext t, CancellationToken ct) =>
        {
            var cfg = await ai.GetConfigAsync(ct);
            if (cfg is null) return Results.Json(new { error = "AI assistant is not configured. An admin can enable it under Settings → AI assistant." }, statusCode: 409);
            var tenant = await db.Tenants.AsNoTracking().FirstAsync(x => x.Id == t.TenantId, ct);
            var messages = new JsonArray { new JsonObject { ["role"] = "system", ["content"] = ai.SystemPrompt(tenant.Name) } };
            foreach (var m in (r.Messages ?? []).TakeLast(12))
            {
                if (m.Role is not ("user" or "assistant")) continue;
                var content = m.Content ?? "";
                messages.Add(new JsonObject { ["role"] = m.Role, ["content"] = content.Length > 4000 ? content[..4000] : content });
            }
            try { return Results.Ok(new { answer = await ai.ChatAsync(cfg, messages, true, ct), actions = ai.Proposals }); }
            catch (Exception ex) { return Results.Json(new { error = "AI provider error: " + ex.Message }, statusCode: 502); }
        }).RequireRateLimiting("ai");

        read.MapPost("/ai/briefing", async (AiService ai, AutopilotService autopilot, AppDb db, TenantContext t, CancellationToken ct) =>
        {
            var recs = await autopilot.RecommendAsync(ct);
            var cfg = await ai.GetConfigAsync(ct);
            if (cfg is null) return Results.Ok(new { briefing = (string?)null, aiConfigured = false });
            var tenant = await db.Tenants.AsNoTracking().FirstAsync(x => x.Id == t.TenantId, ct);
            var messages = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = ai.SystemPrompt(tenant.Name) + "\nWrite an executive certificate-risk briefing: 3-sentence summary, then 'Top actions' (max 5, numbered, each with why it matters), then 'Can wait'. No invented data." },
                new JsonObject { ["role"] = "user", ["content"] = System.Text.Json.JsonSerializer.Serialize(recs) }
            };
            try { return Results.Ok(new { briefing = await ai.ChatAsync(cfg, messages, false, ct), aiConfigured = true }); }
            catch (Exception ex) { return Results.Ok(new { briefing = (string?)null, aiConfigured = true, error = ex.Message }); }
        }).RequireRateLimiting("ai");

        read.MapPost("/ai/diagnose/{id:guid}", async (Guid id, AiService ai, AppDb db, CancellationToken ct) =>
        {
            var job = await db.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == id, ct);
            if (job is null) return Results.NotFound();
            var findings = RuleDiagnostics.Analyze(job.Log, job.Message);
            var cfg = await ai.GetConfigAsync(ct);
            if (cfg is null) return Results.Ok(new { findings, ai = (string?)null, aiConfigured = false, aiError = (string?)null });
            var profile = job.ProfileId is { } pid ? await db.Profiles.AsNoTracking().Include(p => p.Targets).Include(p => p.Issuer).FirstOrDefaultAsync(p => p.Id == pid, ct) : null;
            var tail = job.Log.Length > 7000 ? job.Log[^7000..] : job.Log;
            var context = $"""
Job type: {job.Type} {job.Phase}
Status: {job.Status}
Message: {job.Message}
Profile: {profile?.Name} | domains: {profile?.Domains} | issuer: {profile?.Issuer?.Name} ({profile?.Issuer?.Type}, {profile?.Issuer?.DirectoryUrl})
Key: {profile?.KeyAlgorithm} / {profile?.KeyStorage} | targets: {string.Join(", ", profile?.Targets.Select(x => x.Template) ?? [])}
Built-in findings: {string.Join(" | ", findings.Select(f => f.Title))}
--- log ---
{RuleDiagnostics.Redact(tail)}
""";
            var messages = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = "You are a senior Windows PKI, AD FS, Dynamics CRM and ACME engineer. Diagnose why this AutomateCert job failed. Format: **Root cause**, **Evidence** (quote log lines), **Fix** (numbered, exact steps incl. PowerShell), **Prevent**. Do not invent facts." },
                new JsonObject { ["role"] = "user", ["content"] = context }
            };
            try { return Results.Ok(new { findings, ai = await ai.ChatAsync(cfg, messages, false, ct), aiConfigured = true, aiError = (string?)null }); }
            catch (Exception ex) { return Results.Ok(new { findings, ai = (string?)null, aiConfigured = true, aiError = ex.Message }); }
        }).RequireRateLimiting("ai");

        // ---------------- Agent distribution ----------------
        read.MapGet("/agent-package", (IWebHostEnvironment env, IConfiguration cfg, HttpContext http) =>
        {
            var zip = AgentPackage.Find(env, cfg, AgentPackage.ZipName);
            var exe = AgentPackage.Find(env, cfg, AgentPackage.SetupName);
            var url = AgentPackage.ServerUrl(cfg, http.Request);
            var host = Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : "";
            return Results.Ok(new
            {
                available = zip is not null, sha256 = zip?.Sha256, sizeMb = zip is null ? 0 : Math.Round(zip.Size / 1048576.0, 1),
                setupAvailable = exe is not null, setupSizeMb = exe is null ? 0 : Math.Round(exe.Size / 1048576.0, 1),
                serverUrl = url, isLocalOnly = host is "localhost" or "127.0.0.1" or "::1" or "[::1]"
            });
        });

        app.MapGet("/downloads/AutomateCert.Agent.zip", (IWebHostEnvironment env, IConfiguration cfg) =>
            AgentPackage.Find(env, cfg, AgentPackage.ZipName) is { } i ? Results.File(i.Path, "application/zip", AgentPackage.ZipName) : Results.NotFound("Agent package not built. Run deploy\\build.ps1."))
            .RequireRateLimiting("agent");

        app.MapGet("/downloads/AutomateCertAgent-Setup.exe", (IWebHostEnvironment env, IConfiguration cfg) =>
            AgentPackage.Find(env, cfg, AgentPackage.SetupName) is { } i ? Results.File(i.Path, "application/octet-stream", AgentPackage.SetupName) : Results.NotFound("Agent installer not built. Run installer\\build-installers.ps1."))
            .RequireRateLimiting("agent");

        app.MapGet("/install.ps1", (IWebHostEnvironment env, IConfiguration cfg, HttpContext http) =>
            AgentPackage.Find(env, cfg, AgentPackage.ZipName) is { } i
                ? Results.Text(AgentPackage.InstallScript(AgentPackage.ServerUrl(cfg, http.Request), i.Sha256), "text/plain; charset=utf-8")
                : Results.NotFound("Agent package not built. Run deploy\\build.ps1."))
            .RequireRateLimiting("agent");
    }
}

public static class AgentPackage
{
    public const string ZipName = "AutomateCert.Agent.zip";
    public const string SetupName = "AutomateCertAgent-Setup.exe";
    public sealed record Info(string Path, string Sha256, long Size);
    private static readonly object Gate = new();
    private static readonly Dictionary<string, (DateTime Stamp, Info Info)> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static string ServerUrl(IConfiguration cfg, HttpRequest req) => (cfg["App:PublicUrl"] is { Length: > 0 } u ? u : $"{req.Scheme}://{req.Host}").TrimEnd('/');

    public static Info? Find(IWebHostEnvironment env, IConfiguration cfg, string fileName)
    {
        var candidates = new List<string>();
        if (cfg["AgentPackage:Directory"] is { Length: > 0 } configured)
            candidates.Add(System.IO.Path.Combine(System.IO.Path.IsPathRooted(configured) ? configured : System.IO.Path.Combine(env.ContentRootPath, configured), fileName));
        candidates.Add(System.IO.Path.Combine(env.ContentRootPath, "agent", fileName));
        candidates.Add(System.IO.Path.GetFullPath(System.IO.Path.Combine(env.ContentRootPath, "..", "..", "artifacts", fileName)));
        candidates.Add(System.IO.Path.GetFullPath(System.IO.Path.Combine(env.ContentRootPath, "..", "..", "dist", fileName)));
        var path = candidates.FirstOrDefault(File.Exists);
        if (path is null) return null;
        var stamp = File.GetLastWriteTimeUtc(path);
        lock (Gate)
        {
            if (Cache.TryGetValue(path, out var c) && c.Stamp == stamp) return c.Info;
            using var fs = File.OpenRead(path);
            var info = new Info(path, Convert.ToHexString(SHA256.HashData(fs)), fs.Length);
            Cache[path] = (stamp, info);
            return info;
        }
    }

    public static string InstallScript(string serverUrl, string sha256) => $$"""
# AutomateCert agent installer (generated by {{serverUrl}})
param(
    [Parameter(Mandatory = $true)][string]$Token,
    [switch]$PinServerCertificate
)
$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) { throw "Open PowerShell with 'Run as administrator' and run the command again." }
$server = "{{serverUrl}}"
$expected = "{{sha256}}"
$work = Join-Path $env:TEMP ("AutomateCertAgent-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $work | Out-Null
try {
    $zip = Join-Path $work "AutomateCert.Agent.zip"
    Write-Host "[1/4] Downloading agent package from $server ..." -ForegroundColor Cyan
    Invoke-WebRequest -Uri "$server/downloads/AutomateCert.Agent.zip" -OutFile $zip -UseBasicParsing
    Write-Host "[2/4] Verifying SHA-256 ..." -ForegroundColor Cyan
    $actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $zip).Hash
    if ($actual -ne $expected) { throw "Package hash mismatch (expected $expected, got $actual). Installation aborted." }
    Write-Host "[3/4] Extracting ..." -ForegroundColor Cyan
    $pkg = Join-Path $work "pkg"
    Expand-Archive -LiteralPath $zip -DestinationPath $pkg -Force
    Get-ChildItem -LiteralPath $pkg -Recurse | Unblock-File
    Write-Host "[4/4] Installing service ..." -ForegroundColor Cyan
    $psArgs = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", (Join-Path $pkg "Install-AutomateCertAgent.ps1"), "-ServerUrl", $server, "-EnrollmentToken", $Token)
    if ($PinServerCertificate) { $psArgs += "-PinServerCertificate" }
    & powershell.exe @psArgs
    if ($LASTEXITCODE -ne 0) { throw "Installer failed (exit code $LASTEXITCODE). See messages above." }
}
finally { Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue }
""";
}
