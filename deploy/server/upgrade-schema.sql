-- AutomateCert: upgrade an existing database to v1.5 (idempotent, safe to re-run).
-- The installer and the server run this automatically; use it only for DBA-managed upgrades.
USE [AutomateCert];
GO
IF COL_LENGTH('dbo.Tenants','AlertEmails') IS NULL ALTER TABLE dbo.Tenants ADD AlertEmails nvarchar(2000) NULL;
GO
IF COL_LENGTH('dbo.Tenants','AlertDays') IS NULL ALTER TABLE dbo.Tenants ADD AlertDays nvarchar(100) NOT NULL CONSTRAINT DF_Tenants_AlertDays DEFAULT N'30,14,7,3,1';
GO
IF COL_LENGTH('dbo.Tenants','AlertUnmanaged') IS NULL ALTER TABLE dbo.Tenants ADD AlertUnmanaged bit NOT NULL CONSTRAINT DF_Tenants_AlertUnmanaged DEFAULT 1;
GO
IF COL_LENGTH('dbo.Tenants','NotifyOnSuccess') IS NULL ALTER TABLE dbo.Tenants ADD NotifyOnSuccess bit NOT NULL CONSTRAINT DF_Tenants_NotifyOnSuccess DEFAULT 1;
GO
IF COL_LENGTH('dbo.Tenants','RequireMfa') IS NULL ALTER TABLE dbo.Tenants ADD RequireMfa bit NOT NULL CONSTRAINT DF_Tenants_RequireMfa DEFAULT 0;
GO
IF COL_LENGTH('dbo.Tenants','AiEnabled') IS NULL ALTER TABLE dbo.Tenants ADD AiEnabled bit NOT NULL CONSTRAINT DF_Tenants_AiEnabled DEFAULT 0;
GO
IF COL_LENGTH('dbo.Tenants','AiProvider') IS NULL ALTER TABLE dbo.Tenants ADD AiProvider nvarchar(30) NULL;
GO
IF COL_LENGTH('dbo.Tenants','AiEndpoint') IS NULL ALTER TABLE dbo.Tenants ADD AiEndpoint nvarchar(500) NULL;
GO
IF COL_LENGTH('dbo.Tenants','AiModel') IS NULL ALTER TABLE dbo.Tenants ADD AiModel nvarchar(200) NULL;
GO
IF COL_LENGTH('dbo.Tenants','AiApiKeyProtected') IS NULL ALTER TABLE dbo.Tenants ADD AiApiKeyProtected nvarchar(max) NULL;
GO
IF COL_LENGTH('dbo.Users','NotifyByEmail') IS NULL ALTER TABLE dbo.Users ADD NotifyByEmail bit NOT NULL CONSTRAINT DF_Users_NotifyByEmail DEFAULT 1;
GO
IF COL_LENGTH('dbo.Users','MfaEnabled') IS NULL ALTER TABLE dbo.Users ADD MfaEnabled bit NOT NULL CONSTRAINT DF_Users_MfaEnabled DEFAULT 0;
GO
IF COL_LENGTH('dbo.Users','MfaMethod') IS NULL ALTER TABLE dbo.Users ADD MfaMethod nvarchar(10) NOT NULL CONSTRAINT DF_Users_MfaMethod DEFAULT N'None';
GO
IF COL_LENGTH('dbo.Users','TotpSecretProtected') IS NULL ALTER TABLE dbo.Users ADD TotpSecretProtected nvarchar(max) NULL;
GO
IF COL_LENGTH('dbo.Users','PendingTotpSecretProtected') IS NULL ALTER TABLE dbo.Users ADD PendingTotpSecretProtected nvarchar(max) NULL;
GO
IF COL_LENGTH('dbo.Users','LastTotpStep') IS NULL ALTER TABLE dbo.Users ADD LastTotpStep bigint NOT NULL CONSTRAINT DF_Users_LastTotpStep DEFAULT 0;
GO
IF COL_LENGTH('dbo.Users','RecoveryCodes') IS NULL ALTER TABLE dbo.Users ADD RecoveryCodes nvarchar(2000) NOT NULL CONSTRAINT DF_Users_RecoveryCodes DEFAULT N'[]';
GO
IF COL_LENGTH('dbo.Users','EmailOtpHash') IS NULL ALTER TABLE dbo.Users ADD EmailOtpHash varbinary(max) NULL;
GO
IF COL_LENGTH('dbo.Users','EmailOtpExpires') IS NULL ALTER TABLE dbo.Users ADD EmailOtpExpires datetimeoffset NULL;
GO
IF COL_LENGTH('dbo.Users','EmailOtpAttempts') IS NULL ALTER TABLE dbo.Users ADD EmailOtpAttempts int NOT NULL CONSTRAINT DF_Users_EmailOtpAttempts DEFAULT 0;
GO
IF COL_LENGTH('dbo.Users','EmailOtpSentAt') IS NULL ALTER TABLE dbo.Users ADD EmailOtpSentAt datetimeoffset NULL;
GO
IF COL_LENGTH('dbo.EnrollmentTokens','MaxUses') IS NULL ALTER TABLE dbo.EnrollmentTokens ADD MaxUses int NOT NULL CONSTRAINT DF_EnrollmentTokens_MaxUses DEFAULT 1;
GO
IF COL_LENGTH('dbo.EnrollmentTokens','UseCount') IS NULL ALTER TABLE dbo.EnrollmentTokens ADD UseCount int NOT NULL CONSTRAINT DF_EnrollmentTokens_UseCount DEFAULT 0;
GO
IF COL_LENGTH('dbo.Tenants','SessionHours') IS NULL ALTER TABLE dbo.Tenants ADD SessionHours int NOT NULL CONSTRAINT DF_Tenants_SessionHours DEFAULT 8;
GO
IF COL_LENGTH('dbo.Tenants','WeeklySummary') IS NULL ALTER TABLE dbo.Tenants ADD WeeklySummary bit NOT NULL CONSTRAINT DF_Tenants_WeeklySummary DEFAULT 1;
GO
IF COL_LENGTH('dbo.Tenants','CtDomains') IS NULL ALTER TABLE dbo.Tenants ADD CtDomains nvarchar(2000) NULL;
GO
IF COL_LENGTH('dbo.Users','LastLoginIp') IS NULL ALTER TABLE dbo.Users ADD LastLoginIp nvarchar(64) NULL;
GO
IF COL_LENGTH('dbo.Users','MustChangePassword') IS NULL ALTER TABLE dbo.Users ADD MustChangePassword bit NOT NULL CONSTRAINT DF_Users_MustChangePassword DEFAULT 0;
GO
IF COL_LENGTH('dbo.Agents','Roles') IS NULL ALTER TABLE dbo.Agents ADD Roles nvarchar(500) NULL;
GO
IF COL_LENGTH('dbo.Issuers','CoverageEndsAt') IS NULL ALTER TABLE dbo.Issuers ADD CoverageEndsAt datetimeoffset NULL;
GO
IF COL_LENGTH('dbo.DnsProviders','ConfigJson') IS NULL ALTER TABLE dbo.DnsProviders ADD ConfigJson nvarchar(4000) NOT NULL CONSTRAINT DF_DnsProviders_ConfigJson DEFAULT (NCHAR(123) + NCHAR(125));
GO
IF COL_LENGTH('dbo.DnsProviders','AliasZone') IS NULL ALTER TABLE dbo.DnsProviders ADD AliasZone nvarchar(255) NULL;
GO
IF COL_LENGTH('dbo.DnsProviders','LastTestAt') IS NULL ALTER TABLE dbo.DnsProviders ADD LastTestAt datetimeoffset NULL;
GO
IF COL_LENGTH('dbo.DnsProviders','LastTestOk') IS NULL ALTER TABLE dbo.DnsProviders ADD LastTestOk bit NULL;
GO
IF COL_LENGTH('dbo.DnsProviders','LastTestMessage') IS NULL ALTER TABLE dbo.DnsProviders ADD LastTestMessage nvarchar(1000) NULL;
GO
IF COL_LENGTH('dbo.Profiles','WindowStartHour') IS NULL ALTER TABLE dbo.Profiles ADD WindowStartHour int NULL;
GO
IF COL_LENGTH('dbo.Profiles','WindowEndHour') IS NULL ALTER TABLE dbo.Profiles ADD WindowEndHour int NULL;
GO
IF COL_LENGTH('dbo.Profiles','Scenario') IS NULL ALTER TABLE dbo.Profiles ADD Scenario nvarchar(30) NULL;
GO
IF COL_LENGTH('dbo.Targets','AgentId') IS NULL ALTER TABLE dbo.Targets ADD AgentId uniqueidentifier NULL;
GO
IF COL_LENGTH('dbo.Jobs','ParentJobId') IS NULL ALTER TABLE dbo.Jobs ADD ParentJobId uniqueidentifier NULL;
GO
IF COL_LENGTH('dbo.Jobs','Phase') IS NULL ALTER TABLE dbo.Jobs ADD Phase nvarchar(20) NULL;
GO
IF COL_LENGTH('dbo.Jobs','NotBefore') IS NULL ALTER TABLE dbo.Jobs ADD NotBefore datetimeoffset NULL;
GO
IF COL_LENGTH('dbo.Jobs','TargetIds') IS NULL ALTER TABLE dbo.Jobs ADD TargetIds nvarchar(2000) NULL;
GO
IF COL_LENGTH('dbo.Discovered','KeyInfo') IS NULL ALTER TABLE dbo.Discovered ADD KeyInfo nvarchar(40) NULL;
GO
IF COL_LENGTH('dbo.Discovered','SignatureAlgorithm') IS NULL ALTER TABLE dbo.Discovered ADD SignatureAlgorithm nvarchar(60) NULL;
GO
IF COL_LENGTH('dbo.Discovered','Serial') IS NULL ALTER TABLE dbo.Discovered ADD Serial nvarchar(80) NULL;
GO
IF OBJECT_ID(N'dbo.AlertStates', N'U') IS NULL
CREATE TABLE dbo.AlertStates (Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_AlertStates PRIMARY KEY,
TenantId uniqueidentifier NOT NULL, [Key] nvarchar(300) NOT NULL, SentAt datetimeoffset NOT NULL);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_AlertStates_TenantId_Key')
CREATE UNIQUE INDEX IX_AlertStates_TenantId_Key ON dbo.AlertStates (TenantId, [Key]);
GO
IF OBJECT_ID(N'dbo.Endpoints', N'U') IS NULL
CREATE TABLE dbo.Endpoints (Id uniqueidentifier NOT NULL CONSTRAINT PK_Endpoints PRIMARY KEY,
TenantId uniqueidentifier NOT NULL, Host nvarchar(255) NOT NULL, Port int NOT NULL, Label nvarchar(200) NULL,
Thumbprint nvarchar(64) NULL, Subject nvarchar(1000) NULL, Issuer nvarchar(1000) NULL, DnsNames nvarchar(4000) NULL,
NotBefore datetimeoffset NULL, NotAfter datetimeoffset NULL, ChainValid bit NULL, NameMatches bit NULL,
ChainStatus nvarchar(1000) NULL, LastError nvarchar(1000) NULL, LastCheckedAt datetimeoffset NULL, CreatedAt datetimeoffset NOT NULL);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Endpoints_TenantId_Host_Port')
CREATE UNIQUE INDEX IX_Endpoints_TenantId_Host_Port ON dbo.Endpoints (TenantId, Host, Port);
GO
IF OBJECT_ID(N'dbo.CaAccounts', N'U') IS NULL
CREATE TABLE dbo.CaAccounts (Id uniqueidentifier NOT NULL CONSTRAINT PK_CaAccounts PRIMARY KEY,
TenantId uniqueidentifier NOT NULL, Name nvarchar(200) NOT NULL, Type nvarchar(30) NOT NULL,
ConfigJson nvarchar(4000) NOT NULL, CredentialProtected nvarchar(max) NULL, SnapshotJson nvarchar(max) NULL,
LastSyncAt datetimeoffset NULL, LastError nvarchar(1000) NULL, LowBalanceThreshold decimal(18,2) NULL, CreatedAt datetimeoffset NOT NULL);
GO
IF OBJECT_ID(N'dbo.DnsTasks', N'U') IS NULL
CREATE TABLE dbo.DnsTasks (Id uniqueidentifier NOT NULL CONSTRAINT PK_DnsTasks PRIMARY KEY,
TenantId uniqueidentifier NOT NULL, AgentId uniqueidentifier NOT NULL, Action nvarchar(10) NOT NULL,
Name nvarchar(300) NOT NULL, Value nvarchar(300) NOT NULL, DnsServer nvarchar(255) NULL, Zone nvarchar(255) NULL,
Status nvarchar(20) NOT NULL, Message nvarchar(1000) NULL, CreatedAt datetimeoffset NOT NULL, CompletedAt datetimeoffset NULL);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_DnsTasks_AgentId_Status')
CREATE INDEX IX_DnsTasks_AgentId_Status ON dbo.DnsTasks (AgentId, Status);
GO
IF OBJECT_ID(N'dbo.CtCertificates', N'U') IS NULL
CREATE TABLE dbo.CtCertificates (Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_CtCertificates PRIMARY KEY,
TenantId uniqueidentifier NOT NULL, CrtShId bigint NOT NULL, Domain nvarchar(255) NOT NULL, Issuer nvarchar(1000) NOT NULL,
Names nvarchar(4000) NOT NULL, Serial nvarchar(80) NULL, NotBefore datetimeoffset NOT NULL, NotAfter datetimeoffset NOT NULL,
Known bit NOT NULL, Acknowledged bit NOT NULL, FirstSeenAt datetimeoffset NOT NULL);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_CtCertificates_TenantId_CrtShId')
CREATE UNIQUE INDEX IX_CtCertificates_TenantId_CrtShId ON dbo.CtCertificates (TenantId, CrtShId);
GO
IF COL_LENGTH('dbo.Endpoints','KeyInfo') IS NULL ALTER TABLE dbo.Endpoints ADD KeyInfo nvarchar(40) NULL;
GO
IF COL_LENGTH('dbo.Endpoints','SignatureAlgorithm') IS NULL ALTER TABLE dbo.Endpoints ADD SignatureAlgorithm nvarchar(60) NULL;
GO
IF COL_LENGTH('dbo.Endpoints','TlsProtocol') IS NULL ALTER TABLE dbo.Endpoints ADD TlsProtocol nvarchar(20) NULL;
GO
UPDATE dbo.DnsProviders SET ConfigJson = NCHAR(123) + NCHAR(125) WHERE ConfigJson = N'';
GO
