namespace Fleet.Identity.Configuration;

// Startup-only custody configuration. No discovery, TOFU, generated keys or live grants.
public sealed class IdentityOptions
{
    public bool Enabled { get; set; }
    public int HookPort { get; set; } = 17441;
    public int IngressPort { get; set; } = 17443;
    public int UpstreamTimeoutSeconds { get; set; } = 120;
    public string ServerCertificatePath { get; set; } = "";
    public string ServerKeyPath { get; set; } = "";
    public string GatewayId { get; set; } = "";
    public string GatewayIssuer { get; set; } = "";
    public string GatewayAudience { get; set; } = "";
    public string GatewayJwksPath { get; set; } = "";
    public string Issuer { get; set; } = "";
    public string KeyId { get; set; } = "";
    public string AssertionPrivateKeyPath { get; set; } = "";
    public string ConnectionString { get; set; } = "";
    public string IngressHost { get; set; } = "";
    public string BootstrapReceiver { get; set; } = "";
    public string MemoryReceiver { get; set; } = "";
    public string ReceiverCaPath { get; set; } = "";
    public string ClientCertificatePath { get; set; } = "";
    public string ClientKeyPath { get; set; } = "";
    public bool HasBootstrapReceiver => Uri.TryCreate(BootstrapReceiver, UriKind.Absolute, out var receiver) && receiver.Scheme == "https"
        && receiver.AbsolutePath == "/identity/ready" && receiver.Query == "" && receiver.Fragment == "" && receiver.UserInfo == "";
    public void Validate()
    {
        if (BootstrapReceiver.Length != 0 && !HasBootstrapReceiver) throw new InvalidOperationException("bootstrap_receiver_invalid");
        if (UpstreamTimeoutSeconds is < 1 or > 600 || HookPort is < 1 or > 65535 || IngressPort is < 1 or > 65535 || HookPort == IngressPort
            || new[] { ServerCertificatePath, ServerKeyPath, GatewayId, GatewayIssuer, GatewayAudience, GatewayJwksPath,
                Issuer, KeyId, AssertionPrivateKeyPath, ConnectionString, IngressHost, ReceiverCaPath,
                ClientCertificatePath, ClientKeyPath }.Any(string.IsNullOrWhiteSpace)
            || Uri.CheckHostName(IngressHost) != UriHostNameType.Dns
            || !Uri.TryCreate(MemoryReceiver, UriKind.Absolute, out var receiver) || receiver.Scheme != "https"
            || receiver.AbsolutePath != "/mcp" || receiver.Query != "" || receiver.Fragment != "" || receiver.UserInfo != "")
            throw new InvalidOperationException("Complete pinned identity custody and receiver configuration is required.");
    }
}
