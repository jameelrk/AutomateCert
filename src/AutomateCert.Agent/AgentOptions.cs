namespace AutomateCert.Agent;

public sealed class AgentOptions
{
    public string ServerUrl { get; set; } = "";
    public string? EnrollmentToken { get; set; }
    /// <summary>Optional SHA-1 thumbprint pin for the console TLS certificate (private PKI / self-signed console).</summary>
    public string? ServerCertificateThumbprint { get; set; }
    public int PollSeconds { get; set; } = 30;
    public int InventoryHours { get; set; } = 6;
    public string DataDirectory { get; set; } = @"C:\ProgramData\AutomateCert\Agent";
}
