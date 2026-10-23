using Microsoft.EntityFrameworkCore;

namespace AutomateCert.Server.Data;

/// <summary>Per-request tenant scope. Guid.Empty means "no tenant" and matches nothing.</summary>
public sealed class TenantContext
{
    public Guid TenantId { get; set; }
    public string Actor { get; set; } = "system";
}

public sealed class AppDb(DbContextOptions<AppDb> options, TenantContext tenant) : DbContext(options)
{
    public Guid CurrentTenantId => tenant.TenantId;

    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<User> Users => Set<User>();
    public DbSet<Agent> Agents => Set<Agent>();
    public DbSet<EnrollmentToken> EnrollmentTokens => Set<EnrollmentToken>();
    public DbSet<Issuer> Issuers => Set<Issuer>();
    public DbSet<DnsProvider> DnsProviders => Set<DnsProvider>();
    public DbSet<CaAccount> CaAccounts => Set<CaAccount>();
    public DbSet<CertificateProfile> Profiles => Set<CertificateProfile>();
    public DbSet<DeploymentTarget> Targets => Set<DeploymentTarget>();
    public DbSet<Job> Jobs => Set<Job>();
    public DbSet<DnsTask> DnsTasks => Set<DnsTask>();
    public DbSet<DiscoveredCertificate> Discovered => Set<DiscoveredCertificate>();
    public DbSet<MonitoredEndpoint> Endpoints => Set<MonitoredEndpoint>();
    public DbSet<CtCertificate> CtCertificates => Set<CtCertificate>();
    public DbSet<AuditEntry> Audit => Set<AuditEntry>();
    public DbSet<AlertState> AlertStates => Set<AlertState>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<User>().HasIndex(u => u.Email).IsUnique();
        b.Entity<EnrollmentToken>().HasIndex(t => t.TokenHash).IsUnique();
        b.Entity<Job>().HasIndex(j => new { j.AgentId, j.Status });
        b.Entity<Job>().HasIndex(j => new { j.TenantId, j.CreatedAt });
        b.Entity<DnsTask>().HasIndex(t => new { t.AgentId, t.Status });
        b.Entity<DiscoveredCertificate>().HasIndex(d => new { d.AgentId, d.Thumbprint }).IsUnique();
        b.Entity<DiscoveredCertificate>().HasIndex(d => new { d.TenantId, d.NotAfter });
        b.Entity<MonitoredEndpoint>().HasIndex(e => new { e.TenantId, e.Host, e.Port }).IsUnique();
        b.Entity<CtCertificate>().HasIndex(c => new { c.TenantId, c.CrtShId }).IsUnique();
        b.Entity<AuditEntry>().HasIndex(a => new { a.TenantId, a.At });
        b.Entity<AlertState>().HasIndex(a => new { a.TenantId, a.Key }).IsUnique();
        b.Entity<CaAccount>().Property(a => a.LowBalanceThreshold).HasPrecision(18, 2);

        b.Entity<CertificateProfile>().HasOne(p => p.Issuer).WithMany().HasForeignKey(p => p.IssuerId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<CertificateProfile>().HasOne(p => p.DnsProvider).WithMany().HasForeignKey(p => p.DnsProviderId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<CertificateProfile>().HasOne(p => p.Agent).WithMany().HasForeignKey(p => p.AgentId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<CertificateProfile>().HasMany(p => p.Targets).WithOne().HasForeignKey(t => t.ProfileId).OnDelete(DeleteBehavior.Cascade);

        // Tenant isolation: every tenant-owned entity is filtered automatically.
        b.Entity<User>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        b.Entity<Agent>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        b.Entity<EnrollmentToken>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        b.Entity<Issuer>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        b.Entity<DnsProvider>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        b.Entity<CaAccount>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        b.Entity<CertificateProfile>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        b.Entity<DeploymentTarget>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        b.Entity<Job>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        b.Entity<DnsTask>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        b.Entity<DiscoveredCertificate>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        b.Entity<MonitoredEndpoint>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        b.Entity<CtCertificate>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        b.Entity<AuditEntry>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
        b.Entity<AlertState>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        foreach (var e in ChangeTracker.Entries<ITenantOwned>())
        {
            if (e.State == EntityState.Added && e.Entity.TenantId == Guid.Empty)
                e.Entity.TenantId = CurrentTenantId;
            if (e.State is EntityState.Added or EntityState.Modified && e.Entity.TenantId == Guid.Empty)
                throw new InvalidOperationException($"Refusing to save {e.Entity.GetType().Name} without a tenant.");
        }
        return base.SaveChangesAsync(cancellationToken);
    }
}
