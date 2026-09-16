namespace Fleet.Protocol;

/// <summary>
/// Protocol version constants (D17). <c>protocol</c> is <c>fleet.conversation.v&lt;major&gt;</c>.
/// Minor evolution is additive only — new optional payload fields, new kinds, new enum members.
/// A breaking change is a new major, and both majors must be servable through one migration window.
/// </summary>
public static class ProtocolVersion
{
    /// <summary>The current major version number.</summary>
    public const int Major = 1;

    /// <summary>The wire value of the envelope's <c>protocol</c> field for v1.</summary>
    public const string V1 = "fleet.conversation.v1";

    /// <summary>The version this build emits.</summary>
    public const string Current = V1;

    /// <summary>True when the runtime can serve the supplied protocol string.</summary>
    public static bool IsSupported(string? protocol) =>
        string.Equals(protocol, V1, StringComparison.Ordinal);
}
