using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using AutomateCert.Agent.Services;
using AutomateCert.Shared;
using Microsoft.Extensions.Options;

namespace AutomateCert.Agent;

public sealed class AgentWorker(
    IOptions<AgentOptions> options, CredentialStore creds, PendingKeyStore keys, ServerClient server, CertificateInstaller installer,
    AdcsClient adcs, InventoryCollector inventory, DeploymentEngine deployer, PowerShellRunner ps, ILogger<AgentWorker> log) : BackgroundService
{
    private DateTimeOffset _lastInventory = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await EnsureEnrolledAsync(ct);
        var poll = TimeSpan.FromSeconds(Math.Clamp(options.Value.PollSeconds, 10, 600));
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await server.HeartbeatAsync(new HeartbeatRequest(AgentProtocol.Version, Environment.OSVersion.VersionString), ct);
                await ProcessDnsTasksAsync(ct);
                if (DateTimeOffset.UtcNow - _lastInventory > TimeSpan.FromHours(Math.Max(1, options.Value.InventoryHours)))
                {
                    await server.SendInventoryAsync(await inventory.CollectAsync(ct), ct);
                    _lastInventory = DateTimeOffset.UtcNow;
                    keys.Prune();
                }
                while (await server.NextJobAsync(ct) is { } job)
                {
                    await ProcessAsync(job, ct);
                    await ProcessDnsTasksAsync(ct);
                }
            }
            catch (UnauthorizedAccessException ex) { log.LogError(ex, "Credentials rejected (agent revoked?). Reinstall the agent with a new enrollment token."); await Task.Delay(TimeSpan.FromMinutes(5), ct); }
            catch (Exception ex) when (!ct.IsCancellationRequested) { log.LogWarning(ex, "Agent cycle failed; will retry."); }
            // Poll faster while DNS work may be pending so Windows DNS challenges complete quickly.
            await Task.Delay(poll, ct);
        }
    }

    private async Task EnsureEnrolledAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (creds.Load() is not null) return;
            var token = options.Value.EnrollmentToken;
            if (string.IsNullOrWhiteSpace(token)) log.LogError("Agent is not enrolled and no EnrollmentToken is configured.");
            else
            {
                try
                {
                    var r = await server.EnrollAsync(new EnrollRequest(token, Environment.MachineName, Environment.OSVersion.VersionString, AgentProtocol.Version), ct);
                    creds.Save(new CredentialStore.Credential(r.AgentId, r.Secret));
                    log.LogInformation("Enrolled as agent {AgentId}.", r.AgentId);
                    return;
                }
                catch (Exception ex) { log.LogError(ex, "Enrollment failed."); }
            }
            await Task.Delay(TimeSpan.FromMinutes(1), ct);
        }
    }

    private async Task ProcessDnsTasksAsync(CancellationToken ct)
    {
        foreach (var t in await server.DnsTasksAsync(ct))
        {
            var r = await ps.RunAsync("dnswindows", new { action = t.Action, name = t.Name, value = t.Value, server = t.DnsServer ?? "", zone = t.Zone ?? "" }, ct, TimeSpan.FromMinutes(2));
            await server.CompleteDnsTaskAsync(t.Id, new AgentDnsTaskResult(r.Ok, r.Message), ct);
        }
    }

    private async Task ProcessAsync(AgentJob job, CancellationToken ct)
    {
        var sb = new StringBuilder();
        log.LogInformation("Job {Id} {Type} ({Status}) {Phase}", job.Id, job.Type, job.Status, job.Phase);
        try
        {
            switch (job.Type, job.Status)
            {
                case (JobTypes.Discover, _):
                    var report = await inventory.CollectAsync(ct);
                    await server.SendInventoryAsync(report, ct);
                    _lastInventory = DateTimeOffset.UtcNow;
                    await server.CompleteAsync(job.Id, new JobResult(true, null, null, null, $"Reported {report.Certificates.Length} certificates; roles: {string.Join(", ", report.Roles ?? [])}."), ct);
                    break;
                case (JobTypes.Renew, JobStatuses.CsrRequested):
                    await BeginRenewAsync(job, sb, ct);
                    break;
                case (JobTypes.Renew, JobStatuses.Deploying):
                    await InstallAndDeployAsync(job, await server.GetCertificateAsync(job.Id, ct), sb, ct);
                    break;
                case (JobTypes.Task, _):
                {
                    var ok = await deployer.RunTasksAsync(job, sb, ct);
                    await server.CompleteAsync(job.Id, new JobResult(ok, null, null, null, sb.ToString()), ct);
                    break;
                }
                case (JobTypes.Deploy, _):
                {
                    var tp = job.ThumbprintToDeploy ?? throw new InvalidOperationException("No thumbprint supplied.");
                    var cert = CertificateInstaller.FindInMachineStore(tp) ?? throw new InvalidOperationException($"Certificate {tp} not found in LocalMachine\\My.");
                    if (!cert.HasPrivateKey) throw new InvalidOperationException("Certificate has no private key.");
                    var ok = await deployer.DeployAsync(job, cert.Thumbprint, sb, ct);
                    await server.CompleteAsync(job.Id, new JobResult(ok, cert.Thumbprint, new DateTimeOffset(cert.NotBefore.ToUniversalTime()), new DateTimeOffset(cert.NotAfter.ToUniversalTime()), sb.ToString()), ct);
                    break;
                }
                default:
                    throw new InvalidOperationException($"Unexpected job state {job.Type}/{job.Status}.");
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            log.LogError(ex, "Job {Id} failed", job.Id);
            sb.AppendLine("ERROR: " + ex.Message);
            try { await server.CompleteAsync(job.Id, new JobResult(false, null, null, null, sb.ToString()), ct); }
            catch (Exception ex2) { log.LogWarning(ex2, "Could not report failure for job {Id}", job.Id); }
            if (job.Type == JobTypes.Renew) keys.Delete(job.Id);
        }
    }

    private async Task BeginRenewAsync(AgentJob job, StringBuilder sb, CancellationToken ct)
    {
        var (csrPem, pkcs8) = CsrFactory.Create(job.Domains, job.KeyAlgorithm);
        try
        {
            keys.Save(job.Id, job.KeyAlgorithm, pkcs8);
            sb.AppendLine($"Generated {job.KeyAlgorithm} key and CSR locally for {string.Join(", ", job.Domains)}.");
            if (job.IssuerType == IssuerTypes.AdcsInternal)
            {
                var certPem = await adcs.SubmitAsync(csrPem, job.AdcsConfig ?? "", job.AdcsTemplate ?? "", ct);
                sb.AppendLine($"Issued by AD CS ({job.AdcsConfig}).");
                await InstallAndDeployAsync(job, certPem, sb, ct);
            }
            else
            {
                await server.SubmitCsrAsync(job.Id, csrPem, ct);
                log.LogInformation("CSR submitted for job {Id}; waiting for ACME issuance.", job.Id);
            }
        }
        finally { CryptographicOperations.ZeroMemory(pkcs8); }
    }

    private async Task InstallAndDeployAsync(AgentJob job, string chainPem, StringBuilder sb, CancellationToken ct)
    {
        var key = keys.Load(job.Id) ?? throw new InvalidOperationException("Pending private key not found on this server (was the agent reinstalled?).");
        X509Certificate2 cert;
        try { cert = installer.Install(chainPem, key.Algorithm, key.Pkcs8, job.KeyStorage, job.Exportable, job.Domains, $"AutomateCert {job.ProfileName} {DateTime.UtcNow:yyyy-MM-dd}"); }
        finally { CryptographicOperations.ZeroMemory(key.Pkcs8); }
        keys.Delete(job.Id);
        sb.AppendLine($"Installed {cert.Thumbprint} in LocalMachine\\My, valid to {cert.NotAfter:u}.");
        var ok = job.Targets.Length == 0 || await deployer.DeployAsync(job, cert.Thumbprint, sb, ct);
        if (!ok) sb.AppendLine("Deployment failed; previous bindings were restored. The new certificate remains in the store.");
        await server.CompleteAsync(job.Id, new JobResult(ok, cert.Thumbprint, new DateTimeOffset(cert.NotBefore.ToUniversalTime()), new DateTimeOffset(cert.NotAfter.ToUniversalTime()), sb.ToString()), ct);
    }
}
