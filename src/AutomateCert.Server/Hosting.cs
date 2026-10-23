using System.Security.Cryptography.X509Certificates;
using AutomateCert.Server.Data;
using AutomateCert.Server.Security;
using AutomateCert.Server.Services;
using AutomateCert.Shared;
using Microsoft.EntityFrameworkCore;

namespace AutomateCert.Server;

/// <summary>
/// Supplies the console's TLS certificate from LocalMachine\My. With a host name configured it always picks the newest valid
/// certificate for that name, so when AutomateCert renews its own certificate the console switches over within 10 minutes.
/// </summary>
public sealed class ConsoleCertificate(string? thumbprint, string? host)
{
    private readonly object _gate = new();
    private X509Certificate2? _current;
    private DateTime _loadedAt;

    public X509Certificate2 Current()
    {
        var c = _current;
        if (c is not null && DateTime.UtcNow - _loadedAt < TimeSpan.FromMinutes(10)) return c;
        lock (_gate)
        {
            if (_current is not null && DateTime.UtcNow - _loadedAt < TimeSpan.FromMinutes(10)) return _current;
            _current = Load();
            _loadedAt = DateTime.UtcNow;
            return _current;
        }
    }

    private X509Certificate2 Load()
    {
        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly);
        var now = DateTime.Now;
        var all = store.Certificates.Cast<X509Certificate2>().Where(x => x.HasPrivateKey).ToList();
        if (!string.IsNullOrWhiteSpace(host))
        {
            var match = all.Where(x => x.NotBefore <= now && x.NotAfter > now && Covers(x, host!)).OrderByDescending(x => x.NotAfter).FirstOrDefault();
            if (match is not null) return match;
        }
        if (!string.IsNullOrWhiteSpace(thumbprint))
        {
            var byThumb = all.FirstOrDefault(x => x.Thumbprint.Equals(thumbprint!.Replace(" ", ""), StringComparison.OrdinalIgnoreCase));
            if (byThumb is not null) return byThumb;
        }
        throw new InvalidOperationException($"No usable TLS certificate in LocalMachine\\My (host '{host}', thumbprint '{thumbprint}').");
    }

    private static bool Covers(X509Certificate2 cert, string name)
    {
        var san = cert.Extensions.OfType<X509SubjectAlternativeNameExtension>().FirstOrDefault();
        foreach (var dns in san?.EnumerateDnsNames() ?? [])
        {
            if (dns.Equals(name, StringComparison.OrdinalIgnoreCase)) return true;
            if (dns.StartsWith("*.", StringComparison.Ordinal) && name.IndexOf('.') is var i and > 0 && name[(i + 1)..].Equals(dns[2..], StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}

/// <summary>
/// Installer bootstrap (AutomateCert.Server.exe --init). Creates the organization, first administrator, free Let's Encrypt issuers
/// and a manual DNS provider when the database is empty; optionally issues a one-time token so Setup can enroll the local agent.
/// The administrator password is read from AUTOMATECERT_INIT_PASSWORD, never from the command line.
/// </summary>
public static class Initializer
{
    public static void SeedDefaults(AppDb db, Tenant tenant, string contactEmail)
    {
        db.Issuers.Add(new Issuer { TenantId = tenant.Id, Name = "Let's Encrypt (free)", Type = IssuerTypes.Acme, DirectoryUrl = "https://acme-v02.api.letsencrypt.org/directory", ContactEmail = contactEmail });
        db.Issuers.Add(new Issuer { TenantId = tenant.Id, Name = "Let's Encrypt STAGING (testing)", Type = IssuerTypes.Acme, DirectoryUrl = "https://acme-staging-v02.api.letsencrypt.org/directory", ContactEmail = contactEmail });
        db.DnsProviders.Add(new DnsProvider { TenantId = tenant.Id, Name = "Manual DNS", Type = DnsProviderTypes.Manual, ConfigJson = "{}" });
    }

    public static async Task<int> RunAsync(IServiceProvider services, IConfiguration cfg)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        Tenant? tenant;

        if (await db.Users.IgnoreQueryFilters().AnyAsync())
        {
            Console.WriteLine("AUTOMATECERT-INIT: existing installation detected; database schema is up to date. Administrator unchanged.");
            tenant = await db.Tenants.OrderBy(t => t.CreatedAt).FirstAsync();
        }
        else
        {
            var org = cfg["Init:Org"]?.Trim();
            var email = cfg["Init:AdminEmail"]?.Trim().ToLowerInvariant();
            var name = cfg["Init:AdminName"]?.Trim();
            var password = Environment.GetEnvironmentVariable("AUTOMATECERT_INIT_PASSWORD");
            if (string.IsNullOrWhiteSpace(org) || string.IsNullOrWhiteSpace(email) || !email.Contains('@'))
            {
                Console.Error.WriteLine("AUTOMATECERT-INIT: --Init:Org and a valid --Init:AdminEmail are required.");
                return 2;
            }
            if (PasswordHasher.PolicyError(password, email) is { } pe) { Console.Error.WriteLine("AUTOMATECERT-INIT: " + pe); return 2; }

            tenant = new Tenant
            {
                Name = org, RequireMfa = cfg.GetValue("Init:RequireMfa", false),
                AlertEmails = string.IsNullOrWhiteSpace(cfg["Init:AlertEmails"]) ? null : cfg["Init:AlertEmails"]!.Trim()
            };
            db.Tenants.Add(tenant);
            db.Users.Add(new User { TenantId = tenant.Id, Email = email, DisplayName = string.IsNullOrWhiteSpace(name) ? email : name, PasswordHash = PasswordHasher.Hash(password!), Role = Roles.Admin });
            SeedDefaults(db, tenant, email);
            db.Audit.Add(new AuditEntry { TenantId = tenant.Id, Actor = "installer", Action = "setup.completed", Target = org });
            await db.SaveChangesAsync();
            Console.WriteLine($"AUTOMATECERT-INIT: organization '{org}' and administrator '{email}' created (Let's Encrypt issuers added).");
        }

        if (cfg.GetValue("Init:AgentToken", false))
        {
            var token = Secrets.NewToken();
            db.EnrollmentTokens.Add(new EnrollmentToken { TenantId = tenant.Id, TokenHash = Secrets.Hash(token), ExpiresAt = DateTimeOffset.UtcNow.AddHours(2), CreatedBy = "installer", MaxUses = 1 });
            await db.SaveChangesAsync();
            Console.WriteLine("AUTOMATECERT-AGENT-TOKEN: " + token);
        }
        return 0;
    }
}
