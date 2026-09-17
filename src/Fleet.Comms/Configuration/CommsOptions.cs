namespace Fleet.Comms.Configuration;

/// <summary>
/// Operator configuration for the north boundary.
///
/// <para>Deliberately small, and deliberately carrying no secret, port, certificate, hostname or
/// deployment identity: this is a public repository, and those belong to the deployment (issue #292
/// scope 7). Listener addresses come from the host environment at run time, not from a file here.</para>
/// </summary>
public sealed class CommsOptions
{
    public const string SectionName = "Comms";

    /// <summary>
    /// Non-secret display label returned by <c>GET /v1/session</c>. Not an agent identity and not
    /// used for authorisation.
    /// </summary>
    public string AgentLabel { get; set; } = "assistant";
}

/// <summary>
/// The server limits <c>GET /v1/session</c> advertises, fixed by docs/first-party-api.md.
///
/// <para>Each is a single value with a unit in the contract, so each is a constant here rather than
/// configuration — a client that asked the server and got a different answer than the document
/// states would have no way to tell which one to believe.</para>
/// </summary>
public static class CommsLimits
{
    /// <summary>§5.1 catch-up default page size.</summary>
    public const int CatchUpLimitDefault = 200;

    /// <summary>§5.1 catch-up maximum page size. Above it is `unsupported_kind`, never a clamp.</summary>
    public const int CatchUpLimitMax = 1000;

    /// <summary>§4 identifier length bound, shared by all four client-chosen identifiers.</summary>
    public const int IdentifierMaxLength = 128;

    /// <summary>§9 per-connection outbound buffer, matching the in-process progress capacity.</summary>
    public const int OutboundBufferEvents = 256;
}
