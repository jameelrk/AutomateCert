using System.Text.Json;
using Certes;
using Certes.Acme;
using Certes.Acme.Resource;
using AutomateCert.Server.Data;
using AutomateCert.Server.Security;
using AutomateCert.Shared;
using Microsoft.EntityFrameworkCore;

namespace AutomateCert.Server.Services;

public sealed record PendingChallenge(string RecordName, string Value, string? RecordId);

/// <summary>
/// ACME (RFC 8555) issuance using DNS-01. Works with any ACME CA: Let's Encrypt (free), DigiCert, Sectigo, GlobalSign,
/// ZeroSSL, Google Trust Services (via External Account Binding) and private ACME CAs.
/// The private key never reaches the server: the agent generates key + CSR on the target machine.
/// </summary>
public sealed class AcmeIssuanceService(
    AppDb db, SecretProtector secrets, DnsProviderFactory dnsFactory, IConfiguration config, ILogger<AcmeIssuanceService> log)
{
    private TimeSpan PropagationTimeout => TimeSpan.FromSeconds(config.GetValue("Acme:DnsPropagationTimeoutSeconds", 300));

    private async Task<(CertificateProfile Profile, Issuer Issuer)> LoadAsync(Job job, CancellationToken ct)
    {
        var profile = await db.Profiles.IgnoreQueryFilters()
            .Include(p => p.Issuer).Include(p => p.DnsProvider)
            .FirstAsync(p => p.Id == job.ProfileId && p.TenantId == job.TenantId, ct);
        if (profile.Issuer is null || profile.Issuer.Type != IssuerTypes.Acme)
            throw new InvalidOperationException("Profile issuer is not an ACME issuer.");
        return (profile, profile.Issuer);
    }

    private async Task<IAcmeContext> GetContextAsync(Issuer issuer, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(issuer.DirectoryUrl)) throw new InvalidOperationException("ACME directory URL is missing.");
        var dir = new Uri(issuer.DirectoryUrl);
        if (issuer.AccountKeyProtected is not null)
        {
            var ctx = new AcmeContext(dir, KeyFactory.FromPem(secrets.Unprotect(issuer.AccountKeyProtected)));
            await ctx.Account();
            return ctx;
        }
        var key = KeyFactory.NewKey(KeyAlgorithm.ES256);
        var acme = new AcmeContext(dir, key);
        var eabKey = issuer.EabHmacKeyProtected is null ? null : secrets.Unprotect(issuer.EabHmacKeyProtected);
        var contact = new List<string>();
        if (!string.IsNullOrWhiteSpace(issuer.ContactEmail)) contact.Add("mailto:" + issuer.ContactEmail);
        await acme.NewAccount(contact, true, issuer.EabKeyId, eabKey, eabKey is null ? null : "HS256");
        issuer.AccountKeyProtected = secrets.Protect(key.ToPem());
        await db.SaveChangesAsync(ct);
        log.LogInformation("Registered ACME account for issuer {Issuer}", issuer.Name);
        return acme;
    }

    public async Task StartAsync(Job job, CancellationToken ct)
    {
        var (profile, issuer) = await LoadAsync(job, ct);
        if (issuer.CoverageEndsAt is { } end && end < DateTimeOffset.UtcNow)
            job.Append($"Warning: the coverage of '{issuer.Name}' ended on {end:yyyy-MM-dd}; the CA may refuse the order.");

        if (CertIntel.IsPublicAcme(issuer.DirectoryUrl))
        {
            var bad = profile.DomainList().Select(d => (d, r: CertIntel.InternalNameReason(d))).FirstOrDefault(x => x.r is not null);
            if (bad.r is not null)
                throw new InvalidOperationException($"'{bad.d}' {bad.r}. Public CAs only issue for public domain names. Use your internal AD CS issuer for this profile, or a public name such as app.yourdomain.com.");
        }

        var dnsProvider = profile.DnsProvider;
        if (dnsProvider?.AliasZone is { Length: > 0 } alias)
        {
            foreach (var (name, target) in DnsProviderFactory.RequiredCnames(alias, profile.DomainList()))
            {
                var actual = await DnsPropagation.CnameTargetAsync(name, ct);
                job.Append(actual is not null && actual.Equals(target, StringComparison.OrdinalIgnoreCase)
                    ? $"CNAME delegation OK: {name} → {target}"
                    : $"Warning: {name} should be a CNAME to {target} (found: {actual ?? "none"}). Validation will fail until it exists.");
            }
        }

        var acme = await GetContextAsync(issuer, ct);
        var order = await acme.NewOrder(profile.DomainList());
        job.OrderUrl = order.Location.ToString();
        job.Append($"ACME order created at {issuer.Name}: {job.OrderUrl}");

        var dns = dnsFactory.Create(dnsProvider);
        var challenges = new List<PendingChallenge>();
        foreach (var authz in await order.Authorizations())
        {
            var res = await authz.Resource();
            if (res.Status == AuthorizationStatus.Valid)
            {
                job.Append($"Authorization for {res.Identifier.Value} already valid (pre-validated / reused).");
                continue;
            }
            var challenge = await authz.Dns()
                ?? throw new InvalidOperationException($"The CA did not offer a DNS-01 challenge for {res.Identifier.Value}.");
            var value = acme.AccountKey.DnsTxt(challenge.Token);
            var name = "_acme-challenge." + res.Identifier.Value.TrimStart('*', '.');
            var recordId = await dns.CreateTxtAsync(name, value, ct);
            challenges.Add(new PendingChallenge(name, value, recordId));
            job.Append(dns.IsAutomatic ? $"TXT created via {dnsProvider?.Type ?? "DNS"}: {name}" : $"Manual TXT required: {name} = {value}");
            job.ChallengeJson = JsonSerializer.Serialize(challenges);
            await db.SaveChangesAsync(ct);
        }
        job.ChallengeJson = JsonSerializer.Serialize(challenges);

        if (challenges.Count > 0 && !dns.IsAutomatic)
        {
            var instructions = string.Join("; ", challenges.Select(c => $"{c.RecordName} TXT \"{c.Value}\""));
            job.SetStatus(JobStatuses.WaitingForManualDns, "Create these DNS records, then click 'DNS ready': " + instructions);
            await db.SaveChangesAsync(ct);
            return;
        }

        foreach (var c in challenges)
        {
            var visible = await DnsPropagation.WaitForTxtAsync(c.RecordName, c.Value, PropagationTimeout, ct);
            job.Append(visible ? $"TXT visible on public resolvers: {c.RecordName}" : $"TXT not yet visible on public resolvers: {c.RecordName} (continuing)");
        }
        await db.SaveChangesAsync(ct);
        await CompleteAsync(job, acme, profile, ct);
    }

    public async Task ContinueAsync(Job job, CancellationToken ct)
    {
        var (profile, issuer) = await LoadAsync(job, ct);
        var acme = await GetContextAsync(issuer, ct);
        await CompleteAsync(job, acme, profile, ct);
    }

    private async Task CompleteAsync(Job job, IAcmeContext acme, CertificateProfile profile, CancellationToken ct)
    {
        var order = acme.Order(new Uri(job.OrderUrl!));
        try
        {
            foreach (var authz in await order.Authorizations())
            {
                var res = await authz.Resource();
                if (res.Status == AuthorizationStatus.Valid) continue;
                var challenge = await authz.Dns();
                await challenge.Validate();
                for (var i = 0; i < 40; i++)
                {
                    await Task.Delay(TimeSpan.FromSeconds(3), ct);
                    res = await authz.Resource();
                    if (res.Status is AuthorizationStatus.Valid or AuthorizationStatus.Invalid) break;
                }
                if (res.Status != AuthorizationStatus.Valid)
                {
                    var detail = res.Challenges?.FirstOrDefault(c => c.Error is not null)?.Error?.Detail ?? res.Status.ToString();
                    throw new InvalidOperationException($"Validation failed for {res.Identifier.Value}: {detail}");
                }
                job.Append($"Validated {res.Identifier.Value}");
            }

            var csrDer = PemToDer(job.CsrPem ?? throw new InvalidOperationException("CSR missing."));
            await order.Finalize(csrDer);
            Order state = await order.Resource();
            for (var i = 0; i < 60 && state.Status is not (OrderStatus.Valid or OrderStatus.Invalid); i++)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
                state = await order.Resource();
            }
            if (state.Status != OrderStatus.Valid)
                throw new InvalidOperationException($"Order did not become valid (status {state.Status}). OV/EV orders may be waiting for approval at the CA.");

            var chain = await order.Download();
            job.CertificatePem = chain.ToPem();
            job.SetStatus(JobStatuses.CertificateReady, "Certificate issued; waiting for agent to install and deploy.");
            await db.SaveChangesAsync(ct);
        }
        finally
        {
            await CleanupAsync(job, profile, ct);
        }
    }

    private async Task CleanupAsync(Job job, CertificateProfile profile, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(job.ChallengeJson)) return;
        try
        {
            var dns = dnsFactory.Create(profile.DnsProvider);
            foreach (var c in JsonSerializer.Deserialize<List<PendingChallenge>>(job.ChallengeJson) ?? [])
            {
                await dns.DeleteTxtAsync(c.RecordName, c.Value, c.RecordId, ct);
                job.Append($"TXT removed: {c.RecordName}");
            }
        }
        catch (Exception ex)
        {
            job.Append("Warning: DNS cleanup failed: " + ex.Message);
            log.LogWarning(ex, "DNS challenge cleanup failed for job {Job}", job.Id);
        }
    }

    private static byte[] PemToDer(string pem) =>
        Convert.FromBase64String(string.Concat(pem.Split('\n').Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith("-----", StringComparison.Ordinal))));
}
