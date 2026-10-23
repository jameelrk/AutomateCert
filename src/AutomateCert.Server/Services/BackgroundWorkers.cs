using System.Text.Json;
using System.Text.Json.Nodes;
using AutomateCert.Server.Data;
using AutomateCert.Shared;
using Microsoft.EntityFrameworkCore;

namespace AutomateCert.Server.Services;

/// <summary>Runs ACME issuance for jobs whose CSR has been submitted or whose manual DNS is confirmed.</summary>
public sealed class IssuanceWorker(IServiceScopeFactory scopes, ILogger<IssuanceWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await TickAsync(stoppingToken); }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested) { log.LogError(ex, "Issuance loop error"); }
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        var job = await db.Jobs.IgnoreQueryFilters()
            .Where(j => j.Type == JobTypes.Renew && (j.Status == JobStatuses.CsrSubmitted || j.Status == JobStatuses.DnsReady))
            .OrderBy(j => j.UpdatedAt).FirstOrDefaultAsync(ct);
        if (job is null) return;

        var resume = job.Status == JobStatuses.DnsReady;
        job.SetStatus(JobStatuses.Issuing);
        await db.SaveChangesAsync(ct);

        scope.ServiceProvider.GetRequiredService<TenantContext>().TenantId = job.TenantId;
        var acme = scope.ServiceProvider.GetRequiredService<AcmeIssuanceService>();
        var notify = scope.ServiceProvider.GetRequiredService<NotificationService>();
        try
        {
            if (resume) await acme.ContinueAsync(job, ct);
            else await acme.StartAsync(job, ct);

            if (job.Status == JobStatuses.WaitingForManualDns)
            {
                var profile = await db.Profiles.AsNoTracking().FirstOrDefaultAsync(p => p.Id == job.ProfileId, ct);
                await notify.NotifyAsync(job.TenantId, new AlertMessage(Severity.Warning, $"DNS records needed for '{profile?.Name}'",
                    "A renewal is waiting for you to create DNS TXT records. Create them, then click 'DNS ready' on the job.",
                    [new("Profile", profile?.Name ?? "?"), new("Instructions", job.Message ?? "")], "#/jobs"), ct);
            }
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Issuance failed for job {Job}", job.Id);
            job.SetStatus(JobStatuses.Failed, "Issuance failed: " + ex.Message);
            await db.SaveChangesAsync(ct);
            var profile = await db.Profiles.AsNoTracking().FirstOrDefaultAsync(p => p.Id == job.ProfileId, ct);
            await notify.NotifyAsync(job.TenantId, new AlertMessage(Severity.Critical, $"Certificate issuance failed: {profile?.Name}",
                "The certificate authority did not issue the certificate. The current certificate is still in place. Open the job and click 'Diagnose'.",
                [new("Profile", profile?.Name ?? "?"), new("Domains", profile?.Domains ?? ""), new("Error", ex.Message)], "#/jobs"), ct);
        }
    }
}

public static class FollowUps
{
    public static int PromoteHours(DeploymentTarget t)
    {
        var p = JsonSerializer.Deserialize<Dictionary<string, string>>(t.ParametersJson) ?? [];
        return p.TryGetValue("promoteAfterHours", out var v) && int.TryParse(v, out var h) ? Math.Clamp(h, 0, 720) : 48;
    }

    /// <summary>
    /// After a successful renewal/deployment: schedules the staged AD FS token promotion and/or the steps that run on
    /// other servers (AD FS relying-party refresh, CRM reload). Remote steps wait for the promotion when staging is used.
    /// </summary>
    public static void Create(AppDb db, Job parent, CertificateProfile profile, string thumbprint)
    {
        var local = profile.Targets.Where(t => t.AgentId is null || t.AgentId == profile.AgentId).ToList();
        var remote = profile.Targets.Where(t => t.AgentId is { } a && a != profile.AgentId).ToList();
        var staged = local.FirstOrDefault(t => t.Template == DeploymentTemplates.AdfsToken && PromoteHours(t) > 0);

        if (parent.Phase != JobPhases.Promote && staged is not null)
        {
            var hours = PromoteHours(staged);
            var promote = new Job
            {
                TenantId = parent.TenantId, AgentId = profile.AgentId, ProfileId = profile.Id, ParentJobId = parent.Id,
                Type = JobTypes.Deploy, Phase = JobPhases.Promote, ThumbprintToDeploy = thumbprint, TargetIds = staged.Id.ToString(),
                RequestedBy = "scheduler", NotBefore = DateTimeOffset.UtcNow.AddHours(hours)
            };
            promote.SetStatus(JobStatuses.Scheduled, $"Promote the new AD FS token certificate to primary at {promote.NotBefore:yyyy-MM-dd HH:mm} UTC (after relying parties refresh metadata).");
            db.Jobs.Add(promote);
            parent.Append($"Scheduled promotion to primary in {hours} h (job {promote.Id}).");
            return; // remote steps follow the promotion
        }

        foreach (var group in remote.GroupBy(t => t.AgentId!.Value))
        {
            var task = new Job
            {
                TenantId = parent.TenantId, AgentId = group.Key, ProfileId = profile.Id, ParentJobId = parent.Id, Type = JobTypes.Task,
                ThumbprintToDeploy = thumbprint, TargetIds = string.Join(',', group.OrderBy(t => t.Order).Select(t => t.Id)), RequestedBy = "scheduler"
            };
            task.SetStatus(JobStatuses.Pending, $"Follow-up steps after '{profile.Name}' renewal: {string.Join(", ", group.Select(t => t.Template))}");
            db.Jobs.Add(task);
            parent.Append($"Queued follow-up job {task.Id} on another server ({group.Count()} step(s)).");
        }
    }
}

/// <summary>Schedules renewals (maintenance windows, back-off), releases scheduled jobs, times out stuck jobs, runs alerts.</summary>
public sealed class RenewalPlanner(IServiceScopeFactory scopes, ILogger<RenewalPlanner> log) : BackgroundService
{
    private static readonly string[] AgentStages =
        [JobStatuses.Pending, JobStatuses.CsrRequested, JobStatuses.CertificateReady, JobStatuses.Deploying, JobStatuses.Issuing, JobStatuses.CsrSubmitted];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PlanRenewalsAsync(stoppingToken);
                await ReleaseScheduledAsync(stoppingToken);
                await TimeoutJobsAsync(stoppingToken);
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<AlertEngine>().RunAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested) { log.LogError(ex, "Planner loop error"); }
            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
        }
    }

    public static bool IsDue(CertificateProfile p, DateTimeOffset now) => NextRenewal(p) is not { } n || n <= now;

    public static DateTimeOffset? NextRenewal(CertificateProfile p)
    {
        if (p.CurrentNotAfter is null || p.CurrentNotBefore is null) return null;
        var lifetime = p.CurrentNotAfter.Value - p.CurrentNotBefore.Value;
        return p.CurrentNotAfter.Value - TimeSpan.FromTicks(lifetime.Ticks * Math.Clamp(p.RenewAtRemainingPercent, 5, 80) / 100);
    }

    public static bool InWindow(CertificateProfile p, DateTimeOffset now)
    {
        if (p.WindowStartHour is not { } s || p.WindowEndHour is not { } e || s == e) return true;
        var h = now.UtcDateTime.Hour;
        return s < e ? h >= s && h < e : h >= s || h < e;
    }

    private async Task PlanRenewalsAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        var now = DateTimeOffset.UtcNow;
        var profiles = await db.Profiles.IgnoreQueryFilters().Include(p => p.Targets).Where(p => p.Enabled).ToListAsync(ct);

        foreach (var p in profiles.Where(p => IsDue(p, now)))
        {
            var active = await db.Jobs.IgnoreQueryFilters().AnyAsync(j =>
                j.ProfileId == p.Id && j.Type == JobTypes.Renew && !JobStatuses.Terminal.Contains(j.Status), ct);
            if (active) continue;

            var disruptive = p.Targets.Any(t => DeploymentTemplates.All.TryGetValue(t.Template, out var d) && d.Disruptive);
            var urgent = p.CurrentNotAfter is { } na && na - now < TimeSpan.FromDays(3);
            if (disruptive && !urgent && !InWindow(p, now)) continue;

            var failures = await db.Jobs.IgnoreQueryFilters()
                .Where(j => j.ProfileId == p.Id && j.Type == JobTypes.Renew && j.Status == JobStatuses.Failed && j.UpdatedAt > now.AddDays(-7))
                .OrderByDescending(j => j.UpdatedAt).Select(j => j.UpdatedAt).ToListAsync(ct);
            var lastSuccess = await db.Jobs.IgnoreQueryFilters()
                .Where(j => j.ProfileId == p.Id && j.Type == JobTypes.Renew && j.Status == JobStatuses.Succeeded)
                .OrderByDescending(j => j.UpdatedAt).Select(j => (DateTimeOffset?)j.UpdatedAt).FirstOrDefaultAsync(ct);
            var consecutive = failures.Count(f => lastSuccess is null || f > lastSuccess);
            if (consecutive > 0 && failures[0] + TimeSpan.FromMinutes(Math.Min(24 * 60, 30 * Math.Pow(2, consecutive - 1))) > now) continue;

            db.Jobs.Add(NewRenewJob(p, "scheduler"));
            log.LogInformation("Scheduled renewal for profile {Profile}", p.Name);
        }
        await db.SaveChangesAsync(ct);
    }

    private async Task ReleaseScheduledAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        var now = DateTimeOffset.UtcNow;
        var due = await db.Jobs.IgnoreQueryFilters().Where(j => j.Status == JobStatuses.Scheduled && j.NotBefore <= now).ToListAsync(ct);
        foreach (var j in due)
        {
            var requireApproval = j.ProfileId is { } pid &&
                await db.Profiles.IgnoreQueryFilters().AnyAsync(p => p.Id == pid && p.RequireApproval, ct);
            j.SetStatus(requireApproval ? JobStatuses.AwaitingApproval : JobStatuses.Pending,
                requireApproval ? "Scheduled step is due and needs approval." : "Scheduled step is due.");
        }
        if (due.Count > 0) await db.SaveChangesAsync(ct);
    }

    public static Job NewRenewJob(CertificateProfile p, string requestedBy)
    {
        var disruptive = p.Targets.Any(t => DeploymentTemplates.All.TryGetValue(t.Template, out var d) && d.Disruptive);
        var job = new Job { TenantId = p.TenantId, AgentId = p.AgentId, ProfileId = p.Id, Type = JobTypes.Renew, RequestedBy = requestedBy };
        job.SetStatus(p.RequireApproval ? JobStatuses.AwaitingApproval : JobStatuses.Pending,
            p.RequireApproval ? (disruptive ? "Approval required (includes service restarts)." : "Approval required.") : null);
        return job;
    }

    private async Task TimeoutJobsAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        var cutoff = DateTimeOffset.UtcNow.AddHours(-3);
        var stuck = await db.Jobs.IgnoreQueryFilters().Where(j => AgentStages.Contains(j.Status) && j.UpdatedAt < cutoff).ToListAsync(ct);
        foreach (var j in stuck) j.SetStatus(JobStatuses.Failed, "Timed out (no progress for 3 hours). Check the agent is online.");
        var oldDns = await db.DnsTasks.IgnoreQueryFilters().Where(t => t.Status == "Pending" && t.CreatedAt < DateTimeOffset.UtcNow.AddMinutes(-10)).ToListAsync(ct);
        foreach (var t in oldDns) { t.Status = "Failed"; t.Message = "Expired: the agent did not pick up the DNS change."; }
        if (stuck.Count > 0 || oldDns.Count > 0) await db.SaveChangesAsync(ct);
    }
}

/// <summary>
/// Alert rules: expiry ladder (default 30/14/7/3/1 days) for server certificates and endpoints, agents offline,
/// approvals waiting, CA subscription coverage ending, low CA balance, DNS provider tests failing. Each alert is sent once.
/// </summary>
public sealed class AlertEngine(AppDb db, NotificationService notify, ILogger<AlertEngine> log)
{
    public async Task RunAsync(CancellationToken ct)
    {
        var tenants = await db.Tenants.AsNoTracking().ToListAsync(ct);
        foreach (var t in tenants)
        {
            try { await RunTenantAsync(t, ct); }
            catch (Exception ex) { log.LogWarning(ex, "Alert evaluation failed for tenant {Tenant}", t.Name); }
        }
        var old = DateTimeOffset.UtcNow.AddDays(-400);
        await db.AlertStates.IgnoreQueryFilters().Where(a => a.SentAt < old).ExecuteDeleteAsync(ct);
    }

    /// <summary>Executive summary of the estate: health, expiries, renewals, failures, coverage. Sent weekly or on demand.</summary>
    public async Task SendWeeklySummaryAsync(Tenant t, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var certs = await db.Discovered.IgnoreQueryFilters().AsNoTracking().Where(d => d.TenantId == t.Id)
            .Select(d => new { d.Thumbprint, d.NotAfter, d.HasPrivateKey }).ToListAsync(ct);
        var managed = (await db.Profiles.IgnoreQueryFilters().AsNoTracking().Where(p => p.TenantId == t.Id && p.CurrentThumbprint != null)
            .Select(p => p.CurrentThumbprint!).ToListAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var jobs = await db.Jobs.IgnoreQueryFilters().AsNoTracking().Where(j => j.TenantId == t.Id && j.UpdatedAt > now.AddDays(-7))
            .Select(j => new { j.Type, j.Status }).ToListAsync(ct);
        var endpoints = await db.Endpoints.IgnoreQueryFilters().AsNoTracking().Where(e => e.TenantId == t.Id)
            .Select(e => new { e.NotAfter, e.ChainValid }).ToListAsync(ct);
        var agents = await db.Agents.IgnoreQueryFilters().AsNoTracking().Where(a => a.TenantId == t.Id && !a.Revoked).Select(a => a.LastSeenAt).ToListAsync(ct);

        var live = certs.Where(c => c.HasPrivateKey && c.NotAfter > now).ToList();
        var coverage = live.Count == 0 ? 0 : (int)Math.Round(100.0 * live.Count(c => managed.Contains(c.Thumbprint)) / live.Count);
        var expired = certs.Count(c => c.NotAfter <= now) + endpoints.Count(e => e.NotAfter <= now);
        var exp30 = certs.Count(c => c.NotAfter > now && c.NotAfter <= now.AddDays(30)) + endpoints.Count(e => e.NotAfter > now && e.NotAfter <= now.AddDays(30));
        var manual30 = certs.Count(c => c.NotAfter > now && c.NotAfter <= now.AddDays(30) && !managed.Contains(c.Thumbprint));
        var renewed = jobs.Count(j => j.Type == JobTypes.Renew && j.Status == JobStatuses.Succeeded);
        var failed = jobs.Count(j => j.Status == JobStatuses.Failed);
        var offline = agents.Count(s => s is null || s < now.AddMinutes(-15));
        var untrusted = endpoints.Count(e => e.ChainValid == false);
        var severity = expired > 0 || manual30 > 0 || failed > 0 ? Severity.Warning : Severity.Success;

        await notify.NotifyAsync(t.Id, new AlertMessage(severity, $"Weekly certificate summary — {t.Name}",
            expired + manual30 + failed + offline == 0
                ? "All good: no expired certificates, no failed renewals and every server is online."
                : "Items that need attention are listed below. Open AutomateCert for details and one-click fixes.",
            [
                new("Certificates tracked", $"{certs.Count} on servers · {endpoints.Count} on endpoints"),
                new("Automation coverage", $"{coverage}% of live server certificates"),
                new("Expired", expired.ToString()),
                new("Expiring in 30 days", $"{exp30} ({manual30} without automation)"),
                new("Renewals last 7 days", $"{renewed} succeeded · {failed} failed"),
                new("Servers offline", offline.ToString()),
                new("Endpoints with trust problems", untrusted.ToString()),
            ], "#/overview", ForceEmail: true), ct);
    }

    public static int[] ParseDays(string? csv) =>
        (csv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => int.TryParse(s, out var d) ? d : -1).Where(d => d is >= 1 and <= 365)
            .Distinct().OrderByDescending(d => d).ToArray();

    private async Task<bool> MarkOnceAsync(Guid tenantId, string key, CancellationToken ct)
    {
        if (key.Length > 300) key = key[..300];
        if (db.AlertStates.Local.Any(a => a.TenantId == tenantId && a.Key == key)) return false;
        if (await db.AlertStates.IgnoreQueryFilters().AnyAsync(a => a.TenantId == tenantId && a.Key == key, ct)) return false;
        db.AlertStates.Add(new AlertState { TenantId = tenantId, Key = key });
        return true;
    }

    private static string? ExpiryKey(string prefix, string thumbprint, DateTimeOffset notAfter, int[] thresholds, DateTimeOffset now)
    {
        if (notAfter <= now) return $"{prefix}expired:{thumbprint}";
        var daysLeft = (int)Math.Floor((notAfter - now).TotalDays);
        var hit = thresholds.Where(th => daysLeft <= th).DefaultIfEmpty(-1).Min();
        return hit < 0 ? null : $"{prefix}exp:{thumbprint}:{hit}";
    }

    private async Task RunTenantAsync(Tenant t, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var thresholds = ParseDays(t.AlertDays);
        var maxDays = thresholds.Length > 0 ? thresholds[0] : 30;

        var managed = (await db.Profiles.IgnoreQueryFilters().Where(p => p.TenantId == t.Id && p.CurrentThumbprint != null)
                .Select(p => new { p.CurrentThumbprint, p.Name }).ToListAsync(ct))
            .GroupBy(p => p.CurrentThumbprint!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Name, StringComparer.OrdinalIgnoreCase);
        var agents = await db.Agents.IgnoreQueryFilters().Where(a => a.TenantId == t.Id).ToDictionaryAsync(a => a.Id, a => a, ct);

        var rows = new List<KeyValuePair<string, string>>();
        var critical = false;
        var certs = await db.Discovered.IgnoreQueryFilters()
            .Where(d => d.TenantId == t.Id && d.NotAfter > now.AddDays(-30) && d.NotAfter <= now.AddDays(maxDays))
            .OrderBy(d => d.NotAfter).ToListAsync(ct);
        foreach (var c in certs)
        {
            if (!t.AlertUnmanaged && !managed.ContainsKey(c.Thumbprint)) continue;
            var key = ExpiryKey("", c.Thumbprint, c.NotAfter, thresholds, now);
            if (key is null || !await MarkOnceAsync(t.Id, key, ct)) continue;
            critical |= c.NotAfter <= now.AddDays(7);
            var label = c.NotAfter <= now ? "EXPIRED" : $"{(int)Math.Floor((c.NotAfter - now).TotalDays)} days left";
            var automation = managed.TryGetValue(c.Thumbprint, out var p) ? $"auto-renews via '{p}'" : "NOT automated";
            var server = agents.TryGetValue(c.AgentId, out var a) ? a.Name : "?";
            rows.Add(new($"{label} · {c.NotAfter:yyyy-MM-dd}",
                $"{c.Subject} on {server} · {CertIntel.Vendor(c.Issuer, c.Subject)} ({automation}){(string.IsNullOrEmpty(c.Bindings) ? "" : " — " + c.Bindings)}"));
        }
        var endpoints = await db.Endpoints.IgnoreQueryFilters()
            .Where(e => e.TenantId == t.Id && e.NotAfter != null && e.Thumbprint != null && e.NotAfter <= now.AddDays(maxDays)).ToListAsync(ct);
        foreach (var e in endpoints)
        {
            var key = ExpiryKey("ep:", e.Thumbprint!, e.NotAfter!.Value, thresholds, now);
            if (key is null || !await MarkOnceAsync(t.Id, key, ct)) continue;
            critical |= e.NotAfter <= now.AddDays(7);
            var label = e.NotAfter <= now ? "EXPIRED" : $"{(int)Math.Floor((e.NotAfter.Value - now).TotalDays)} days left";
            rows.Add(new($"{label} · {e.NotAfter:yyyy-MM-dd}",
                $"Endpoint {e.Host}:{e.Port}{(string.IsNullOrEmpty(e.Label) ? "" : " (" + e.Label + ")")} · {CertIntel.Vendor(e.Issuer, e.Subject)} — renew on the device"));
        }
        if (rows.Count > 0)
            await notify.NotifyAsync(t.Id, new AlertMessage(critical ? Severity.Critical : Severity.Warning, $"{rows.Count} certificate(s) approaching expiry",
                "These certificates crossed an alert threshold. Automated ones renew on their own; the others need automation or manual renewal.",
                rows.Take(30).ToList(), "#/certificates?days=30"), ct);

        foreach (var a in agents.Values.Where(a => !a.Revoked && a.LastSeenAt is not null && a.LastSeenAt < now.AddMinutes(-15)))
            if (await MarkOnceAsync(t.Id, $"offline:{a.Id}:{a.LastSeenAt!.Value.UtcTicks}", ct))
                await notify.NotifyAsync(t.Id, new AlertMessage(Severity.Warning, $"Agent offline: {a.Name}",
                    "The agent has not checked in for over 15 minutes. Renewals and DNS changes through this server will not run until it is back.",
                    [new("Server", a.Name), new("Last seen", $"{a.LastSeenAt:yyyy-MM-dd HH:mm} UTC"), new("Last IP", a.LastIp ?? "?")], "#/agents"), ct);

        foreach (var j in await db.Jobs.IgnoreQueryFilters().Where(j => j.TenantId == t.Id && j.Status == JobStatuses.AwaitingApproval).ToListAsync(ct))
            if (await MarkOnceAsync(t.Id, $"approval:{j.Id}", ct))
            {
                var profile = await db.Profiles.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(p => p.Id == j.ProfileId, ct);
                await notify.NotifyAsync(t.Id, new AlertMessage(Severity.Info, $"Approval needed: {profile?.Name ?? j.Type}",
                    "A certificate change is waiting for approval. The requester cannot approve their own change.",
                    [new("Job", j.Type + (j.Phase is null ? "" : $" ({j.Phase})")), new("Requested by", j.RequestedBy), new("Note", j.Message ?? "")], "#/jobs?status=AwaitingApproval"), ct);
            }

        // Weekly executive summary: Monday after 06:00 UTC, once per ISO week.
        if (t.WeeklySummary && now.DayOfWeek == DayOfWeek.Monday && now.Hour >= 6 &&
            await MarkOnceAsync(t.Id, $"weekly:{System.Globalization.ISOWeek.GetYear(now.UtcDateTime)}-W{System.Globalization.ISOWeek.GetWeekOfYear(now.UtcDateTime)}", ct))
            await SendWeeklySummaryAsync(t, ct);

        foreach (var i in await db.Issuers.IgnoreQueryFilters().Where(i => i.TenantId == t.Id && i.CoverageEndsAt != null).ToListAsync(ct))
        {
            var days = (int)Math.Floor((i.CoverageEndsAt!.Value - now).TotalDays);
            var step = days <= 0 ? 0 : days <= 7 ? 7 : days <= 30 ? 30 : days <= 60 ? 60 : -1;
            if (step >= 0 && await MarkOnceAsync(t.Id, $"coverage:{i.Id}:{i.CoverageEndsAt:yyyyMMdd}:{step}", ct))
                await notify.NotifyAsync(t.Id, new AlertMessage(days <= 7 ? Severity.Critical : Severity.Warning,
                    days <= 0 ? $"CA subscription ended: {i.Name}" : $"CA subscription ends in {days} days: {i.Name}",
                    "Renewals through this CA will be refused once the paid coverage ends. Renew the subscription with the CA, or switch the affected automations to another CA (e.g. free Let's Encrypt for public names).",
                    [new("Issuer", i.Name), new("Coverage ends", $"{i.CoverageEndsAt:yyyy-MM-dd}")], "#/issuers"), ct);
        }

        foreach (var acc in await db.CaAccounts.IgnoreQueryFilters().Where(a => a.TenantId == t.Id && a.LowBalanceThreshold != null && a.SnapshotJson != null).ToListAsync(ct))
        {
            var node = JsonNode.Parse(acc.SnapshotJson!);
            var raw = node?["availableFunds"]?.ToString() ?? node?["balance"]?.ToString();
            if (decimal.TryParse(raw, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var bal) &&
                bal < acc.LowBalanceThreshold && await MarkOnceAsync(t.Id, $"balance:{acc.Id}:{now:yyyyMMdd}", ct))
                await notify.NotifyAsync(t.Id, new AlertMessage(Severity.Warning, $"Low CA balance: {acc.Name}",
                    "Available funds are below your threshold; new orders may fail.",
                    [new("Available", $"{bal} {node?["currency"]}"), new("Threshold", $"{acc.LowBalanceThreshold}")], "#/issuers"), ct);
        }

        await db.SaveChangesAsync(ct);
    }
}

/// <summary>Endpoint rescans (6 h), CT log sync (12 h), CA account sync (6 h).</summary>
public sealed class MonitoringWorker(IServiceScopeFactory scopes, ILogger<MonitoringWorker> log) : BackgroundService
{
    private DateTimeOffset _lastCt = DateTimeOffset.MinValue, _lastCa = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDb>();
                var scanner = scope.ServiceProvider.GetRequiredService<EndpointScanner>();
                var cutoff = DateTimeOffset.UtcNow.AddHours(-6);
                var due = await db.Endpoints.IgnoreQueryFilters().Where(e => e.LastCheckedAt == null || e.LastCheckedAt < cutoff)
                    .OrderBy(e => e.LastCheckedAt).Take(50).ToListAsync(stoppingToken);
                foreach (var e in due) await scanner.ScanAsync(e, stoppingToken);
                if (due.Count > 0) await db.SaveChangesAsync(stoppingToken);

                if (DateTimeOffset.UtcNow - _lastCt > TimeSpan.FromHours(12))
                {
                    _lastCt = DateTimeOffset.UtcNow;
                    var ct = scope.ServiceProvider.GetRequiredService<CtMonitor>();
                    foreach (var t in await db.Tenants.AsNoTracking().Where(t => t.CtDomains != null && t.CtDomains != "").ToListAsync(stoppingToken))
                        await ct.SyncTenantAsync(t, stoppingToken);
                }

                if (DateTimeOffset.UtcNow - _lastCa > TimeSpan.FromHours(6))
                {
                    _lastCa = DateTimeOffset.UtcNow;
                    var svc = scope.ServiceProvider.GetRequiredService<CaAccountService>();
                    foreach (var a in await db.CaAccounts.IgnoreQueryFilters().ToListAsync(stoppingToken))
                    {
                        try { a.SnapshotJson = (await svc.SyncAsync(a, stoppingToken)).ToJsonString(); a.LastError = null; }
                        catch (Exception ex) { a.LastError = ex.Message.Length > 1000 ? ex.Message[..1000] : ex.Message; log.LogWarning("CA account {Name} sync failed: {Error}", a.Name, ex.Message); }
                        a.LastSyncAt = DateTimeOffset.UtcNow;
                    }
                    await db.SaveChangesAsync(stoppingToken);
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested) { log.LogError(ex, "Monitoring loop error"); }
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }
}
