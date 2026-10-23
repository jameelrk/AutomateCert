using System.Text.Json;
using System.Text.RegularExpressions;
using AutomateCert.Server.Data;
using AutomateCert.Server.Security;
using AutomateCert.Server.Services;
using AutomateCert.Shared;
using Microsoft.EntityFrameworkCore;

namespace AutomateCert.Server.Endpoints;

/// <summary>
/// Agent API. Authentication: "Authorization: AutomateCertAgent {agentId}:{secret}" (SHA-256 of the secret stored).
/// Agents can only see and update their own jobs and DNS tasks.
/// </summary>
public static partial class AgentEndpoints
{
    private const string AgentItem = "ac.agent";

    [GeneratedRegex(@"^[A-Fa-f0-9]{40}$")]
    private static partial Regex ThumbprintRegex();

    public sealed record LogLine(string Line);
    private static string Trunc(string? s, int max) => s is null ? "" : s.Length > max ? s[..max] : s;

    public static void Map(WebApplication app)
    {
        var pub = app.MapGroup("/api/agent").RequireRateLimiting("agent");

        pub.MapPost("/enroll", async (EnrollRequest r, AppDb db, TenantContext tenant, AuditService audit, NotificationService notify, HttpContext http) =>
        {
            if (string.IsNullOrWhiteSpace(r.Token) || string.IsNullOrWhiteSpace(r.MachineName)) return Results.BadRequest();
            var hash = Secrets.Hash(r.Token.Trim());
            var token = await db.EnrollmentTokens.IgnoreQueryFilters().FirstOrDefaultAsync(t => t.TokenHash == hash);
            if (token is null || token.ExpiresAt < DateTimeOffset.UtcNow || token.UseCount >= Math.Max(1, token.MaxUses))
                return Results.Json(new { error = "Enrollment token invalid, used up or expired." }, statusCode: 401);
            tenant.TenantId = token.TenantId;
            token.UseCount++;
            if (token.UseCount >= Math.Max(1, token.MaxUses)) token.UsedAt = DateTimeOffset.UtcNow;
            var secret = Secrets.NewToken();
            var agent = new Agent
            {
                TenantId = token.TenantId, Name = Trunc(r.MachineName.Trim(), 200), SecretHash = Secrets.Hash(secret),
                OsVersion = Trunc(r.OsVersion, 200), Version = Trunc(r.AgentVersion, 40),
                LastIp = http.Connection.RemoteIpAddress?.ToString(), LastSeenAt = DateTimeOffset.UtcNow
            };
            db.Agents.Add(agent);
            audit.Record(token.TenantId, "agent:" + agent.Name, "agent.enrolled", agent.Name, $"{agent.LastIp}; token use {token.UseCount}/{token.MaxUses}");
            await db.SaveChangesAsync();
            await notify.NotifyAsync(token.TenantId, new AlertMessage(Severity.Success, $"New server connected: {agent.Name}",
                "An agent enrolled and will upload its certificate inventory within a minute.",
                [new("Server", agent.Name), new("OS", agent.OsVersion ?? ""), new("IP", agent.LastIp ?? "?")], "#/agents"));
            return Results.Ok(new EnrollResponse(agent.Id, secret));
        });

        var g = app.MapGroup("/api/agent").RequireRateLimiting("agent");
        g.AddEndpointFilter(async (ctx, next) =>
        {
            var http = ctx.HttpContext;
            var header = http.Request.Headers.Authorization.ToString();
            var prefix = AgentProtocol.AuthScheme + " ";
            if (!header.StartsWith(prefix, StringComparison.Ordinal)) return Results.Unauthorized();
            var parts = header[prefix.Length..].Split(':', 2);
            if (parts.Length != 2 || !Guid.TryParse(parts[0], out var agentId)) return Results.Unauthorized();
            var db = http.RequestServices.GetRequiredService<AppDb>();
            var agent = await db.Agents.IgnoreQueryFilters().FirstOrDefaultAsync(a => a.Id == agentId);
            if (agent is null || agent.Revoked || !Secrets.Matches(parts[1], agent.SecretHash)) return Results.Unauthorized();
            var tenant = http.RequestServices.GetRequiredService<TenantContext>();
            tenant.TenantId = agent.TenantId;
            tenant.Actor = "agent:" + agent.Name;
            agent.LastSeenAt = DateTimeOffset.UtcNow;
            agent.LastIp = http.Connection.RemoteIpAddress?.ToString();
            http.Items[AgentItem] = agent;
            return await next(ctx);
        });

        g.MapPost("/heartbeat", async (HeartbeatRequest r, HttpContext http, AppDb db) =>
        {
            var agent = (Agent)http.Items[AgentItem]!;
            agent.Version = Trunc(r.AgentVersion, 40);
            agent.OsVersion = Trunc(r.OsVersion, 200);
            await db.SaveChangesAsync();
            return Results.Ok();
        });

        g.MapGet("/jobs/next", async (HttpContext http, AppDb db) =>
        {
            var agent = (Agent)http.Items[AgentItem]!;
            var job = await db.Jobs.Where(j => j.AgentId == agent.Id && (j.Status == JobStatuses.Pending || j.Status == JobStatuses.CertificateReady))
                .OrderBy(j => j.CreatedAt).FirstOrDefaultAsync();
            if (job is null) { await db.SaveChangesAsync(); return Results.NoContent(); }

            CertificateProfile? profile = null;
            if (job.ProfileId is { } pid)
            {
                profile = await db.Profiles.Include(p => p.Targets).Include(p => p.Issuer).FirstOrDefaultAsync(p => p.Id == pid);
                if (profile is null) { job.SetStatus(JobStatuses.Failed, "Profile no longer exists."); await db.SaveChangesAsync(); return Results.NoContent(); }
            }

            job.SetStatus(job.Type == JobTypes.Renew && job.Status == JobStatuses.Pending ? JobStatuses.CsrRequested : JobStatuses.Deploying);
            await db.SaveChangesAsync();

            var selected = new HashSet<string>((job.TargetIds ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);
            var all = profile?.Targets.OrderBy(t => t.Order).ToList() ?? [];
            var chosen = job.Type == JobTypes.Task || job.Phase == JobPhases.Promote
                ? all.Where(t => selected.Contains(t.Id.ToString()))
                : all.Where(t => t.AgentId is null || t.AgentId == job.AgentId);
            var current = profile?.CurrentThumbprint;
            var targets = chosen.Select(t =>
            {
                var prm = JsonSerializer.Deserialize<Dictionary<string, string>>(t.ParametersJson) ?? [];
                if (t.Template is DeploymentTemplates.Rebind or DeploymentTemplates.Adfs && !string.IsNullOrEmpty(current))
                    prm[DeploymentTemplates.OldThumbprintParameter] = current;
                if (t.Template == DeploymentTemplates.AdfsToken)
                    prm[DeploymentTemplates.PhaseParameter] = job.Phase == JobPhases.Promote ? "promote" : FollowUps.PromoteHours(t) > 0 ? "stage" : "now";
                return new AgentTarget(t.Id, t.Template, prm, t.VerifyHost, t.VerifyPort);
            }).ToArray();

            return Results.Ok(new AgentJob(job.Id, job.Type, job.Status, profile?.Name, profile?.DomainList() ?? [],
                profile?.KeyAlgorithm ?? KeyAlgorithms.Rsa2048, profile?.KeyStorage ?? KeyStorages.Cng,
                profile?.Issuer?.Type ?? IssuerTypes.Acme, profile?.Issuer?.AdcsConfig, profile?.Issuer?.AdcsTemplate,
                profile?.Exportable ?? false, job.ThumbprintToDeploy, targets, job.Phase));
        });

        g.MapPost("/jobs/{id:guid}/csr", async (Guid id, CsrSubmission r, HttpContext http, AppDb db) =>
        {
            var agent = (Agent)http.Items[AgentItem]!;
            var job = await db.Jobs.FirstOrDefaultAsync(j => j.Id == id && j.AgentId == agent.Id);
            if (job is null) return Results.NotFound();
            if (job.Status != JobStatuses.CsrRequested) return Results.Conflict(new { error = "Job is not expecting a CSR." });
            if (string.IsNullOrWhiteSpace(r.CsrPem) || !r.CsrPem.Contains("CERTIFICATE REQUEST") || r.CsrPem.Length > 20_000) return Results.BadRequest(new { error = "Invalid CSR." });
            job.CsrPem = r.CsrPem;
            job.SetStatus(JobStatuses.CsrSubmitted, "CSR received from agent (private key stays on the agent).");
            await db.SaveChangesAsync();
            return Results.Ok();
        });

        g.MapGet("/jobs/{id:guid}/certificate", async (Guid id, HttpContext http, AppDb db) =>
        {
            var agent = (Agent)http.Items[AgentItem]!;
            var job = await db.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == id && j.AgentId == agent.Id);
            if (job?.CertificatePem is null) return Results.NotFound();
            await db.SaveChangesAsync();
            return Results.Ok(new CertificateDelivery(job.CertificatePem));
        });

        g.MapPost("/jobs/{id:guid}/log", async (Guid id, LogLine r, HttpContext http, AppDb db) =>
        {
            var agent = (Agent)http.Items[AgentItem]!;
            var job = await db.Jobs.FirstOrDefaultAsync(j => j.Id == id && j.AgentId == agent.Id);
            if (job is null) return Results.NotFound();
            job.Append("agent: " + Trunc(r.Line, 2000));
            await db.SaveChangesAsync();
            return Results.Ok();
        });

        g.MapPost("/jobs/{id:guid}/complete", async (Guid id, JobResult r, HttpContext http, AppDb db, AuditService audit, NotificationService notify) =>
        {
            var agent = (Agent)http.Items[AgentItem]!;
            var job = await db.Jobs.FirstOrDefaultAsync(j => j.Id == id && j.AgentId == agent.Id);
            if (job is null) return Results.NotFound();
            if (JobStatuses.Terminal.Contains(job.Status)) return Results.Conflict(new { error = "Job already finished." });
            if (r.Thumbprint is { } tp && !ThumbprintRegex().IsMatch(tp)) return Results.BadRequest(new { error = "Invalid thumbprint." });
            foreach (var line in (r.Log ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(200)) job.Append("agent: " + Trunc(line, 1000));

            var profile = job.ProfileId is { } pid ? await db.Profiles.Include(p => p.Targets).FirstOrDefaultAsync(p => p.Id == pid) : null;
            if (r.Success)
            {
                job.ResultThumbprint = r.Thumbprint?.ToUpperInvariant();
                job.SetStatus(JobStatuses.Succeeded, job.Type switch
                {
                    JobTypes.Renew => "Renewed, deployed and verified.",
                    JobTypes.Task => "Follow-up steps completed.",
                    _ => job.Phase == JobPhases.Promote ? "New AD FS token certificate is now primary." : "Completed."
                });
                if (profile is not null && r.Thumbprint is not null && job.Type is JobTypes.Renew or JobTypes.Deploy)
                {
                    profile.CurrentThumbprint = r.Thumbprint.ToUpperInvariant();
                    if (r.NotBefore is not null) profile.CurrentNotBefore = r.NotBefore;
                    if (r.NotAfter is not null) profile.CurrentNotAfter = r.NotAfter;
                    if (job.Type == JobTypes.Renew) profile.LastRenewedAt = DateTimeOffset.UtcNow;
                    FollowUps.Create(db, job, profile, r.Thumbprint.ToUpperInvariant());
                }
                audit.Record(job.TenantId, "agent:" + agent.Name, $"job.{job.Type.ToLowerInvariant()}.succeeded", profile?.Name ?? agent.Name, r.Thumbprint);
            }
            else
            {
                job.SetStatus(JobStatuses.Failed, "Agent reported failure. Previous bindings were restored where possible. Click Diagnose for the cause.");
                audit.Record(job.TenantId, "agent:" + agent.Name, $"job.{job.Type.ToLowerInvariant()}.failed", profile?.Name ?? agent.Name);
            }
            await db.SaveChangesAsync();

            if (job.Type != JobTypes.Discover)
            {
                var lastLines = string.Join(" | ", (r.Log ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(3));
                await notify.NotifyAsync(job.TenantId, r.Success
                    ? new AlertMessage(Severity.Success, $"{(job.Type == JobTypes.Renew ? "Certificate renewed" : job.Type + " completed")}: {profile?.Name}",
                        "The change was applied and verified. No action needed.",
                        [new("Profile", profile?.Name ?? "?"), new("Server", agent.Name), new("Thumbprint", r.Thumbprint ?? ""), new("Valid until", r.NotAfter?.ToString("yyyy-MM-dd") ?? "—")], "#/profiles")
                    : new AlertMessage(Severity.Critical, $"{job.Type} failed: {profile?.Name}",
                        "AutomateCert rolled back to the previous certificate where possible. Open the job and click Diagnose.",
                        [new("Profile", profile?.Name ?? "?"), new("Server", agent.Name), new("Last log lines", RuleDiagnostics.Redact(lastLines))], "#/jobs"));
            }
            return Results.Ok();
        });

        g.MapPost("/inventory", async (InventoryReport r, HttpContext http, AppDb db) =>
        {
            var agent = (Agent)http.Items[AgentItem]!;
            if (r.Roles is { } roles) agent.Roles = Trunc(string.Join(',', roles.Select(x => Trunc(x, 40)).Distinct().Take(20)), 500);
            var existing = await db.Discovered.Where(d => d.AgentId == agent.Id).ToListAsync();
            var byThumb = existing.ToDictionary(d => d.Thumbprint, StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var now = DateTimeOffset.UtcNow;
            foreach (var c in (r.Certificates ?? []).Take(5000))
            {
                if (!ThumbprintRegex().IsMatch(c.Thumbprint ?? "")) continue;
                var tp = c.Thumbprint!.ToUpperInvariant();
                if (!seen.Add(tp)) continue;
                if (!byThumb.TryGetValue(tp, out var d))
                {
                    d = new DiscoveredCertificate { AgentId = agent.Id, TenantId = agent.TenantId, Thumbprint = tp };
                    db.Discovered.Add(d);
                }
                d.Subject = Trunc(c.Subject, 1000);
                d.DnsNames = Trunc(string.Join(", ", c.DnsNames ?? []), 4000);
                d.Issuer = Trunc(c.Issuer, 1000);
                d.NotBefore = c.NotBefore;
                d.NotAfter = c.NotAfter;
                d.HasPrivateKey = c.HasPrivateKey;
                d.Store = Trunc(c.Store, 100);
                d.Bindings = Trunc(string.Join("; ", c.Bindings ?? []), 4000);
                d.KeyInfo = c.KeyInfo is null ? null : Trunc(c.KeyInfo, 40);
                d.SignatureAlgorithm = c.SignatureAlgorithm is null ? null : Trunc(c.SignatureAlgorithm, 60);
                d.Serial = c.Serial is null ? null : Trunc(c.Serial, 80);
                d.LastSeenAt = now;
            }
            db.Discovered.RemoveRange(existing.Where(e => !seen.Contains(e.Thumbprint)));
            await db.SaveChangesAsync();
            return Results.Ok(new { count = seen.Count });
        });

        g.MapGet("/dns-tasks", async (HttpContext http, AppDb db) =>
        {
            var agent = (Agent)http.Items[AgentItem]!;
            var tasks = await db.DnsTasks.Where(t => t.AgentId == agent.Id && t.Status == "Pending").OrderBy(t => t.CreatedAt).Take(20).ToListAsync();
            foreach (var t in tasks) t.Status = "Running";
            await db.SaveChangesAsync();
            return Results.Ok(tasks.Select(t => new AgentDnsTask(t.Id, t.Action, t.Name, t.Value, t.DnsServer, t.Zone)));
        });

        g.MapPost("/dns-tasks/{id:guid}", async (Guid id, AgentDnsTaskResult r, HttpContext http, AppDb db) =>
        {
            var agent = (Agent)http.Items[AgentItem]!;
            var t = await db.DnsTasks.FirstOrDefaultAsync(x => x.Id == id && x.AgentId == agent.Id);
            if (t is null) return Results.NotFound();
            t.Status = r.Success ? "Done" : "Failed";
            t.Message = Trunc(r.Message, 1000);
            t.CompletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
            return Results.Ok();
        });
    }
}
