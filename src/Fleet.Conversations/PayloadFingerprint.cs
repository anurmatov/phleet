using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Fleet.Conversations;

/// <summary>
/// The payload fingerprint an idempotency key is bound to (#276 §4.15).
/// </summary>
/// <remarks>
/// <para>
/// The same key with the same fingerprint replays the original result; the same key with a
/// different fingerprint is a conflict. So what counts as "the same payload" is a contract, and
/// every clause below changes which user messages collide.
/// </para>
/// </remarks>
public static class PayloadFingerprint
{
    /// <summary>Unit separator, between the three fields. Not a character text can contain here.</summary>
    private const char Separator = '';

    /// <summary>
    /// <c>UTF8(TrimWhitespace(NFC(text)))</c>.
    /// </summary>
    /// <remarks>
    /// <para><b>NFC, not NFKC.</b> NFKC folds "①" to "1" and full-width forms to half-width, which
    /// would make two visibly different user messages share a fingerprint — and therefore make a
    /// genuinely different submission look like a replay of an earlier one.</para>
    /// <para><b>Interior whitespace is preserved and significant.</b> Collapsing it would make a
    /// code block and its reflowed paraphrase identical.</para>
    /// <para><b>No case folding.</b></para>
    /// </remarks>
    public static string Canonical(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var normalized = text.Normalize(NormalizationForm.FormC);

        // Leading and trailing code points where char.IsWhiteSpace is true. Trim() with no argument
        // is exactly that predicate.
        return normalized.Trim();
    }

    /// <summary>
    /// <c>SHA256( canonical(text) ‖ 0x1F ‖ replyToEventId ?? "" ‖ 0x1F ‖ conversationId )</c>.
    /// </summary>
    public static string Compute(string text, string? replyToEventId, string conversationId)
    {
        ArgumentNullException.ThrowIfNull(conversationId);

        var material = new StringBuilder()
            .Append(Canonical(text))
            .Append(Separator)
            .Append(replyToEventId ?? string.Empty)
            .Append(Separator)
            .Append(conversationId)
            .ToString();

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

    /// <summary>
    /// Formats a fingerprint for the <c>CHAR(64) ascii_bin</c> column.
    /// </summary>
    internal static string ForColumn(string fingerprint) =>
        fingerprint.Length == 64
            ? fingerprint
            : throw new ArgumentException(
                $"a payload fingerprint is 64 hex characters, got {fingerprint.Length.ToString(CultureInfo.InvariantCulture)}",
                nameof(fingerprint));
}
