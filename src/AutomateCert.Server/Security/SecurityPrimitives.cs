using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace AutomateCert.Server.Security;

/// <summary>PBKDF2-HMAC-SHA256 with 600,000 iterations (OWASP guidance), versioned format.</summary>
public static class PasswordHasher
{
    private const int Iterations = 600_000;

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2-sha256${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string stored)
    {
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2-sha256" || !int.TryParse(parts[1], out var it)) return false;
        var salt = Convert.FromBase64String(parts[2]);
        var expected = Convert.FromBase64String(parts[3]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, it, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>NIST SP 800-63B style: length over complexity, block trivially weak values.</summary>
    public static string? PolicyError(string? password, string? email = null)
    {
        if (string.IsNullOrEmpty(password) || password.Length < 12) return "Password must be at least 12 characters.";
        if (password.Length > 256) return "Password is too long.";
        if (password.Distinct().Count() < 5) return "Password is too repetitive.";
        var lower = password.ToLowerInvariant();
        string[] common = ["password", "123456", "qwerty", "automatecert", "welcome", "letmein", "admin"];
        if (common.Any(c => lower.Contains(c))) return "Password contains a common word. Use a passphrase.";
        if (!string.IsNullOrEmpty(email) && email.Contains('@') && lower.Contains(email[..email.IndexOf('@')].ToLowerInvariant()))
            return "Password must not contain your user name.";
        return null;
    }

    public static string Generate(int length = 20)
    {
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789-_!@#";
        return string.Create(length, chars, (span, c) =>
        {
            for (var i = 0; i < span.Length; i++) span[i] = c[RandomNumberGenerator.GetInt32(c.Length)];
        });
    }
}

/// <summary>High-entropy bearer secrets. Only SHA-256 hashes are persisted.</summary>
public static class Secrets
{
    public static string NewToken(int bytes = 32) => Convert.ToHexString(RandomNumberGenerator.GetBytes(bytes)).ToLowerInvariant();
    public static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));
    public static bool Matches(string token, byte[] storedHash) =>
        CryptographicOperations.FixedTimeEquals(Hash(token), storedHash);
}

/// <summary>
/// Encrypts secrets at rest (ACME account keys, EAB keys, DNS/CA API credentials, TOTP seeds, AI keys) using
/// ASP.NET Core Data Protection (AES-256-CBC + HMACSHA256). On Windows the key ring is protected with machine DPAPI.
/// </summary>
public sealed class SecretProtector(IDataProtectionProvider provider)
{
    private readonly IDataProtector _p = provider.CreateProtector("AutomateCert.Secrets.v1");
    public string Protect(string plaintext) => _p.Protect(plaintext);
    public string Unprotect(string protectedText) => _p.Unprotect(protectedText);

    public string ProtectMap(IDictionary<string, string> values) => Protect(JsonSerializer.Serialize(values));

    /// <summary>Reads an encrypted JSON map. Legacy (v1.0-v1.2) single-value secrets are returned under <paramref name="legacyKey"/>.</summary>
    public Dictionary<string, string> UnprotectMap(string? protectedText, string legacyKey = "apiToken")
    {
        if (string.IsNullOrEmpty(protectedText)) return new(StringComparer.OrdinalIgnoreCase);
        var plain = Unprotect(protectedText);
        if (plain.TrimStart().StartsWith('{'))
            return new(JsonSerializer.Deserialize<Dictionary<string, string>>(plain) ?? [], StringComparer.OrdinalIgnoreCase);
        return new(StringComparer.OrdinalIgnoreCase) { [legacyKey] = plain };
    }
}

public static class JsonMap
{
    public static Dictionary<string, string> Read(string? json) =>
        string.IsNullOrWhiteSpace(json)
            ? new(StringComparer.OrdinalIgnoreCase)
            : new(JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [], StringComparer.OrdinalIgnoreCase);

    public static string Write(IDictionary<string, string> map) => JsonSerializer.Serialize(map);
}

/// <summary>RFC 4648 Base32 without padding (authenticator-app secret format).</summary>
public static class Base32
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string Encode(ReadOnlySpan<byte> data)
    {
        var sb = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = ((buffer << 8) | b) & 0xFFFF;
            bits += 8;
            while (bits >= 5)
            {
                sb.Append(Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }
        if (bits > 0) sb.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        return sb.ToString();
    }

    public static byte[] Decode(string input)
    {
        var clean = input.Trim().TrimEnd('=').Replace(" ", "").ToUpperInvariant();
        var output = new List<byte>(clean.Length * 5 / 8);
        int buffer = 0, bits = 0;
        foreach (var c in clean)
        {
            var v = Alphabet.IndexOf(c);
            if (v < 0) throw new FormatException("Invalid Base32 character.");
            buffer = ((buffer << 5) | v) & 0xFFFF;
            bits += 5;
            if (bits >= 8)
            {
                output.Add((byte)((buffer >> (bits - 8)) & 0xFF));
                bits -= 8;
            }
        }
        return output.ToArray();
    }
}

/// <summary>RFC 6238 TOTP (HMAC-SHA1, 30-second step, 6 digits) with ±1 step tolerance and replay protection.</summary>
public static class Totp
{
    public const int StepSeconds = 30;

    public static string NewSecret() => Base32.Encode(RandomNumberGenerator.GetBytes(20));
    public static long CurrentStep() => DateTimeOffset.UtcNow.ToUnixTimeSeconds() / StepSeconds;

    public static string Compute(byte[] key, long step)
    {
        Span<byte> counter = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, step);
        var hash = HMACSHA1.HashData(key, counter);
        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binary % 1_000_000).ToString("D6");
    }

    public static long? Verify(string base32Secret, string? code, long lastUsedStep)
    {
        var digits = new string((code ?? "").Where(char.IsDigit).ToArray());
        if (digits.Length != 6) return null;
        var key = Base32.Decode(base32Secret);
        var now = CurrentStep();
        for (var delta = -1; delta <= 1; delta++)
        {
            var step = now + delta;
            if (step <= lastUsedStep) continue;
            if (CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Compute(key, step)), Encoding.ASCII.GetBytes(digits)))
                return step;
        }
        return null;
    }

    public static string BuildUri(string issuer, string account, string secret) =>
        $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(account)}" +
        $"?secret={secret}&issuer={Uri.EscapeDataString(issuer)}&algorithm=SHA1&digits=6&period={StepSeconds}";
}
