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

    /// <summary>The most HTML the tool lines of <see cref="FormatToolBlock"/> may take, before the "..." that marks a cut.</summary>
    internal const int MaxToolBlockLength = 2000;

    /// <summary>
    /// Returns an HTML &lt;blockquote expandable&gt; block with tool calls, or empty string if none.
    /// The lines are capped at <see cref="MaxToolBlockLength"/> characters of HTML. The cap falls
    /// between two entries, so an entry is shown whole or not at all and an encoded character is
    /// never split; a "..." line stands for the entries left out. Only a first entry that is
    /// larger than the cap by itself is cut, and that cut is made in its text before encoding.
    /// </summary>
    public string FormatToolBlock()
    {
        if (ToolCalls is null or []) return "";

        var lines = new System.Text.StringBuilder();
        foreach (var call in ToolCalls)
        {
            var entry = $"{System.Net.WebUtility.HtmlEncode(call.Name)}({System.Net.WebUtility.HtmlEncode(call.Args)})";
            var separator = lines.Length == 0 ? "" : "\n";
            if (lines.Length + separator.Length + entry.Length > MaxToolBlockLength)
            {
                lines.Append(lines.Length == 0
                    ? EncodeToFit($"{call.Name}({call.Args}", MaxToolBlockLength) + "..."
                    : "\n...");
                break;
            }

            lines.Append(separator).Append(entry);
        }

        return $"\n<blockquote expandable>{lines}</blockquote>";
    }

    /// <summary>
    /// The longest prefix of <paramref name="text"/> whose HTML encoding fits in
    /// <paramref name="maxEncodedLength"/> characters, encoded. The cut is made in the text, never
    /// in its encoding, and never inside a surrogate pair.
    /// </summary>
    private static string EncodeToFit(string text, int maxEncodedLength)
    {
        var cut = Math.Min(text.Length, maxEncodedLength);
        while (cut > 0)
        {
            cut = Fleet.Shared.TextTruncation.SafeCutIndex(text, cut);
            var encoded = System.Net.WebUtility.HtmlEncode(text[..cut]);
            if (encoded.Length <= maxEncodedLength)
                return encoded;
            // One character encodes to at most ten ("&#1114111;" for a surrogate pair's two).
            cut -= Math.Max(1, (encoded.Length - maxEncodedLength) / 10);
        }

        return "";
    }

    private static string FormatTokens(int tokens)
    {
        if (tokens >= 1_000_000) return $"{tokens / 1_000_000.0:0.#}M";
        if (tokens >= 1_000) return $"{tokens / 1_000.0:0.#}k";
        return tokens.ToString();
    }
}
