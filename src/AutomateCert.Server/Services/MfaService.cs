using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AutomateCert.Server.Data;
using AutomateCert.Server.Security;
using QRCoder;

namespace AutomateCert.Server.Services;

/// <summary>Second factor: authenticator app (TOTP, RFC 6238), email one-time code and single-use recovery codes.</summary>
public sealed class MfaService(SecretProtector secrets, EmailSender email)
{
    private const int RecoveryCodeCount = 10;
    private static readonly TimeSpan EmailCodeLifetime = TimeSpan.FromMinutes(10);

    public sealed record TotpEnrollment(string Secret, string Uri, string QrDataUri);

    public TotpEnrollment BeginTotp(User user)
    {
        var secret = Totp.NewSecret();
        user.PendingTotpSecretProtected = secrets.Protect(secret);
        var uri = Totp.BuildUri("AutomateCert", user.Email, secret);
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(uri, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(6);
        return new TotpEnrollment(secret, uri, "data:image/png;base64," + Convert.ToBase64String(png));
    }

    public bool ConfirmTotp(User user, string code)
    {
        if (user.PendingTotpSecretProtected is null) return false;
        var secret = secrets.Unprotect(user.PendingTotpSecretProtected);
        var step = Totp.Verify(secret, code, 0);
        if (step is null) return false;
        user.TotpSecretProtected = user.PendingTotpSecretProtected;
        user.PendingTotpSecretProtected = null;
        user.LastTotpStep = step.Value;
        user.MfaEnabled = true;
        user.MfaMethod = MfaMethods.Totp;
        return true;
    }

    public async Task SendEmailCodeAsync(User user, CancellationToken ct)
    {
        if (!email.IsConfigured) throw new InvalidOperationException("Email is not configured on this server. Use an authenticator app.");
        if (user.EmailOtpSentAt > DateTimeOffset.UtcNow.AddSeconds(-30))
            throw new InvalidOperationException("Please wait 30 seconds before requesting another code.");
        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        user.EmailOtpHash = HashEmailCode(user, code);
        user.EmailOtpExpires = DateTimeOffset.UtcNow + EmailCodeLifetime;
        user.EmailOtpAttempts = 0;
        user.EmailOtpSentAt = DateTimeOffset.UtcNow;
        var alert = new AlertMessage(Severity.Info, "Your sign-in code",
            $"Use this code to sign in to AutomateCert: {code}. It expires in 10 minutes. If you did not try to sign in, change your password.",
            [new("Code", code), new("Account", user.Email)]);
        var (html, text) = EmailTemplates.Render(alert, null);
        try
        {
            await email.SendAsync([user.Email], $"AutomateCert sign-in code: {code}", html, text, ct);
        }
        catch (Exception ex) when (ex is not InvalidOperationException and not OperationCanceledException)
        {
            user.EmailOtpHash = null;
            user.EmailOtpExpires = null;
            user.EmailOtpSentAt = null;
            throw new InvalidOperationException($"Could not send the email code: {EmailSender.Explain(ex)} Use an authenticator app instead.", ex);
        }
    }

    public bool VerifyEmailCode(User user, string? code)
    {
        var digits = new string((code ?? "").Where(char.IsDigit).ToArray());
        if (user.EmailOtpHash is null || user.EmailOtpExpires < DateTimeOffset.UtcNow || user.EmailOtpAttempts >= 5 || digits.Length != 6)
        {
            user.EmailOtpAttempts++;
            return false;
        }
        if (!CryptographicOperations.FixedTimeEquals(HashEmailCode(user, digits), user.EmailOtpHash))
        {
            user.EmailOtpAttempts++;
            return false;
        }
        user.EmailOtpHash = null;
        user.EmailOtpExpires = null;
        user.EmailOtpAttempts = 0;
        return true;
    }

    public bool ConfirmEmail(User user, string code)
    {
        if (!VerifyEmailCode(user, code)) return false;
        user.MfaEnabled = true;
        user.MfaMethod = MfaMethods.Email;
        user.TotpSecretProtected = null;
        user.PendingTotpSecretProtected = null;
        return true;
    }

    public bool VerifySecondFactor(User user, string? code)
    {
        var input = (code ?? "").Trim();
        if (input.Length >= 10 && input.Any(char.IsLetter)) return UseRecoveryCode(user, input);
        if (user.MfaMethod == MfaMethods.Totp && user.TotpSecretProtected is not null)
        {
            var step = Totp.Verify(secrets.Unprotect(user.TotpSecretProtected), input, user.LastTotpStep);
            if (step is null) return false;
            user.LastTotpStep = step.Value;
            return true;
        }
        if (user.MfaMethod == MfaMethods.Email) return VerifyEmailCode(user, input);
        return false;
    }

    public List<string> GenerateRecoveryCodes(User user)
    {
        var codes = new List<string>();
        var hashes = new List<string>();
        for (var i = 0; i < RecoveryCodeCount; i++)
        {
            var raw = Base32.Encode(RandomNumberGenerator.GetBytes(7))[..10];
            codes.Add(raw[..5] + "-" + raw[5..]);
            hashes.Add(Convert.ToBase64String(HashRecovery(raw)));
        }
        user.RecoveryCodes = JsonSerializer.Serialize(hashes);
        return codes;
    }

    public int RecoveryCodesLeft(User user) => (JsonSerializer.Deserialize<List<string>>(user.RecoveryCodes) ?? []).Count;

    public void Disable(User user)
    {
        user.MfaEnabled = false;
        user.MfaMethod = MfaMethods.None;
        user.TotpSecretProtected = null;
        user.PendingTotpSecretProtected = null;
        user.RecoveryCodes = "[]";
        user.EmailOtpHash = null;
        user.LastTotpStep = 0;
    }

    private bool UseRecoveryCode(User user, string input)
    {
        var normalized = new string(input.ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());
        if (normalized.Length != 10) return false;
        var hash = Convert.ToBase64String(HashRecovery(normalized));
        var list = JsonSerializer.Deserialize<List<string>>(user.RecoveryCodes) ?? [];
        var match = list.FirstOrDefault(h => CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(h), Encoding.ASCII.GetBytes(hash)));
        if (match is null) return false;
        list.Remove(match);
        user.RecoveryCodes = JsonSerializer.Serialize(list);
        return true;
    }

    private static byte[] HashRecovery(string normalized) => SHA256.HashData(Encoding.UTF8.GetBytes("ac-recovery:" + normalized));
    private static byte[] HashEmailCode(User user, string code) => SHA256.HashData(Encoding.UTF8.GetBytes($"ac-email-otp:{user.Id}:{code}"));
}
