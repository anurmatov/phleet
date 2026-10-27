namespace Fleet.Memory.Configuration;

public sealed class FleetIdentityOptions
{
    public const string Section = "FleetIdentity";
    public bool Enabled { get; set; }
    public int VerifiedPort { get; set; } = 3101;
    // Explicit staged set. Legacy query attribution remains untrusted and requires network isolation.
    public string ProtectedAgents { get; set; } = "";
    public bool IsProtected(string agent) => ProtectedAgents.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .Contains(agent, StringComparer.OrdinalIgnoreCase);
    public string Issuer { get; set; } = "";
    public string Audience { get; set; } = "memory:/mcp";
    public string KeyId { get; set; } = "";
    public string AssertionPublicKeyPath { get; set; } = "";
    public string IngressCaPath { get; set; } = "";
    public string IngressDnsName { get; set; } = "";
    public string ServerCertificatePath { get; set; } = "";
    public string ServerKeyPath { get; set; } = "";
    public string ConnectionString { get; set; } = "";
}
