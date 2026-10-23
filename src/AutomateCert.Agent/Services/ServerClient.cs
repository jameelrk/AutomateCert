using System.Net;
using System.Net.Http.Json;
using System.Security.Authentication;
using AutomateCert.Shared;
using Microsoft.Extensions.Options;

namespace AutomateCert.Agent.Services;

/// <summary>Outbound-only HTTPS (TLS 1.2/1.3) client to the AutomateCert console. The agent never opens a listening port.</summary>
public sealed class ServerClient(IHttpClientFactory factory, IOptions<AgentOptions> options, CredentialStore creds)
{
    public const string HttpClientName = "automatecert-server";

    public static HttpMessageHandler CreateHandler(IServiceProvider sp)
    {
        var o = sp.GetRequiredService<IOptions<AgentOptions>>().Value;
        var pin = o.ServerCertificateThumbprint?.Replace(" ", "");
        var loopback = Uri.TryCreate(o.ServerUrl, UriKind.Absolute, out var u) && u.IsLoopback;
        var handler = new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(10) };
        handler.SslOptions.EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;
        if (loopback || !string.IsNullOrWhiteSpace(pin))
            handler.SslOptions.RemoteCertificateValidationCallback = (_, cert, _, errors) =>
                // Loopback traffic never leaves the machine. Otherwise accept a normally trusted certificate (so the console can
                // move from self-signed to a CA-issued certificate) or the pinned one.
                loopback || errors == System.Net.Security.SslPolicyErrors.None ||
                (cert is not null && string.Equals(cert.GetCertHashString(), pin, StringComparison.OrdinalIgnoreCase));
        return handler;
    }

    private HttpClient Client()
    {
        var c = factory.CreateClient(HttpClientName);
        var url = options.Value.ServerUrl.TrimEnd('/') + "/";
        if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("ServerUrl must use https://");
        c.BaseAddress = new Uri(url);
        c.Timeout = TimeSpan.FromSeconds(60);
        return c;
    }

    private HttpRequestMessage Req(HttpMethod m, string path, object? body = null)
    {
        var cred = creds.Load() ?? throw new InvalidOperationException("Agent is not enrolled.");
        var r = new HttpRequestMessage(m, path);
        r.Headers.TryAddWithoutValidation("Authorization", $"{AgentProtocol.AuthScheme} {cred.AgentId}:{cred.Secret}");
        if (body is not null) r.Content = JsonContent.Create(body);
        return r;
    }

    private async Task SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        using var resp = await Client().SendAsync(req, ct);
        await EnsureAsync(resp, ct);
    }

    private static async Task EnsureAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        if (resp.StatusCode == HttpStatusCode.Unauthorized) throw new UnauthorizedAccessException("Server rejected agent credentials (revoked?).");
        if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"Server returned {(int)resp.StatusCode}: {await resp.Content.ReadAsStringAsync(ct)}");
    }

    public async Task<EnrollResponse> EnrollAsync(EnrollRequest request, CancellationToken ct)
    {
        using var resp = await Client().PostAsJsonAsync("api/agent/enroll", request, ct);
        if (resp.StatusCode == HttpStatusCode.Unauthorized) throw new InvalidOperationException("Enrollment token is invalid, used up or expired. Generate a new one in the console.");
        await EnsureAsync(resp, ct);
        return (await resp.Content.ReadFromJsonAsync<EnrollResponse>(ct))!;
    }

    public Task HeartbeatAsync(HeartbeatRequest r, CancellationToken ct) => SendAsync(Req(HttpMethod.Post, "api/agent/heartbeat", r), ct);

    public async Task<AgentJob?> NextJobAsync(CancellationToken ct)
    {
        using var resp = await Client().SendAsync(Req(HttpMethod.Get, "api/agent/jobs/next"), ct);
        if (resp.StatusCode == HttpStatusCode.NoContent) return null;
        await EnsureAsync(resp, ct);
        return await resp.Content.ReadFromJsonAsync<AgentJob>(ct);
    }

    public Task SubmitCsrAsync(Guid jobId, string csrPem, CancellationToken ct) => SendAsync(Req(HttpMethod.Post, $"api/agent/jobs/{jobId}/csr", new CsrSubmission(csrPem)), ct);

    public async Task<string> GetCertificateAsync(Guid jobId, CancellationToken ct)
    {
        using var resp = await Client().SendAsync(Req(HttpMethod.Get, $"api/agent/jobs/{jobId}/certificate"), ct);
        await EnsureAsync(resp, ct);
        return (await resp.Content.ReadFromJsonAsync<CertificateDelivery>(ct))!.ChainPem;
    }

    public Task CompleteAsync(Guid jobId, JobResult result, CancellationToken ct) => SendAsync(Req(HttpMethod.Post, $"api/agent/jobs/{jobId}/complete", result), ct);
    public Task SendInventoryAsync(InventoryReport report, CancellationToken ct) => SendAsync(Req(HttpMethod.Post, "api/agent/inventory", report), ct);

    public async Task<AgentDnsTask[]> DnsTasksAsync(CancellationToken ct)
    {
        using var resp = await Client().SendAsync(Req(HttpMethod.Get, "api/agent/dns-tasks"), ct);
        await EnsureAsync(resp, ct);
        return await resp.Content.ReadFromJsonAsync<AgentDnsTask[]>(ct) ?? [];
    }

    public Task CompleteDnsTaskAsync(Guid id, AgentDnsTaskResult r, CancellationToken ct) => SendAsync(Req(HttpMethod.Post, $"api/agent/dns-tasks/{id}", r), ct);
}
