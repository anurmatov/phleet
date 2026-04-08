namespace Fleet.Agent.Models;

public sealed record ToolCallEntry(string Name, string Args);

public sealed class ExecutionStats
{
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public int CacheReadTokens { get; set; }
    public int CacheCreationTokens { get; set; }
    public decimal CostUsd { get; set; }
    public int ContextWindow { get; set; }
    public int NumTurns { get; set; }
    public int DurationMs { get; set; }
    public List<ToolCallEntry>? ToolCalls { get; set; }

    public string Format()
    {
        var parts = new List<string>();

        if (InputTokens > 0 || OutputTokens > 0)
            parts.Add($"{FormatTokens(InputTokens)} in / {FormatTokens(OutputTokens)} out");

        if (CacheReadTokens > 0)
            parts.Add($"cache: {FormatTokens(CacheReadTokens)}");

        if (CostUsd > 0)
            parts.Add($"${CostUsd:0.####}");

        var totalInput = InputTokens + CacheReadTokens + CacheCreationTokens;
        if (ContextWindow > 0 && totalInput > 0)
        {
            var pct = (double)totalInput / ContextWindow * 100;
            parts.Add($"ctx: {pct:0}%");
        }

        return parts.Count > 0 ? $"({string.Join(" | ", parts)})" : "";
    }

    /// <summary>Returns an HTML &lt;blockquote expandable&gt; block with tool calls, or empty string if none.</summary>
    public string FormatToolBlock()
    {
        if (ToolCalls is null or []) return "";
        var lines = string.Join("\n", ToolCalls.Select(t =>
            $"{System.Net.WebUtility.HtmlEncode(t.Name)}({System.Net.WebUtility.HtmlEncode(t.Args)})"));
        return $"\n<blockquote expandable>{lines}</blockquote>";
    }

    private static string FormatTokens(int tokens)
    {
        if (tokens >= 1_000_000) return $"{tokens / 1_000_000.0:0.#}M";
        if (tokens >= 1_000) return $"{tokens / 1_000.0:0.#}k";
        return tokens.ToString();
    }
}
