using System.Text.RegularExpressions;

namespace AutomateCert.Shared;

public static class AgentProtocol
{
    public const string AuthScheme = "AutomateCertAgent";
    public const string Version = "1.5.0";
}

public static class JobTypes
{
    public const string Discover = "Discover";
    public const string Renew = "Renew";
    public const string Deploy = "Deploy";
    /// <summary>Runs deployment steps on another server after a renewal (e.g. refresh AD FS relying parties, reset CRM).</summary>
    public const string Task = "Task";
}

public static class JobStatuses
{
    public const string AwaitingApproval = "AwaitingApproval";
    public const string Scheduled = "Scheduled";
    public const string Pending = "Pending";
    public const string CsrRequested = "CsrRequested";
    public const string CsrSubmitted = "CsrSubmitted";
    public const string Issuing = "Issuing";
    public const string WaitingForManualDns = "WaitingForManualDns";
    public const string DnsReady = "DnsReady";
    public const string CertificateReady = "CertificateReady";
    public const string Deploying = "Deploying";
    public const string Succeeded = "Succeeded";
    public const string Failed = "Failed";
    public const string Cancelled = "Cancelled";

    public static readonly string[] Terminal = [Succeeded, Failed, Cancelled];
}

public static class JobPhases
{
    /// <summary>Second stage of a staged AD FS token certificate rotation: make the new certificate primary.</summary>
    public const string Promote = "promote";
}

public static class IssuerTypes
{
    public const string Acme = "Acme";
    public const string AdcsInternal = "AdcsInternal";
}

public static class KeyAlgorithms
{
    public const string Rsa2048 = "RSA2048";
    public const string Rsa3072 = "RSA3072";
    public const string Rsa4096 = "RSA4096";
    public const string EcdsaP256 = "ECDSAP256";
    public const string EcdsaP384 = "ECDSAP384";
    public static readonly string[] All = [Rsa2048, Rsa3072, Rsa4096, EcdsaP256, EcdsaP384];
    public static bool IsEcdsa(string a) => a.StartsWith("ECDSA", StringComparison.Ordinal);
}

public static class KeyStorages
{
    public const string Cng = "CNG";
    /// <summary>Legacy CryptoAPI CSP, for older applications (some Exchange / CRM builds) that cannot use CNG keys.</summary>
    public const string Capi = "CAPI";
}

public sealed record TemplateParameter(string Name, string Label, bool Required, string? Default, string? Help, string[]? Options = null);

public sealed record TemplateDefinition(
    string Key,
    string DisplayName,
    string Category,
    string Description,
    bool Disruptive,
    bool RequiresLocalCertificate,
    bool SupportsTlsVerification,
    TemplateParameter[] Parameters)
{
    public string[] RequiredParameters => Parameters.Where(p => p.Required).Select(p => p.Name).ToArray();
}

/// <summary>
/// The fixed allow-list of deployment templates. The agent only runs scripts embedded in its own binary;
/// the server chooses a template and validated parameters, never script content.
/// </summary>
public static partial class DeploymentTemplates
{
    public const string Rebind = "rebind";
    public const string Iis = "iis";
    public const string Adfs = "adfs";
    public const string AdfsToken = "adfstoken";
    public const string AdfsRpRefresh = "adfsrprefresh";
    public const string Wap = "wap";
    public const string CrmClaims = "crmclaims";
    public const string CrmRefresh = "crmrefresh";
    public const string Exchange = "exchange";
    public const string RdGateway = "rdgateway";
    public const string RdpListener = "rdplistener";
    public const string SqlServer = "sqlserver";
    public const string ServiceRestart = "servicerestart";
    public const string AutomateCertConsole = "automatecert";

    /// <summary>Parameters filled by the server at dispatch time; never accepted from users.</summary>
    public const string OldThumbprintParameter = "oldThumbprint";
    public const string PhaseParameter = "phase";
    public static bool IsHiddenParameter(string name) =>
        name.Equals(OldThumbprintParameter, StringComparison.OrdinalIgnoreCase) || name.Equals(PhaseParameter, StringComparison.OrdinalIgnoreCase);

    private static TemplateParameter P(string n, string l, bool r = false, string? d = null, string? h = null, string[]? o = null) => new(n, l, r, d, h, o);
    private static readonly TemplateParameter OldTp = P(OldThumbprintParameter, "", false);
    private static readonly TemplateParameter PhaseP = P(PhaseParameter, "", false);

    public static readonly IReadOnlyDictionary<string, TemplateDefinition> All =
        new Dictionary<string, TemplateDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            [Rebind] = new(Rebind, "Replace everywhere (zero-touch)", "General",
                "Finds every place the current certificate is bound on this server (IIS, AD FS SSL, WAP, Exchange, RD Gateway, RDP, SQL Server, AutomateCert) and switches it to the new one. Nothing else changes.",
                true, true, false, [OldTp]),
            [Iis] = new(Iis, "IIS HTTPS binding", "Web",
                "Creates or updates an HTTPS binding on an IIS site (SNI when a host name is given).",
                false, true, true, [P("siteName", "IIS site name", true, "Default Web Site"), P("hostName", "Host name (SNI)"), P("port", "Port", false, "443"), P("ipAddress", "IP address", false, "*")]),
            [Adfs] = new(Adfs, "AD FS SSL + service communications", "AD FS",
                "Sets the AD FS SSL and service-communications certificates on this node, grants the AD FS service account key access and restarts AD FS. Use one profile per farm node.",
                true, true, true, [OldTp]),
            [AdfsToken] = new(AdfsToken, "AD FS token-signing / decrypting (staged)", "AD FS",
                "Staged rotation: adds the new certificate as SECONDARY, then promotes it to PRIMARY after the configured delay so relying parties (CRM, Microsoft 365, SaaS) can pick it up from federation metadata. Requires AD FS AutoCertificateRollover to be off.",
                true, true, false,
                [P("certificateType", "Certificate type", true, "Token-Signing", null, ["Token-Signing", "Token-Decrypting"]),
                 P("promoteAfterHours", "Promote to primary after (hours)", false, "48", "0 = promote immediately"),
                 P("allowDisableAutoRollover", "Turn off AutoCertificateRollover if on", false, "false", null, ["false", "true"]), PhaseP]),
            [AdfsRpRefresh] = new(AdfsRpRefresh, "AD FS: refresh relying parties", "AD FS",
                "Runs Update-AdfsRelyingPartyTrust from federation metadata for the listed relying parties (e.g. CRM internal + IFD) so AD FS uses CRM's new encryption certificate. Run on the primary AD FS server.",
                false, false, false, [P("relyingParties", "Relying party names (comma-separated)", true, null, "Exactly as shown in AD FS Management, e.g. CRM Claims, CRM IFD")]),
            [Wap] = new(Wap, "Web Application Proxy", "AD FS",
                "Sets the WAP SSL certificate and moves published applications from the previous certificate.",
                true, true, true, []),
            [CrmClaims] = new(CrmClaims, "Dynamics CRM claims / IFD encryption", "Dynamics CRM",
                "On the CRM server: grants the CRM application pool identity read access to the new key, sets it as the claims-based authentication encryption certificate (Set-CrmSetting ClaimsSettings) and resets IIS. Pair with 'AD FS: refresh relying parties'.",
                true, true, false, [P("appPool", "CRM application pool", false, "CRMAppPool"), P("updateClaimsSetting", "Update claims encryption setting", false, "true", null, ["true", "false"]), P("iisReset", "Run iisreset", false, "true", null, ["true", "false"])]),
            [CrmRefresh] = new(CrmRefresh, "Dynamics CRM: reload federation metadata", "Dynamics CRM",
                "Resets IIS (and optionally the CRM asynchronous service) so CRM reloads AD FS federation metadata after a token-signing change. Run on each CRM front-end server.",
                true, false, false, [P("restartAsync", "Also restart CRM async services", false, "false", null, ["false", "true"])]),
            [Exchange] = new(Exchange, "Exchange Server", "Messaging",
                "Enables the certificate for Exchange services (default IIS,SMTP).",
                true, true, true, [P("services", "Services", false, "IIS,SMTP")]),
            [RdGateway] = new(RdGateway, "RD Gateway", "Remote access", "Sets the RD Gateway certificate and restarts the gateway service.", true, true, true, []),
            [RdpListener] = new(RdpListener, "RDP listener", "Remote access", "Sets the RDP-Tcp listener certificate and grants NETWORK SERVICE key access.", false, true, true, []),
            [SqlServer] = new(SqlServer, "SQL Server TLS", "Database",
                "Grants the SQL service account key access, sets the instance certificate and restarts SQL Server.",
                true, true, false, [P("instanceName", "Instance", false, "MSSQLSERVER"), P("forceEncryption", "Force encryption", false, "false", null, ["false", "true"])]),
            [ServiceRestart] = new(ServiceRestart, "Restart a Windows service", "General",
                "Restarts a Windows service after deployment (for applications that load the certificate at start-up).",
                true, false, false, [P("serviceName", "Service name", true)]),
            [AutomateCertConsole] = new(AutomateCertConsole, "AutomateCert console itself", "General",
                "Grants the AutomateCert server service key access. The console switches to the renewed certificate automatically within 10 minutes.",
                false, true, true, []),
        };

    [GeneratedRegex(@"^[A-Za-z0-9 ._,:\-\*\$\(\)&']{0,200}$")]
    private static partial Regex SafeValueRegex();

    [GeneratedRegex(@"^[a-zA-Z][a-zA-Z0-9]{0,40}$")]
    private static partial Regex SafeKeyRegex();

    /// <summary>Returns null when valid, otherwise an error message. Used by both server and agent.</summary>
    public static string? Validate(string template, IReadOnlyDictionary<string, string>? parameters)
    {
        if (!All.TryGetValue(template, out var def)) return $"Unknown deployment template '{template}'.";
        parameters ??= new Dictionary<string, string>();
        foreach (var req in def.RequiredParameters)
            if (!parameters.TryGetValue(req, out var v) || string.IsNullOrWhiteSpace(v))
                return $"'{def.DisplayName}' requires '{def.Parameters.First(p => p.Name == req).Label}'.";
        foreach (var (k, v) in parameters)
        {
            if (!SafeKeyRegex().IsMatch(k)) return $"Invalid parameter name '{k}'.";
            var p = def.Parameters.FirstOrDefault(x => x.Name.Equals(k, StringComparison.OrdinalIgnoreCase));
            if (p is null) return $"Parameter '{k}' is not supported by '{def.DisplayName}'.";
            if (!SafeValueRegex().IsMatch(v ?? "")) return $"'{p.Label}' contains unsupported characters.";
            if (p.Options is { Length: > 0 } && !string.IsNullOrEmpty(v) && !p.Options.Contains(v, StringComparer.OrdinalIgnoreCase))
                return $"'{p.Label}' must be one of: {string.Join(", ", p.Options)}.";
        }
        return null;
    }
}

// ---------- Agent <-> Server wire contracts ----------

public sealed record EnrollRequest(string Token, string MachineName, string OsVersion, string AgentVersion);
public sealed record EnrollResponse(Guid AgentId, string Secret);
public sealed record HeartbeatRequest(string AgentVersion, string OsVersion);

public sealed record AgentTarget(
    Guid Id,
    string Template,
    Dictionary<string, string> Parameters,
    string? VerifyHost,
    int? VerifyPort);

public sealed record AgentJob(
    Guid Id,
    string Type,
    string Status,
    string? ProfileName,
    string[] Domains,
    string KeyAlgorithm,
    string KeyStorage,
    string IssuerType,
    string? AdcsConfig,
    string? AdcsTemplate,
    bool Exportable,
    string? ThumbprintToDeploy,
    AgentTarget[] Targets,
    string? Phase = null);

public sealed record CsrSubmission(string CsrPem);
public sealed record CertificateDelivery(string ChainPem);

public sealed record JobResult(
    bool Success,
    string? Thumbprint,
    DateTimeOffset? NotBefore,
    DateTimeOffset? NotAfter,
    string Log);

public sealed record InventoryCertificate(
    string Thumbprint,
    string Subject,
    string[] DnsNames,
    string Issuer,
    DateTimeOffset NotBefore,
    DateTimeOffset NotAfter,
    bool HasPrivateKey,
    string Store,
    string[] Bindings,
    string? KeyInfo = null,
    string? SignatureAlgorithm = null,
    string? Serial = null);

/// <summary>Roles: IIS, ADFS, ADFS-AutoRollover, WAP, CRM, Exchange, SQL, RDGateway, DNS, AutomateCert.</summary>
public sealed record InventoryReport(InventoryCertificate[] Certificates, string[]? Roles = null);

/// <summary>DNS TXT change executed by an agent on a Windows DNS server (own name servers).</summary>
public sealed record AgentDnsTask(Guid Id, string Action, string Name, string Value, string? DnsServer, string? Zone);
public sealed record AgentDnsTaskResult(bool Success, string Message);
