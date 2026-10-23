using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using DnsClient;
using AutomateCert.Server.Data;
using AutomateCert.Server.Security;
using Microsoft.EntityFrameworkCore;

namespace AutomateCert.Server.Services;

public interface IDnsChallengeProvider
{
    /// <summary>True when TXT records are created by API; false when an operator must create them.</summary>
    bool IsAutomatic { get; }
    Task<string?> CreateTxtAsync(string recordName, string value, CancellationToken ct);
    Task DeleteTxtAsync(string recordName, string value, string? recordId, CancellationToken ct);
}

public sealed record DnsField(string Key, string Label, bool Secret, bool Required, string? Placeholder = null, string? Help = null, string? Kind = null);
public sealed record DnsProviderType(string Type, string Name, string Description, bool Automatic, DnsField[] Fields);

public static class DnsProviderTypes
{
    public const string Manual = "Manual";
    public const string Cloudflare = "Cloudflare";
    public const string AzureDns = "AzureDns";
    public const string Route53 = "Route53";
    public const string GoDaddy = "GoDaddy";
    public const string Rfc2136 = "Rfc2136";
    public const string WindowsDns = "WindowsDns";
    public const string Webhook = "Webhook";

    public static readonly DnsProviderType[] Catalog =
    [
        new(Cloudflare, "Cloudflare", "Fully automatic via a scoped API token.", true,
            [new("apiToken", "API token", true, true, null, "Cloudflare → My Profile → API Tokens → token with Zone:DNS:Edit and Zone:Zone:Read for the needed zones.")]),
        new(AzureDns, "Azure DNS", "Public Azure DNS zones via a service principal (app registration).", true,
            [new("tenantId", "Directory (tenant) ID", false, true), new("clientId", "Application (client) ID", false, true),
             new("clientSecret", "Client secret", true, true), new("subscriptionId", "Subscription ID", false, true),
             new("resourceGroup", "Resource group of the DNS zones", false, true, null, "Grant the app 'DNS Zone Contributor' on this resource group.")]),
        new(Route53, "AWS Route 53", "Route 53 hosted zones via an IAM access key.", true,
            [new("accessKeyId", "Access key ID", false, true), new("secretAccessKey", "Secret access key", true, true),
             new("hostedZoneId", "Hosted zone ID (optional)", false, false, "Z0123456789ABC", "Leave empty to find the zone automatically (needs route53:ListHostedZonesByName).")]),
        new(GoDaddy, "GoDaddy", "GoDaddy DNS via production API key.", true,
            [new("apiKey", "API key", false, true), new("apiSecret", "API secret", true, true, null, "GoDaddy limits API access to some account types; use CNAME delegation if access is denied.")]),
        new(Rfc2136, "Own name servers (RFC 2136)", "BIND, PowerDNS, Knot, Infoblox and other servers that accept TSIG-signed dynamic updates.", true,
            [new("server", "Primary name server (host or IP)", false, true), new("port", "Port", false, false, "53"),
             new("keyName", "TSIG key name", false, true, "automatecert-key"), new("tsigSecret", "TSIG secret (base64)", true, true),
             new("algorithm", "Algorithm", false, false, "hmac-sha256", null, "hmac-sha256|hmac-sha512"),
             new("zone", "Zone (optional)", false, false, "contoso.com", "Leave empty to detect from the SOA record.")]),
        new(WindowsDns, "Windows DNS (own name servers)", "Microsoft DNS servers hosting your public zones. An enrolled AutomateCert agent on the DNS server (or a server with the DNS RSAT tools) creates the records.", true,
            [new("agentId", "Run through server (agent)", false, true, null, "Pick the agent on the DNS server.", "agent"),
             new("dnsServer", "DNS server name (optional)", false, false, null, "Leave empty when the agent runs on the DNS server itself."),
             new("zone", "Zone (optional)", false, false, "contoso.com")]),
        new(Webhook, "Webhook / script (any provider)", "AutomateCert POSTs a signed JSON request to your endpoint to add/remove the TXT record. Use for any DNS host with an API (Dynadot, Namecheap, internal tools).", true,
            [new("url", "Webhook URL (https)", false, true), new("hmacSecret", "Signing secret", true, true, null, "Verify the X-AutomateCert-Signature header (HMAC-SHA256 of the body).")]),
        new(Manual, "Manual", "You create the TXT record when asked (email alert + 'DNS ready' button).", false, []),
    ];

    public static readonly string[] All = Catalog.Select(c => c.Type).ToArray();
}

public sealed class DnsProviderFactory(SecretProtector secrets, IHttpClientFactory http, AppDb db, ILoggerFactory logs)
{
    public IDnsChallengeProvider Create(DnsProvider? provider)
    {
        if (provider is null) return new ManualDnsProvider();
        var cfg = JsonMap.Read(provider.ConfigJson);
        var sec = secrets.UnprotectMap(provider.CredentialProtected);
        string C(string k) => cfg.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : throw new InvalidOperationException($"DNS provider '{provider.Name}': '{k}' is not configured.");
        string S(string k) => sec.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : throw new InvalidOperationException($"DNS provider '{provider.Name}': secret '{k}' is missing.");
        string? O(string k) => cfg.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;

        IDnsChallengeProvider inner = provider.Type switch
        {
            DnsProviderTypes.Manual => new ManualDnsProvider(),
            DnsProviderTypes.Cloudflare => new CloudflareDnsProvider(http.CreateClient("dns"), S("apiToken")),
            DnsProviderTypes.AzureDns => new AzureDnsProvider(http.CreateClient("dns"), C("tenantId"), C("clientId"), S("clientSecret"), C("subscriptionId"), C("resourceGroup")),
            DnsProviderTypes.Route53 => new Route53DnsProvider(http.CreateClient("dns"), C("accessKeyId"), S("secretAccessKey"), O("hostedZoneId")),
            DnsProviderTypes.GoDaddy => new GoDaddyDnsProvider(http.CreateClient("dns"), C("apiKey"), S("apiSecret")),
            DnsProviderTypes.Rfc2136 => new Rfc2136DnsProvider(C("server"), int.TryParse(O("port"), out var p) ? p : 53, C("keyName"),
                Convert.FromBase64String(S("tsigSecret")), O("algorithm") ?? "hmac-sha256", O("zone")),
            DnsProviderTypes.WindowsDns => new WindowsDnsProvider(db, provider.TenantId, Guid.Parse(C("agentId")), O("dnsServer"), O("zone")),
            DnsProviderTypes.Webhook => new WebhookDnsProvider(http.CreateClient("dns"), C("url"), S("hmacSecret")),
            _ => throw new NotSupportedException($"DNS provider type '{provider.Type}' is not supported.")
        };
        return string.IsNullOrWhiteSpace(provider.AliasZone) ? inner : new AliasedDnsProvider(inner, provider.AliasZone.Trim().TrimEnd('.'));
    }

    /// <summary>CNAME records the customer must create once when CNAME delegation is used.</summary>
    public static IEnumerable<(string Name, string Target)> RequiredCnames(string aliasZone, IEnumerable<string> domains) =>
        domains.Select(d => "_acme-challenge." + d.TrimStart('*', '.')).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(n => (n, $"{n}.{aliasZone.TrimEnd('.')}"));
}

/// <summary>CNAME delegation (alias mode): _acme-challenge.www.contoso.com CNAME _acme-challenge.www.contoso.com.&lt;alias zone&gt;.</summary>
public sealed class AliasedDnsProvider(IDnsChallengeProvider inner, string aliasZone) : IDnsChallengeProvider
{
    public bool IsAutomatic => inner.IsAutomatic;
    private string Map(string name) => $"{name.TrimEnd('.')}.{aliasZone}";
    public Task<string?> CreateTxtAsync(string recordName, string value, CancellationToken ct) => inner.CreateTxtAsync(Map(recordName), value, ct);
    public Task DeleteTxtAsync(string recordName, string value, string? recordId, CancellationToken ct) => inner.DeleteTxtAsync(Map(recordName), value, recordId, ct);
}

public sealed class ManualDnsProvider : IDnsChallengeProvider
{
    public bool IsAutomatic => false;
    public Task<string?> CreateTxtAsync(string recordName, string value, CancellationToken ct) => Task.FromResult<string?>(null);
    public Task DeleteTxtAsync(string recordName, string value, string? recordId, CancellationToken ct) => Task.CompletedTask;
}

internal static class DnsNames
{
    /// <summary>Candidate parent zones from most to least specific, excluding the TLD.</summary>
    public static IEnumerable<string> Candidates(string name)
    {
        var labels = name.TrimEnd('.').Split('.');
        for (var i = 1; i < labels.Length - 1; i++) yield return string.Join('.', labels[i..]);
    }

    public static string Relative(string name, string zone)
    {
        var n = name.TrimEnd('.');
        return n.Equals(zone, StringComparison.OrdinalIgnoreCase) ? "@" : n[..^(zone.Length + 1)];
    }
}

// ---------------------------------------------------------------- Cloudflare
public sealed class CloudflareDnsProvider(HttpClient http, string apiToken) : IDnsChallengeProvider
{
    private const string Api = "https://api.cloudflare.com/client/v4";
    public bool IsAutomatic => true;

    private HttpRequestMessage Req(HttpMethod m, string url, object? body = null)
    {
        var r = new HttpRequestMessage(m, url);
        r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiToken);
        if (body is not null) r.Content = JsonContent.Create(body);
        return r;
    }

    private async Task<string> FindZoneIdAsync(string recordName, CancellationToken ct)
    {
        foreach (var candidate in DnsNames.Candidates(recordName))
        {
            using var resp = await http.SendAsync(Req(HttpMethod.Get, $"{Api}/zones?name={Uri.EscapeDataString(candidate)}"), ct);
            if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new InvalidOperationException("Cloudflare rejected the API token (401/403). Check the token and its zone permissions.");
            resp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var result = doc.RootElement.GetProperty("result");
            if (result.GetArrayLength() > 0) return result[0].GetProperty("id").GetString()!;
        }
        throw new InvalidOperationException($"No Cloudflare zone found for '{recordName}'. Check token scope.");
    }

    public async Task<string?> CreateTxtAsync(string recordName, string value, CancellationToken ct)
    {
        var zoneId = await FindZoneIdAsync(recordName, ct);
        using var resp = await http.SendAsync(Req(HttpMethod.Post, $"{Api}/zones/{zoneId}/dns_records",
            new { type = "TXT", name = recordName, content = value, ttl = 60 }), ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"Cloudflare create failed ({(int)resp.StatusCode}): {text}");
        using var doc = JsonDocument.Parse(text);
        return $"{zoneId}:{doc.RootElement.GetProperty("result").GetProperty("id").GetString()}";
    }

    public async Task DeleteTxtAsync(string recordName, string value, string? recordId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(recordId) || !recordId.Contains(':')) return;
        var parts = recordId.Split(':', 2);
        using var resp = await http.SendAsync(Req(HttpMethod.Delete, $"{Api}/zones/{parts[0]}/dns_records/{parts[1]}"), ct);
    }
}

// ---------------------------------------------------------------- Azure DNS
public sealed class AzureDnsProvider(HttpClient http, string tenantId, string clientId, string clientSecret, string subscriptionId, string resourceGroup) : IDnsChallengeProvider
{
    private const string Api = "api-version=2018-05-01";
    private string? _token;
    private DateTimeOffset _tokenExpires;
    public bool IsAutomatic => true;
    private string Base => $"https://management.azure.com/subscriptions/{subscriptionId}/resourceGroups/{resourceGroup}/providers/Microsoft.Network/dnsZones";

    private async Task<string> TokenAsync(CancellationToken ct)
    {
        if (_token is not null && _tokenExpires > DateTimeOffset.UtcNow.AddMinutes(2)) return _token;
        using var resp = await http.PostAsync($"https://login.microsoftonline.com/{Uri.EscapeDataString(tenantId)}/oauth2/v2.0/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = clientId, ["client_secret"] = clientSecret,
                ["scope"] = "https://management.azure.com/.default", ["grant_type"] = "client_credentials"
            }), ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"Azure sign-in failed ({(int)resp.StatusCode}). Check tenant, client ID and secret. {Trim(text)}");
        var json = JsonNode.Parse(text)!;
        _token = json["access_token"]!.GetValue<string>();
        _tokenExpires = DateTimeOffset.UtcNow.AddSeconds(json["expires_in"]?.GetValue<int>() ?? 3000);
        return _token;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod m, string url, JsonNode? body, CancellationToken ct)
    {
        var req = new HttpRequestMessage(m, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync(ct));
        if (body is not null) req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        return await http.SendAsync(req, ct);
    }

    private async Task<string> ZoneAsync(string name, CancellationToken ct)
    {
        using var resp = await SendAsync(HttpMethod.Get, $"{Base}?{Api}", null, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"Azure DNS: cannot list zones in '{resourceGroup}' ({(int)resp.StatusCode}). Grant 'DNS Zone Contributor'. {Trim(text)}");
        var zones = (JsonNode.Parse(text)?["value"] as JsonArray ?? []).Select(z => z?["name"]?.GetValue<string>() ?? "").Where(z => z.Length > 0);
        var n = name.TrimEnd('.');
        return zones.Where(z => n.Equals(z, StringComparison.OrdinalIgnoreCase) || n.EndsWith("." + z, StringComparison.OrdinalIgnoreCase))
                   .OrderByDescending(z => z.Length).FirstOrDefault()
               ?? throw new InvalidOperationException($"Azure DNS: no zone in resource group '{resourceGroup}' contains '{n}'.");
    }

    private async Task<List<string>> GetValuesAsync(string url, CancellationToken ct)
    {
        using var resp = await SendAsync(HttpMethod.Get, url, null, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return [];
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"Azure DNS read failed ({(int)resp.StatusCode}): {Trim(text)}");
        var recs = JsonNode.Parse(text)?["properties"]?["TXTRecords"] as JsonArray ?? [];
        return recs.Select(r => string.Concat((r?["value"] as JsonArray ?? []).Select(v => v?.GetValue<string>() ?? ""))).ToList();
    }

    private async Task PutAsync(string url, List<string> values, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["properties"] = new JsonObject
            {
                ["TTL"] = 60,
                ["TXTRecords"] = new JsonArray(values.Select(v => (JsonNode)new JsonObject { ["value"] = new JsonArray(v) }).ToArray())
            }
        };
        using var resp = await SendAsync(HttpMethod.Put, url, body, ct);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"Azure DNS write failed ({(int)resp.StatusCode}): {Trim(await resp.Content.ReadAsStringAsync(ct))}");
    }

    private async Task<string> RecordUrlAsync(string name, CancellationToken ct)
    {
        var zone = await ZoneAsync(name, ct);
        return $"{Base}/{zone}/TXT/{Uri.EscapeDataString(DnsNames.Relative(name, zone))}?{Api}";
    }

    public async Task<string?> CreateTxtAsync(string recordName, string value, CancellationToken ct)
    {
        var url = await RecordUrlAsync(recordName, ct);
        var values = await GetValuesAsync(url, ct);
        if (!values.Contains(value)) values.Add(value);
        await PutAsync(url, values, ct);
        return null;
    }

    public async Task DeleteTxtAsync(string recordName, string value, string? recordId, CancellationToken ct)
    {
        var url = await RecordUrlAsync(recordName, ct);
        var values = await GetValuesAsync(url, ct);
        values.Remove(value);
        if (values.Count > 0) { await PutAsync(url, values, ct); return; }
        using var resp = await SendAsync(HttpMethod.Delete, url, null, ct);
    }

    private static string Trim(string s) => s.Length > 300 ? s[..300] : s;
}

// ---------------------------------------------------------------- AWS Route 53 (SigV4)
public sealed class Route53DnsProvider(HttpClient http, string accessKeyId, string secretAccessKey, string? hostedZoneId) : IDnsChallengeProvider
{
    private const string Host = "route53.amazonaws.com", Region = "us-east-1", Service = "route53", Ns = "https://route53.amazonaws.com/doc/2013-04-01/";
    public bool IsAutomatic => true;

    private static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();
    private static byte[] Hmac(byte[] key, string data) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data));

    private async Task<(HttpStatusCode Status, string Body)> SendAsync(HttpMethod method, string path, SortedDictionary<string, string>? query, string? body, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var amzDate = now.ToString("yyyyMMdd'T'HHmmss'Z'");
        var date = now.ToString("yyyyMMdd");
        var qs = query is null ? "" : string.Join('&', query.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
        var payloadHash = Hex(SHA256.HashData(Encoding.UTF8.GetBytes(body ?? "")));
        const string signed = "host;x-amz-content-sha256;x-amz-date";
        var canonical = $"{method.Method}\n{path}\n{qs}\nhost:{Host}\nx-amz-content-sha256:{payloadHash}\nx-amz-date:{amzDate}\n\n{signed}\n{payloadHash}";
        var scope = $"{date}/{Region}/{Service}/aws4_request";
        var toSign = $"AWS4-HMAC-SHA256\n{amzDate}\n{scope}\n{Hex(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))}";
        var key = Hmac(Hmac(Hmac(Hmac(Encoding.UTF8.GetBytes("AWS4" + secretAccessKey), date), Region), Service), "aws4_request");
        var signature = Hex(Hmac(key, toSign));

        using var req = new HttpRequestMessage(method, $"https://{Host}{path}{(qs.Length > 0 ? "?" + qs : "")}");
        req.Headers.TryAddWithoutValidation("x-amz-date", amzDate);
        req.Headers.TryAddWithoutValidation("x-amz-content-sha256", payloadHash);
        req.Headers.TryAddWithoutValidation("Authorization", $"AWS4-HMAC-SHA256 Credential={accessKeyId}/{scope}, SignedHeaders={signed}, Signature={signature}");
        if (body is not null) req.Content = new StringContent(body, Encoding.UTF8, "application/xml");
        using var resp = await http.SendAsync(req, ct);
        return (resp.StatusCode, await resp.Content.ReadAsStringAsync(ct));
    }

    private static IEnumerable<XElement> Els(XContainer x, string local) => x.Descendants().Where(e => e.Name.LocalName == local);

    private async Task<string> ZoneIdAsync(string name, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(hostedZoneId)) return hostedZoneId.Replace("/hostedzone/", "");
        foreach (var candidate in DnsNames.Candidates(name))
        {
            var (status, body) = await SendAsync(HttpMethod.Get, "/2013-04-01/hostedzonesbyname",
                new(StringComparer.Ordinal) { ["dnsname"] = candidate, ["maxitems"] = "1" }, null, ct);
            if (status != HttpStatusCode.OK) throw new InvalidOperationException($"Route 53 rejected the request ({(int)status}). Check the access key and IAM permissions. {Trim(body)}");
            var zone = Els(XDocument.Parse(body), "HostedZone").FirstOrDefault();
            if (zone is not null && Els(zone, "Name").First().Value.TrimEnd('.').Equals(candidate, StringComparison.OrdinalIgnoreCase))
                return Els(zone, "Id").First().Value.Replace("/hostedzone/", "");
        }
        throw new InvalidOperationException($"No Route 53 hosted zone found for '{name}'.");
    }

    private async Task<(List<string> Values, int Ttl)> GetAsync(string zoneId, string fqdn, CancellationToken ct)
    {
        var (status, body) = await SendAsync(HttpMethod.Get, $"/2013-04-01/hostedzone/{zoneId}/rrset",
            new(StringComparer.Ordinal) { ["maxitems"] = "1", ["name"] = fqdn + ".", ["type"] = "TXT" }, null, ct);
        if (status != HttpStatusCode.OK) throw new InvalidOperationException($"Route 53 read failed ({(int)status}): {Trim(body)}");
        var set = Els(XDocument.Parse(body), "ResourceRecordSet").FirstOrDefault();
        if (set is null || !Els(set, "Name").First().Value.TrimEnd('.').Equals(fqdn, StringComparison.OrdinalIgnoreCase) || Els(set, "Type").First().Value != "TXT")
            return ([], 60);
        var ttl = int.TryParse(Els(set, "TTL").FirstOrDefault()?.Value, out var t) ? t : 60;
        return (Els(set, "Value").Select(v => v.Value.Trim('"')).ToList(), ttl);
    }

    private async Task ChangeAsync(string zoneId, string action, string fqdn, IEnumerable<string> values, int ttl, CancellationToken ct)
    {
        XNamespace ns = Ns;
        var doc = new XDocument(new XDeclaration("1.0", "UTF-8", null),
            new XElement(ns + "ChangeResourceRecordSetsRequest",
                new XElement(ns + "ChangeBatch", new XElement(ns + "Changes", new XElement(ns + "Change",
                    new XElement(ns + "Action", action),
                    new XElement(ns + "ResourceRecordSet",
                        new XElement(ns + "Name", fqdn + "."), new XElement(ns + "Type", "TXT"), new XElement(ns + "TTL", ttl),
                        new XElement(ns + "ResourceRecords", values.Select(v => new XElement(ns + "ResourceRecord", new XElement(ns + "Value", $"\"{v}\""))))))))));
        var xml = doc.Declaration + doc.ToString(SaveOptions.DisableFormatting);
        var (status, body) = await SendAsync(HttpMethod.Post, $"/2013-04-01/hostedzone/{zoneId}/rrset", null, xml, ct);
        if (status != HttpStatusCode.OK) throw new InvalidOperationException($"Route 53 {action} failed ({(int)status}): {Trim(body)}");
    }

    public async Task<string?> CreateTxtAsync(string recordName, string value, CancellationToken ct)
    {
        var fqdn = recordName.TrimEnd('.');
        var zoneId = await ZoneIdAsync(fqdn, ct);
        var (values, _) = await GetAsync(zoneId, fqdn, ct);
        if (!values.Contains(value)) values.Add(value);
        await ChangeAsync(zoneId, "UPSERT", fqdn, values, 60, ct);
        return null;
    }

    public async Task DeleteTxtAsync(string recordName, string value, string? recordId, CancellationToken ct)
    {
        var fqdn = recordName.TrimEnd('.');
        var zoneId = await ZoneIdAsync(fqdn, ct);
        var (values, ttl) = await GetAsync(zoneId, fqdn, ct);
        if (!values.Contains(value)) return;
        var remaining = values.Where(v => v != value).ToList();
        if (remaining.Count > 0) await ChangeAsync(zoneId, "UPSERT", fqdn, remaining, ttl, ct);
        else await ChangeAsync(zoneId, "DELETE", fqdn, values, ttl, ct);
    }

    private static string Trim(string s) => s.Length > 300 ? s[..300] : s;
}

// ---------------------------------------------------------------- GoDaddy
public sealed class GoDaddyDnsProvider(HttpClient http, string apiKey, string apiSecret) : IDnsChallengeProvider
{
    private const string Api = "https://api.godaddy.com/v1/domains";
    public bool IsAutomatic => true;

    private HttpRequestMessage Req(HttpMethod m, string url, object? body = null)
    {
        var r = new HttpRequestMessage(m, url);
        r.Headers.TryAddWithoutValidation("Authorization", $"sso-key {apiKey}:{apiSecret}");
        if (body is not null) r.Content = JsonContent.Create(body);
        return r;
    }

    private async Task<string> DomainAsync(string name, CancellationToken ct)
    {
        foreach (var c in DnsNames.Candidates(name))
        {
            using var resp = await http.SendAsync(Req(HttpMethod.Get, $"{Api}/{c}"), ct);
            if (resp.IsSuccessStatusCode) return c;
            if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new InvalidOperationException("GoDaddy rejected the API key (401/403). GoDaddy restricts API access for some accounts; use CNAME delegation instead.");
        }
        throw new InvalidOperationException($"No GoDaddy domain found for '{name}'.");
    }

    private async Task<List<string>> GetAsync(string domain, string rel, CancellationToken ct)
    {
        using var resp = await http.SendAsync(Req(HttpMethod.Get, $"{Api}/{domain}/records/TXT/{Uri.EscapeDataString(rel)}"), ct);
        if (!resp.IsSuccessStatusCode) return [];
        var arr = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct)) as JsonArray ?? [];
        return arr.Select(r => r?["data"]?.GetValue<string>() ?? "").Where(v => v.Length > 0).ToList();
    }

    public async Task<string?> CreateTxtAsync(string recordName, string value, CancellationToken ct)
    {
        var domain = await DomainAsync(recordName, ct);
        var rel = DnsNames.Relative(recordName, domain);
        var values = await GetAsync(domain, rel, ct);
        if (!values.Contains(value)) values.Add(value);
        using var resp = await http.SendAsync(Req(HttpMethod.Put, $"{Api}/{domain}/records/TXT/{Uri.EscapeDataString(rel)}",
            values.Select(v => new { data = v, ttl = 600 }).ToArray()), ct);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"GoDaddy write failed ({(int)resp.StatusCode}): {await resp.Content.ReadAsStringAsync(ct)}");
        return null;
    }

    public async Task DeleteTxtAsync(string recordName, string value, string? recordId, CancellationToken ct)
    {
        var domain = await DomainAsync(recordName, ct);
        var rel = DnsNames.Relative(recordName, domain);
        var remaining = (await GetAsync(domain, rel, ct)).Where(v => v != value).ToList();
        using var resp = remaining.Count > 0
            ? await http.SendAsync(Req(HttpMethod.Put, $"{Api}/{domain}/records/TXT/{Uri.EscapeDataString(rel)}", remaining.Select(v => new { data = v, ttl = 600 }).ToArray()), ct)
            : await http.SendAsync(Req(HttpMethod.Delete, $"{Api}/{domain}/records/TXT/{Uri.EscapeDataString(rel)}"), ct);
    }
}

// ---------------------------------------------------------------- RFC 2136 dynamic update with TSIG (RFC 8945)
public sealed class Rfc2136DnsProvider(string server, int port, string keyName, byte[] secret, string algorithm, string? zone) : IDnsChallengeProvider
{
    public bool IsAutomatic => true;

    private async Task<IPAddress> ServerAddressAsync(CancellationToken ct) =>
        IPAddress.TryParse(server, out var ip) ? ip : (await Dns.GetHostAddressesAsync(server, ct)).First();

    private async Task<string> ZoneAsync(string name, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(zone)) return zone.TrimEnd('.');
        var client = new LookupClient(new LookupClientOptions(new NameServer(await ServerAddressAsync(ct), port)) { UseCache = false, Timeout = TimeSpan.FromSeconds(5) });
        foreach (var c in DnsNames.Candidates(name))
        {
            var r = await client.QueryAsync(c, QueryType.SOA, cancellationToken: ct);
            if (r.Answers.SoaRecords().Any(s => s.DomainName.Value.TrimEnd('.').Equals(c, StringComparison.OrdinalIgnoreCase))) return c;
        }
        throw new InvalidOperationException($"Could not find the zone for '{name}' on {server}. Set the zone explicitly.");
    }

    private static void Name(List<byte> b, string name)
    {
        foreach (var label in name.TrimEnd('.').ToLowerInvariant().Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var l = Encoding.ASCII.GetBytes(label);
            if (l.Length > 63) throw new InvalidOperationException("DNS label too long.");
            b.Add((byte)l.Length);
            b.AddRange(l);
        }
        b.Add(0);
    }

    private static void U16(List<byte> b, int v) { b.Add((byte)(v >> 8)); b.Add((byte)v); }
    private static void U32(List<byte> b, uint v) { b.Add((byte)(v >> 24)); b.Add((byte)(v >> 16)); b.Add((byte)(v >> 8)); b.Add((byte)v); }
    private static void U48(List<byte> b, long v) { for (var s = 40; s >= 0; s -= 8) b.Add((byte)(v >> s)); }

    private async Task UpdateAsync(string name, string value, bool add, CancellationToken ct)
    {
        var z = await ZoneAsync(name, ct);
        var id = RandomNumberGenerator.GetInt32(1, 65535);
        var msg = new List<byte>();
        U16(msg, id); U16(msg, 0x2800); U16(msg, 1); U16(msg, 0); U16(msg, 1); U16(msg, 0);
        Name(msg, z); U16(msg, 6); U16(msg, 1);                                   // zone section: SOA IN
        Name(msg, name); U16(msg, 16); U16(msg, add ? 1 : 254); U32(msg, add ? 60u : 0u);   // update: TXT, IN (add) / NONE (delete RR)
        var txt = Encoding.ASCII.GetBytes(value);
        U16(msg, txt.Length + 1); msg.Add((byte)txt.Length); msg.AddRange(txt);

        var alg = algorithm.Equals("hmac-sha512", StringComparison.OrdinalIgnoreCase) ? "hmac-sha512" : "hmac-sha256";
        var time = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var vars = new List<byte>();
        Name(vars, keyName); U16(vars, 255); U32(vars, 0); Name(vars, alg); U48(vars, time); U16(vars, 300); U16(vars, 0); U16(vars, 0);
        var signed = msg.Concat(vars).ToArray();
        var mac = alg == "hmac-sha512" ? HMACSHA512.HashData(secret, signed) : HMACSHA256.HashData(secret, signed);

        var rdata = new List<byte>();
        Name(rdata, alg); U48(rdata, time); U16(rdata, 300); U16(rdata, mac.Length); rdata.AddRange(mac); U16(rdata, id); U16(rdata, 0); U16(rdata, 0);
        Name(msg, keyName); U16(msg, 250); U16(msg, 255); U32(msg, 0); U16(msg, rdata.Count); msg.AddRange(rdata);
        msg[10] = 0; msg[11] = 1; // ARCOUNT = 1

        using var tcp = new TcpClient();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(15));
        await tcp.ConnectAsync(await ServerAddressAsync(ct), port, cts.Token);
        var stream = tcp.GetStream();
        var framed = new byte[msg.Count + 2];
        framed[0] = (byte)(msg.Count >> 8); framed[1] = (byte)msg.Count;
        msg.CopyTo(framed, 2);
        await stream.WriteAsync(framed, cts.Token);
        var lenBuf = new byte[2];
        await stream.ReadExactlyAsync(lenBuf, cts.Token);
        var resp = new byte[(lenBuf[0] << 8) | lenBuf[1]];
        await stream.ReadExactlyAsync(resp, cts.Token);
        var rcode = resp[3] & 0x0F;
        if (rcode != 0)
            throw new InvalidOperationException(rcode switch
            {
                5 => $"{server} REFUSED the update. Allow updates for key '{keyName}' on zone {z}.",
                9 => $"{server} answered NOTAUTH: TSIG key name/secret/algorithm mismatch, clock skew, or not authoritative for {z}.",
                2 => $"{server} SERVFAIL while updating {z}.",
                _ => $"{server} rejected the update (RCODE {rcode})."
            });
    }

    public async Task<string?> CreateTxtAsync(string recordName, string value, CancellationToken ct) { await UpdateAsync(recordName, value, true, ct); return null; }
    public Task DeleteTxtAsync(string recordName, string value, string? recordId, CancellationToken ct) => UpdateAsync(recordName, value, false, ct);
}

// ---------------------------------------------------------------- Windows DNS via agent
public sealed class WindowsDnsProvider(AppDb db, Guid tenantId, Guid agentId, string? dnsServer, string? zone) : IDnsChallengeProvider
{
    public bool IsAutomatic => true;

    private async Task RunAsync(string action, string name, string value, bool throwOnFail, CancellationToken ct)
    {
        var task = new DnsTask { TenantId = tenantId, AgentId = agentId, Action = action, Name = name.TrimEnd('.'), Value = value, DnsServer = dnsServer, Zone = zone };
        db.DnsTasks.Add(task);
        await db.SaveChangesAsync(ct);
        var deadline = DateTimeOffset.UtcNow.AddMinutes(4);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
            var s = await db.DnsTasks.IgnoreQueryFilters().AsNoTracking().Where(t => t.Id == task.Id)
                .Select(t => new { t.Status, t.Message }).FirstAsync(ct);
            if (s.Status == "Done") return;
            if (s.Status == "Failed")
            {
                if (throwOnFail) throw new InvalidOperationException("Windows DNS: " + s.Message);
                return;
            }
        }
        if (throwOnFail) throw new InvalidOperationException("Windows DNS: the agent did not process the DNS change within 4 minutes. Is it online?");
    }

    public async Task<string?> CreateTxtAsync(string recordName, string value, CancellationToken ct) { await RunAsync("add", recordName, value, true, ct); return null; }
    public Task DeleteTxtAsync(string recordName, string value, string? recordId, CancellationToken ct) => RunAsync("delete", recordName, value, false, ct);
}

// ---------------------------------------------------------------- Webhook (any provider / script)
public sealed class WebhookDnsProvider(HttpClient http, string url, string hmacSecret) : IDnsChallengeProvider
{
    public bool IsAutomatic => true;

    private async Task<string?> CallAsync(string action, string name, string value, string? id, CancellationToken ct)
    {
        if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Webhook URL must use https://");
        var body = JsonSerializer.Serialize(new { action, name = name.TrimEnd('.'), value, recordId = id, ttl = 60, timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        req.Headers.TryAddWithoutValidation("X-AutomateCert-Signature", "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(hmacSecret), Encoding.UTF8.GetBytes(body))).ToLowerInvariant());
        using var resp = await http.SendAsync(req, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"Webhook {action} failed ({(int)resp.StatusCode}): {(text.Length > 300 ? text[..300] : text)}");
        try { return JsonNode.Parse(text)?["id"]?.GetValue<string>(); } catch { return null; }
    }

    public Task<string?> CreateTxtAsync(string recordName, string value, CancellationToken ct) => CallAsync("create", recordName, value, null, ct);
    public Task DeleteTxtAsync(string recordName, string value, string? recordId, CancellationToken ct) => CallAsync("delete", recordName, value, recordId, ct);
}

// ---------------------------------------------------------------- Propagation + testing
public static class DnsPropagation
{
    private static LookupClient Public() => new(new LookupClientOptions(IPAddress.Parse("1.1.1.1"), IPAddress.Parse("8.8.8.8"))
    {
        UseCache = false, Timeout = TimeSpan.FromSeconds(5)
    });

    /// <summary>Polls public resolvers until the TXT value is visible (CNAMEs are followed) or the timeout passes.</summary>
    public static async Task<bool> WaitForTxtAsync(string name, string value, TimeSpan timeout, CancellationToken ct)
    {
        var client = Public();
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                var r = await client.QueryAsync(name, QueryType.TXT, cancellationToken: ct);
                if (r.Answers.TxtRecords().SelectMany(t => t.Text).Contains(value)) return true;
            }
            catch (DnsResponseException) { /* retry */ }
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
        }
        return false;
    }

    public static async Task<string?> CnameTargetAsync(string name, CancellationToken ct)
    {
        try
        {
            var r = await Public().QueryAsync(name, QueryType.CNAME, cancellationToken: ct);
            return r.Answers.CnameRecords().FirstOrDefault()?.CanonicalName.Value.TrimEnd('.');
        }
        catch { return null; }
    }
}
