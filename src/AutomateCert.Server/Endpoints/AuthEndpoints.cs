using AutomateCert.Server;
using System.Security.Claims;
using AutomateCert.Server.Data;
using AutomateCert.Server.Security;
using AutomateCert.Server.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace AutomateCert.Server.Endpoints;

public sealed record SetupRequest(string TenantName, string Email, string DisplayName, string Password);
public sealed record LoginRequest(string Email, string Password);
public sealed record MfaLoginRequest(string Ticket, string Code);
public sealed record TicketRequest(string Ticket);
public sealed record CodeRequest(string Code);
public sealed record PasswordRequest(string Password);
public sealed record CreateUserRequest(string Email, string DisplayName, string Role, string? Password, bool SendWelcome = true);
public sealed record RoleRequest(string Role);
public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

public sealed class MfaTicketProtector(IDataProtectionProvider provider)
{
    private readonly ITimeLimitedDataProtector _p = provider.CreateProtector("AutomateCert.MfaTicket.v1").ToTimeLimitedDataProtector();
    public string Issue(Guid userId) => _p.Protect(userId.ToString(), TimeSpan.FromMinutes(5));
    public Guid? Read(string? ticket)
    {
        if (string.IsNullOrWhiteSpace(ticket)) return null;
        try { return Guid.Parse(_p.Unprotect(ticket)); } catch { return null; }
    }
}

public static class AuthEndpoints
{
    public const string TenantClaim = "ac_tenant";
    public const string MfaSetupClaim = "ac_mfa_setup";
    public const string PasswordChangeClaim = "ac_pw_change";

    private static IResult Bad(string error) => Results.BadRequest(new { error });

    public static async Task SignInAsync(HttpContext http, User user, bool mfaSetupRequired, int sessionHours)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()), new(ClaimTypes.Name, user.Email),
            new(ClaimTypes.GivenName, user.DisplayName), new(ClaimTypes.Role, user.Role), new(TenantClaim, user.TenantId.ToString())
        };
        if (mfaSetupRequired) claims.Add(new Claim(MfaSetupClaim, "1"));
        if (user.MustChangePassword) claims.Add(new Claim(PasswordChangeClaim, "1"));
        await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)),
            new AuthenticationProperties { IsPersistent = false, ExpiresUtc = DateTimeOffset.UtcNow.AddHours(Math.Clamp(sessionHours, 1, 24)), AllowRefresh = true });
    }

    private static async Task<User?> CurrentUserAsync(ClaimsPrincipal p, AppDb db) =>
        Guid.TryParse(p.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? await db.Users.FirstOrDefaultAsync(u => u.Id == id) : null;

    private static bool RegisterFailure(User user)
    {
        user.FailedLogins++;
        if (user.FailedLogins < 5) return false;
        user.LockoutUntil = DateTimeOffset.UtcNow.AddMinutes(15);
        user.FailedLogins = 0;
        return true;
    }

    private static async Task CompleteLoginAsync(HttpContext http, AppDb db, User user, AuditService audit, string method, CancellationToken ct)
    {
        var tenant = await db.Tenants.AsNoTracking().FirstAsync(t => t.Id == user.TenantId, ct);
        user.FailedLogins = 0;
        user.LockoutUntil = null;
        user.LastLoginAt = DateTimeOffset.UtcNow;
        user.LastLoginIp = http.Connection.RemoteIpAddress?.ToString();
        audit.Record(user.TenantId, user.Email, "auth.login", user.Email, $"{method} from {user.LastLoginIp}");
        await db.SaveChangesAsync(ct);
        await SignInAsync(http, user, tenant.RequireMfa && !user.MfaEnabled, tenant.SessionHours);
    }

    public static void Map(WebApplication app)
    {
        var g = app.MapGroup("/api");

        g.MapGet("/setup/status", async (AppDb db) => Results.Ok(new { required = !await db.Users.IgnoreQueryFilters().AnyAsync() }));

        g.MapPost("/setup", async (SetupRequest r, AppDb db, AuditService audit) =>
        {
            if (await db.Users.IgnoreQueryFilters().AnyAsync()) return Results.Conflict(new { error = "Setup already completed." });
            if (PasswordHasher.PolicyError(r.Password, r.Email) is { } pe) return Bad(pe);
            if (string.IsNullOrWhiteSpace(r.TenantName) || string.IsNullOrWhiteSpace(r.Email) || !r.Email.Contains('@'))
                return Bad("Organization and a valid email are required.");
            var tenant = new Tenant { Name = r.TenantName.Trim() };
            db.Tenants.Add(tenant);
            db.Users.Add(new User
            {
                TenantId = tenant.Id, Email = r.Email.Trim().ToLowerInvariant(),
                DisplayName = string.IsNullOrWhiteSpace(r.DisplayName) ? r.Email : r.DisplayName.Trim(),
                PasswordHash = PasswordHasher.Hash(r.Password), Role = Roles.Admin
            });
            Initializer.SeedDefaults(db, tenant, r.Email.Trim().ToLowerInvariant());
            audit.Record(tenant.Id, r.Email, "setup.completed", tenant.Name);
            await db.SaveChangesAsync();
            return Results.Ok();
        }).RequireRateLimiting("login");

        g.MapPost("/auth/login", async (LoginRequest r, AppDb db, HttpContext http, AuditService audit, MfaService mfa, MfaTicketProtector tickets, CancellationToken ct) =>
        {
            var email = (r.Email ?? "").Trim().ToLowerInvariant();
            var user = await db.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.Email == email, ct);
            if (user is null || user.Disabled)
            {
                PasswordHasher.Hash("timing-equalizer-password");
                return Results.Json(new { error = "Invalid email or password." }, statusCode: 401);
            }
            if (user.LockoutUntil > DateTimeOffset.UtcNow)
                return Results.Json(new { error = "Account temporarily locked after failed attempts. Try again in 15 minutes." }, statusCode: 423);
            if (!PasswordHasher.Verify(r.Password ?? "", user.PasswordHash))
            {
                var locked = RegisterFailure(user);
                audit.Record(user.TenantId, email, locked ? "auth.locked" : "auth.failed", email, http.Connection.RemoteIpAddress?.ToString());
                await db.SaveChangesAsync(ct);
                return Results.Json(new { error = "Invalid email or password." }, statusCode: 401);
            }
            if (user.MfaEnabled)
            {
                string? info = null;
                if (user.MfaMethod == MfaMethods.Email)
                {
                    try { await mfa.SendEmailCodeAsync(user, ct); info = $"We emailed a code to {MaskEmail(user.Email)}."; }
                    catch (InvalidOperationException ex) { info = ex.Message + " You can also use a recovery code."; }
                }
                await db.SaveChangesAsync(ct);
                return Results.Ok(new { mfaRequired = true, method = user.MfaMethod, ticket = tickets.Issue(user.Id), info });
            }
            await CompleteLoginAsync(http, db, user, audit, "password only", ct);
            return Results.Ok(new { mfaRequired = false });
        }).RequireRateLimiting("login");

        g.MapPost("/auth/login/mfa", async (MfaLoginRequest r, AppDb db, HttpContext http, AuditService audit, MfaService mfa, MfaTicketProtector tickets, CancellationToken ct) =>
        {
            if (tickets.Read(r.Ticket) is not { } id) return Results.Json(new { error = "Sign-in session expired. Enter your password again." }, statusCode: 401);
            var user = await db.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.Id == id, ct);
            if (user is null || user.Disabled || !user.MfaEnabled) return Results.Json(new { error = "Invalid sign-in." }, statusCode: 401);
            if (user.LockoutUntil > DateTimeOffset.UtcNow)
                return Results.Json(new { error = "Account temporarily locked after failed attempts. Try again in 15 minutes." }, statusCode: 423);
            var usedRecovery = (r.Code ?? "").Trim().Length >= 10;
            if (!mfa.VerifySecondFactor(user, r.Code))
            {
                var locked = RegisterFailure(user);
                audit.Record(user.TenantId, user.Email, locked ? "auth.locked" : "auth.mfa.failed", user.Email);
                await db.SaveChangesAsync(ct);
                return Results.Json(new { error = "Invalid or expired code." }, statusCode: 401);
            }
            await CompleteLoginAsync(http, db, user, audit, usedRecovery ? "recovery code" : user.MfaMethod, ct);
            return Results.Ok(new { recoveryCodesLeft = mfa.RecoveryCodesLeft(user) });
        }).RequireRateLimiting("login");

        g.MapPost("/auth/login/mfa/resend", async (TicketRequest r, AppDb db, MfaService mfa, MfaTicketProtector tickets, CancellationToken ct) =>
        {
            if (tickets.Read(r.Ticket) is not { } id) return Results.Json(new { error = "Sign-in session expired." }, statusCode: 401);
            var user = await db.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.Id == id, ct);
            if (user is null || user.MfaMethod != MfaMethods.Email) return Bad("Email codes are not enabled for this account.");
            try { await mfa.SendEmailCodeAsync(user, ct); }
            catch (InvalidOperationException ex) { await db.SaveChangesAsync(ct); return Bad(ex.Message); }
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { info = $"New code sent to {MaskEmail(user.Email)}." });
        }).RequireRateLimiting("login");

        g.MapPost("/auth/logout", async (HttpContext http) =>
        {
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Ok();
        });

        g.MapGet("/auth/me", async (ClaimsPrincipal p, AppDb db, TenantContext t, MfaService mfa) =>
        {
            var user = await CurrentUserAsync(p, db);
            if (user is null) return Results.Unauthorized();
            var tenant = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(x => x.Id == t.TenantId);
            return Results.Ok(new
            {
                email = user.Email, displayName = user.DisplayName, role = user.Role, tenant = tenant?.Name,
                mfaEnabled = user.MfaEnabled, mfaMethod = user.MfaMethod,
                mfaSetupRequired = p.HasClaim(c => c.Type == MfaSetupClaim),
                passwordChangeRequired = user.MustChangePassword,
                orgRequiresMfa = tenant?.RequireMfa ?? false, recoveryCodesLeft = mfa.RecoveryCodesLeft(user),
                notifyByEmail = user.NotifyByEmail, lastLoginAt = user.LastLoginAt, lastLoginIp = user.LastLoginIp
            });
        }).RequireAuthorization();

        var me = g.MapGroup("/me").RequireAuthorization();

        me.MapPost("/password", async (ChangePasswordRequest r, ClaimsPrincipal p, AppDb db, AuditService audit, TenantContext t, HttpContext http) =>
        {
            var user = await CurrentUserAsync(p, db);
            if (user is null) return Results.Unauthorized();
            if (!PasswordHasher.Verify(r.CurrentPassword ?? "", user.PasswordHash)) return Bad("Current password is incorrect.");
            if (PasswordHasher.PolicyError(r.NewPassword, user.Email) is { } pe) return Bad(pe);
            if (PasswordHasher.Verify(r.NewPassword, user.PasswordHash)) return Bad("Choose a password you have not used here.");
            user.PasswordHash = PasswordHasher.Hash(r.NewPassword);
            user.MustChangePassword = false;
            audit.Record(t.TenantId, t.Actor, "user.password.changed", user.Email);
            await db.SaveChangesAsync();
            var tenant = await db.Tenants.AsNoTracking().FirstAsync(x => x.Id == user.TenantId);
            await SignInAsync(http, user, tenant.RequireMfa && !user.MfaEnabled, tenant.SessionHours);
            return Results.Ok();
        });

        me.MapPost("/notifications", async (NotifyPref r, ClaimsPrincipal p, AppDb db) =>
        {
            var user = await CurrentUserAsync(p, db);
            if (user is null) return Results.Unauthorized();
            user.NotifyByEmail = r.Enabled;
            await db.SaveChangesAsync();
            return Results.Ok();
        });

        me.MapPost("/mfa/totp/begin", async (ClaimsPrincipal p, AppDb db, MfaService mfa) =>
        {
            var user = await CurrentUserAsync(p, db);
            if (user is null) return Results.Unauthorized();
            var e = mfa.BeginTotp(user);
            await db.SaveChangesAsync();
            return Results.Ok(new { secret = e.Secret, uri = e.Uri, qr = e.QrDataUri });
        });

        me.MapPost("/mfa/totp/confirm", async (CodeRequest r, ClaimsPrincipal p, AppDb db, MfaService mfa, HttpContext http, AuditService audit, TenantContext t) =>
        {
            var user = await CurrentUserAsync(p, db);
            if (user is null) return Results.Unauthorized();
            if (!mfa.ConfirmTotp(user, r.Code)) return Bad("That code didn't match. Check the time on your phone and try the next code.");
            var codes = mfa.GenerateRecoveryCodes(user);
            audit.Record(t.TenantId, t.Actor, "mfa.enabled", user.Email, "authenticator app");
            await db.SaveChangesAsync();
            var tenant = await db.Tenants.AsNoTracking().FirstAsync(x => x.Id == user.TenantId);
            await SignInAsync(http, user, false, tenant.SessionHours);
            return Results.Ok(new { recoveryCodes = codes });
        });

        me.MapPost("/mfa/email/begin", async (ClaimsPrincipal p, AppDb db, MfaService mfa, CancellationToken ct) =>
        {
            var user = await CurrentUserAsync(p, db);
            if (user is null) return Results.Unauthorized();
            try { await mfa.SendEmailCodeAsync(user, ct); }
            catch (InvalidOperationException ex) { await db.SaveChangesAsync(ct); return Bad(ex.Message); }
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { info = $"Code sent to {MaskEmail(user.Email)}." });
        });

        me.MapPost("/mfa/email/confirm", async (CodeRequest r, ClaimsPrincipal p, AppDb db, MfaService mfa, HttpContext http, AuditService audit, TenantContext t) =>
        {
            var user = await CurrentUserAsync(p, db);
            if (user is null) return Results.Unauthorized();
            if (!mfa.ConfirmEmail(user, r.Code)) { await db.SaveChangesAsync(); return Bad("Invalid or expired code."); }
            var codes = mfa.GenerateRecoveryCodes(user);
            audit.Record(t.TenantId, t.Actor, "mfa.enabled", user.Email, "email code");
            await db.SaveChangesAsync();
            var tenant = await db.Tenants.AsNoTracking().FirstAsync(x => x.Id == user.TenantId);
            await SignInAsync(http, user, false, tenant.SessionHours);
            return Results.Ok(new { recoveryCodes = codes });
        });

        me.MapPost("/mfa/recovery-codes", async (PasswordRequest r, ClaimsPrincipal p, AppDb db, MfaService mfa, AuditService audit, TenantContext t) =>
        {
            var user = await CurrentUserAsync(p, db);
            if (user is null) return Results.Unauthorized();
            if (!user.MfaEnabled) return Bad("Enable two-step verification first.");
            if (!PasswordHasher.Verify(r.Password ?? "", user.PasswordHash)) return Bad("Password is incorrect.");
            var codes = mfa.GenerateRecoveryCodes(user);
            audit.Record(t.TenantId, t.Actor, "mfa.recovery.regenerated", user.Email);
            await db.SaveChangesAsync();
            return Results.Ok(new { recoveryCodes = codes });
        });

        me.MapPost("/mfa/disable", async (PasswordRequest r, ClaimsPrincipal p, AppDb db, MfaService mfa, AuditService audit, TenantContext t) =>
        {
            var user = await CurrentUserAsync(p, db);
            if (user is null) return Results.Unauthorized();
            var tenant = await db.Tenants.AsNoTracking().FirstAsync(x => x.Id == t.TenantId);
            if (tenant.RequireMfa) return Bad("Your organization requires two-step verification.");
            if (!PasswordHasher.Verify(r.Password ?? "", user.PasswordHash)) return Bad("Password is incorrect.");
            mfa.Disable(user);
            audit.Record(t.TenantId, t.Actor, "mfa.disabled", user.Email);
            await db.SaveChangesAsync();
            return Results.Ok();
        });

        // ---------------- User administration ----------------
        var users = g.MapGroup("/users").RequireAuthorization("Admin");
        users.MapGet("", async (AppDb db) => Results.Ok(await db.Users.AsNoTracking().OrderBy(u => u.Email)
            .Select(u => new { u.Id, u.Email, u.DisplayName, u.Role, u.Disabled, u.LastLoginAt, u.LastLoginIp, u.MfaEnabled, u.MfaMethod, u.MustChangePassword, u.CreatedAt }).ToListAsync()));

        users.MapGet("/roles", () => Results.Ok(Roles.All.Select(r => new { role = r, description = Roles.Descriptions[r] })));

        users.MapPost("", async (CreateUserRequest r, AppDb db, TenantContext t, AuditService audit, EmailSender email, IConfiguration cfg, CancellationToken ct) =>
        {
            if (!Roles.All.Contains(r.Role)) return Bad("Invalid role.");
            var mail = (r.Email ?? "").Trim().ToLowerInvariant();
            if (!mail.Contains('@')) return Bad("Invalid email.");
            if (await db.Users.IgnoreQueryFilters().AnyAsync(u => u.Email == mail, ct)) return Results.Conflict(new { error = "Email already in use." });
            var temp = string.IsNullOrWhiteSpace(r.Password) ? PasswordHasher.Generate() : r.Password!;
            if (PasswordHasher.PolicyError(temp, mail) is { } pe) return Bad(pe);
            db.Users.Add(new User
            {
                Email = mail, DisplayName = string.IsNullOrWhiteSpace(r.DisplayName) ? mail : r.DisplayName.Trim(),
                PasswordHash = PasswordHasher.Hash(temp), Role = r.Role, MustChangePassword = true
            });
            audit.Record(t.TenantId, t.Actor, "user.created", mail, r.Role);
            await db.SaveChangesAsync(ct);
            var emailed = false;
            if (r.SendWelcome && email.IsConfigured)
            {
                try
                {
                    var (html, text) = EmailTemplates.Render(new AlertMessage(Severity.Info, "You have been invited to AutomateCert",
                        $"{t.Actor} added you as {r.Role}. Sign in with your email; the temporary password is shared with you separately by your administrator. You will be asked to choose a new password.",
                        [new("Role", r.Role), new("Permissions", Roles.Descriptions[r.Role])]), cfg["App:PublicUrl"]);
                    await email.SendAsync([mail], "You have been invited to AutomateCert", html, text, ct);
                    emailed = true;
                }
                catch { /* invitation email is best effort */ }
            }
            // The temporary password is returned once to the admin and never emailed (separate channel).
            return Results.Ok(new { temporaryPassword = string.IsNullOrWhiteSpace(r.Password) ? temp : null, invitationEmailed = emailed });
        });

        users.MapPut("/{id:guid}/role", async (Guid id, RoleRequest r, AppDb db, TenantContext t, AuditService audit) =>
        {
            PrincipalCache.Invalidate(id);
            if (!Roles.All.Contains(r.Role)) return Bad("Invalid role.");
            var u = await db.Users.FirstOrDefaultAsync(x => x.Id == id);
            if (u is null) return Results.NotFound();
            if (u.Email == t.Actor && r.Role != Roles.Admin) return Bad("You cannot remove your own Admin role.");
            if (u.Role == Roles.Admin && r.Role != Roles.Admin && await db.Users.CountAsync(x => x.Role == Roles.Admin && !x.Disabled) <= 1)
                return Bad("At least one active Admin is required.");
            audit.Record(t.TenantId, t.Actor, "user.role.changed", u.Email, $"{u.Role} → {r.Role}");
            u.Role = r.Role;
            await db.SaveChangesAsync();
            return Results.Ok();
        });

        users.MapPost("/{id:guid}/reset-password", async (Guid id, AppDb db, TenantContext t, AuditService audit) =>
        {
            PrincipalCache.Invalidate(id);
            var u = await db.Users.FirstOrDefaultAsync(x => x.Id == id);
            if (u is null) return Results.NotFound();
            var temp = PasswordHasher.Generate();
            u.PasswordHash = PasswordHasher.Hash(temp);
            u.MustChangePassword = true;
            u.LockoutUntil = null;
            u.FailedLogins = 0;
            audit.Record(t.TenantId, t.Actor, "user.password.reset", u.Email);
            await db.SaveChangesAsync();
            return Results.Ok(new { temporaryPassword = temp });
        });

        users.MapPost("/{id:guid}/disable", async (Guid id, AppDb db, TenantContext t, AuditService audit) =>
        {
            PrincipalCache.Invalidate(id);
            var u = await db.Users.FirstOrDefaultAsync(x => x.Id == id);
            if (u is null) return Results.NotFound();
            if (u.Email == t.Actor) return Bad("You cannot disable yourself.");
            u.Disabled = true;
            audit.Record(t.TenantId, t.Actor, "user.disabled", u.Email);
            await db.SaveChangesAsync();
            return Results.Ok();
        });

        users.MapPost("/{id:guid}/enable", async (Guid id, AppDb db, TenantContext t, AuditService audit) =>
        {
            var u = await db.Users.FirstOrDefaultAsync(x => x.Id == id);
            if (u is null) return Results.NotFound();
            u.Disabled = false; u.LockoutUntil = null; u.FailedLogins = 0;
            audit.Record(t.TenantId, t.Actor, "user.enabled", u.Email);
            await db.SaveChangesAsync();
            return Results.Ok();
        });

        users.MapPost("/{id:guid}/reset-mfa", async (Guid id, AppDb db, MfaService mfa, TenantContext t, AuditService audit) =>
        {
            var u = await db.Users.FirstOrDefaultAsync(x => x.Id == id);
            if (u is null) return Results.NotFound();
            mfa.Disable(u);
            audit.Record(t.TenantId, t.Actor, "mfa.reset", u.Email);
            await db.SaveChangesAsync();
            return Results.Ok();
        });
    }

    public sealed record NotifyPref(bool Enabled);

    public static string MaskEmail(string email)
    {
        var at = email.IndexOf('@');
        return at <= 1 ? email : email[0] + new string('•', Math.Min(6, at - 1)) + email[at..];
    }
}
