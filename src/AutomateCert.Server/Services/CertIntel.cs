using System.Net;

namespace AutomateCert.Server.Services;

/// <summary>
/// Certificate intelligence shared by inventory, analytics, compliance, alerts and AI:
/// issuing vendor, public/internal names, public ACME CAs and compliance findings.
/// </summary>
public static class CertIntel
{
    private static readonly (string Vendor, string[] Markers)[] VendorMarkers =
    [
        ("Let's Encrypt", ["Let's Encrypt", "(STAGING)", "CN=R3,", "CN=R10", "CN=R11", "CN=R12", "CN=R13", "CN=E5", "CN=E6", "CN=E7", "CN=E8", "ISRG"]),
        ("DigiCert", ["DigiCert", "Thawte", "GeoTrust", "RapidSSL", "Symantec", "VeriSign", "Encryption Everywhere"]),
        ("Sectigo", ["Sectigo", "COMODO", "USERTrust", "InCommon"]),
        ("GlobalSign", ["GlobalSign", "AlphaSSL"]),
        ("Entrust", ["Entrust"]),
        ("GoDaddy", ["GoDaddy", "Go Daddy", "Starfield"]),
        ("ZeroSSL", ["ZeroSSL"]),
        ("Google Trust Services", ["Google Trust Services", "CN=WR1", "CN=WR2", "CN=WE1", "GTS CA"]),
        ("Amazon", ["Amazon"]),
        ("Microsoft (Azure)", ["Microsoft Azure", "Microsoft RSA TLS", "Microsoft ECC TLS"]),
        ("Buypass", ["Buypass"]),
        ("SSL.com", ["SSL.com"]),
        ("Certum", ["Certum", "Asseco"]),
        ("IdenTrust", ["IdenTrust"]),
        ("HARICA", ["HARICA", "Hellenic Academic"]),
        ("Actalis", ["Actalis"]),
    ];

    public static readonly string[] FreeVendors = ["Let's Encrypt", "ZeroSSL", "Google Trust Services", "Buypass"];

    public static string Vendor(string? issuer, string? subject)
    {
        var i = issuer ?? "";
        foreach (var (vendor, markers) in VendorMarkers)
            if (markers.Any(m => i.Contains(m, StringComparison.OrdinalIgnoreCase))) return vendor;
        if (!string.IsNullOrEmpty(i) && string.Equals(i, subject, StringComparison.OrdinalIgnoreCase)) return "Self-signed";
        return string.IsNullOrEmpty(i) ? "Unknown" : "Internal / private CA";
    }

    public static bool IsPublicVendor(string vendor) => vendor is not ("Self-signed" or "Internal / private CA" or "Unknown");

    private static readonly string[] PublicAcmeHosts =
    [
        "letsencrypt.org", "digicert.com", "sectigo.com", "globalsign.com", "zerossl.com", "pki.goog",
        "buypass.com", "ssl.com", "entrust.net", "actalis.it", "harica.gr"
    ];

    public static bool IsPublicAcme(string? directoryUrl) =>
        Uri.TryCreate(directoryUrl, UriKind.Absolute, out var u) &&
        PublicAcmeHosts.Any(h => u.Host.Equals(h, StringComparison.OrdinalIgnoreCase) || u.Host.EndsWith("." + h, StringComparison.OrdinalIgnoreCase));

    public static bool IsLetsEncrypt(string? directoryUrl) =>
        directoryUrl?.Contains("letsencrypt.org", StringComparison.OrdinalIgnoreCase) == true;

    private static readonly HashSet<string> InternalTlds = new(StringComparer.OrdinalIgnoreCase)
    {
        "local", "localdomain", "lan", "corp", "internal", "intranet", "home", "private", "domain", "ad",
        "test", "example", "invalid", "localhost", "home.arpa", "priv", "int", "loc", "office", "grp"
    };

    /// <summary>Reason when a name can never be issued by a public CA; otherwise null.</summary>
    public static string? InternalNameReason(string name)
    {
        var n = name.TrimStart('*', '.').TrimEnd('.');
        if (IPAddress.TryParse(n, out _)) return "is an IP address";
        if (!n.Contains('.')) return "is a single-label (internal) name";
        if (n.EndsWith(".home.arpa", StringComparison.OrdinalIgnoreCase)) return "uses the private .home.arpa suffix";
        var tld = n[(n.LastIndexOf('.') + 1)..];
        return InternalTlds.Contains(tld) ? $"ends with the internal suffix .{tld}" : null;
    }

    public sealed record ComplianceIssue(string Rule, string Severity, string Detail);

    /// <summary>Policy checks aligned with CA/Browser Forum baseline requirements and common enterprise PKI policy.</summary>
    public static List<ComplianceIssue> Check(string? keyInfo, string? signature, DateTimeOffset notBefore, DateTimeOffset notAfter,
        string issuer, string subject, string dnsNames, bool bound, DateTimeOffset now)
    {
        var list = new List<ComplianceIssue>();
        var vendor = Vendor(issuer, subject);
        if (notAfter <= now) list.Add(new("Expired certificate", bound ? "critical" : "warning", bound ? "Expired but still bound to a service." : "Expired; remove it from the store."));
        if (keyInfo is { } k)
        {
            if (k.StartsWith("RSA", StringComparison.Ordinal) && int.TryParse(k[3..].Trim(), out var bits) && bits < 2048)
                list.Add(new("Weak RSA key", "critical", $"{k}: below the 2048-bit minimum."));
        }
        if (signature is { } s && (s.Contains("sha1", StringComparison.OrdinalIgnoreCase) || s.Contains("md5", StringComparison.OrdinalIgnoreCase)))
            list.Add(new("Weak signature", "critical", $"{s} is deprecated; browsers reject SHA-1 server certificates."));
        if (IsPublicVendor(vendor) && (notAfter - notBefore).TotalDays > 398)
            list.Add(new("Lifetime above public limit", "info", $"{(int)(notAfter - notBefore).TotalDays}-day validity; new public certificates are limited to 200 days (100 from Mar 2027)."));
        if (vendor == "Self-signed" && bound)
            list.Add(new("Self-signed certificate in use", "warning", "Clients cannot validate it; replace with a CA-issued certificate."));
        if (dnsNames.Contains('*') && bound)
            list.Add(new("Wildcard in use", "info", "Wildcards widen the blast radius of a key compromise; prefer specific names where practical."));
        if (string.IsNullOrWhiteSpace(dnsNames) && bound && vendor != "Self-signed")
            list.Add(new("No SAN", "warning", "Modern clients ignore the CN; the certificate needs Subject Alternative Names."));
        return list;
    }
}
