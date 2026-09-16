using System.Text.RegularExpressions;
using Fleet.Protocol;
using Fleet.Shared;

namespace Fleet.Agent.Services;

/// <summary>
/// Strips runtime control markers and local paths from every client-bound text field (D12).
///
/// These markers are part of the Telegram contract, not user content, and several carry local
/// filesystem paths. <c>[IMAGE:/workspace/attachments/…]</c> in particular would leak the
/// attachment directory layout to a client that has no way to fetch the bytes anyway.
/// </summary>
public static partial class ProtocolSanitizer
{
    // AgentTransport strips these on the Telegram side; the event path strips them too, and
    // strips the [IMAGE:...] PATH with them rather than merely detaching the marker.
    [GeneratedRegex(@"\[IMAGE:[^\]]*\]", RegexOptions.IgnoreCase)]
    private static partial Regex ImageMarker();

    [GeneratedRegex(@"\[reply_to:\s*-?\d+\]", RegexOptions.IgnoreCase)]
    private static partial Regex ReplyToMarker();

    [GeneratedRegex(@"\[TASK_FAILED:[^\]]*\]", RegexOptions.IgnoreCase)]
    private static partial Regex TaskFailedMarker();

    /// <summary>
    /// Remove control markers and redact any occurrence of the configured attachment directory.
    /// </summary>
    /// <param name="text">Text destined for a client event.</param>
    /// <param name="attachmentDirectory">
    /// The configured attachment directory prefix, redacted wherever it appears. Optional because
    /// tests and non-Telegram callers may not have one configured.
    /// </param>
    public static string Sanitize(string? text, string? attachmentDirectory = null)
    {
        if (string.IsNullOrEmpty(text))
            return "";

        var result = ImageMarker().Replace(text, "");
        result = ReplyToMarker().Replace(result, "");
        result = TaskFailedMarker().Replace(result, "");

        if (!string.IsNullOrWhiteSpace(attachmentDirectory))
            result = result.Replace(attachmentDirectory, "[redacted]", StringComparison.OrdinalIgnoreCase);

        return result.Trim();
    }

    /// <summary>
    /// Sanitize and enforce a UTF-16 bound, reporting whether the text was cut.
    ///
    /// Truncation goes through the shared surrogate-safe helper rather than a local
    /// <c>Substring</c> loop — splitting a surrogate pair here has already been a shipped bug once
    /// in this codebase (D18).
    /// </summary>
    public static (string Text, bool Truncated) SanitizeAndBound(
        string? text, int maxChars, string? attachmentDirectory = null)
    {
        var sanitized = Sanitize(text, attachmentDirectory);
        if (sanitized.Length <= maxChars)
            return (sanitized, false);

        var cut = TextTruncation.SafeCutIndex(sanitized, maxChars);
        return (sanitized[..cut], true);
    }

    /// <summary>
    /// Bound a tool name. Only the NAME leaves the runtime — the Telegram path appends truncated
    /// tool ARGUMENTS to its progress string and that composite must never be reused here
    /// (Constraint 4).
    /// </summary>
    public static string BoundToolName(string? toolName)
    {
        if (string.IsNullOrEmpty(toolName))
            return "";
        if (toolName.Length <= ProtocolLimits.MaxToolNameChars)
            return toolName;
        var cut = TextTruncation.SafeCutIndex(toolName, ProtocolLimits.MaxToolNameChars);
        return toolName[..cut];
    }

    /// <summary>True when the text is the internal IDLE contract marker.</summary>
    public static bool IsIdleMarker(string? text) =>
        text is not null && text.Trim().Equals("IDLE", StringComparison.OrdinalIgnoreCase);
}
