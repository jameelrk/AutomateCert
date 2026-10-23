using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using AutomateCert.Shared;
using Microsoft.Extensions.Options;

namespace AutomateCert.Agent.Services;

public static class CsrFactory
{
    /// <summary>Generates a fresh key pair and PKCS#10 CSR locally. The private key never leaves this machine.</summary>
    public static (string CsrPem, byte[] Pkcs8) Create(string[] domains, string algorithm)
    {
        if (domains.Length == 0) throw new ArgumentException("No domains.");
        var subject = new X500DistinguishedName("CN=" + domains[0]);
        var san = new SubjectAlternativeNameBuilder();
        foreach (var d in domains) san.AddDnsName(d);

        void AddExtensions(CertificateRequest req, X509KeyUsageFlags usage)
        {
            req.CertificateExtensions.Add(san.Build());
            req.CertificateExtensions.Add(new X509KeyUsageExtension(usage, false));
            req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
        }

        switch (algorithm)
        {
            case KeyAlgorithms.Rsa2048:
            case KeyAlgorithms.Rsa3072:
            case KeyAlgorithms.Rsa4096:
            {
                using var rsa = RSA.Create(algorithm switch { KeyAlgorithms.Rsa3072 => 3072, KeyAlgorithms.Rsa4096 => 4096, _ => 2048 });
                var req = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                AddExtensions(req, X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment);
                return (req.CreateSigningRequestPem(), rsa.ExportPkcs8PrivateKey());
            }
            case KeyAlgorithms.EcdsaP256:
            case KeyAlgorithms.EcdsaP384:
            {
                using var ec = ECDsa.Create(algorithm == KeyAlgorithms.EcdsaP384 ? ECCurve.NamedCurves.nistP384 : ECCurve.NamedCurves.nistP256);
                var req = new CertificateRequest(subject, ec, algorithm == KeyAlgorithms.EcdsaP384 ? HashAlgorithmName.SHA384 : HashAlgorithmName.SHA256);
                AddExtensions(req, X509KeyUsageFlags.DigitalSignature);
                return (req.CreateSigningRequestPem(), ec.ExportPkcs8PrivateKey());
            }
            default:
                throw new NotSupportedException($"Key algorithm {algorithm} is not supported.");
        }
    }
}

public sealed class CertificateInstaller(ILogger<CertificateInstaller> log)
{
    public X509Certificate2 Install(string chainPem, string algorithm, byte[] pkcs8, string keyStorage, bool exportable, string[] expectedDomains, string friendlyName)
    {
        var chain = new X509Certificate2Collection();
        chain.ImportFromPem(chainPem);
        if (chain.Count == 0) throw new InvalidOperationException("Issued chain contained no certificates.");
        var leaf = chain[0];
        var dnsNames = GetDnsNames(leaf);
        var missing = expectedDomains.Where(d => !dnsNames.Contains(d, StringComparer.OrdinalIgnoreCase)).ToArray();
        if (missing.Length > 0) throw new InvalidOperationException($"Issued certificate is missing SANs: {string.Join(", ", missing)}");

        var persisted = keyStorage == KeyStorages.Capi && !KeyAlgorithms.IsEcdsa(algorithm)
            ? PersistWithCapi(leaf, pkcs8, exportable)
            : PersistWithCng(leaf, algorithm, pkcs8, exportable);
        persisted.FriendlyName = friendlyName;
        using (var my = new X509Store(StoreName.My, StoreLocation.LocalMachine)) { my.Open(OpenFlags.ReadWrite); my.Add(persisted); }
        using (var ca = new X509Store(StoreName.CertificateAuthority, StoreLocation.LocalMachine))
        {
            ca.Open(OpenFlags.ReadWrite);
            foreach (var issuer in chain.Skip(1).Where(c => c.Subject != c.Issuer)) ca.Add(issuer);
        }
        log.LogInformation("Installed {Thumbprint} ({Subject}) expiring {NotAfter}", persisted.Thumbprint, persisted.Subject, persisted.NotAfter);
        return persisted;
    }

    private static X509Certificate2 PersistWithCng(X509Certificate2 leaf, string algorithm, byte[] pkcs8, bool exportable)
    {
        X509Certificate2 withKey;
        if (KeyAlgorithms.IsEcdsa(algorithm))
        {
            using var ec = ECDsa.Create();
            ec.ImportPkcs8PrivateKey(pkcs8, out _);
            withKey = leaf.CopyWithPrivateKey(ec);
        }
        else
        {
            using var rsa = RSA.Create();
            rsa.ImportPkcs8PrivateKey(pkcs8, out _);
            withKey = leaf.CopyWithPrivateKey(rsa);
        }
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var pfx = withKey.Export(X509ContentType.Pkcs12, password);
        try
        {
            var flags = X509KeyStorageFlags.MachineKeySet | X509KeyStorageFlags.PersistKeySet;
            if (exportable) flags |= X509KeyStorageFlags.Exportable;
            return X509CertificateLoader.LoadPkcs12(pfx, password, flags);
        }
        finally { CryptographicOperations.ZeroMemory(pfx); withKey.Dispose(); }
    }

    private static X509Certificate2 PersistWithCapi(X509Certificate2 leaf, byte[] pkcs8, bool exportable)
    {
        using var temp = RSA.Create();
        temp.ImportPkcs8PrivateKey(pkcs8, out _);
        var parameters = temp.ExportParameters(true);
        try
        {
            var csp = new CspParameters(24, "Microsoft Enhanced RSA and AES Cryptographic Provider", "AutomateCert-" + Guid.NewGuid().ToString("N"))
            { Flags = CspProviderFlags.UseMachineKeyStore | (exportable ? 0 : CspProviderFlags.UseNonExportableKey) };
            using var rsa = new RSACryptoServiceProvider(csp) { PersistKeyInCsp = true };
            rsa.ImportParameters(parameters);
            return leaf.CopyWithPrivateKey(rsa);
        }
        finally
        {
            foreach (var arr in new[] { parameters.D, parameters.DP, parameters.DQ, parameters.InverseQ, parameters.P, parameters.Q })
                if (arr is not null) CryptographicOperations.ZeroMemory(arr);
        }
    }

    public static string[] GetDnsNames(X509Certificate2 cert) =>
        cert.Extensions.OfType<X509SubjectAlternativeNameExtension>().FirstOrDefault()?.EnumerateDnsNames().ToArray() ?? [];

    public static string KeyInfo(X509Certificate2 c)
    {
        using var rsa = c.GetRSAPublicKey();
        if (rsa is not null) return $"RSA {rsa.KeySize}";
        using var ec = c.GetECDsaPublicKey();
        if (ec is not null) return $"ECDSA P-{ec.KeySize}";
        return c.PublicKey.Oid.FriendlyName ?? "Unknown";
    }

    public static X509Certificate2? FindInMachineStore(string thumbprint)
    {
        using var my = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        my.Open(OpenFlags.ReadOnly);
        var found = my.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, false);
        return found.Count > 0 ? found[0] : null;
    }
}

/// <summary>Submits a CSR to an internal Microsoft AD CS enterprise CA using certreq.exe (computer account context).</summary>
public sealed partial class AdcsClient(IOptions<AgentOptions> options, ILogger<AdcsClient> log)
{
    [GeneratedRegex(@"^[A-Za-z0-9 ._\-\\]{1,300}$")]
    private static partial Regex SafeRegex();

    public async Task<string> SubmitAsync(string csrPem, string config, string template, CancellationToken ct)
    {
        if (!SafeRegex().IsMatch(config) || !SafeRegex().IsMatch(template)) throw new InvalidOperationException("Invalid AD CS configuration.");
        var work = Path.Combine(options.Value.DataDirectory, "work", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var reqFile = Path.Combine(work, "request.req");
        var cerFile = Path.Combine(work, "issued.cer");
        try
        {
            await File.WriteAllTextAsync(reqFile, csrPem, ct);
            var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "certreq.exe")) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in new[] { "-submit", "-q", "-config", config, "-attrib", "CertificateTemplate:" + template, reqFile, cerFile }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            var stdout = await p.StandardOutput.ReadToEndAsync(ct);
            var stderr = await p.StandardError.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            log.LogInformation("certreq exit {Code}: {Out}", p.ExitCode, stdout.Trim());
            if (p.ExitCode != 0 || !File.Exists(cerFile))
                throw new InvalidOperationException($"AD CS (certreq) did not issue the certificate (it may be pending CA manager approval). {stdout} {stderr}".Trim());
            var raw = await File.ReadAllBytesAsync(cerFile, ct);
            var text = System.Text.Encoding.ASCII.GetString(raw);
            using var cert = text.Contains("-----BEGIN CERTIFICATE-----", StringComparison.Ordinal) ? X509Certificate2.CreateFromPem(text) : X509CertificateLoader.LoadCertificate(raw);
            return cert.ExportCertificatePem();
        }
        finally { try { Directory.Delete(work, true); } catch { /* best effort */ } }
    }
}
