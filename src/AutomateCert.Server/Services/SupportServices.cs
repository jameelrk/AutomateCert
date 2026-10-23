using System.Collections.Concurrent;
using System.Net.Http.Json;
using AutomateCert.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace AutomateCert.Server.Services;

public sealed class AuditService(AppDb db)
{
    /// <summary>Adds an audit entry to the current unit of work. Caller saves.</summary>
    public void Record(Guid tenantId, string actor, string action, string target, string? details = null) =>
        db.Audit.Add(new AuditEntry
        {
            TenantId = tenantId,
            Actor = actor.Length > 256 ? actor[..256] : actor,
            Action = action,
            Target = target.Length > 300 ? target[..300] : target,
            Details = details is { Length: > 2000 } ? details[..2000] : details
        });
}

/// <summary>Delivers alerts by email and webhook. Never throws: notification failures are logged.</summary>
public sealed class NotificationService(
    AppDb db, EmailSender email, IHttpClientFactory http, IConfiguration config, ILogger<NotificationService> log)
{
    public async Task<IReadOnlyList<string>> RecipientsAsync(Tenant tenant, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(tenant.AlertEmails))
            return tenant.AlertEmails.Split(new[] { ',', ';', ' ', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(e => e.Contains('@')).Distinct(StringComparer.OrdinalIgnoreCase).Take(50).ToList();

        return await db.Users.IgnoreQueryFilters()
            .Where(u => u.TenantId == tenant.Id && !u.Disabled && u.NotifyByEmail && (u.Role == Roles.Admin || u.Role == Roles.Operator))
            .Select(u => u.Email).ToListAsync(ct);
    }

    public async Task NotifyAsync(Guid tenantId, AlertMessage alert, CancellationToken ct = default)
    {
        Tenant? tenant;
        try { tenant = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tenantId, ct); }
        catch (Exception ex) { log.LogWarning(ex, "Could not load tenant for notification"); return; }
        if (tenant is null) return;
        if (alert.Severity == Severity.Success && !tenant.NotifyOnSuccess && !alert.ForceEmail) return;
        log.LogInformation("Alert [{Severity}] {Title}", alert.Severity, alert.Title);

        if (email.IsConfigured)
        {
            try
            {
                var to = await RecipientsAsync(tenant, ct);
                var (html, text) = EmailTemplates.Render(alert, config["App:PublicUrl"]);
                var prefix = alert.Severity switch { Severity.Critical => "[ACTION] ", Severity.Warning => "[Warning] ", _ => "" };
                await email.SendAsync(to, $"{prefix}AutomateCert: {alert.Title}", html, text, ct);
            }
            catch (Exception ex) { log.LogWarning("Email notification failed: {Reason}", EmailSender.Explain(ex)); }
        }

        if (!string.IsNullOrWhiteSpace(tenant.WebhookUrl))
        {
            try
            {
                var lines = string.Join("\n", alert.Rows.Take(15).Select(r => $"- {r.Key}: {r.Value}"));
                using var client = http.CreateClient("webhook");
                using var resp = await client.PostAsJsonAsync(tenant.WebhookUrl,
                    new { text = $"**[AutomateCert] {alert.Title}**\n{alert.Summary}\n{lines}", severity = alert.Severity, title = alert.Title }, ct);
                if (!resp.IsSuccessStatusCode) log.LogWarning("Webhook returned {Status}", (int)resp.StatusCode);
            }
            catch (Exception ex) { log.LogWarning(ex, "Webhook notification failed"); }
        }
    }
}

/// <summary>In-memory ring buffer of recent server log events, shown under Logs → System (admins only).</summary>
public sealed class SystemLogBuffer
{
    public sealed record Entry(DateTimeOffset At, string Level, string Category, string Message);
    private readonly ConcurrentQueue<Entry> _q = new();
    private const int Max = 1000;

    public void Add(Entry e)
    {
        _q.Enqueue(e);
        while (_q.Count > Max && _q.TryDequeue(out _)) { }
    }

    public IReadOnlyList<Entry> Snapshot() => _q.Reverse().ToList();
}

public sealed class SystemLogProvider(SystemLogBuffer buffer) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new L(buffer, categoryName);
    public void Dispose() { }

    private sealed class L(SystemLogBuffer buffer, string category) : ILogger
    {
        private readonly bool _ours = category.StartsWith("AutomateCert", StringComparison.Ordinal);
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => level >= LogLevel.Warning || (_ours && level >= LogLevel.Information);

        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(level)) return;
            var msg = formatter(state, exception);
            if (exception is not null) msg += " | " + exception.GetType().Name + ": " + exception.Message;
            var shortCat = category[(category.LastIndexOf('.') + 1)..];
            buffer.Add(new SystemLogBuffer.Entry(DateTimeOffset.UtcNow, level.ToString(), shortCat, msg.Length > 2000 ? msg[..2000] : msg));
        }
    }
}
