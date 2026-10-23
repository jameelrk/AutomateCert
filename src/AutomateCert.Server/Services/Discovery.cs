using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using AutomateCert.Server.Data;
using AutomateCert.Server.Security;
using Microsoft.EntityFrameworkCore;

namespace AutomateCert.Server.Services;

public static class CertFacts
{
    public static string KeyInfo(X509Certificate2 c)
    {
        using var rsa = c.GetRSAPublicKey();
        if (rsa is not null) return $"RSA {rsa.KeySize}";
        using var ec = c.GetECDsaPublicKey();
        if (ec is not null) return $"ECDSA P-{ec.KeySize}";
        return c.PublicKey.Oid.FriendlyName ?? "Unknown";
    }
}

/// <summary>Agentless TLS endpoint monitoring and network discovery.</summary>
public sealed class EndpointScanner(ILogger<EndpointScanner> log)
{
    public async Task ScanAsync(MonitoredEndpoint e, CancellationToken ct)
    {
        e.LastCheckedAt = DateTimeOffset.UtcNow;
        try
        {
            using var tcp = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(15));
            await tcp.ConnectAsync(e.Host, e.Port, cts.Token);

            X509Certificate2? presented = null;
            SslPolicyErrors errors = SslPolicyErrors.None;
            string chainText = "";
            await using var ssl = new SslStream(tcp.GetStream(), false, (_, cert, chain, policyErrors) =>
            {
                if (cert is not null) presented = X509CertificateLoader.LoadCertificate(cert.GetRawCertData());
                errors = policyErrors;
                if (chain is not null)
                    chainText = string.Join("; ", chain.ChainStatus.Select(s => s.StatusInformation.Trim()).Where(s => s.Length > 0).Distinct());
                return true; // observe only
            });
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = e.Host }, cts.Token);
            if (presented is null) throw new InvalidOperationException("The endpoint did not present a certificate.");

            e.Thumbprint = presented.Thumbprint;
            e.Subject = Trunc(presented.Subject, 1000);
            e.Issuer = Trunc(presented.Issuer, 1000);
            var san = presented.Extensions.OfType<X509SubjectAlternativeNameExtension>().FirstOrDefault();
            e.DnsNames = Trunc(string.Join(", ", san?.EnumerateDnsNames() ?? []), 4000);
            e.NotBefore = new DateTimeOffset(presented.NotBefore.ToUniversalTime());
            e.NotAfter = new DateTimeOffset(presented.NotAfter.ToUniversalTime());
            e.KeyInfo = CertFacts.KeyInfo(presented);
            e.SignatureAlgorithm = presented.SignatureAlgorithm.FriendlyName;
            e.TlsProtocol = ssl.SslProtocol.ToString();
            e.NameMatches = !errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch);
            e.ChainValid = !errors.HasFlag(SslPolicyErrors.RemoteCertificateChainErrors);
            e.ChainStatus = errors == SslPolicyErrors.None ? "Trusted" : Trunc(string.IsNullOrEmpty(chainText) ? errors.ToString() : chainText, 1000);
            e.LastError = null;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            e.LastError = Trunc(ex is OperationCanceledException ? "Connection timed out." : ex.Message, 1000);
            log.LogDebug(ex, "Endpoint scan failed for {Host}:{Port}", e.Host, e.Port);
        }
    }

    /// <summary>Scans an IPv4 range (max /22) on the given ports; returns host:port pairs that complete a TLS handshake.</summary>
    public async Task<List<(string Host, int Port)>> DiscoverAsync(string cidr, int[] ports, CancellationToken ct)
    {
        var parts = cidr.Trim().Split('/');
        if (!IPAddress.TryParse(parts[0], out var ip) || ip.AddressFamily != AddressFamily.InterNetwork)
            throw new InvalidOperationException("Enter an IPv4 range such as 10.0.0.0/24.");
        var prefix = parts.Length > 1 && int.TryParse(parts[1], out var p) ? p : 32;
        if (prefix is < 22 or > 32) throw new InvalidOperationException("Ranges are limited to /22 (1024 addresses) per scan.");
        var b = ip.GetAddressBytes();
        var start = ((uint)b[0] << 24 | (uint)b[1] << 16 | (uint)b[2] << 8 | b[3]) & (prefix == 0 ? 0 : uint.MaxValue << (32 - prefix));
        var count = 1u << (32 - prefix);
        var targets = new List<(string, int)>();
        for (uint i = 0; i < count; i++)
        {
            var a = start + i;
            var host = $"{a >> 24}.{(a >> 16) & 255}.{(a >> 8) & 255}.{a & 255}";
            foreach (var port in ports) targets.Add((host, port));
        }

        var found = new System.Collections.Concurrent.ConcurrentBag<(string, int)>();
        await Parallel.ForEachAsync(targets, new ParallelOptions { MaxDegreeOfParallelism = 64, CancellationToken = ct }, async (t, token) =>
        {
            try
            {
                using var tcp = new TcpClient();
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
                cts.CancelAfter(TimeSpan.FromMilliseconds(1500));
                await tcp.ConnectAsync(t.Item1, t.Item2, cts.Token);
                await using var ssl = new SslStream(tcp.GetStream(), false, (_, _, _, _) => true);
                using var hs = CancellationTokenSource.CreateLinkedTokenSource(token);
                hs.CancelAfter(TimeSpan.FromSeconds(4));
                await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = t.Item1 }, hs.Token);
                found.Add(t);
            }
            catch { /* closed, filtered or not TLS */ }
        });
        return found.OrderBy(f => f.Item1).ThenBy(f => f.Item2).ToList();
    }

    private static string Trunc(string s, int max) => s.Length > max ? s[..max] : s;
}

/// <summary>
/// Certificate Transparency monitoring via crt.sh: detects certificates issued for your domains that are not in your
/// inventory (shadow IT, forgotten services or mis-issuance).
/// </summary>
public sealed class CtMonitor(AppDb db, IHttpClientFactory http, NotificationService notify, ILogger<CtMonitor> log)
{
    public async Task<int> SyncTenantAsync(Tenant t, CancellationToken ct)
    {
        var domains = (t.CtDomains ?? "").Split(new[] { ',', ';', ' ', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(d => d.ToLowerInvariant().TrimStart('*', '.')).Distinct().Take(25).ToList();
        if (domains.Count == 0) return 0;

        var inventory = (await db.Discovered.IgnoreQueryFilters().AsNoTracking().Where(d => d.TenantId == t.Id)
                .Select(d => new { d.NotAfter, d.DnsNames, d.Serial }).ToListAsync(ct))
            .Concat((await db.Endpoints.IgnoreQueryFilters().AsNoTracking().Where(e => e.TenantId == t.Id && e.NotAfter != null)
                .Select(e => new { e.NotAfter, e.DnsNames }).ToListAsync(ct)).Select(e => new { NotAfter = e.NotAfter!.Value, DnsNames = e.DnsNames ?? "", Serial = (string?)null }))
            .ToList();
        var existing = (await db.CtCertificates.IgnoreQueryFilters().Where(c => c.TenantId == t.Id).Select(c => c.CrtShId).ToListAsync(ct)).ToHashSet();
        var client = http.CreateClient("ct");
        var added = new List<CtCertificate>();

        foreach (var domain in domains)
        {
            JsonArray arr;
            try
            {
                using var resp = await client.GetAsync($"https://crt.sh/?q=%25.{Uri.EscapeDataString(domain)}&output=json&exclude=expired", ct);
                if (!resp.IsSuccessStatusCode) { log.LogWarning("crt.sh returned {Status} for {Domain}", (int)resp.StatusCode, domain); continue; }
                arr = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct)) as JsonArray ?? [];
            }
            catch (Exception ex) { log.LogWarning("crt.sh query for {Domain} failed: {Error}", domain, ex.Message); continue; }

            foreach (var n in arr.Take(2000))
            {
                var id = n?["id"]?.GetValue<long>() ?? 0;
                if (id == 0 || existing.Contains(id)) continue;
                existing.Add(id);
                var names = (n?["name_value"]?.GetValue<string>() ?? "").Replace('\n', ',');
                var serial = n?["serial_number"]?.GetValue<string>()?.ToUpperInvariant();
                _ = DateTimeOffset.TryParse(n?["not_before"]?.GetValue<string>() + "Z", out var nb);
                _ = DateTimeOffset.TryParse(n?["not_after"]?.GetValue<string>() + "Z", out var na);
                var nameSet = names.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var known = inventory.Any(i =>
                    (serial is not null && string.Equals(i.Serial, serial, StringComparison.OrdinalIgnoreCase)) ||
                    (Math.Abs((i.NotAfter - na).TotalMinutes) < 2 && i.DnsNames.Split(',').Any(x => nameSet.Contains(x.Trim()))));
                var c = new CtCertificate
                {
                    TenantId = t.Id, CrtShId = id, Domain = domain, Issuer = Trunc(n?["issuer_name"]?.GetValue<string>() ?? "", 1000),
                    Names = Trunc(names, 4000), Serial = serial, NotBefore = nb, NotAfter = na, Known = known
                };
                db.CtCertificates.Add(c);
                added.Add(c);
            }
        }
        await db.SaveChangesAsync(ct);

        var unknownRecent = added.Where(c => !c.Known && c.NotBefore > DateTimeOffset.UtcNow.AddDays(-14)).Take(20).ToList();
        if (unknownRecent.Count > 0)
            await notify.NotifyAsync(t.Id, new AlertMessage(Severity.Warning, $"{unknownRecent.Count} new certificate(s) issued for your domains",
                "Certificate Transparency logs show recently issued certificates that are not in your AutomateCert inventory. Confirm they are expected (other teams, SaaS providers) or investigate.",
                unknownRecent.Select(c => new KeyValuePair<string, string>(c.NotBefore.ToString("yyyy-MM-dd"), $"{c.Names} · {CertIntel.Vendor(c.Issuer, null)}")).ToList(),
                "#/transparency"), ct);
        return added.Count;
    }

    private static string Trunc(string s, int max) => s.Length > max ? s[..max] : s;
}

public sealed record CaAccountField(string Key, string Label, bool Secret, bool Required, string? Placeholder = null, string? Help = null);
public sealed record CaAccountType(string Type, string Name, string Description, CaAccountField[] Fields);

/// <summary>Reads balances, order counts and expiring orders from commercial CA accounts.</summary>
public sealed class CaAccountService(SecretProtector secrets, IHttpClientFactory http, ILogger<CaAccountService> log)
{
    public const string DigiCert = "DigiCert";
    public const string Sectigo = "SectigoScm";

    public static readonly CaAccountType[] Catalog =
    [
        new(DigiCert, "DigiCert CertCentral", "Account balance, orders and upcoming expirations via the CertCentral Services API.",
            [new("apiKey", "API key", true, true, null, "CertCentral → Automation → API Keys. A read-only (View Only) key is enough."),
             new("region", "Instance", false, false, "us", "us or eu"), new("containerId", "Division (container) ID", false, false, null, "Required only for accounts with several divisions.")]),
        new(Sectigo, "Sectigo Certificate Manager (SCM)", "Certificate counts and recent SSL certificates via the SCM Admin API (header authentication).",
            [new("customerUri", "Customer URI", false, true, "contoso"), new("login", "Admin login", false, true),
             new("password", "Password", true, true, null, "Use a dedicated API admin with read-only rights."),
             new("baseUrl", "Base URL", false, false, "https://cert-manager.com")]),
    ];

    public async Task<JsonObject> SyncAsync(CaAccount a, CancellationToken ct)
    {
        var cfg = JsonMap.Read(a.ConfigJson);
        var sec = secrets.UnprotectMap(a.CredentialProtected, "apiKey");
        var client = http.CreateClient("ca");
        return a.Type switch
        {
            DigiCert => await DigiCertAsync(client, cfg, sec, ct),
            Sectigo => await SectigoAsync(client, cfg, sec, ct),
            _ => throw new NotSupportedException($"CA account type {a.Type} is not supported.")
        };
    }

    private static async Task<JsonNode?> GetJsonAsync(HttpClient c, HttpRequestMessage req, string what, CancellationToken ct)
    {
        using var resp = await c.SendAsync(req, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"{what} failed ({(int)resp.StatusCode}): {(text.Length > 250 ? text[..250] : text)}");
        return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
    }

    private static string? S(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : n?.ToString();

    private static async Task<JsonObject> DigiCertAsync(HttpClient c, Dictionary<string, string> cfg, Dictionary<string, string> sec, CancellationToken ct)
    {
        var baseUrl = cfg.GetValueOrDefault("region")?.Equals("eu", StringComparison.OrdinalIgnoreCase) == true
            ? "https://certcentral.digicert.eu/services/v2" : "https://www.digicert.com/services/v2";
        var container = cfg.GetValueOrDefault("containerId");
        var key = sec.GetValueOrDefault("apiKey") ?? throw new InvalidOperationException("API key missing.");
        HttpRequestMessage R(string path)
        {
            var r = new HttpRequestMessage(HttpMethod.Get, baseUrl + path);
            r.Headers.TryAddWithoutValidation("X-DC-DEVKEY", key);
            r.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            return r;
        }
        var q = string.IsNullOrWhiteSpace(container) ? "" : $"container_id={Uri.EscapeDataString(container)}";
        var bal = await GetJsonAsync(c, R("/finance/balance" + (q.Length > 0 ? "?" + q : "")), "DigiCert balance", ct);
        var orders = await GetJsonAsync(c, R("/order/certificate?limit=1000" + (q.Length > 0 ? "&" + q : "")), "DigiCert orders", ct);
        var list = orders?["orders"] as JsonArray ?? [];
        var now = DateTimeOffset.UtcNow;
        var rows = list.Select(o => new
        {
            name = S(o?["certificate"]?["common_name"]) ?? "",
            status = S(o?["status"]) ?? "",
            product = S(o?["product"]?["name"]) ?? "",
            expires = DateTimeOffset.TryParse(S(o?["certificate"]?["valid_till"]), out var d) ? d : (DateTimeOffset?)null,
            coverage = DateTimeOffset.TryParse(S(o?["order_valid_till"]), out var cv) ? cv : (DateTimeOffset?)null
        }).ToList();
        var result = new JsonObject
        {
            ["balance"] = S(bal?["balance"]),
            ["currency"] = S(bal?["currency"]),
            ["availableFunds"] = S(bal?["total_available_funds"]),
            ["unpaidInvoices"] = S(bal?["unpaid_invoice_balance"]),
            ["totalOrders"] = S(orders?["page"]?["total"]) ?? list.Count.ToString(),
            ["issued"] = rows.Count(r => r.status.Equals("issued", StringComparison.OrdinalIgnoreCase)),
            ["pending"] = rows.Count(r => r.status.Contains("pending", StringComparison.OrdinalIgnoreCase)),
            ["expiring30"] = rows.Count(r => r.expires is { } e && e > now && e <= now.AddDays(30)),
            ["coverageEnding60"] = rows.Count(r => r.coverage is { } e && e > now && e <= now.AddDays(60)),
            ["recent"] = new JsonArray(rows.Where(r => r.expires is not null).OrderBy(r => r.expires).Where(r => r.expires > now).Take(15)
                .Select(r => (JsonNode)new JsonObject { ["name"] = r.name, ["status"] = r.status, ["product"] = r.product, ["expires"] = r.expires?.ToString("yyyy-MM-dd") }).ToArray())
        };
        return result;
    }

    private static async Task<JsonObject> SectigoAsync(HttpClient c, Dictionary<string, string> cfg, Dictionary<string, string> sec, CancellationToken ct)
    {
        var baseUrl = (cfg.GetValueOrDefault("baseUrl") is { Length: > 0 } b ? b : "https://cert-manager.com").TrimEnd('/');
        var req = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/api/ssl/v1?size=200&position=0");
        req.Headers.TryAddWithoutValidation("login", cfg.GetValueOrDefault("login") ?? "");
        req.Headers.TryAddWithoutValidation("password", sec.GetValueOrDefault("password") ?? "");
        req.Headers.TryAddWithoutValidation("customerUri", cfg.GetValueOrDefault("customerUri") ?? "");
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var resp = await c.SendAsync(req, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"Sectigo SCM failed ({(int)resp.StatusCode}): {(text.Length > 250 ? text[..250] : text)}");
        var arr = JsonNode.Parse(text) as JsonArray ?? [];
        var total = resp.Headers.TryGetValues("X-Total-Count", out var tc) ? tc.FirstOrDefault() : null;
        return new JsonObject
        {
            ["totalOrders"] = total ?? arr.Count.ToString(),
            ["recent"] = new JsonArray(arr.Take(15).Select(x => (JsonNode)new JsonObject
            {
                ["name"] = S(x?["commonName"]) ?? "", ["status"] = S(x?["status"]) ?? "", ["product"] = S(x?["certType"]?["name"]) ?? "",
                ["expires"] = S(x?["expires"]) ?? ""
            }).ToArray()),
            ["note"] = "Balance is not exposed by the SCM API; counts come from the SSL certificate list."
        };
    }
}
