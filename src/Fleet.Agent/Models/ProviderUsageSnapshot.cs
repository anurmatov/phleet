using System.Text.Json;
using System.Text.Json.Nodes;

namespace Fleet.Agent.Models;

/// <summary>
/// What one provider reported about one terminal turn, typed and nullable, kept beside the display
/// <see cref="ExecutionStats"/> and never derived from it. A missing value stays null and a reported
/// zero stays zero; <see cref="ExecutionStats"/> cannot tell the two apart.
/// </summary>
/// <remarks>
/// <para>
/// When <see cref="IsCumulative"/> is true the four token counts and the cost are the provider's
/// running totals for the session, and <see cref="Services.UsageReporter"/> turns them into this
/// turn's share. Otherwise they are already this turn's own values (or absent) and are never
/// differenced. <see cref="ContextWindow"/>, <see cref="NumTurns"/> and <see cref="DurationMs"/> are
/// always this turn's own values.
/// </para>
/// <para>
/// <see cref="ProcessGeneration"/> counts the provider processes this executor has started; with
/// <see cref="SessionId"/> and <see cref="Model"/> it decides whether two cumulative snapshots can be
/// compared.
/// </para>
/// </remarks>
public sealed record ProviderUsageSnapshot
{
    /// <summary>The model attribution when several models served one turn.</summary>
    public const string MultipleModels = "multiple";

    /// <summary>The model attribution when neither the provider nor the configuration names one.</summary>
    public const string UnknownModel = "unknown";

    public required string Provider { get; init; }
    public required string Model { get; init; }
    public string? SessionId { get; init; }
    public long ProcessGeneration { get; init; }
    public bool IsCumulative { get; init; }

    public long? InputTokens { get; init; }
    public long? OutputTokens { get; init; }
    public long? CacheReadTokens { get; init; }
    public long? CacheCreationTokens { get; init; }
    public decimal? CostUsd { get; init; }

    public long? ContextWindow { get; init; }
    public long? NumTurns { get; init; }
    public long? DurationMs { get; init; }

    /// <summary>The reported model if any, else the configured one, else <see cref="UnknownModel"/>.</summary>
    public static string ModelOrFallback(string? reported, string? configured) =>
        !string.IsNullOrWhiteSpace(reported) ? reported
        : !string.IsNullOrWhiteSpace(configured) ? configured
        : UnknownModel;

    /// <summary>
    /// A Claude <c>result</c> event. Its <c>modelUsage</c> and <c>total_cost_usd</c> are cumulative for
    /// the process. Every model entry counts: a token component is the sum over all entries, or null
    /// when any entry leaves it out. The cost is <c>total_cost_usd</c>, else the sum of the entries'
    /// <c>costUSD</c> when every entry has one. One model names the row; several are
    /// <see cref="MultipleModels"/>, so no model is charged for another's tokens.
    /// </summary>
    public static ProviderUsageSnapshot ForClaude(
        IReadOnlyDictionary<string, object>? extensionData,
        int? numTurns,
        string? sessionId,
        string? configuredModel,
        long processGeneration)
    {
        var models = new List<JsonProperty>();
        if (extensionData is not null
            && extensionData.TryGetValue("modelUsage", out var raw)
            && raw is JsonElement { ValueKind: JsonValueKind.Object } modelUsage)
        {
            models.AddRange(modelUsage.EnumerateObject());
        }

        long? Sum(string name)
        {
            if (models.Count == 0) return null;
            long total = 0;
            foreach (var m in models)
            {
                if (ReadLong(m.Value, name) is not { } v) return null;
                total += v;
            }
            return total;
        }

        decimal? cost = extensionData is not null
            && extensionData.TryGetValue("total_cost_usd", out var totalCost)
            && totalCost is JsonElement { ValueKind: JsonValueKind.Number } tc
            && tc.TryGetDecimal(out var reportedTotal)
                ? reportedTotal
                : SumCosts(models);

        long? duration = extensionData is not null
            && extensionData.TryGetValue("duration_ms", out var durationRaw)
            && durationRaw is JsonElement d
                ? ReadLong(d)
                : null;

        return new ProviderUsageSnapshot
        {
            Provider = "claude",
            Model = models.Count switch
            {
                0 => ModelOrFallback(null, configuredModel),
                1 => ModelOrFallback(models[0].Name, configuredModel),
                _ => MultipleModels,
            },
            SessionId = string.IsNullOrWhiteSpace(sessionId) ? null : sessionId,
            ProcessGeneration = processGeneration,
            IsCumulative = true,
            InputTokens = Sum("inputTokens"),
            OutputTokens = Sum("outputTokens"),
            CacheReadTokens = Sum("cacheReadInputTokens"),
            CacheCreationTokens = Sum("cacheCreationInputTokens"),
            CostUsd = cost,
            ContextWindow = models.Count == 1 ? ReadLong(models[0].Value, "contextWindow") : null,
            NumTurns = numTurns,
            DurationMs = duration,
        };
    }

    /// <summary>
    /// A Codex terminal turn. <paramref name="total"/> is the thread's <c>tokenUsage.total</c> as last
    /// reported for this turn: cumulative for the thread, mapped as reported (raw provider-defined
    /// input, cached input, output, cache-write input). An absent <c>cacheWriteInputTokens</c> stays
    /// null. Codex reports no cost.
    /// </summary>
    public static ProviderUsageSnapshot ForCodex(
        JsonObject? total,
        long? contextWindow,
        long? durationMs,
        string? threadId,
        string? model,
        long processGeneration) => new()
    {
        Provider = "codex",
        Model = ModelOrFallback(model, null),
        SessionId = string.IsNullOrWhiteSpace(threadId) ? null : threadId,
        ProcessGeneration = processGeneration,
        IsCumulative = true,
        InputTokens = ReadLong(total, "inputTokens"),
        OutputTokens = ReadLong(total, "outputTokens"),
        CacheReadTokens = ReadLong(total, "cachedInputTokens"),
        CacheCreationTokens = ReadLong(total, "cacheWriteInputTokens"),
        CostUsd = null,
        ContextWindow = contextWindow,
        DurationMs = durationMs,
    };

    /// <summary>A Gemini turn: the CLI reports no tokens or cost here, only how long the turn took.</summary>
    public static ProviderUsageSnapshot ForGemini(string? configuredModel, long durationMs) => new()
    {
        Provider = "gemini",
        Model = ModelOrFallback(null, configuredModel),
        DurationMs = durationMs,
    };

    private static decimal? SumCosts(List<JsonProperty> models)
    {
        if (models.Count == 0) return null;
        decimal total = 0;
        foreach (var m in models)
        {
            if (!m.Value.TryGetProperty("costUSD", out var c)
                || c.ValueKind != JsonValueKind.Number
                || !c.TryGetDecimal(out var v))
                return null;
            total += v;
        }
        return total;
    }

    private static long? ReadLong(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var v) ? ReadLong(v) : null;

    private static long? ReadLong(JsonElement value) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var n) && n >= 0 ? n : null;

    private static long? ReadLong(JsonObject? obj, string name) => ReadCount(obj?[name]);

    /// <summary>A non-negative integer JSON value, or null when absent, negative or not an integer.</summary>
    internal static long? ReadCount(JsonNode? node) => node switch
    {
        JsonValue v when v.TryGetValue<long>(out var n) => n >= 0 ? n : null,
        JsonValue v when v.TryGetValue<int>(out var n) => n >= 0 ? n : null,
        _ => null,
    };
}
