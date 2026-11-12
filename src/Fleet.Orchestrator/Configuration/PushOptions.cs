namespace Fleet.Orchestrator.Configuration;

/// <summary>
/// Push notifications to the first-party iOS app (off by default).
/// </summary>
/// <remarks>
/// The APNs key signs for the operator's whole Apple team, so it is configuration of this process
/// and nothing else: it is never returned by an endpoint, never logged, and never given to an agent.
/// </remarks>
public sealed class PushOptions
{
    public const string Section = "Push";

    public bool Enabled { get; set; }

    /// <summary>The <c>.p8</c> file Apple issued, as base64 of the file (or the PEM text itself).</summary>
    public string ApnsKeyBase64 { get; set; } = "";

    /// <summary>The key's ten-character identifier.</summary>
    public string ApnsKeyId { get; set; } = "";

    /// <summary>The Apple developer team identifier.</summary>
    public string ApnsTeamId { get; set; } = "";

    /// <summary>The app's bundle identifier.</summary>
    public string ApnsTopic { get; set; } = "";

    /// <summary>
    /// Apple's production host. A key restricted to the sandbox environment needs
    /// <c>api.sandbox.push.apple.com</c> instead; builds distributed through TestFlight or the App
    /// Store use production.
    /// </summary>
    public string ApnsHost { get; set; } = "api.push.apple.com";

    /// <summary>How often the fleet is looked at for something worth a notification.</summary>
    public int IntervalSeconds { get; set; } = 30;

    /// <summary>How long an agent must be silent before "down" is worth a notification.</summary>
    public int AgentDownGraceSeconds { get; set; } = 300;

    /// <summary>
    /// What is missing, or <c>null</c> when the feature can run. Names the setting, never its value.
    /// </summary>
    public string? DescribeFault()
    {
        if (string.IsNullOrWhiteSpace(ApnsKeyBase64)) return "push_apns_key_missing";
        if (string.IsNullOrWhiteSpace(ApnsKeyId)) return "push_apns_key_id_missing";
        if (string.IsNullOrWhiteSpace(ApnsTeamId)) return "push_apns_team_id_missing";
        if (string.IsNullOrWhiteSpace(ApnsTopic)) return "push_apns_topic_missing";
        if (string.IsNullOrWhiteSpace(ApnsHost) || ApnsHost.Contains('/') || ApnsHost.Contains(' ')) return "push_apns_host_invalid";
        return null;
    }

    public int EffectiveIntervalSeconds => Math.Clamp(IntervalSeconds, 10, 600);
    public int EffectiveAgentDownGraceSeconds => Math.Clamp(AgentDownGraceSeconds, 90, 86_400);
}
