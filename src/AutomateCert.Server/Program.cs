using System.Security.Authentication;
using System.Security.Claims;
using System.Threading.RateLimiting;
using AutomateCert.Server;
using AutomateCert.Server.Data;
using AutomateCert.Server.Endpoints;
using AutomateCert.Server.Security;
using AutomateCert.Server.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting.WindowsServices;

// "--init" is used by the installer: create/upgrade the database, the organization and first administrator, then exit.
var initMode = args.Contains("--init", StringComparer.OrdinalIgnoreCase);
var hostArgs = args.Where(a => !a.Equals("--init", StringComparison.OrdinalIgnoreCase)).ToArray();

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = hostArgs,
    ContentRootPath = WindowsServiceHelpers.IsWindowsService() ? AppContext.BaseDirectory : default
});
builder.Services.AddWindowsService(o => o.ServiceName = "AutomateCertServer");
var cfg = builder.Configuration;

var logBuffer = new SystemLogBuffer();
builder.Services.AddSingleton(logBuffer);
builder.Logging.AddProvider(new SystemLogProvider(logBuffer));

// ---------- HTTPS: TLS 1.2/1.3 only; certificate from the Windows store, follows renewals without restart ----------
var certThumbprint = cfg["Server:CertificateThumbprint"];
var certHost = cfg["Server:CertificateHost"];
if (!initMode && (!string.IsNullOrWhiteSpace(certThumbprint) || !string.IsNullOrWhiteSpace(certHost)))
{
    var httpsPort = cfg.GetValue("Server:HttpsPort", 443);
    var consoleCert = new ConsoleCertificate(certThumbprint, certHost);
    builder.WebHost.ConfigureKestrel(k =>
    {
        k.AddServerHeader = false;
        k.ListenAnyIP(httpsPort, l => l.UseHttps(h =>
        {
            h.SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;
            h.ServerCertificateSelector = (_, _) => consoleCert.Current();
        }));
        if (cfg.GetValue<int?>("Server:HttpPort") is { } httpPort and > 0) k.ListenAnyIP(httpPort);
    });
    builder.Services.AddHttpsRedirection(o => o.HttpsPort = httpsPort);
}
else
{
    builder.WebHost.ConfigureKestrel(k =>
    {
        k.AddServerHeader = false;
        k.ConfigureHttpsDefaults(h => h.SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13);
    });
}

// ---------- Data ----------
builder.Services.AddScoped<TenantContext>();
builder.Services.AddDbContext<AppDb>(o => o.UseSqlServer(
    cfg.GetConnectionString("AutomateCert") ?? throw new InvalidOperationException("ConnectionStrings:AutomateCert is not configured."),
    sql => sql.EnableRetryOnFailure()));

// ---------- Secrets at rest ----------
var keysDir = cfg["DataProtection:KeysDirectory"] is { Length: > 0 } kd
    ? (Path.IsPathRooted(kd) ? kd : Path.Combine(builder.Environment.ContentRootPath, kd))
    : Path.Combine(builder.Environment.ContentRootPath, "App_Data", "keys");
Directory.CreateDirectory(keysDir);
var dp = builder.Services.AddDataProtection().SetApplicationName("AutomateCert").PersistKeysToFileSystem(new DirectoryInfo(keysDir));
if (OperatingSystem.IsWindows()) dp.ProtectKeysWithDpapi(protectToLocalMachine: true);
builder.Services.AddSingleton<SecretProtector>();
builder.Services.AddSingleton<MfaTicketProtector>();

// ---------- Services ----------
builder.Services.Configure<SmtpOptions>(cfg.GetSection("Smtp"));
builder.Services.AddSingleton<EmailSender>();
foreach (var (name, seconds) in new[] { ("webhook", 15), ("dns", 30), ("ai", 120), ("ct", 90), ("ca", 60) })
    builder.Services.AddHttpClient(name, c => { c.Timeout = TimeSpan.FromSeconds(seconds); c.DefaultRequestHeaders.UserAgent.ParseAdd("AutomateCert/" + AutomateCert.Shared.AgentProtocol.Version); });
builder.Services.AddMemoryCache();
// Compress static UI files only. API responses are not compressed (avoids BREACH-style leaks).
builder.Services.AddResponseCompression(o =>
{
    o.EnableForHttps = true;
    o.MimeTypes = ["application/javascript", "text/javascript", "text/css", "image/svg+xml", "application/manifest+json"];
});
builder.Services.AddScoped<AuditService>();
builder.Services.AddScoped<NotificationService>();
builder.Services.AddScoped<AlertEngine>();
builder.Services.AddScoped<MfaService>();
builder.Services.AddScoped<AutopilotService>();
builder.Services.AddScoped<AiService>();
builder.Services.AddScoped<DnsProviderFactory>();
builder.Services.AddScoped<AcmeIssuanceService>();
builder.Services.AddScoped<EndpointScanner>();
builder.Services.AddScoped<CtMonitor>();
builder.Services.AddScoped<CaAccountService>();
if (!initMode)
{
    builder.Services.AddHostedService<IssuanceWorker>();
    builder.Services.AddHostedService<RenewalPlanner>();
    builder.Services.AddHostedService<MonitoringWorker>();
}

// ---------- Authentication ----------
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.Cookie.Name = "__Host-AutomateCert";
        o.Cookie.HttpOnly = true;
        o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        o.Cookie.SameSite = SameSiteMode.Strict;
        o.Cookie.Path = "/";
        o.ExpireTimeSpan = TimeSpan.FromHours(8);
        o.SlidingExpiration = true;
        o.Events.OnRedirectToLogin = c => { c.Response.StatusCode = 401; return Task.CompletedTask; };
        o.Events.OnRedirectToAccessDenied = c => { c.Response.StatusCode = 403; return Task.CompletedTask; };
        o.Events.OnValidatePrincipal = async c =>
        {
            var id = c.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!Guid.TryParse(id, out var uid)) { c.RejectPrincipal(); return; }
            if (!PrincipalCache.TryGet(uid, out var disabled, out var role))
            {
                var db = c.HttpContext.RequestServices.GetRequiredService<AppDb>();
                var user = await db.Users.IgnoreQueryFilters().AsNoTracking().Where(u => u.Id == uid)
                    .Select(u => new { u.Disabled, u.Role }).FirstOrDefaultAsync();
                if (user is null) { c.RejectPrincipal(); return; }
                (disabled, role) = (user.Disabled, user.Role);
                PrincipalCache.Set(uid, disabled, role);
            }
            if (disabled || role != c.Principal!.FindFirstValue(ClaimTypes.Role)) c.RejectPrincipal();
        };
    });
builder.Services.AddAuthorizationBuilder()
    .AddPolicy("Admin", p => p.RequireRole(Roles.Admin))
    .AddPolicy("Operator", p => p.RequireRole(Roles.Admin, Roles.Operator))
    .AddPolicy("Approve", p => p.RequireRole(Roles.Admin, Roles.Operator, Roles.Approver));

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    o.AddPolicy("login", ctx => RateLimitPartition.GetFixedWindowLimiter(ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 15, Window = TimeSpan.FromMinutes(5) }));
    o.AddPolicy("agent", ctx => RateLimitPartition.GetFixedWindowLimiter(ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 900, Window = TimeSpan.FromMinutes(1) }));
    o.AddPolicy("ai", ctx => RateLimitPartition.GetFixedWindowLimiter(ctx.User.Identity?.Name ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1) }));
});

builder.Services.Configure<ForwardedHeadersOptions>(o => o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto);
builder.Services.AddHsts(o => { o.MaxAge = TimeSpan.FromDays(365); o.IncludeSubDomains = true; });

var app = builder.Build();

// ---------- Database create / upgrade ----------
if (initMode || cfg.GetValue("Database:AutoCreate", false))
{
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDb>();
    var startupLog = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("AutomateCert.Startup");
    if (db.Database.GetMigrations().Any()) await db.Database.MigrateAsync();
    else
    {
        await db.Database.EnsureCreatedAsync();
        await SchemaUpgrader.RunAsync(db, startupLog);
    }
}

if (initMode)
{
    Environment.ExitCode = await Initializer.RunAsync(app.Services, cfg);
    return;
}

app.UseForwardedHeaders();
if (!app.Environment.IsDevelopment()) app.UseHsts();
app.UseHttpsRedirection();

app.Use(async (ctx, next) =>
{
    var h = ctx.Response.Headers;
    h["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; font-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'; object-src 'none'";
    h["X-Content-Type-Options"] = "nosniff";
    h["X-Frame-Options"] = "DENY";
    h["Referrer-Policy"] = "no-referrer";
    h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=(), usb=()";
    h["Cross-Origin-Opener-Policy"] = "same-origin";
    h["Cross-Origin-Resource-Policy"] = "same-origin";
    if (ctx.Request.Path.StartsWithSegments("/api")) h["Cache-Control"] = "no-store";
    await next();
});

app.UseResponseCompression();
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = sf =>
    {
        // index.html always revalidates; versioned app.js/app.css (?v=) and images are cached by the browser.
        var versioned = sf.Context.Request.Query.ContainsKey("v") || sf.Context.Request.Path.StartsWithSegments("/img");
        sf.Context.Response.Headers.CacheControl = versioned && !sf.File.Name.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
            ? "public, max-age=604800" : "no-cache";
    }
});
app.UseRateLimiter();
app.UseAuthentication();

// CSRF defence: state-changing cookie-authenticated API calls need a custom header (impossible cross-site without CORS).
app.Use(async (ctx, next) =>
{
    var path = ctx.Request.Path;
    if (path.StartsWithSegments("/api") && !path.StartsWithSegments("/api/agent") &&
        !HttpMethods.IsGet(ctx.Request.Method) && !HttpMethods.IsHead(ctx.Request.Method) && ctx.Request.Headers["X-AutomateCert-Client"] != "web")
    {
        ctx.Response.StatusCode = 400;
        await ctx.Response.WriteAsJsonAsync(new { error = "Missing client header." });
        return;
    }
    await next();
});

// Tenant resolution + mandatory gates (password change, two-step enrollment).
app.Use(async (ctx, next) =>
{
    if (ctx.User.Identity?.IsAuthenticated == true && Guid.TryParse(ctx.User.FindFirstValue(AuthEndpoints.TenantClaim), out var tid))
    {
        var t = ctx.RequestServices.GetRequiredService<TenantContext>();
        t.TenantId = tid;
        t.Actor = ctx.User.Identity.Name ?? "unknown";
        var path = ctx.Request.Path;
        var gated = path.StartsWithSegments("/api") && !path.StartsWithSegments("/api/auth") && !path.StartsWithSegments("/api/me");
        if (gated && ctx.User.HasClaim(c => c.Type == AuthEndpoints.PasswordChangeClaim))
        {
            ctx.Response.StatusCode = 403;
            await ctx.Response.WriteAsJsonAsync(new { error = "Choose a new password to continue.", passwordChangeRequired = true });
            return;
        }
        if (gated && ctx.User.HasClaim(c => c.Type == AuthEndpoints.MfaSetupClaim))
        {
            ctx.Response.StatusCode = 403;
            await ctx.Response.WriteAsJsonAsync(new { error = "Set up two-step verification to continue.", mfaSetupRequired = true });
            return;
        }
    }
    await next();
});

app.UseAuthorization();

AuthEndpoints.Map(app);
ManagementEndpoints.Map(app);
InsightsEndpoints.Map(app);
AgentEndpoints.Map(app);

app.MapGet("/healthz", () => Results.Ok(new { status = "ok", version = AutomateCert.Shared.AgentProtocol.Version }));
app.MapFallbackToFile("index.html");

// Warm up EF Core and the SQL connection pool so the first sign-in is not slow.
app.Lifetime.ApplicationStarted.Register(() => _ = Task.Run(async () =>
{
    try
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDb>();
        await db.Users.IgnoreQueryFilters().AsNoTracking().AnyAsync();
        await db.Discovered.IgnoreQueryFilters().AsNoTracking().AnyAsync();
    }
    catch { /* best effort */ }
}));

app.Run();
