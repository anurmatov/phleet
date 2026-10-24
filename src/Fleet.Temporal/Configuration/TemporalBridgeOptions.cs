namespace Fleet.Temporal.Configuration;

public sealed class TemporalBridgeOptions
{
    public const string Section = "TemporalBridge";

    /// <summary>The relay queue the bridge consumes agent replies from. Never a late-result queue.</summary>
    public const string RelayQueue = "fleet.agent.temporal-bridge";

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

    /// <summary>
    /// #476: capture agent replies whose waiter is already gone. Off (the default) keeps the legacy
    /// behaviour — such a reply is rejected without requeue, i.e. discarded — and logs one startup
    /// warning. On: the reply is handed to <see cref="WorkflowOutcomeLateQueue"/> with a publisher
    /// confirm before the original is acknowledged, and stored through the orchestrator's
    /// late-results API. Startup-only (TemporalBridge__WorkflowOutcomeLateCaptureEnabled).
    /// </summary>
    public bool WorkflowOutcomeLateCaptureEnabled { get; set; }

    /// <summary>Durable private queue holding captured late replies until the orchestrator stores them.</summary>
    public string WorkflowOutcomeLateQueue { get; set; } = "fleet.workflow-outcomes.late";

    /// <summary>
    /// Durable private queue for late replies that could not be stored. It has no consumer and no
    /// replay: recovery from it is an operator action on an exact message.
    /// </summary>
    public string WorkflowOutcomeQuarantineQueue { get; set; } = "fleet.workflow-outcomes.quarantine";

    /// <summary>
    /// Null when the late-result settings are usable, otherwise why not. Only checked when capture is
    /// enabled: an invalid value fails startup rather than being silently replaced.
    /// </summary>
    public string? ValidateWorkflowOutcomeLateCapture()
    {
        if (!WorkflowOutcomeLateCaptureEnabled) return null;
        // Captured replies are stored through the orchestrator; without it every one would be
        // retried into quarantine.
        if (!Uri.TryCreate(OrchestratorUrl, UriKind.Absolute, out var orchestrator)
            || (orchestrator.Scheme != Uri.UriSchemeHttp && orchestrator.Scheme != Uri.UriSchemeHttps))
            return "TemporalBridge:OrchestratorUrl must be an absolute http(s) URL when late-result capture is enabled";
        if (string.IsNullOrWhiteSpace(OrchestratorAuthToken))
            return "TemporalBridge:OrchestratorAuthToken is required when late-result capture is enabled";
        if (string.IsNullOrWhiteSpace(WorkflowOutcomeLateQueue) || WorkflowOutcomeLateQueue != WorkflowOutcomeLateQueue.Trim())
            return "TemporalBridge:WorkflowOutcomeLateQueue must be a nonblank queue name without surrounding whitespace";
        if (string.IsNullOrWhiteSpace(WorkflowOutcomeQuarantineQueue) || WorkflowOutcomeQuarantineQueue != WorkflowOutcomeQuarantineQueue.Trim())
            return "TemporalBridge:WorkflowOutcomeQuarantineQueue must be a nonblank queue name without surrounding whitespace";
        if (string.Equals(WorkflowOutcomeLateQueue, WorkflowOutcomeQuarantineQueue, StringComparison.Ordinal))
            return "TemporalBridge:WorkflowOutcomeLateQueue and WorkflowOutcomeQuarantineQueue must differ";
        if (WorkflowOutcomeLateQueue == RelayQueue || WorkflowOutcomeQuarantineQueue == RelayQueue)
            return $"the late-result queues must not be the relay queue {RelayQueue}";
        if (WorkflowOutcomeLateQueue.StartsWith("amq.", StringComparison.Ordinal) ||
            WorkflowOutcomeQuarantineQueue.StartsWith("amq.", StringComparison.Ordinal))
            return "the late-result queues must not use the reserved amq. prefix";
        return null;
    }
}
