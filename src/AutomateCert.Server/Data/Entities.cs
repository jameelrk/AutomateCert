using System.ComponentModel.DataAnnotations;

namespace AutomateCert.Server.Data;

public interface ITenantOwned
{
    Guid TenantId { get; set; }
}

public static class Roles
{
    public const string Admin = "Admin";
    public const string Operator = "Operator";
    public const string Approver = "Approver";
    public const string Viewer = "Viewer";
    public static readonly string[] All = [Admin, Operator, Approver, Viewer];

    public static readonly IReadOnlyDictionary<string, string> Descriptions = new Dictionary<string, string>
    {
        [Admin] = "Everything: users, CAs, DNS, servers, security and AI settings.",
        [Operator] = "Day-to-day PKI work: create/adopt automations, renew, deploy, approve, endpoints.",
        [Approver] = "Read everything and approve or reject changes (change-advisory board, security team).",
        [Viewer] = "Read-only dashboards, inventory and reports (auditors, management)."
    };
}

public static class MfaMethods
{
    public const string None = "None";
    public const string Totp = "Totp";
    public const string Email = "Email";
}

public class Tenant
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(200)] public string Name { get; set; } = "";
    [MaxLength(1000)] public string? WebhookUrl { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [MaxLength(2000)] public string? AlertEmails { get; set; }
    [MaxLength(100)] public string AlertDays { get; set; } = "30,14,7,3,1";
    public bool AlertUnmanaged { get; set; } = true;
    public bool NotifyOnSuccess { get; set; } = true;
    /// <summary>Weekly executive summary email (Mondays).</summary>
    public bool WeeklySummary { get; set; } = true;

    public bool RequireMfa { get; set; }
    public int SessionHours { get; set; } = 8;

    /// <summary>Comma-separated domains watched in Certificate Transparency logs.</summary>
    [MaxLength(2000)] public string? CtDomains { get; set; }

    public bool AiEnabled { get; set; }
    [MaxLength(30)] public string? AiProvider { get; set; }
    [MaxLength(500)] public string? AiEndpoint { get; set; }
    [MaxLength(200)] public string? AiModel { get; set; }
    public string? AiApiKeyProtected { get; set; }
}

public class User : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    [MaxLength(256)] public string Email { get; set; } = "";
    [MaxLength(200)] public string DisplayName { get; set; } = "";
    [MaxLength(300)] public string PasswordHash { get; set; } = "";
    [MaxLength(20)] public string Role { get; set; } = Roles.Viewer;
    public int FailedLogins { get; set; }
    public DateTimeOffset? LockoutUntil { get; set; }
    public bool Disabled { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastLoginAt { get; set; }
    [MaxLength(64)] public string? LastLoginIp { get; set; }
    public bool NotifyByEmail { get; set; } = true;
    public bool MustChangePassword { get; set; }

    public bool MfaEnabled { get; set; }
    [MaxLength(10)] public string MfaMethod { get; set; } = MfaMethods.None;
    public string? TotpSecretProtected { get; set; }
    public string? PendingTotpSecretProtected { get; set; }
    public long LastTotpStep { get; set; }
    [MaxLength(2000)] public string RecoveryCodes { get; set; } = "[]";
    public byte[]? EmailOtpHash { get; set; }
    public DateTimeOffset? EmailOtpExpires { get; set; }
    public int EmailOtpAttempts { get; set; }
    public DateTimeOffset? EmailOtpSentAt { get; set; }
}

public class Agent : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    [MaxLength(200)] public string Name { get; set; } = "";
    public byte[] SecretHash { get; set; } = [];
    [MaxLength(40)] public string? Version { get; set; }
    [MaxLength(200)] public string? OsVersion { get; set; }
    [MaxLength(64)] public string? LastIp { get; set; }
    /// <summary>Comma-separated detected roles (IIS, ADFS, WAP, CRM, Exchange, SQL, DNS…).</summary>
    [MaxLength(500)] public string? Roles { get; set; }
    public DateTimeOffset? LastSeenAt { get; set; }
    public bool Revoked { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class EnrollmentToken : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public byte[] TokenHash { get; set; } = [];
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
    [MaxLength(256)] public string CreatedBy { get; set; } = "";
    public int MaxUses { get; set; } = 1;
    public int UseCount { get; set; }
}

public class Issuer : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    [MaxLength(200)] public string Name { get; set; } = "";
    [MaxLength(20)] public string Type { get; set; } = "Acme";
    [MaxLength(500)] public string? DirectoryUrl { get; set; }
    [MaxLength(256)] public string? ContactEmail { get; set; }
    [MaxLength(200)] public string? EabKeyId { get; set; }
    public string? EabHmacKeyProtected { get; set; }
    public string? AccountKeyProtected { get; set; }
    [MaxLength(300)] public string? AdcsConfig { get; set; }
    [MaxLength(200)] public string? AdcsTemplate { get; set; }
    /// <summary>End of the paid subscription / ACME credential coverage (alerts 60/30/7 days before).</summary>
    public DateTimeOffset? CoverageEndsAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class DnsProvider : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    [MaxLength(200)] public string Name { get; set; } = "";
    [MaxLength(30)] public string Type { get; set; } = "Manual";
    /// <summary>Non-secret settings (JSON object).</summary>
    [MaxLength(4000)] public string ConfigJson { get; set; } = "{}";
    /// <summary>Secret settings (JSON object) encrypted with Data Protection.</summary>
    public string? CredentialProtected { get; set; }
    /// <summary>CNAME delegation: when set, TXT records are written below this zone instead of the domain's own zone.</summary>
    [MaxLength(255)] public string? AliasZone { get; set; }
    public DateTimeOffset? LastTestAt { get; set; }
    public bool? LastTestOk { get; set; }
    [MaxLength(1000)] public string? LastTestMessage { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Commercial CA account integration (balances, orders) — DigiCert CertCentral, Sectigo SCM.</summary>
public class CaAccount : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    [MaxLength(200)] public string Name { get; set; } = "";
    [MaxLength(30)] public string Type { get; set; } = "";
    [MaxLength(4000)] public string ConfigJson { get; set; } = "{}";
    public string? CredentialProtected { get; set; }
    public string? SnapshotJson { get; set; }
    public DateTimeOffset? LastSyncAt { get; set; }
    [MaxLength(1000)] public string? LastError { get; set; }
    public decimal? LowBalanceThreshold { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public class CertificateProfile : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    [MaxLength(200)] public string Name { get; set; } = "";
    [MaxLength(4000)] public string Domains { get; set; } = "";
    [MaxLength(20)] public string KeyAlgorithm { get; set; } = "RSA2048";
    [MaxLength(10)] public string KeyStorage { get; set; } = "CNG";
    public Guid IssuerId { get; set; }
    public Issuer? Issuer { get; set; }
    public Guid? DnsProviderId { get; set; }
    public DnsProvider? DnsProvider { get; set; }
    public Guid AgentId { get; set; }
    public Agent? Agent { get; set; }
    public int RenewAtRemainingPercent { get; set; } = 33;
    public bool RequireApproval { get; set; }
    public bool Exportable { get; set; }
    public bool Enabled { get; set; } = true;
    /// <summary>Optional maintenance window (UTC hours, e.g. 22-4). Disruptive renewals start only inside it.</summary>
    public int? WindowStartHour { get; set; }
    public int? WindowEndHour { get; set; }
    [MaxLength(30)] public string? Scenario { get; set; }
    [MaxLength(64)] public string? CurrentThumbprint { get; set; }
    public DateTimeOffset? CurrentNotBefore { get; set; }
    public DateTimeOffset? CurrentNotAfter { get; set; }
    public DateTimeOffset? LastRenewedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<DeploymentTarget> Targets { get; set; } = [];

    public string[] DomainList() =>
        Domains.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

public class DeploymentTarget : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid ProfileId { get; set; }
    public int Order { get; set; }
    [MaxLength(30)] public string Template { get; set; } = "";
    [MaxLength(4000)] public string ParametersJson { get; set; } = "{}";
    [MaxLength(255)] public string? VerifyHost { get; set; }
    public int? VerifyPort { get; set; }
    /// <summary>When set (and different from the profile's server), the step runs on that server after the renewal.</summary>
    public Guid? AgentId { get; set; }
}

public class Job : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid AgentId { get; set; }
    public Guid? ProfileId { get; set; }
    public Guid? ParentJobId { get; set; }
    [MaxLength(20)] public string Type { get; set; } = "";
    [MaxLength(30)] public string Status { get; set; } = "";
    [MaxLength(20)] public string? Phase { get; set; }
    public DateTimeOffset? NotBefore { get; set; }
    [MaxLength(2000)] public string? TargetIds { get; set; }
    [MaxLength(64)] public string? ThumbprintToDeploy { get; set; }
    public string? CsrPem { get; set; }
    public string? CertificatePem { get; set; }
    [MaxLength(500)] public string? OrderUrl { get; set; }
    public string? ChallengeJson { get; set; }
    [MaxLength(2000)] public string? Message { get; set; }
    public string Log { get; set; } = "";
    [MaxLength(256)] public string RequestedBy { get; set; } = "system";
    [MaxLength(256)] public string? ApprovedBy { get; set; }
    [MaxLength(64)] public string? ResultThumbprint { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public void Append(string line)
    {
        Log += $"[{DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm:ss}Z] {line}\n";
        if (Log.Length > 32_000) Log = Log[^32_000..];
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    public void SetStatus(string status, string? message = null)
    {
        Status = status;
        if (message is not null) Message = message.Length > 2000 ? message[..2000] : message;
        Append($"Status -> {status}{(message is null ? "" : ": " + message)}");
    }
}

public class DnsTask : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid AgentId { get; set; }
    [MaxLength(10)] public string Action { get; set; } = "add";
    [MaxLength(300)] public string Name { get; set; } = "";
    [MaxLength(300)] public string Value { get; set; } = "";
    [MaxLength(255)] public string? DnsServer { get; set; }
    [MaxLength(255)] public string? Zone { get; set; }
    [MaxLength(20)] public string Status { get; set; } = "Pending";
    [MaxLength(1000)] public string? Message { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
}

public class DiscoveredCertificate : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid AgentId { get; set; }
    [MaxLength(64)] public string Thumbprint { get; set; } = "";
    [MaxLength(1000)] public string Subject { get; set; } = "";
    [MaxLength(4000)] public string DnsNames { get; set; } = "";
    [MaxLength(1000)] public string Issuer { get; set; } = "";
    public DateTimeOffset NotBefore { get; set; }
    public DateTimeOffset NotAfter { get; set; }
    public bool HasPrivateKey { get; set; }
    [MaxLength(100)] public string Store { get; set; } = "";
    [MaxLength(4000)] public string Bindings { get; set; } = "";
    [MaxLength(40)] public string? KeyInfo { get; set; }
    [MaxLength(60)] public string? SignatureAlgorithm { get; set; }
    [MaxLength(80)] public string? Serial { get; set; }
    public DateTimeOffset LastSeenAt { get; set; } = DateTimeOffset.UtcNow;
}

public class MonitoredEndpoint : ITenantOwned
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    [MaxLength(255)] public string Host { get; set; } = "";
    public int Port { get; set; } = 443;
    [MaxLength(200)] public string? Label { get; set; }
    [MaxLength(64)] public string? Thumbprint { get; set; }
    [MaxLength(1000)] public string? Subject { get; set; }
    [MaxLength(1000)] public string? Issuer { get; set; }
    [MaxLength(4000)] public string? DnsNames { get; set; }
    public DateTimeOffset? NotBefore { get; set; }
    public DateTimeOffset? NotAfter { get; set; }
    public bool? ChainValid { get; set; }
    public bool? NameMatches { get; set; }
    [MaxLength(1000)] public string? ChainStatus { get; set; }
    [MaxLength(40)] public string? KeyInfo { get; set; }
    [MaxLength(60)] public string? SignatureAlgorithm { get; set; }
    [MaxLength(20)] public string? TlsProtocol { get; set; }
    [MaxLength(1000)] public string? LastError { get; set; }
    public DateTimeOffset? LastCheckedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Certificate seen in public Certificate Transparency logs for a watched domain.</summary>
public class CtCertificate : ITenantOwned
{
    public long Id { get; set; }
    public Guid TenantId { get; set; }
    public long CrtShId { get; set; }
    [MaxLength(255)] public string Domain { get; set; } = "";
    [MaxLength(1000)] public string Issuer { get; set; } = "";
    [MaxLength(4000)] public string Names { get; set; } = "";
    [MaxLength(80)] public string? Serial { get; set; }
    public DateTimeOffset NotBefore { get; set; }
    public DateTimeOffset NotAfter { get; set; }
    public bool Known { get; set; }
    public bool Acknowledged { get; set; }
    public DateTimeOffset FirstSeenAt { get; set; } = DateTimeOffset.UtcNow;
}

public class AuditEntry : ITenantOwned
{
    public long Id { get; set; }
    public Guid TenantId { get; set; }
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
    [MaxLength(256)] public string Actor { get; set; } = "";
    [MaxLength(100)] public string Action { get; set; } = "";
    [MaxLength(300)] public string Target { get; set; } = "";
    [MaxLength(2000)] public string? Details { get; set; }
}

public class AlertState : ITenantOwned
{
    public long Id { get; set; }
    public Guid TenantId { get; set; }
    [MaxLength(300)] public string Key { get; set; } = "";
    public DateTimeOffset SentAt { get; set; } = DateTimeOffset.UtcNow;
}
