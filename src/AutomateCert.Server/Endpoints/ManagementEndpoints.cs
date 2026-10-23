using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AutomateCert.Server.Data;
using AutomateCert.Server.Security;
using AutomateCert.Server.Services;
using AutomateCert.Shared;
using Microsoft.EntityFrameworkCore;

namespace AutomateCert.Server.Endpoints;

public sealed record IssuerInput(string Name, string Type, string? DirectoryUrl, string? ContactEmail,
    string? EabKeyId, string? EabHmacKey, string? AdcsConfig, string? AdcsTemplate, DateTimeOffset? CoverageEndsAt);
public sealed record IssuerUpdate(string? Name, DateTimeOffset? CoverageEndsAt, string? ContactEmail);
public sealed record DnsProviderInput(string Name, string Type, Dictionary<string, string>? Settings, string? AliasZone);
public sealed record DnsTestInput(string Domain);
public sealed record CaAccountInput(string Name, string Type, Dictionary<string, string>? Settings, decimal? LowBalanceThreshold);
public sealed record TargetInput(string Template, Dictionary<string, string>? Parameters, string? VerifyHost, int? VerifyPort, Guid? AgentId);
public sealed record ProfileInput(string Name, string[] Domains, string KeyAlgorithm, string? KeyStorage, Guid IssuerId,
    Guid? DnsProviderId, Guid AgentId, int RenewAtRemainingPercent, bool RequireApproval, bool Exportable, bool Enabled,
    TargetInput[] Targets, Guid? AdoptCertificateId = null, int? WindowStartHour = null, int? WindowEndHour = null, string? Scenario = null);
public sealed record DeployInput(string Thumbprint);
public sealed record TokenInput(int? MaxUses, int? ValidDays);
public sealed record EndpointInput(string Lines, string? Label);
public sealed record NetworkScanInput(string Cidr, string? Ports, string? Label);

public static partial class ManagementEndpoints
{
    [GeneratedRegex(@"^(\*\.)?([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.)*[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$")]
    private static partial Regex DomainRegex();
    [GeneratedRegex(@"^[A-Za-z0-9 ._\-\\]{1,300}$")]
    private static partial Regex AdcsRegex();
    [GeneratedRegex(@"^[A-Fa-f0-9]{40}$")]
    private static partial Regex ThumbprintRegex();
    [GeneratedRegex(@"^[A-Za-z0-9]([A-Za-z0-9\-\.]{0,253})$")]
    private static partial Regex HostRegex();

    private static IResult Bad(string error) => Results.BadRequest(new { error });

    public static void Map(WebApplication app)
    {
        var read = app.MapGroup("/api").RequireAuthorization();
        var op = app.MapGroup("/api").RequireAuthorization("Operator");
        var approve = app.MapGroup("/api").RequireAuthorization("Approve");
        var admin = app.MapGroup("/api").RequireAuthorization("Admin");

        // ---------------- Dashboard ----------------
        read.MapGet("/dashboard", async (AppDb db) =>
        {
            var now = DateTimeOffset.UtcNow;
            var discovered = db.Discovered.AsNoTracking();
            var upcoming = await discovered.Where(d => d.NotAfter > now).OrderBy(d => d.NotAfter).Take(8)
                .Select(d => new { d.Id, d.Subject, d.Thumbprint, d.NotAfter, d.AgentId, d.Bindings, d.Issuer }).ToListAsync();
            var agentNames = await db.Agents.AsNoTracking().ToDictionaryAsync(a => a.Id, a => a.Name);
            return Results.Ok(new
            {
                certificates = await discovered.CountAsync(),
                expired = await discovered.CountAsync(d => d.NotAfter <= now),
                expiring7 = await discovered.CountAsync(d => d.NotAfter > now && d.NotAfter <= now.AddDays(7)),
                expiring30 = await discovered.CountAsync(d => d.NotAfter > now && d.NotAfter <= now.AddDays(30)),
                endpoints = await db.Endpoints.CountAsync(),
                endpointsAtRisk = await db.Endpoints.CountAsync(e => (e.NotAfter != null && e.NotAfter <= now.AddDays(30)) || e.ChainValid == false || e.NameMatches == false),
                profiles = await db.Profiles.CountAsync(),
                profilesEnabled = await db.Profiles.CountAsync(p => p.Enabled),
                agents = await db.Agents.CountAsync(a => !a.Revoked),
                agentsOnline = await db.Agents.CountAsync(a => !a.Revoked && a.LastSeenAt > now.AddMinutes(-5)),
                awaitingAction = await db.Jobs.CountAsync(j => j.Status == JobStatuses.AwaitingApproval || j.Status == JobStatuses.WaitingForManualDns),
                failed7 = await db.Jobs.CountAsync(j => j.Status == JobStatuses.Failed && j.UpdatedAt > now.AddDays(-7)),
                succeeded30 = await db.Jobs.CountAsync(j => j.Status == JobStatuses.Succeeded && j.Type == JobTypes.Renew && j.UpdatedAt > now.AddDays(-30)),
                hasIssuer = await db.Issuers.AnyAsync(),
                hasDns = await db.DnsProviders.AnyAsync(d => d.Type != DnsProviderTypes.Manual),
                upcoming = upcoming.Select(u => new { u.Id, u.Subject, u.Thumbprint, u.NotAfter, u.Bindings, vendor = CertIntel.Vendor(u.Issuer, u.Subject), agent = agentNames.GetValueOrDefault(u.AgentId, "?") })
            });
        });

        // ---------------- Inventory ----------------
        read.MapGet("/certificates", async (AppDb db, string? q, string? days) =>
        {
            var query = db.Discovered.AsNoTracking();
            if (!string.IsNullOrWhiteSpace(q))
                query = query.Where(d => d.Subject.Contains(q) || d.DnsNames.Contains(q) || d.Thumbprint.Contains(q) || d.Issuer.Contains(q));
            if (int.TryParse(days, out var dayLimit) && dayLimit is > 0 and <= 3650)
            {
                var cutoff = DateTimeOffset.UtcNow.AddDays(dayLimit);
                query = query.Where(d => d.NotAfter <= cutoff);
            }
            var agents = await db.Agents.AsNoTracking().ToDictionaryAsync(a => a.Id, a => a.Name);
            var managed = (await db.Profiles.AsNoTracking().Where(p => p.CurrentThumbprint != null).Select(p => new { p.CurrentThumbprint, p.Name }).ToListAsync())
                .GroupBy(p => p.CurrentThumbprint!, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Name, StringComparer.OrdinalIgnoreCase);
            var now = DateTimeOffset.UtcNow;
            var list = await query.OrderBy(d => d.NotAfter).Take(3000).ToListAsync();
            return Results.Ok(list.Select(d => new
            {
                d.Id, d.Thumbprint, d.Subject, d.DnsNames, d.Issuer, d.NotBefore, d.NotAfter, d.HasPrivateKey, d.Store, d.KeyInfo, d.SignatureAlgorithm,
                d.Bindings, d.LastSeenAt, agent = agents.GetValueOrDefault(d.AgentId, "?"), d.AgentId, vendor = CertIntel.Vendor(d.Issuer, d.Subject),
                internalOnly = d.DnsNames.Length > 0 && d.DnsNames.Split(',').All(n => CertIntel.InternalNameReason(n.Trim()) is not null),
                issues = CertIntel.Check(d.KeyInfo, d.SignatureAlgorithm, d.NotBefore, d.NotAfter, d.Issuer, d.Subject, d.DnsNames, d.Bindings.Length > 0, now).Count,
                managedBy = managed.GetValueOrDefault(d.Thumbprint)
            }));
        });

        // ---------------- Agents ----------------
        read.MapGet("/agents", async (AppDb db) =>
        {
            var now = DateTimeOffset.UtcNow;
            var list = await db.Agents.AsNoTracking().OrderBy(a => a.Name).ToListAsync();
            var counts = await db.Discovered.AsNoTracking().GroupBy(d => d.AgentId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count);
            return Results.Ok(list.Select(a => new
            {
                a.Id, a.Name, a.Version, a.OsVersion, a.LastIp, a.LastSeenAt, a.Revoked, a.CreatedAt,
                roles = (a.Roles ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries),
                online = !a.Revoked && a.LastSeenAt > now.AddMinutes(-5), certificates = counts.GetValueOrDefault(a.Id, 0)
            }));
        });

        admin.MapPost("/agents/enrollment-tokens", async (TokenInput? r, AppDb db, TenantContext t, AuditService audit) =>
        {
            var maxUses = Math.Clamp(r?.MaxUses ?? 1, 1, 1000);
            var validDays = Math.Clamp(r?.ValidDays ?? 1, 1, 30);
            var token = Secrets.NewToken();
            var expires = DateTimeOffset.UtcNow.AddDays(validDays);
            db.EnrollmentTokens.Add(new EnrollmentToken { TokenHash = Secrets.Hash(token), ExpiresAt = expires, CreatedBy = t.Actor, MaxUses = maxUses });
            audit.Record(t.TenantId, t.Actor, "agent.token.created", maxUses == 1 ? "single-server token" : $"fleet token ({maxUses} servers)", $"expires {expires:u}");
            await db.SaveChangesAsync();
            return Results.Ok(new { token, expiresAt = expires, maxUses });
        });

        admin.MapPost("/agents/{id:guid}/revoke", async (Guid id, AppDb db, TenantContext t, AuditService audit) =>
        {
            var a = await db.Agents.FirstOrDefaultAsync(x => x.Id == id);
            if (a is null) return Results.NotFound();
            a.Revoked = true;
            audit.Record(t.TenantId, t.Actor, "agent.revoked", a.Name);
            await db.SaveChangesAsync();
            return Results.Ok();
        });

        op.MapPost("/agents/{id:guid}/discover", async (Guid id, AppDb db, TenantContext t, AuditService audit) =>
        {
            var a = await db.Agents.FirstOrDefaultAsync(x => x.Id == id && !x.Revoked);
            if (a is null) return Results.NotFound();
            var job = new Job { AgentId = a.Id, Type = JobTypes.Discover, RequestedBy = t.Actor };
            job.SetStatus(JobStatuses.Pending);
            db.Jobs.Add(job);
            audit.Record(t.TenantId, t.Actor, "agent.discover", a.Name);
            await db.SaveChangesAsync();
            return Results.Ok(new { job.Id });
        });

        // ---------------- Endpoints ----------------
        read.MapGet("/endpoints", async (AppDb db) =>
        {
            var list = await db.Endpoints.AsNoTracking().OrderBy(e => e.NotAfter == null).ThenBy(e => e.NotAfter).ToListAsync();
            return Results.Ok(list.Select(e => new
            {
                e.Id, e.Host, e.Port, e.Label, e.Thumbprint, e.Subject, e.Issuer, e.DnsNames, e.NotBefore, e.NotAfter, e.KeyInfo, e.TlsProtocol,
                e.ChainValid, e.NameMatches, e.ChainStatus, e.LastError, e.LastCheckedAt, vendor = CertIntel.Vendor(e.Issuer, e.Subject)
            }));
        });

        op.MapPost("/endpoints", async (EndpointInput r, AppDb db, TenantContext t, AuditService audit) =>
        {
            var (added, invalid) = await AddEndpointsAsync(db, (r.Lines ?? "").Split(new[] { '\n', ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), r.Label);
            if (added > 0) { audit.Record(t.TenantId, t.Actor, "endpoint.added", $"{added} endpoint(s)", r.Label); await db.SaveChangesAsync(); }
            return Results.Ok(new { added, invalid });
        });

        op.MapPost("/endpoints/discover", async (NetworkScanInput r, AppDb db, EndpointScanner scanner, TenantContext t, AuditService audit, CancellationToken ct) =>
        {
            var ports = (r.Ports ?? "443,8443,636,3389").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(p => int.TryParse(p, out var n) ? n : 0).Where(n => n is > 0 and < 65536).Distinct().Take(8).ToArray();
            if (ports.Length == 0) return Bad("Enter at least one port.");
            List<(string Host, int Port)> found;
            try { found = await scanner.DiscoverAsync(r.Cidr ?? "", ports, ct); }
            catch (InvalidOperationException ex) { return Bad(ex.Message); }
            var (added, _) = await AddEndpointsAsync(db, found.Select(f => $"{f.Host}:{f.Port}"), string.IsNullOrWhiteSpace(r.Label) ? $"Discovered {r.Cidr}" : r.Label);
            audit.Record(t.TenantId, t.Actor, "endpoint.discovery", r.Cidr ?? "", $"{found.Count} TLS service(s), {added} new");
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { found = found.Count, added });
        });

        op.MapPost("/endpoints/{id:guid}/scan", async (Guid id, AppDb db, EndpointScanner scanner, CancellationToken ct) =>
        {
            var e = await db.Endpoints.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (e is null) return Results.NotFound();
            await scanner.ScanAsync(e, ct);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { e.LastError, e.NotAfter });
        });

        op.MapDelete("/endpoints/{id:guid}", async (Guid id, AppDb db, TenantContext t, AuditService audit) =>
        {
            var e = await db.Endpoints.FirstOrDefaultAsync(x => x.Id == id);
            if (e is null) return Results.NotFound();
            db.Endpoints.Remove(e);
            audit.Record(t.TenantId, t.Actor, "endpoint.deleted", $"{e.Host}:{e.Port}");
            await db.SaveChangesAsync();
            return Results.Ok();
        });

        // ---------------- Issuers ----------------
        read.MapGet("/issuers", async (AppDb db) =>
        {
            var list = await db.Issuers.AsNoTracking().OrderBy(i => i.Name).ToListAsync();
            var usage = await db.Profiles.AsNoTracking().GroupBy(p => p.IssuerId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count);
            return Results.Ok(list.Select(i => new
            {
                i.Id, i.Name, i.Type, i.DirectoryUrl, i.ContactEmail, i.EabKeyId, i.AdcsConfig, i.AdcsTemplate, i.CoverageEndsAt,
                registered = i.AccountKeyProtected != null, hasEab = i.EabHmacKeyProtected != null,
                isPublic = i.Type == IssuerTypes.Acme && CertIntel.IsPublicAcme(i.DirectoryUrl),
                isFree = CertIntel.IsLetsEncrypt(i.DirectoryUrl), profiles = usage.GetValueOrDefault(i.Id, 0)
            }));
        });

        admin.MapPost("/issuers", async (IssuerInput r, AppDb db, SecretProtector sp, TenantContext t, AuditService audit) =>
        {
            if (string.IsNullOrWhiteSpace(r.Name)) return Bad("Name is required.");
            var issuer = new Issuer { Name = r.Name.Trim(), Type = r.Type, CoverageEndsAt = r.CoverageEndsAt };
            if (r.Type == IssuerTypes.Acme)
            {
                if (!Uri.TryCreate(r.DirectoryUrl?.Trim(), UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps)
                    return Bad("ACME directory URL must be an absolute https:// URL.");
                issuer.DirectoryUrl = u.ToString();
                issuer.ContactEmail = r.ContactEmail?.Trim();
                if (!string.IsNullOrWhiteSpace(r.EabKeyId))
                {
                    if (string.IsNullOrWhiteSpace(r.EabHmacKey)) return Bad("EAB HMAC key is required when EAB key ID is set.");
                    issuer.EabKeyId = r.EabKeyId.Trim();
                    issuer.EabHmacKeyProtected = sp.Protect(r.EabHmacKey.Trim());
                }
            }
            else if (r.Type == IssuerTypes.AdcsInternal)
            {
                if (string.IsNullOrWhiteSpace(r.AdcsConfig) || !AdcsRegex().IsMatch(r.AdcsConfig)) return Bad("CA config must look like 'CAHOST.domain.local\\Issuing CA'.");
                if (string.IsNullOrWhiteSpace(r.AdcsTemplate) || !AdcsRegex().IsMatch(r.AdcsTemplate)) return Bad("Certificate template name is required.");
                issuer.AdcsConfig = r.AdcsConfig.Trim();
                issuer.AdcsTemplate = r.AdcsTemplate.Trim();
            }
            else return Bad("Type must be Acme or AdcsInternal.");
            db.Issuers.Add(issuer);
            audit.Record(t.TenantId, t.Actor, "issuer.created", issuer.Name, issuer.Type);
            await db.SaveChangesAsync();
            return Results.Ok(new { issuer.Id });
        });

        admin.MapPut("/issuers/{id:guid}", async (Guid id, IssuerUpdate r, AppDb db, TenantContext t, AuditService audit) =>
        {
            var i = await db.Issuers.FirstOrDefaultAsync(x => x.Id == id);
            if (i is null) return Results.NotFound();
            if (!string.IsNullOrWhiteSpace(r.Name)) i.Name = r.Name.Trim();
            i.CoverageEndsAt = r.CoverageEndsAt;
            if (r.ContactEmail is not null) i.ContactEmail = r.ContactEmail.Trim();
            audit.Record(t.TenantId, t.Actor, "issuer.updated", i.Name, $"coverage {r.CoverageEndsAt:yyyy-MM-dd}");
            await db.SaveChangesAsync();
            return Results.Ok();
        });

        admin.MapDelete("/issuers/{id:guid}", async (Guid id, AppDb db, TenantContext t, AuditService audit) =>
        {
            var i = await db.Issuers.FirstOrDefaultAsync(x => x.Id == id);
            if (i is null) return Results.NotFound();
            if (await db.Profiles.AnyAsync(p => p.IssuerId == id)) return Bad("Issuer is used by an automation profile.");
            db.Issuers.Remove(i);
            audit.Record(t.TenantId, t.Actor, "issuer.deleted", i.Name);
            await db.SaveChangesAsync();
            return Results.Ok();
        });

        // ---------------- DNS providers ----------------
        read.MapGet("/dns-provider-types", () => Results.Ok(DnsProviderTypes.Catalog));

        read.MapGet("/dns-providers", async (AppDb db) =>
        {
            var list = await db.DnsProviders.AsNoTracking().OrderBy(d => d.Name).ToListAsync();
            var usage = await db.Profiles.AsNoTracking().Where(p => p.DnsProviderId != null).GroupBy(p => p.DnsProviderId!.Value).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count);
            return Results.Ok(list.Select(d => new
            {
                d.Id, d.Name, d.Type, d.AliasZone, settings = JsonMap.Read(d.ConfigJson), hasCredential = d.CredentialProtected != null,
                d.LastTestAt, d.LastTestOk, d.LastTestMessage, profiles = usage.GetValueOrDefault(d.Id, 0),
                typeName = DnsProviderTypes.Catalog.FirstOrDefault(c => c.Type == d.Type)?.Name ?? d.Type
            }));
        });

        admin.MapPost("/dns-providers", async (DnsProviderInput r, AppDb db, SecretProtector sp, TenantContext t, AuditService audit) =>
        {
            var d = new DnsProvider();
            if (await ApplyDnsAsync(d, r, db, sp, true) is { } err) return Bad(err);
            db.DnsProviders.Add(d);
            audit.Record(t.TenantId, t.Actor, "dns.created", d.Name, d.Type + (d.AliasZone is null ? "" : $" alias {d.AliasZone}"));
            await db.SaveChangesAsync();
            return Results.Ok(new { d.Id });
        });

        admin.MapPut("/dns-providers/{id:guid}", async (Guid id, DnsProviderInput r, AppDb db, SecretProtector sp, TenantContext t, AuditService audit) =>
        {
            var d = await db.DnsProviders.FirstOrDefaultAsync(x => x.Id == id);
            if (d is null) return Results.NotFound();
            if (await ApplyDnsAsync(d, r, db, sp, false) is { } err) return Bad(err);
            audit.Record(t.TenantId, t.Actor, "dns.updated", d.Name, d.Type);
            await db.SaveChangesAsync();
            return Results.Ok();
        });

        admin.MapDelete("/dns-providers/{id:guid}", async (Guid id, AppDb db, TenantContext t, AuditService audit) =>
        {
            var d = await db.DnsProviders.FirstOrDefaultAsync(x => x.Id == id);
            if (d is null) return Results.NotFound();
            if (await db.Profiles.AnyAsync(p => p.DnsProviderId == id)) return Bad("DNS provider is used by an automation profile.");
            db.DnsProviders.Remove(d);
            audit.Record(t.TenantId, t.Actor, "dns.deleted", d.Name);
            await db.SaveChangesAsync();
            return Results.Ok();
        });

        // End-to-end test: create TXT → check public DNS → delete. Results are logged and audited.
        op.MapPost("/dns-providers/{id:guid}/test", async (Guid id, DnsTestInput r, AppDb db, DnsProviderFactory factory, TenantContext t, AuditService audit, ILogger<DnsProvider> log, CancellationToken ct) =>
        {
            var d = await db.DnsProviders.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (d is null) return Results.NotFound();
            var domain = (r.Domain ?? "").Trim().ToLowerInvariant().TrimStart('*', '.');
            if (!DomainRegex().IsMatch(domain) || !domain.Contains('.')) return Bad("Enter a domain you manage, e.g. contoso.com.");
            if (d.Type == DnsProviderTypes.Manual) return Bad("Manual DNS has nothing to test.");
            var steps = new List<string>();
            var value = Secrets.NewToken(16);
            var name = $"_automatecert-test.{domain}";
            var ok = false;
            try
            {
                var p = factory.Create(d);
                steps.Add($"Creating TXT {name}{(d.AliasZone is null ? "" : $" (alias → {name}.{d.AliasZone})")} …");
                var rid = await p.CreateTxtAsync(name, value, ct);
                steps.Add("✓ Created through the provider API.");
                var checkName = string.IsNullOrWhiteSpace(d.AliasZone) ? name : $"{name}.{d.AliasZone}";
                var visible = await DnsPropagation.WaitForTxtAsync(checkName, value, TimeSpan.FromSeconds(90), ct);
                steps.Add(visible ? "✓ Visible on public resolvers (1.1.1.1 / 8.8.8.8)." : "⚠ Not visible on public resolvers within 90 s (slow propagation, or the zone is not public).");
                await p.DeleteTxtAsync(name, value, rid, ct);
                steps.Add("✓ Removed the test record.");
                ok = visible;
            }
            catch (Exception ex)
            {
                steps.Add("✗ " + ex.Message);
                log.LogWarning("DNS provider test {Name} failed: {Error}", d.Name, ex.Message);
            }
            d.LastTestAt = DateTimeOffset.UtcNow;
            d.LastTestOk = ok;
            var summary = string.Join(" ", steps.TakeLast(2));
            d.LastTestMessage = summary.Length > 1000 ? summary[..1000] : summary;
            audit.Record(t.TenantId, t.Actor, ok ? "dns.test.ok" : "dns.test.failed", d.Name, string.Join(" | ", steps));
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { ok, steps });
        }).RequireRateLimiting("ai");

        read.MapGet("/dns-providers/{id:guid}/cnames", async (Guid id, string? domains, AppDb db, CancellationToken ct) =>
        {
            var d = await db.DnsProviders.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
            if (d is null) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(d.AliasZone)) return Results.Ok(Array.Empty<object>());
            var list = new List<object>();
            foreach (var (n, target) in DnsProviderFactory.RequiredCnames(d.AliasZone, (domains ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(50)))
            {
                var actual = await DnsPropagation.CnameTargetAsync(n, ct);
                list.Add(new { name = n, target, actual, ok = actual is not null && actual.Equals(target, StringComparison.OrdinalIgnoreCase) });
            }
            return Results.Ok(list);
        });

        // ---------------- CA accounts (balances / orders) ----------------
        read.MapGet("/ca-account-types", () => Results.Ok(CaAccountService.Catalog));

        read.MapGet("/ca-accounts", async (AppDb db) =>
        {
            var list = await db.CaAccounts.AsNoTracking().OrderBy(a => a.Name).ToListAsync();
            var now = DateTimeOffset.UtcNow;
            var inv = await db.Discovered.AsNoTracking().Where(d => d.NotAfter > now).Select(d => new { d.Issuer, d.Subject }).ToListAsync();
            var vendors = inv.GroupBy(x => CertIntel.Vendor(x.Issuer, x.Subject)).Select(g => new { vendor = g.Key, count = g.Count(), free = CertIntel.FreeVendors.Contains(g.Key) })
                .OrderByDescending(x => x.count).ToList();
            var issuedByAutomateCert = await db.Jobs.AsNoTracking().Where(j => j.Type == JobTypes.Renew && j.Status == JobStatuses.Succeeded && j.UpdatedAt > now.AddDays(-90))
                .Join(db.Profiles.AsNoTracking(), j => j.ProfileId, p => (Guid?)p.Id, (j, p) => p.IssuerId)
                .Join(db.Issuers.AsNoTracking(), i => i, s => s.Id, (i, s) => s.Name).GroupBy(n => n).Select(g => new { issuer = g.Key, count = g.Count() }).ToListAsync();
            return Results.Ok(new
            {
                accounts = list.Select(a => new
                {
                    a.Id, a.Name, a.Type, a.LastSyncAt, a.LastError, a.LowBalanceThreshold, settings = JsonMap.Read(a.ConfigJson),
                    snapshot = a.SnapshotJson is null ? null : JsonNode.Parse(a.SnapshotJson)
                }),
                vendors, issuedByAutomateCert
            });
        });

        admin.MapPost("/ca-accounts", async (CaAccountInput r, AppDb db, SecretProtector sp, CaAccountService svc, TenantContext t, AuditService audit, CancellationToken ct) =>
        {
            var type = CaAccountService.Catalog.FirstOrDefault(c => c.Type == r.Type);
            if (type is null) return Bad("Unsupported CA account type.");
            if (string.IsNullOrWhiteSpace(r.Name)) return Bad("Name is required.");
            var s = r.Settings ?? [];
            foreach (var f in type.Fields.Where(f => f.Required))
                if (!s.TryGetValue(f.Key, out var v) || string.IsNullOrWhiteSpace(v)) return Bad($"{f.Label} is required.");
            var a = new CaAccount
            {
                Name = r.Name.Trim(), Type = r.Type, LowBalanceThreshold = r.LowBalanceThreshold,
                ConfigJson = JsonMap.Write(type.Fields.Where(f => !f.Secret && s.ContainsKey(f.Key)).ToDictionary(f => f.Key, f => s[f.Key].Trim())),
                CredentialProtected = sp.ProtectMap(type.Fields.Where(f => f.Secret && s.ContainsKey(f.Key)).ToDictionary(f => f.Key, f => s[f.Key].Trim()))
            };
            try { a.SnapshotJson = (await svc.SyncAsync(a, ct)).ToJsonString(); }
            catch (Exception ex) { a.LastError = ex.Message.Length > 1000 ? ex.Message[..1000] : ex.Message; }
            a.LastSyncAt = DateTimeOffset.UtcNow;
            db.CaAccounts.Add(a);
            audit.Record(t.TenantId, t.Actor, "ca.account.created", a.Name, a.Type);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { a.Id, a.LastError });
        });

        op.MapPost("/ca-accounts/{id:guid}/sync", async (Guid id, AppDb db, CaAccountService svc, CancellationToken ct) =>
        {
            var a = await db.CaAccounts.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (a is null) return Results.NotFound();
            try { a.SnapshotJson = (await svc.SyncAsync(a, ct)).ToJsonString(); a.LastError = null; }
            catch (Exception ex) { a.LastError = ex.Message.Length > 1000 ? ex.Message[..1000] : ex.Message; }
            a.LastSyncAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { a.LastError });
        }).RequireRateLimiting("ai");

        admin.MapDelete("/ca-accounts/{id:guid}", async (Guid id, AppDb db, TenantContext t, AuditService audit) =>
        {
            var a = await db.CaAccounts.FirstOrDefaultAsync(x => x.Id == id);
            if (a is null) return Results.NotFound();
            db.CaAccounts.Remove(a);
            audit.Record(t.TenantId, t.Actor, "ca.account.deleted", a.Name);
            await db.SaveChangesAsync();
            return Results.Ok();
        });

        // ---------------- Templates ----------------
        read.MapGet("/templates", () => Results.Ok(DeploymentTemplates.All.Values.Select(d => d with
        {
            Parameters = d.Parameters.Where(p => !DeploymentTemplates.IsHiddenParameter(p.Name)).ToArray()
        })));

        // ---------------- Profiles ----------------
        read.MapGet("/profiles", async (AppDb db) =>
        {
            var list = await db.Profiles.AsNoTracking().Include(p => p.Targets).Include(p => p.Issuer).Include(p => p.Agent).Include(p => p.DnsProvider).OrderBy(p => p.Name).ToListAsync();
            var agents = await db.Agents.AsNoTracking().ToDictionaryAsync(a => a.Id, a => a.Name);
            var now = DateTimeOffset.UtcNow;
            return Results.Ok(list.Select(p => new
            {
                p.Id, p.Name, domains = p.DomainList(), p.KeyAlgorithm, p.KeyStorage, p.IssuerId, issuer = p.Issuer?.Name,
                isFree = CertIntel.IsLetsEncrypt(p.Issuer?.DirectoryUrl),
                p.DnsProviderId, dnsProvider = p.DnsProvider?.Name, p.AgentId, agent = p.Agent?.Name,
                p.RenewAtRemainingPercent, p.RequireApproval, p.Exportable, p.Enabled, p.WindowStartHour, p.WindowEndHour, p.Scenario,
                p.CurrentThumbprint, p.CurrentNotBefore, p.CurrentNotAfter, p.LastRenewedAt,
                nextRenewal = RenewalPlanner.NextRenewal(p), due = p.Enabled && RenewalPlanner.IsDue(p, now),
                targets = p.Targets.OrderBy(x => x.Order).Select(x => new
                {
                    x.Id, x.Template, parameters = JsonSerializer.Deserialize<Dictionary<string, string>>(x.ParametersJson), x.VerifyHost, x.VerifyPort, x.AgentId,
                    agent = x.AgentId is { } aid ? agents.GetValueOrDefault(aid) : null
                })
            }));
        });

        op.MapPost("/profiles", async (ProfileInput r, AppDb db, TenantContext t, AuditService audit) =>
        {
            var p = new CertificateProfile();
            if (await ApplyAsync(p, r, db) is { } err) return Bad(err);
            if (r.AdoptCertificateId is { } adoptId)
            {
                var c = await db.Discovered.AsNoTracking().FirstOrDefaultAsync(d => d.Id == adoptId);
                if (c is null) return Bad("The certificate to adopt was not found.");
                if (c.AgentId != p.AgentId) return Bad("The adopted certificate lives on a different server than the one selected.");
                p.CurrentThumbprint = c.Thumbprint;
                p.CurrentNotBefore = c.NotBefore;
                p.CurrentNotAfter = c.NotAfter;
            }
            db.Profiles.Add(p);
            audit.Record(t.TenantId, t.Actor, r.AdoptCertificateId is null ? "profile.created" : "profile.adopted", p.Name, p.Domains);
            await db.SaveChangesAsync();
            return Results.Ok(new { p.Id, nextRenewal = RenewalPlanner.NextRenewal(p) });
        });

        op.MapPut("/profiles/{id:guid}", async (Guid id, ProfileInput r, AppDb db, TenantContext t, AuditService audit) =>
        {
            var p = await db.Profiles.Include(x => x.Targets).FirstOrDefaultAsync(x => x.Id == id);
            if (p is null) return Results.NotFound();
            db.Targets.RemoveRange(p.Targets);
            p.Targets.Clear();
            if (await ApplyAsync(p, r, db) is { } err) return Bad(err);
            audit.Record(t.TenantId, t.Actor, "profile.updated", p.Name, p.Domains);
            await db.SaveChangesAsync();
            return Results.Ok();
        });

        op.MapDelete("/profiles/{id:guid}", async (Guid id, AppDb db, TenantContext t, AuditService audit) =>
        {
            var p = await db.Profiles.Include(x => x.Targets).FirstOrDefaultAsync(x => x.Id == id);
            if (p is null) return Results.NotFound();
            db.Profiles.Remove(p);
            audit.Record(t.TenantId, t.Actor, "profile.deleted", p.Name);
            await db.SaveChangesAsync();
            return Results.Ok();
        });

        op.MapPost("/profiles/{id:guid}/renew", async (Guid id, AppDb db, TenantContext t, AuditService audit) =>
        {
            var p = await db.Profiles.Include(x => x.Targets).FirstOrDefaultAsync(x => x.Id == id);
            if (p is null) return Results.NotFound();
            if (await db.Jobs.AnyAsync(j => j.ProfileId == id && j.Type == JobTypes.Renew && !JobStatuses.Terminal.Contains(j.Status)))
                return Bad("A renewal for this profile is already in progress.");
            var job = RenewalPlanner.NewRenewJob(p, t.Actor);
            db.Jobs.Add(job);
            audit.Record(t.TenantId, t.Actor, "profile.renew", p.Name);
            await db.SaveChangesAsync();
            return Results.Ok(new { job.Id });
        });

        op.MapPost("/profiles/{id:guid}/deploy", async (Guid id, DeployInput r, AppDb db, TenantContext t, AuditService audit) =>
        {
            var p = await db.Profiles.FirstOrDefaultAsync(x => x.Id == id);
            if (p is null) return Results.NotFound();
            if (!ThumbprintRegex().IsMatch(r.Thumbprint ?? "")) return Bad("Invalid thumbprint.");
            var job = new Job { AgentId = p.AgentId, ProfileId = p.Id, Type = JobTypes.Deploy, RequestedBy = t.Actor, ThumbprintToDeploy = r.Thumbprint.ToUpperInvariant() };
            job.SetStatus(p.RequireApproval ? JobStatuses.AwaitingApproval : JobStatuses.Pending);
            db.Jobs.Add(job);
            audit.Record(t.TenantId, t.Actor, "profile.deploy", p.Name, r.Thumbprint);
            await db.SaveChangesAsync();
            return Results.Ok(new { job.Id });
        });

        // ---------------- Jobs ----------------
        read.MapGet("/jobs", async (AppDb db, string? status) =>
        {
            var q = db.Jobs.AsNoTracking();
            if (!string.IsNullOrWhiteSpace(status)) q = q.Where(j => j.Status == status);
            var list = await q.OrderByDescending(j => j.CreatedAt).Take(300)
                .Select(j => new { j.Id, j.Type, j.Phase, j.Status, j.Message, j.AgentId, j.ProfileId, j.ParentJobId, j.NotBefore, j.RequestedBy, j.ApprovedBy, j.ResultThumbprint, j.CreatedAt, j.UpdatedAt }).ToListAsync();
            var agents = await db.Agents.AsNoTracking().ToDictionaryAsync(a => a.Id, a => a.Name);
            var profiles = await db.Profiles.AsNoTracking().ToDictionaryAsync(a => a.Id, a => a.Name);
            return Results.Ok(list.Select(j => new
            {
                j.Id, j.Type, j.Phase, j.Status, j.Message, j.RequestedBy, j.ApprovedBy, j.ResultThumbprint, j.CreatedAt, j.UpdatedAt, j.NotBefore, j.ParentJobId,
                agent = agents.GetValueOrDefault(j.AgentId, "?"), profile = j.ProfileId is { } pid ? profiles.GetValueOrDefault(pid, "(deleted)") : null
            }));
        });

        read.MapGet("/jobs/{id:guid}", async (Guid id, AppDb db) =>
        {
            var j = await db.Jobs.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
            return j is null ? Results.NotFound() : Results.Ok(new { j.Id, j.Type, j.Phase, j.Status, j.Message, j.Log, j.CreatedAt, j.UpdatedAt });
        });

        approve.MapPost("/jobs/{id:guid}/approve", async (Guid id, AppDb db, TenantContext t, AuditService audit) =>
        {
            var j = await db.Jobs.FirstOrDefaultAsync(x => x.Id == id);
            if (j is null) return Results.NotFound();
            if (j.Status != JobStatuses.AwaitingApproval) return Bad("Job is not awaiting approval.");
            if (string.Equals(j.RequestedBy, t.Actor, StringComparison.OrdinalIgnoreCase))
                return Bad("Separation of duties: the requester cannot approve their own change.");
            j.ApprovedBy = t.Actor;
            j.SetStatus(JobStatuses.Pending, $"Approved by {t.Actor}");
            audit.Record(t.TenantId, t.Actor, "job.approved", j.Id.ToString());
            await db.SaveChangesAsync();
            return Results.Ok();
        });

        approve.MapPost("/jobs/{id:guid}/reject", async (Guid id, AppDb db, TenantContext t, AuditService audit) =>
        {
            var j = await db.Jobs.FirstOrDefaultAsync(x => x.Id == id);
            if (j is null) return Results.NotFound();
            if (j.Status != JobStatuses.AwaitingApproval) return Bad("Job is not awaiting approval.");
            j.SetStatus(JobStatuses.Cancelled, $"Rejected by {t.Actor}");
            audit.Record(t.TenantId, t.Actor, "job.rejected", j.Id.ToString());
            await db.SaveChangesAsync();
            return Results.Ok();
        });

        op.MapPost("/jobs/{id:guid}/dns-ready", async (Guid id, AppDb db, TenantContext t, AuditService audit) =>
        {
            var j = await db.Jobs.FirstOrDefaultAsync(x => x.Id == id);
            if (j is null) return Results.NotFound();
            if (j.Status != JobStatuses.WaitingForManualDns) return Bad("Job is not waiting for DNS.");
            j.SetStatus(JobStatuses.DnsReady, $"DNS confirmed by {t.Actor}");
            audit.Record(t.TenantId, t.Actor, "job.dns-ready", j.Id.ToString());
            await db.SaveChangesAsync();
            return Results.Ok();
        });

        op.MapPost("/jobs/{id:guid}/run-now", async (Guid id, AppDb db, TenantContext t, AuditService audit) =>
        {
            var j = await db.Jobs.FirstOrDefaultAsync(x => x.Id == id);
            if (j is null) return Results.NotFound();
            if (j.Status != JobStatuses.Scheduled) return Bad("Only scheduled jobs can be started early.");
            j.NotBefore = DateTimeOffset.UtcNow;
            j.Append($"Start brought forward by {t.Actor}");
            audit.Record(t.TenantId, t.Actor, "job.run-now", j.Id.ToString());
            await db.SaveChangesAsync();
            return Results.Ok();
        });

        op.MapPost("/jobs/{id:guid}/cancel", async (Guid id, AppDb db, TenantContext t, AuditService audit) =>
        {
            var j = await db.Jobs.FirstOrDefaultAsync(x => x.Id == id);
            if (j is null) return Results.NotFound();
            if (JobStatuses.Terminal.Contains(j.Status)) return Bad("Job already finished.");
            if (j.Status is JobStatuses.Deploying or JobStatuses.Issuing) return Bad("Job is executing and cannot be cancelled now.");
            j.SetStatus(JobStatuses.Cancelled, $"Cancelled by {t.Actor}");
            audit.Record(t.TenantId, t.Actor, "job.cancelled", j.Id.ToString());
            await db.SaveChangesAsync();
            return Results.Ok();
        });

        read.MapGet("/audit", async (AppDb db) => Results.Ok(await db.Audit.AsNoTracking().OrderByDescending(a => a.At).Take(500).ToListAsync()));
    }

    private static async Task<(int Added, List<string> Invalid)> AddEndpointsAsync(AppDb db, IEnumerable<string> lines, string? label)
    {
        var added = 0;
        var invalid = new List<string>();
        var existing = (await db.Endpoints.AsNoTracking().Select(e => new { e.Host, e.Port }).ToListAsync()).Select(e => $"{e.Host}:{e.Port}").ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in lines.Take(1500))
        {
            var line = raw.Replace("https://", "", StringComparison.OrdinalIgnoreCase).TrimEnd('/');
            var host = line; var port = 443;
            var colon = line.LastIndexOf(':');
            if (colon > 0 && int.TryParse(line[(colon + 1)..], out var p)) { host = line[..colon]; port = p; }
            host = host.ToLowerInvariant();
            if (!HostRegex().IsMatch(host) || port is < 1 or > 65535) { invalid.Add(raw); continue; }
            if (!existing.Add($"{host}:{port}")) continue;
            db.Endpoints.Add(new MonitoredEndpoint { Host = host, Port = port, Label = string.IsNullOrWhiteSpace(label) ? null : label.Trim() });
            added++;
        }
        return (added, invalid);
    }

    private static async Task<string?> ApplyDnsAsync(DnsProvider d, DnsProviderInput r, AppDb db, SecretProtector sp, bool isNew)
    {
        if (string.IsNullOrWhiteSpace(r.Name)) return "Name is required.";
        var type = DnsProviderTypes.Catalog.FirstOrDefault(c => c.Type == r.Type);
        if (type is null) return "Unsupported DNS provider type.";
        if (!isNew && d.Type != r.Type) return "The provider type cannot be changed; create a new provider.";
        var s = r.Settings ?? [];
        var oldSecrets = isNew ? new Dictionary<string, string>() : sp.UnprotectMap(d.CredentialProtected);
        foreach (var f in type.Fields.Where(f => f.Required))
        {
            var has = s.TryGetValue(f.Key, out var v) && !string.IsNullOrWhiteSpace(v);
            if (!has && !(f.Secret && oldSecrets.ContainsKey(f.Key))) return $"{f.Label} is required.";
        }
        if (type.Type == DnsProviderTypes.Webhook && !(s.GetValueOrDefault("url") ?? "").StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return "Webhook URL must use https://";
        if (type.Type == DnsProviderTypes.WindowsDns)
        {
            if (!Guid.TryParse(s.GetValueOrDefault("agentId"), out var aid) || !await db.Agents.AnyAsync(a => a.Id == aid && !a.Revoked)) return "Choose an enrolled server for Windows DNS.";
        }
        if (type.Type == DnsProviderTypes.Rfc2136 && s.TryGetValue("tsigSecret", out var ts) && !string.IsNullOrWhiteSpace(ts))
        {
            try { Convert.FromBase64String(ts.Trim()); } catch { return "TSIG secret must be base64 (as in the BIND key file)."; }
        }
        var alias = string.IsNullOrWhiteSpace(r.AliasZone) ? null : r.AliasZone.Trim().TrimEnd('.').ToLowerInvariant();
        if (alias is not null && !DomainRegex().IsMatch(alias)) return "Alias zone must be a DNS zone name, e.g. acme.contoso-dns.net.";

        d.Name = r.Name.Trim();
        d.Type = type.Type;
        d.AliasZone = alias;
        d.ConfigJson = JsonMap.Write(type.Fields.Where(f => !f.Secret && s.ContainsKey(f.Key)).ToDictionary(f => f.Key, f => s[f.Key].Trim()));
        var secrets = new Dictionary<string, string>(oldSecrets);
        foreach (var f in type.Fields.Where(f => f.Secret))
            if (s.TryGetValue(f.Key, out var v) && !string.IsNullOrWhiteSpace(v)) secrets[f.Key] = v.Trim();
        d.CredentialProtected = secrets.Count == 0 ? null : sp.ProtectMap(secrets);
        return null;
    }

    private static async Task<string?> ApplyAsync(CertificateProfile p, ProfileInput r, AppDb db)
    {
        if (string.IsNullOrWhiteSpace(r.Name)) return "Name is required.";
        var domains = (r.Domains ?? []).Select(d => d.Trim().ToLowerInvariant()).Where(d => d.Length > 0).Distinct().ToArray();
        if (domains.Length == 0) return "At least one DNS name is required.";
        if (domains.Length > 100) return "A maximum of 100 names is supported.";
        if (domains.FirstOrDefault(d => d.Length > 253 || !DomainRegex().IsMatch(d)) is { } badDomain) return $"Invalid DNS name '{badDomain}'.";
        if (!KeyAlgorithms.All.Contains(r.KeyAlgorithm)) return "Invalid key algorithm.";
        var storage = r.KeyStorage ?? KeyStorages.Cng;
        if (storage is not (KeyStorages.Cng or KeyStorages.Capi)) return "Invalid key storage.";
        if (storage == KeyStorages.Capi && KeyAlgorithms.IsEcdsa(r.KeyAlgorithm)) return "Legacy CAPI storage supports RSA keys only.";
        if (r.RenewAtRemainingPercent is < 5 or > 80) return "Renewal threshold must be between 5% and 80%.";
        if (r.WindowStartHour is < 0 or > 23 || r.WindowEndHour is < 0 or > 23) return "Maintenance window hours must be 0-23.";

        var issuer = await db.Issuers.FirstOrDefaultAsync(i => i.Id == r.IssuerId);
        if (issuer is null) return "Certificate authority not found.";
        if (issuer.Type == IssuerTypes.Acme)
        {
            if (r.DnsProviderId is null || !await db.DnsProviders.AnyAsync(d => d.Id == r.DnsProviderId))
                return "ACME certificate authorities need a DNS provider (DNS-01 validation).";
            if (CertIntel.IsPublicAcme(issuer.DirectoryUrl))
                foreach (var d in domains)
                    if (CertIntel.InternalNameReason(d) is { } reason)
                        return $"'{d}' {reason}. Public CAs like {issuer.Name} only issue for public domain names. Choose your Microsoft AD CS issuer for internal names, or use a public name you own.";
        }
        if (!await db.Agents.AnyAsync(a => a.Id == r.AgentId && !a.Revoked)) return "Server not found or revoked.";

        var targets = r.Targets ?? [];
        if (targets.Length > 20) return "A maximum of 20 deployment steps is supported.";
        foreach (var t in targets)
        {
            if (t.Parameters is { } prm) foreach (var k in prm.Keys.Where(DeploymentTemplates.IsHiddenParameter).ToList()) prm.Remove(k);
            if (DeploymentTemplates.Validate(t.Template, t.Parameters) is { } te) return te;
            if (t.VerifyPort is < 1 or > 65535) return "Verify port must be 1-65535.";
            if (t.VerifyHost is { Length: > 0 } vh && Uri.CheckHostName(vh) == UriHostNameType.Unknown) return $"Invalid verify host '{vh}'.";
            var def = DeploymentTemplates.All[t.Template];
            if (t.AgentId is { } remote && remote != r.AgentId)
            {
                if (def.RequiresLocalCertificate) return $"'{def.DisplayName}' needs the certificate on the same server; it cannot run on another server.";
                if (!await db.Agents.AnyAsync(a => a.Id == remote && !a.Revoked)) return "A deployment step points to an unknown server.";
            }
        }

        p.Name = r.Name.Trim();
        p.Domains = string.Join(',', domains);
        p.KeyAlgorithm = r.KeyAlgorithm;
        p.KeyStorage = storage;
        p.IssuerId = issuer.Id;
        p.DnsProviderId = issuer.Type == IssuerTypes.Acme ? r.DnsProviderId : null;
        p.AgentId = r.AgentId;
        p.RenewAtRemainingPercent = r.RenewAtRemainingPercent;
        p.RequireApproval = r.RequireApproval;
        p.Exportable = r.Exportable;
        p.Enabled = r.Enabled;
        p.WindowStartHour = r.WindowStartHour;
        p.WindowEndHour = r.WindowEndHour;
        p.Scenario = string.IsNullOrWhiteSpace(r.Scenario) ? null : r.Scenario.Trim()[..Math.Min(30, r.Scenario.Trim().Length)];
        var order = 0;
        foreach (var t in targets)
            p.Targets.Add(new DeploymentTarget
            {
                Template = t.Template.ToLowerInvariant(),
                ParametersJson = JsonSerializer.Serialize(t.Parameters ?? new Dictionary<string, string>()),
                VerifyHost = string.IsNullOrWhiteSpace(t.VerifyHost) ? null : t.VerifyHost.Trim(),
                VerifyPort = t.VerifyPort,
                AgentId = t.AgentId is { } aid && aid != r.AgentId ? aid : null,
                Order = order++
            });
        return null;
    }
}
