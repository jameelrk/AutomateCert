using System.Net;
using System.Text;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace AutomateCert.Server.Services;

public sealed class SmtpOptions
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    public bool StartTls { get; set; } = true;
    public bool UseAuthentication { get; set; }
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string FromAddress { get; set; } = "";
    public string FromName { get; set; } = "AutomateCert";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Host) && !string.IsNullOrWhiteSpace(FromAddress);
}

public sealed class EmailSender(IOptions<SmtpOptions> options, ILogger<EmailSender> log)
{
    public bool IsConfigured => options.Value.IsConfigured;
    public bool UsesAuthentication => options.Value.UseAuthentication;

    public async Task SendAsync(IReadOnlyCollection<string> to, string subject, string html, string text, CancellationToken ct = default)
    {
        var o = options.Value;
        if (!o.IsConfigured) throw new InvalidOperationException("SMTP is not configured (Smtp:Host and Smtp:FromAddress).");
        if (to.Count == 0) return;

        var msg = new MimeMessage();
        msg.From.Add(new MailboxAddress(o.FromName, o.FromAddress));
        if (to.Count == 1) msg.To.Add(MailboxAddress.Parse(to.First()));
        else
        {
            msg.To.Add(new MailboxAddress(o.FromName, o.FromAddress));
            foreach (var r in to) msg.Bcc.Add(MailboxAddress.Parse(r));
        }
        msg.Subject = subject;
        msg.Body = new BodyBuilder { HtmlBody = html, TextBody = text }.ToMessageBody();

        using var client = new SmtpClient { Timeout = 30_000 };
        client.SslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13;
        var security = o.StartTls ? SecureSocketOptions.StartTls
            : o.Port == 465 ? SecureSocketOptions.SslOnConnect
            : SecureSocketOptions.Auto;
        await client.ConnectAsync(o.Host, o.Port, security, ct);
        if (o.UseAuthentication) await client.AuthenticateAsync(o.Username, o.Password, ct);
        await client.SendAsync(msg, ct);
        await client.DisconnectAsync(true, ct);
        log.LogInformation("Email '{Subject}' sent to {Count} recipient(s)", subject, to.Count);
    }

    /// <summary>Turns raw SMTP errors into an actionable sentence for admins.</summary>
    public static string Explain(Exception ex)
    {
        var m = ex.Message;
        if (m.Contains("authorised users only", StringComparison.OrdinalIgnoreCase) ||
            m.Contains("authorized users only", StringComparison.OrdinalIgnoreCase) ||
            m.Contains("relay", StringComparison.OrdinalIgnoreCase) || m.Contains("5.7.1", StringComparison.Ordinal))
            return $"{m} → The mail server refuses unauthenticated mail from this machine. Set Smtp:UseAuthentication=true with a username/password, or allow this server's public IP as an outbound sender at your mail provider.";
        if (ex is AuthenticationException || m.Contains("535", StringComparison.Ordinal))
            return $"{m} → Username or password rejected by the mail server.";
        if (ex is System.Net.Sockets.SocketException || ex is TimeoutException || m.Contains("timed out", StringComparison.OrdinalIgnoreCase))
            return $"{m} → Cannot reach the mail server. Check Smtp:Host/Port and that the outbound SMTP port is open on the firewall.";
        return m;
    }
}

public static class Severity
{
    public const string Critical = "critical";
    public const string Warning = "warning";
    public const string Info = "info";
    public const string Success = "success";
}

public sealed record AlertMessage(
    string Severity,
    string Title,
    string Summary,
    IReadOnlyList<KeyValuePair<string, string>> Rows,
    string? LinkPath = null,
    bool ForceEmail = false);

public static class EmailTemplates
{
    private static string Color(string severity) => severity switch
    {
        Severity.Critical => "#4F46E5",
        Severity.Warning => "#D97706",
        Severity.Success => "#059669",
        _ => "#334155"
    };

    public static (string Html, string Text) Render(AlertMessage a, string? baseUrl)
    {
        static string E(string? s) => WebUtility.HtmlEncode(s ?? "");
        var color = Color(a.Severity);
        var rows = new StringBuilder();
        foreach (var r in a.Rows)
            rows.Append($"<tr><td style=\"padding:9px 12px;color:#64748b;font-size:13px;border-bottom:1px solid #eef2f7;white-space:nowrap;vertical-align:top\">{E(r.Key)}</td>" +
                        $"<td style=\"padding:9px 12px;color:#0f172a;font-size:13px;border-bottom:1px solid #eef2f7\">{E(r.Value)}</td></tr>");
        var link = !string.IsNullOrWhiteSpace(baseUrl)
            ? $"<p style=\"margin:24px 0 0\"><a href=\"{E(baseUrl!.TrimEnd('/') + "/" + (a.LinkPath ?? "#/overview").TrimStart('/'))}\" style=\"background:#4F46E5;color:#fff;text-decoration:none;padding:11px 20px;border-radius:10px;font-weight:600;font-size:14px\">Open AutomateCert</a></p>"
            : "";
        var html = $"""
<!doctype html><html><body style="margin:0;background:#f4f5f8;font-family:Segoe UI,Arial,sans-serif">
<table width="100%" cellpadding="0" cellspacing="0" style="background:#f4f5f8;padding:24px 0"><tr><td align="center">
<table width="620" cellpadding="0" cellspacing="0" style="background:#ffffff;border-radius:16px;overflow:hidden;border:1px solid #e5e7eb">
<tr><td style="background:#0B1020;padding:18px 24px;color:#fff;font-size:18px;font-weight:800;letter-spacing:.5px">AutomateCert</td></tr>
<tr><td style="height:4px;background:{color}"></td></tr>
<tr><td style="padding:26px 24px">
<h1 style="margin:0 0 8px;font-size:20px;color:#0f172a">{E(a.Title)}</h1>
<p style="margin:0 0 16px;color:#334155;font-size:14px;line-height:1.55">{E(a.Summary)}</p>
{(a.Rows.Count > 0 ? $"<table width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"border:1px solid #eef2f7;border-radius:10px\">{rows}</table>" : "")}
{link}
</td></tr>
<tr><td style="padding:14px 24px;background:#f8fafc;color:#94a3b8;font-size:12px">Automated message from AutomateCert. Manage alerts under Settings → Alerts.</td></tr>
</table></td></tr></table></body></html>
""";
        var text = new StringBuilder().AppendLine(a.Title).AppendLine().AppendLine(a.Summary).AppendLine();
        foreach (var r in a.Rows) text.AppendLine($"{r.Key}: {r.Value}");
        if (!string.IsNullOrWhiteSpace(baseUrl)) text.AppendLine().AppendLine(baseUrl);
        return (html, text.ToString());
    }
}
