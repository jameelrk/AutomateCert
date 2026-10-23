using Microsoft.EntityFrameworkCore;

namespace AutomateCert.Server.Data;

/// <summary>
/// Idempotent in-place upgrade of databases created by earlier versions with EnsureCreated
/// (EnsureCreated never alters an existing schema). Every statement checks before it changes anything,
/// so it is safe to run on every start and every installer run. Same SQL ships as deploy/server/upgrade-schema.sql.
/// </summary>
public static class SchemaUpgrader
{
    private static readonly (string Table, string Column, string Definition)[] Columns =
    [
        // v1.1
        ("Tenants", "AlertEmails", "nvarchar(2000) NULL"),
        ("Tenants", "AlertDays", "nvarchar(100) NOT NULL CONSTRAINT DF_Tenants_AlertDays DEFAULT N'30,14,7,3,1'"),
        ("Tenants", "AlertUnmanaged", "bit NOT NULL CONSTRAINT DF_Tenants_AlertUnmanaged DEFAULT 1"),
        ("Tenants", "NotifyOnSuccess", "bit NOT NULL CONSTRAINT DF_Tenants_NotifyOnSuccess DEFAULT 1"),
        ("Tenants", "RequireMfa", "bit NOT NULL CONSTRAINT DF_Tenants_RequireMfa DEFAULT 0"),
        ("Tenants", "AiEnabled", "bit NOT NULL CONSTRAINT DF_Tenants_AiEnabled DEFAULT 0"),
        ("Tenants", "AiProvider", "nvarchar(30) NULL"),
        ("Tenants", "AiEndpoint", "nvarchar(500) NULL"),
        ("Tenants", "AiModel", "nvarchar(200) NULL"),
        ("Tenants", "AiApiKeyProtected", "nvarchar(max) NULL"),
        ("Users", "NotifyByEmail", "bit NOT NULL CONSTRAINT DF_Users_NotifyByEmail DEFAULT 1"),
        ("Users", "MfaEnabled", "bit NOT NULL CONSTRAINT DF_Users_MfaEnabled DEFAULT 0"),
        ("Users", "MfaMethod", "nvarchar(10) NOT NULL CONSTRAINT DF_Users_MfaMethod DEFAULT N'None'"),
        ("Users", "TotpSecretProtected", "nvarchar(max) NULL"),
        ("Users", "PendingTotpSecretProtected", "nvarchar(max) NULL"),
        ("Users", "LastTotpStep", "bigint NOT NULL CONSTRAINT DF_Users_LastTotpStep DEFAULT 0"),
        ("Users", "RecoveryCodes", "nvarchar(2000) NOT NULL CONSTRAINT DF_Users_RecoveryCodes DEFAULT N'[]'"),
        ("Users", "EmailOtpHash", "varbinary(max) NULL"),
        ("Users", "EmailOtpExpires", "datetimeoffset NULL"),
        ("Users", "EmailOtpAttempts", "int NOT NULL CONSTRAINT DF_Users_EmailOtpAttempts DEFAULT 0"),
        ("Users", "EmailOtpSentAt", "datetimeoffset NULL"),
        // v1.2
        ("EnrollmentTokens", "MaxUses", "int NOT NULL CONSTRAINT DF_EnrollmentTokens_MaxUses DEFAULT 1"),
        ("EnrollmentTokens", "UseCount", "int NOT NULL CONSTRAINT DF_EnrollmentTokens_UseCount DEFAULT 0"),
        // v1.3
        ("Tenants", "SessionHours", "int NOT NULL CONSTRAINT DF_Tenants_SessionHours DEFAULT 8"),
        ("Tenants", "WeeklySummary", "bit NOT NULL CONSTRAINT DF_Tenants_WeeklySummary DEFAULT 1"),
        ("Tenants", "CtDomains", "nvarchar(2000) NULL"),
        ("Users", "LastLoginIp", "nvarchar(64) NULL"),
        ("Users", "MustChangePassword", "bit NOT NULL CONSTRAINT DF_Users_MustChangePassword DEFAULT 0"),
        ("Agents", "Roles", "nvarchar(500) NULL"),
        ("Issuers", "CoverageEndsAt", "datetimeoffset NULL"),
        ("DnsProviders", "ConfigJson", "nvarchar(4000) NOT NULL CONSTRAINT DF_DnsProviders_ConfigJson DEFAULT (NCHAR(123) + NCHAR(125))"),
        ("DnsProviders", "AliasZone", "nvarchar(255) NULL"),
        ("DnsProviders", "LastTestAt", "datetimeoffset NULL"),
        ("DnsProviders", "LastTestOk", "bit NULL"),
        ("DnsProviders", "LastTestMessage", "nvarchar(1000) NULL"),
        ("Profiles", "WindowStartHour", "int NULL"),
        ("Profiles", "WindowEndHour", "int NULL"),
        ("Profiles", "Scenario", "nvarchar(30) NULL"),
        ("Targets", "AgentId", "uniqueidentifier NULL"),
        ("Jobs", "ParentJobId", "uniqueidentifier NULL"),
        ("Jobs", "Phase", "nvarchar(20) NULL"),
        ("Jobs", "NotBefore", "datetimeoffset NULL"),
        ("Jobs", "TargetIds", "nvarchar(2000) NULL"),
        ("Discovered", "KeyInfo", "nvarchar(40) NULL"),
        ("Discovered", "SignatureAlgorithm", "nvarchar(60) NULL"),
        ("Discovered", "Serial", "nvarchar(80) NULL"),
    ];

    private static readonly string[] Tables =
    [
        """
        IF OBJECT_ID(N'dbo.AlertStates', N'U') IS NULL
        CREATE TABLE dbo.AlertStates (Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_AlertStates PRIMARY KEY,
            TenantId uniqueidentifier NOT NULL, [Key] nvarchar(300) NOT NULL, SentAt datetimeoffset NOT NULL)
        """,
        """
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_AlertStates_TenantId_Key')
        CREATE UNIQUE INDEX IX_AlertStates_TenantId_Key ON dbo.AlertStates (TenantId, [Key])
        """,
        """
        IF OBJECT_ID(N'dbo.Endpoints', N'U') IS NULL
        CREATE TABLE dbo.Endpoints (Id uniqueidentifier NOT NULL CONSTRAINT PK_Endpoints PRIMARY KEY,
            TenantId uniqueidentifier NOT NULL, Host nvarchar(255) NOT NULL, Port int NOT NULL, Label nvarchar(200) NULL,
            Thumbprint nvarchar(64) NULL, Subject nvarchar(1000) NULL, Issuer nvarchar(1000) NULL, DnsNames nvarchar(4000) NULL,
            NotBefore datetimeoffset NULL, NotAfter datetimeoffset NULL, ChainValid bit NULL, NameMatches bit NULL,
            ChainStatus nvarchar(1000) NULL, LastError nvarchar(1000) NULL, LastCheckedAt datetimeoffset NULL, CreatedAt datetimeoffset NOT NULL)
        """,
        """
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Endpoints_TenantId_Host_Port')
        CREATE UNIQUE INDEX IX_Endpoints_TenantId_Host_Port ON dbo.Endpoints (TenantId, Host, Port)
        """,
        """
        IF OBJECT_ID(N'dbo.CaAccounts', N'U') IS NULL
        CREATE TABLE dbo.CaAccounts (Id uniqueidentifier NOT NULL CONSTRAINT PK_CaAccounts PRIMARY KEY,
            TenantId uniqueidentifier NOT NULL, Name nvarchar(200) NOT NULL, Type nvarchar(30) NOT NULL,
            ConfigJson nvarchar(4000) NOT NULL, CredentialProtected nvarchar(max) NULL, SnapshotJson nvarchar(max) NULL,
            LastSyncAt datetimeoffset NULL, LastError nvarchar(1000) NULL, LowBalanceThreshold decimal(18,2) NULL, CreatedAt datetimeoffset NOT NULL)
        """,
        """
        IF OBJECT_ID(N'dbo.DnsTasks', N'U') IS NULL
        CREATE TABLE dbo.DnsTasks (Id uniqueidentifier NOT NULL CONSTRAINT PK_DnsTasks PRIMARY KEY,
            TenantId uniqueidentifier NOT NULL, AgentId uniqueidentifier NOT NULL, Action nvarchar(10) NOT NULL,
            Name nvarchar(300) NOT NULL, Value nvarchar(300) NOT NULL, DnsServer nvarchar(255) NULL, Zone nvarchar(255) NULL,
            Status nvarchar(20) NOT NULL, Message nvarchar(1000) NULL, CreatedAt datetimeoffset NOT NULL, CompletedAt datetimeoffset NULL)
        """,
        """
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_DnsTasks_AgentId_Status')
        CREATE INDEX IX_DnsTasks_AgentId_Status ON dbo.DnsTasks (AgentId, Status)
        """,
        """
        IF OBJECT_ID(N'dbo.CtCertificates', N'U') IS NULL
        CREATE TABLE dbo.CtCertificates (Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_CtCertificates PRIMARY KEY,
            TenantId uniqueidentifier NOT NULL, CrtShId bigint NOT NULL, Domain nvarchar(255) NOT NULL, Issuer nvarchar(1000) NOT NULL,
            Names nvarchar(4000) NOT NULL, Serial nvarchar(80) NULL, NotBefore datetimeoffset NOT NULL, NotAfter datetimeoffset NOT NULL,
            Known bit NOT NULL, Acknowledged bit NOT NULL, FirstSeenAt datetimeoffset NOT NULL)
        """,
        """
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_CtCertificates_TenantId_CrtShId')
        CREATE UNIQUE INDEX IX_CtCertificates_TenantId_CrtShId ON dbo.CtCertificates (TenantId, CrtShId)
        """,
    ];

    // Columns added to tables that may have been created above by an older statement.
    private static readonly (string Table, string Column, string Definition)[] LateColumns =
    [
        ("Endpoints", "KeyInfo", "nvarchar(40) NULL"),
        ("Endpoints", "SignatureAlgorithm", "nvarchar(60) NULL"),
        ("Endpoints", "TlsProtocol", "nvarchar(20) NULL"),
    ];

    public static IEnumerable<string> AllStatements()
    {
        foreach (var (t, c, d) in Columns) yield return $"IF COL_LENGTH('dbo.{t}','{c}') IS NULL ALTER TABLE dbo.{t} ADD {c} {d}";
        foreach (var s in Tables) yield return s;
        foreach (var (t, c, d) in LateColumns) yield return $"IF COL_LENGTH('dbo.{t}','{c}') IS NULL ALTER TABLE dbo.{t} ADD {c} {d}";
        // "{}" built with NCHAR so no braces reach ExecuteSqlRaw (which treats {n} as format placeholders).
        yield return "UPDATE dbo.DnsProviders SET ConfigJson = NCHAR(123) + NCHAR(125) WHERE ConfigJson = N''";
    }

    public static async Task RunAsync(AppDb db, ILogger log, CancellationToken ct = default)
    {
        foreach (var sql in AllStatements())
            await db.Database.ExecuteSqlRawAsync(sql, ct);
        log.LogInformation("Database schema verified/upgraded to v1.5.");
    }
}
