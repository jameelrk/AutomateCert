using System.Diagnostics;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using AutomateCert.Shared;
using Microsoft.Extensions.Options;

namespace AutomateCert.Agent.Services;

public sealed record ScriptResult(bool Ok, string? PreviousThumbprint, string Message, string RawOutput);

/// <summary>
/// Runs only the PowerShell scripts embedded in this agent binary (re-extracted every run). Parameters are passed via a
/// JSON file, never concatenated into a command line, so values cannot inject PowerShell code.
/// </summary>
public sealed class PowerShellRunner(IOptions<AgentOptions> options, ILogger<PowerShellRunner> log)
{
    private const string Marker = "##AUTOMATECERT##";

    public async Task<ScriptResult> RunAsync(string scriptName, object parameters, CancellationToken ct, TimeSpan? timeout = null)
    {
        var dir = Path.Combine(options.Value.DataDirectory, "scripts");
        var work = Path.Combine(options.Value.DataDirectory, "work");
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(work);
        var resource = $"AutomateCert.Agent.Scripts.{scriptName}.ps1";
        await using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource) ?? throw new InvalidOperationException($"Embedded script '{scriptName}' not found."))
        await using (var f = File.Create(Path.Combine(dir, scriptName + ".ps1")))
            await s.CopyToAsync(f, ct);

        var paramFile = Path.Combine(work, Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(paramFile, JsonSerializer.Serialize(parameters), ct);
        try
        {
            var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"))
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8 };
            foreach (var a in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(dir, scriptName + ".ps1"), "-ParamFile", paramFile })
                psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var stdoutTask = p.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = p.StandardError.ReadToEndAsync(ct);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout ?? TimeSpan.FromMinutes(10));
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException) { try { p.Kill(true); } catch { /* ignore */ } return new ScriptResult(false, null, $"Script '{scriptName}' timed out.", ""); }
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            var line = stdout.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith(Marker, StringComparison.Ordinal));
            if (line is null) return new ScriptResult(false, null, $"Script '{scriptName}' produced no result. {stderr}".Trim(), stdout);
            using var doc = JsonDocument.Parse(line[Marker.Length..]);
            var root = doc.RootElement;
            var ok = root.TryGetProperty("ok", out var okEl) && okEl.ValueKind == JsonValueKind.True;
            var prev = root.TryGetProperty("previousThumbprint", out var pv) && pv.ValueKind == JsonValueKind.String ? pv.GetString() : null;
            var msg = root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() ?? "" : "";
            log.LogInformation("Script {Script} ok={Ok}: {Message}", scriptName, ok, msg.Length > 300 ? msg[..300] : msg);
            return new ScriptResult(ok, string.IsNullOrWhiteSpace(prev) ? null : prev.ToUpperInvariant(), msg, stdout);
        }
        finally { try { File.Delete(paramFile); } catch { /* ignore */ } }
    }
}

public sealed class TlsVerifier
{
    public async Task<(bool Ok, string Detail)> VerifyAsync(string host, int port, string expectedThumbprint, CancellationToken ct)
    {
        string? last = null;
        for (var attempt = 1; attempt <= 6; attempt++)
        {
            try
            {
                using var tcp = new TcpClient();
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(10));
                await tcp.ConnectAsync(host, port, cts.Token);
                X509Certificate2? presented = null;
                await using var ssl = new SslStream(tcp.GetStream(), false, (_, cert, _, _) => { if (cert is not null) presented = X509CertificateLoader.LoadCertificate(cert.GetRawCertData()); return true; });
                await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host }, cts.Token);
                last = presented?.Thumbprint;
                if (string.Equals(last, expectedThumbprint, StringComparison.OrdinalIgnoreCase)) return (true, $"{host}:{port} presents {last}");
            }
            catch (Exception ex) when (!ct.IsCancellationRequested) { last = ex.Message; }
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }
        return (false, $"{host}:{port} did not present {expectedThumbprint} (last: {last})");
    }
}

public sealed class InventoryCollector(PowerShellRunner ps, ILogger<InventoryCollector> log)
{
    private sealed record BindingInfo(string? thumbprint, string? binding);
    private sealed record Scan(List<BindingInfo>? bindings, List<string>? roles);

    public async Task<InventoryReport> CollectAsync(CancellationToken ct)
    {
        var bindings = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        string[] roles = [];
        try
        {
            var r = await ps.RunAsync("inventory", new { }, ct, TimeSpan.FromMinutes(4));
            if (r.Ok && !string.IsNullOrEmpty(r.Message))
            {
                var scan = JsonSerializer.Deserialize<Scan>(r.Message);
                roles = scan?.roles?.Distinct().ToArray() ?? [];
                foreach (var b in scan?.bindings ?? [])
                {
                    if (string.IsNullOrWhiteSpace(b.thumbprint)) continue;
                    var key = b.thumbprint.Replace(" ", "").ToUpperInvariant();
                    if (!bindings.TryGetValue(key, out var list)) bindings[key] = list = [];
                    list.Add(b.binding ?? "");
                }
            }
        }
        catch (Exception ex) { log.LogWarning(ex, "Binding discovery failed; continuing with store inventory only."); }

        var certs = new List<InventoryCertificate>();
        using (var store = new X509Store(StoreName.My, StoreLocation.LocalMachine))
        {
            store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
            foreach (var c in store.Certificates)
                certs.Add(new InventoryCertificate(c.Thumbprint, c.Subject, CertificateInstaller.GetDnsNames(c), c.Issuer,
                    new DateTimeOffset(c.NotBefore.ToUniversalTime()), new DateTimeOffset(c.NotAfter.ToUniversalTime()), c.HasPrivateKey, @"LocalMachine\My",
                    bindings.TryGetValue(c.Thumbprint, out var b) ? b.Distinct().ToArray() : [],
                    CertificateInstaller.KeyInfo(c), c.SignatureAlgorithm.FriendlyName, c.SerialNumber));
        }
        return new InventoryReport(certs.ToArray(), roles);
    }
}

/// <summary>Applies deployment steps in order, verifies, and rolls back every applied step on failure.</summary>
public sealed class DeploymentEngine(PowerShellRunner ps, TlsVerifier tls)
{
    public async Task<bool> DeployAsync(AgentJob job, string thumbprint, StringBuilder log, CancellationToken ct)
    {
        var applied = new Stack<(AgentTarget Target, string? Previous)>();
        foreach (var t in job.Targets)
        {
            if (DeploymentTemplates.Validate(t.Template, t.Parameters) is { } err)
            {
                log.AppendLine($"[{t.Template}] rejected locally: {err}");
                await RollbackAsync(applied, thumbprint, log, ct);
                return false;
            }
            log.AppendLine($"[{t.Template}] applying {thumbprint}");
            var r = await ps.RunAsync(t.Template, new { thumbprint, parameters = t.Parameters }, ct, TimeSpan.FromMinutes(15));
            log.AppendLine($"[{t.Template}] {(r.Ok ? "OK" : "FAILED")}: {r.Message}");
            if (!r.Ok)
            {
                await RollbackOneAsync(t, r.PreviousThumbprint, thumbprint, log, ct);
                await RollbackAsync(applied, thumbprint, log, ct);
                return false;
            }
            applied.Push((t, r.PreviousThumbprint));
            if (!string.IsNullOrWhiteSpace(t.VerifyHost))
            {
                var (ok, detail) = await tls.VerifyAsync(t.VerifyHost, t.VerifyPort ?? 443, thumbprint, ct);
                log.AppendLine($"[{t.Template}] verify: {detail}");
                if (!ok) { await RollbackAsync(applied, thumbprint, log, ct); return false; }
            }
        }
        return true;
    }

    /// <summary>Follow-up steps on a server that does not hold the certificate (AD FS relying-party refresh, CRM reload, service restart).</summary>
    public async Task<bool> RunTasksAsync(AgentJob job, StringBuilder log, CancellationToken ct)
    {
        foreach (var t in job.Targets)
        {
            if (DeploymentTemplates.Validate(t.Template, t.Parameters) is { } err) { log.AppendLine($"[{t.Template}] rejected locally: {err}"); return false; }
            var r = await ps.RunAsync(t.Template, new { thumbprint = job.ThumbprintToDeploy ?? "", parameters = t.Parameters }, ct, TimeSpan.FromMinutes(15));
            log.AppendLine($"[{t.Template}] {(r.Ok ? "OK" : "FAILED")}: {r.Message}");
            if (!r.Ok) return false;
        }
        return true;
    }

    private async Task RollbackOneAsync(AgentTarget t, string? previous, string current, StringBuilder log, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(previous) || string.Equals(previous, current, StringComparison.OrdinalIgnoreCase)) return;
        if (t.Template == DeploymentTemplates.AdfsToken) { log.AppendLine("[adfstoken] rollback: the new certificate stays secondary; primary unchanged."); return; }
        if (CertificateInstaller.FindInMachineStore(previous) is null) { log.AppendLine($"[{t.Template}] rollback skipped: previous certificate {previous} no longer in store"); return; }
        var parameters = new Dictionary<string, string>(t.Parameters);
        if (t.Template is DeploymentTemplates.Rebind or DeploymentTemplates.Adfs) parameters[DeploymentTemplates.OldThumbprintParameter] = current;
        var r = await ps.RunAsync(t.Template, new { thumbprint = previous, parameters }, ct, TimeSpan.FromMinutes(15));
        log.AppendLine($"[{t.Template}] rollback to {previous}: {(r.Ok ? "OK" : "FAILED " + r.Message)}");
    }

    private async Task RollbackAsync(Stack<(AgentTarget Target, string? Previous)> applied, string current, StringBuilder log, CancellationToken ct)
    {
        while (applied.Count > 0) { var (t, prev) = applied.Pop(); await RollbackOneAsync(t, prev, current, log, ct); }
    }
}
