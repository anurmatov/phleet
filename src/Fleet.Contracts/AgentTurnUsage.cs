using System.Text.Json.Serialization;

namespace Fleet.Contracts;

/// <summary>
/// One provider turn's token usage, as an agent publishes it on
/// <see cref="OrchestratorBroker.Exchange"/> under <see cref="OrchestratorBroker.Usage"/> and as the
/// orchestrator forwards it, unchanged, inside the usage receiver's <c>{"rows":[row]}</c> body.
/// </summary>
/// <remarks>
/// <para>
/// snake_case, with every null left out: a missing measure means the provider did not report it, a
/// <c>0</c> means it reported zero. The agent serializes a row once and nothing re-serializes it, so a
/// replay is byte-identical, including the receiver's deduplication key
/// (<c>agent, provider, model, turn_started_at, turn_id</c>).
/// </para>
/// <para>
/// Metadata only: no message text, tool argument or output ever goes in a row. Timestamps are UTC
/// with milliseconds (<see cref="TimestampFormat"/>). The token and cost members are this turn's
/// share; the <c>cum_*</c> members are the provider's own session totals, copied as reported.
/// </para>
/// </remarks>
public sealed record AgentTurnUsage
{
    /// <summary>The format of <see cref="TurnStartedAt"/> and <see cref="TurnEndedAt"/>.</summary>
    public const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    [JsonPropertyName("turn_id")] public required string TurnId { get; init; }
    [JsonPropertyName("agent")] public required string Agent { get; init; }
    [JsonPropertyName("provider")] public required string Provider { get; init; }
    [JsonPropertyName("model")] public required string Model { get; init; }

    [JsonPropertyName("session_id"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SessionId { get; init; }

    [JsonPropertyName("input_tokens"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? InputTokens { get; init; }

    [JsonPropertyName("output_tokens"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? OutputTokens { get; init; }

    [JsonPropertyName("cache_read_tokens"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? CacheReadTokens { get; init; }

    [JsonPropertyName("cache_creation_tokens"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? CacheCreationTokens { get; init; }

    [JsonPropertyName("cost_usd"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? CostUsd { get; init; }

    [JsonPropertyName("cum_input_tokens"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? CumInputTokens { get; init; }

    [JsonPropertyName("cum_output_tokens"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? CumOutputTokens { get; init; }

    [JsonPropertyName("cum_cache_read_tokens"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? CumCacheReadTokens { get; init; }

    [JsonPropertyName("cum_cache_creation_tokens"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? CumCacheCreationTokens { get; init; }

    [JsonPropertyName("cum_cost_usd"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? CumCostUsd { get; init; }

    /// <summary>
    /// True when the token and cost members are not a difference from an earlier turn of the same
    /// session: the first turn seen, or a session, process or model change, or a counter that went
    /// down. A reader keeps such a row out of every sum.
    /// </summary>
    [JsonPropertyName("delta_reset")] public bool DeltaReset { get; init; }

    [JsonPropertyName("context_window"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? ContextWindow { get; init; }

    [JsonPropertyName("num_turns"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? NumTurns { get; init; }

    [JsonPropertyName("duration_ms"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? DurationMs { get; init; }

    [JsonPropertyName("workflow_id"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WorkflowId { get; init; }

    [JsonPropertyName("workflow_run_id"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WorkflowRunId { get; init; }

    [JsonPropertyName("turn_started_at")] public required string TurnStartedAt { get; init; }
    [JsonPropertyName("turn_ended_at")] public required string TurnEndedAt { get; init; }

    /// <summary>The largest serialized row an agent sends and the orchestrator accepts.</summary>
    public const int MaxBytes = 16 * 1024;
}
