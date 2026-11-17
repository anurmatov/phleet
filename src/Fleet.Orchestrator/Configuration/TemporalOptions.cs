using Fleet.Shared;

namespace Fleet.Orchestrator.Configuration;

public sealed class TemporalOptions
{
    public const string Section = "Temporal";

    /// <summary><see cref="BearerToken"/> as configuration and refusal messages name it.</summary>
    public const string BearerTokenSetting = Section + ":" + TemporalBearerToken.SettingName;

    /// <summary>Temporal server address (host:port). Empty = poller disabled.</summary>
    public string Address { get; set; } = "";

    /// <summary>Namespaces to query for running workflows.</summary>
    public List<string> Namespaces { get; set; } = [];

    /// <summary>How often to poll Temporal for running workflows (seconds).</summary>
    public int PollIntervalSeconds { get; set; } = 15;

    /// <summary>
    /// Optional bearer token for a Temporal that checks one (<c>Temporal:BearerToken</c>). Empty
    /// (the default): no credential is sent, as before. The token alone, without "Bearer ". Used
    /// only by <c>TemporalConnect</c>, the one place this service constructs a client; read once,
    /// so a replaced token takes a restart.
    /// </summary>
    public string BearerToken { get; set; } = "";
}
