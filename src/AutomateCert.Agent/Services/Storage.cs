using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace AutomateCert.Agent.Services;

/// <summary>Agent credentials, DPAPI (LocalMachine) encrypted in a folder ACL'd to SYSTEM and Administrators.</summary>
public sealed class CredentialStore(IOptions<AgentOptions> options)
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("AutomateCert.Agent.Credential.v1");
    private string FilePath => Path.Combine(options.Value.DataDirectory, "agent.cred");
    public sealed record Credential(Guid AgentId, string Secret);

    public Credential? Load()
    {
        if (!File.Exists(FilePath)) return null;
        var plain = ProtectedData.Unprotect(File.ReadAllBytes(FilePath), Entropy, DataProtectionScope.LocalMachine);
        try { return JsonSerializer.Deserialize<Credential>(plain); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public void Save(Credential c)
    {
        Directory.CreateDirectory(options.Value.DataDirectory);
        var plain = JsonSerializer.SerializeToUtf8Bytes(c);
        File.WriteAllBytes(FilePath, ProtectedData.Protect(plain, Entropy, DataProtectionScope.LocalMachine));
        CryptographicOperations.ZeroMemory(plain);
    }
}

/// <summary>Holds private keys between CSR and certificate delivery (DPAPI-protected, deleted after install).</summary>
public sealed class PendingKeyStore(IOptions<AgentOptions> options)
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("AutomateCert.Agent.PendingKey.v1");
    private string Dir => Path.Combine(options.Value.DataDirectory, "pending");
    private string PathFor(Guid jobId) => Path.Combine(Dir, jobId.ToString("N") + ".key");

    public void Save(Guid jobId, string algorithm, byte[] pkcs8)
    {
        Directory.CreateDirectory(Dir);
        var payload = new byte[pkcs8.Length + 32];
        Buffer.BlockCopy(Encoding.ASCII.GetBytes(algorithm.PadRight(32)), 0, payload, 0, 32);
        Buffer.BlockCopy(pkcs8, 0, payload, 32, pkcs8.Length);
        File.WriteAllBytes(PathFor(jobId), ProtectedData.Protect(payload, Entropy, DataProtectionScope.LocalMachine));
        CryptographicOperations.ZeroMemory(payload);
    }

    public (string Algorithm, byte[] Pkcs8)? Load(Guid jobId)
    {
        var p = PathFor(jobId);
        if (!File.Exists(p)) return null;
        var payload = ProtectedData.Unprotect(File.ReadAllBytes(p), Entropy, DataProtectionScope.LocalMachine);
        var alg = Encoding.ASCII.GetString(payload, 0, 32).Trim();
        var key = payload[32..];
        CryptographicOperations.ZeroMemory(payload);
        return (alg, key);
    }

    public void Delete(Guid jobId) { var p = PathFor(jobId); if (File.Exists(p)) File.Delete(p); }

    public void Prune()
    {
        if (!Directory.Exists(Dir)) return;
        foreach (var f in Directory.GetFiles(Dir, "*.key"))
            if (File.GetCreationTimeUtc(f) < DateTime.UtcNow.AddDays(-7)) File.Delete(f);
    }
}
