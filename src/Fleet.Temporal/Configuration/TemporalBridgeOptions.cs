namespace Fleet.Temporal.Configuration;

public sealed class TemporalBridgeOptions
{
    public const string Section = "TemporalBridge";

    /// <summary>Temporal server address (host:port).</summary>
    public string TemporalAddress { get; set; } = "temporal-server:7233";

    /// <summary>Temporal namespaces to create on startup and register workers for.</summary>
    public List<string> Namespaces { get; set; } = [];

    /// <summary>Default timeout in seconds to wait for an agent response before failing the activity.</summary>
    public int AgentTimeoutSeconds { get; set; } = 600; // 10 min

    /// <summary>Telegram group chat ID to use as the ChatId in workflow-delegated directives.</summary>
    public long GroupChatId { get; set; }

    /// <summary>
    /// Base URL of the Fleet Orchestrator REST API (e.g. http://host.docker.internal:3600).
    /// Used by DelegateToAgentActivity to cancel tasks via POST /api/agents/{name}/cancel.
    /// When empty, targeted cancellation falls back to the RabbitMQ /cancel broadcast.
    /// </summary>
    public string OrchestratorUrl { get; set; } = "";

    /// <summary>
    /// Bearer token for the Fleet Orchestrator REST API (matches Orchestrator:AuthToken on the orchestrator).
    /// Must be set when OrchestratorUrl is configured — POST endpoints require auth.
    /// Set via TEMPORALBRIDGE__ORCHESTRATORAUTHTOKEN env var (same value as ORCHESTRATOR_AUTH_TOKEN).
    /// </summary>
    public string OrchestratorAuthToken { get; set; } = "";
}
