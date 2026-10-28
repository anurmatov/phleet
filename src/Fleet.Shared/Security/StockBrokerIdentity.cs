using RabbitMQ.Client;

namespace Fleet.Shared.Security;

/// <summary>
/// Native AMQP identity uses RabbitMQ's validated user-id, not payload Sender or a caller header.
/// One isolated vhost and distinct non-impersonator principals are required per enrollment epoch.
/// This does not create broker users, permissions, credentials or a stock runtime.
/// </summary>
public sealed class StockBrokerIdentity
{
    private readonly string _authorityFile;
    private readonly string _passwordFile;
    private readonly string _role;
    private readonly Guid _epoch;
    private readonly string _agent;
    private readonly string _sandbox;
    private readonly string _host;
    private readonly string _memoryHost;
    private readonly string _policy;
    private readonly string _stockCommit;

    public StockBrokerIdentity(string authorityFile, string passwordFile, string role)
    {
        if (!Path.IsPathFullyQualified(authorityFile) || !Path.IsPathFullyQualified(passwordFile)
            || role is not ("workload" or "control" or "observer")) throw new StockIdentityDenied("broker_configuration");
        _authorityFile = authorityFile; _passwordFile = passwordFile; _role = role;
        var record = StockEnrollmentAuthority.Load(authorityFile);
        if (Uri.CheckHostName(record.BrokerHost) != UriHostNameType.Dns
            || record.Agent != record.Agent.ToLowerInvariant()) throw new StockIdentityDenied("broker_configuration");
        _epoch = record.Epoch; _agent = record.Agent; _sandbox = record.SandboxId; _host = record.BrokerHost;
        _memoryHost = record.MemoryHost;
        _policy = record.PolicySha256; _stockCommit = record.StockCommit;
    }

    public static StockBrokerIdentity? Optional(string? authorityFile, string? passwordFile, string role)
        => string.IsNullOrEmpty(authorityFile) && string.IsNullOrEmpty(passwordFile) ? null
            : new(authorityFile ?? "", passwordFile ?? "", role);

    public string VirtualHost => "stock-" + _epoch.ToString("N");
    public string Principal(string role) => role is "workload" or "control" or "observer"
        ? VirtualHost + "-" + role : throw new StockIdentityDenied("broker_role");
    public string Agent => _agent;
    public string BrokerHost => _host;

    public void Current()
    {
        var record = StockEnrollmentAuthority.Load(_authorityFile);
        if (record.Epoch != _epoch || record.Agent != _agent || record.SandboxId != _sandbox || record.BrokerHost != _host
            || record.PolicySha256 != _policy || record.StockCommit != _stockCommit || record.MemoryHost != _memoryHost)
            throw new StockIdentityDenied("broker_enrollment_changed");
    }

    public void RequireBinding(StockEnrollmentAuthority expected)
    {
        Current();
        if (expected.Epoch != _epoch || expected.Agent != _agent || expected.SandboxId != _sandbox || expected.BrokerHost != _host
            || expected.PolicySha256 != _policy || expected.StockCommit != _stockCommit || expected.MemoryHost != _memoryHost)
            throw new StockIdentityDenied("broker_enrollment_changed");
    }

    public void Configure(ConnectionFactory factory)
    {
        Current();
        if (factory.HostName != _host) throw new StockIdentityDenied("broker_target");
        string password;
        try { password = StockEnrollmentAuthority.ReadText(_passwordFile).TrimEnd('\n', '\r'); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { throw new StockIdentityDenied("broker_credential_unavailable"); }
        if (password.Length is < 1 or > 4096 || password.Contains('\0') || password.Contains('\n') || password.Contains('\r'))
            throw new StockIdentityDenied("broker_credential_invalid");
        // This bounded profile requires an isolated operator-trusted private network.
        // Broker credentials and AMQP messages are not encrypted on this transport.
        factory.Port = 5672;
        factory.UserName = Principal(_role);
        factory.Password = password;
        factory.VirtualHost = VirtualHost;
        factory.Ssl = new SslOption { Enabled = false };
        // A reconnected consumer must not quietly resume under changed/revoked enrollment.
        // Failure stays held for manual reconciliation; no new stock reconnect loop is added.
        factory.AutomaticRecoveryEnabled = false;
        factory.TopologyRecoveryEnabled = false;
    }

    public BasicProperties Properties()
    {
        Current();
        return new BasicProperties { DeliveryMode = DeliveryModes.Persistent, UserId = Principal(_role) };
    }

    public void RequireDelivery(string? validatedUser, string role, string exchange, string expectedExchange,
        string routingKey, string expectedRoutingKey)
    {
        Current();
        if (validatedUser != Principal(role) || exchange != expectedExchange || routingKey != expectedRoutingKey)
            throw new StockIdentityDenied("broker_identity_denied");
    }
}
