using System.Globalization;
using System.Text.RegularExpressions;

namespace Fleet.Journal.Client;

/// <summary>
/// <c>Journal__*</c>: the agent's side of the conversation journal (#377).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="IngestToken"/> is the enabling key. Blank means nothing is registered — no spool, no
/// drainer, no HTTP client — and the agent behaves exactly as before. Present but not shaped
/// <c>cj1.ingest.&lt;subject&gt;.&lt;mac&gt;</c> fails startup.
/// </para>
/// <para>⚠️ The token is a credential: never logged, never echoed, never a metric label.</para>
/// </remarks>
public sealed class JournalOptions
{
    public const string Section = "Journal";

    /// <summary>Most records the spool holds before a new one is dropped.</summary>
    public const int MaxSpoolRecords = 10_000;

    /// <summary>Most bytes (records plus media) the spool holds before a new record is dropped.</summary>
    public const long MaxSpoolBytes = 1L << 30;

    public string IngestToken { get; set; } = "";
    public string ReadToken { get; set; } = "";
    public bool FilesEnabled { get; set; }
    public bool SendEnabled { get; set; }
    public bool CrossChatEnabled { get; set; }
    public string CrossChatToken { get; set; } = "";

    public string BaseUrl { get; set; } = "http://fleet-comms:8083";

    /// <summary>
    /// Chat ids that are never journaled, comma-separated. Blank elements and <c>0</c> are
    /// ignored — the same rule the Comms listener applies to its own list.
    /// </summary>
    public string ExcludedChatIds { get; set; } = "";

    /// <summary>
    /// Opt-in for archived media. <c>false</c> — the default — registers no uploader, so the agent
    /// journals attachments exactly as it did before media existed.
    /// </summary>
    /// <remarks>
    /// <b>Not derived from the ingest token.</b> The listener answers <c>409 media_disabled</c> and
    /// the drainer rewrites the attachment, which works; but a deployment that has not provisioned
    /// the bucket would then make every agent hash and upload every attachment before learning that.
    /// One flag, set when the bucket exists, is the honest gate.
    /// </remarks>
    public bool MediaEnabled { get; set; }

    /// <summary>Where uploads go. Defaults to the journal's own base URL — the same listener.</summary>
    public string MediaBaseUrl { get; set; } = "";

    public bool Enabled => !string.IsNullOrWhiteSpace(IngestToken);

    private static readonly Regex TokenShape = new(
        "^cj1\\.ingest\\.[A-Za-z0-9_-]{1,128}\\.[A-Za-z0-9_-]+={0,2}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Why this configuration cannot start, or null. Never includes the token.
    /// </summary>
    public string? DescribeFault()
    {
        if (CrossChatEnabled && (!SendEnabled || !Regex.IsMatch(CrossChatToken.Trim(),
            "^cj1\\.read-cross-chat\\.[A-Za-z0-9_-]{1,128}\\.[A-Za-z0-9_-]+={0,2}$", RegexOptions.CultureInvariant)
            || CrossChatToken.Trim().Split('.')[2] != Subject))
            return "Journal:CrossChatEnabled requires send and a matching-subject cross-chat token.";
        if ((FilesEnabled || SendEnabled) && (!Enabled || !Regex.IsMatch(ReadToken.Trim(),
            "^cj1\\.read\\.[A-Za-z0-9_-]{1,128}\\.[A-Za-z0-9_-]+={0,2}$", RegexOptions.CultureInvariant)
            || ReadToken.Trim().Split('.')[2] != Subject))
            return "Journal:FilesEnabled requires capture and a matching-subject journal read token.";
        if (!Enabled) return null;

        if (!TokenShape.IsMatch(IngestToken.Trim()))
            return $"{Section}:{nameof(IngestToken)} is set but is not a journal ingest token "
                + "(expected cj1.ingest.<subject>.<mac>).";

        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return $"{Section}:{nameof(BaseUrl)} is not an absolute http:// or https:// URL.";

        if (MediaEnabled)
        {
            var mediaBase = string.IsNullOrWhiteSpace(MediaBaseUrl) ? BaseUrl : MediaBaseUrl;
            if (!Uri.TryCreate(mediaBase, UriKind.Absolute, out var media)
                || (media.Scheme != Uri.UriSchemeHttp && media.Scheme != Uri.UriSchemeHttps))
                return $"{Section}:{nameof(MediaBaseUrl)} is set to enable media but is not an absolute http:// or https:// URL.";
        }

        try
        {
            ParseExcludedChatIds(ExcludedChatIds);
        }
        catch (FormatException e)
        {
            return $"{Section}:{nameof(ExcludedChatIds)}: {e.Message}.";
        }

        return null;
    }

    /// <summary>
    /// Splits on <c>,</c> and trims. Blank elements and <c>0</c> are ignored; duplicates collapse.
    /// </summary>
    /// <exception cref="FormatException">An element that is not an integer chat id.</exception>
    public static IReadOnlySet<long> ParseExcludedChatIds(string? value)
    {
        var ids = new HashSet<long>();
        var elements = (value ?? string.Empty).Split(',');

        for (var i = 0; i < elements.Length; i++)
        {
            var element = elements[i].Trim();
            if (element.Length == 0) continue;

            if (!long.TryParse(element, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var id))
                throw new FormatException($"element {i + 1} is not an integer chat id");

            if (id != 0) ids.Add(id);
        }

        return ids;
    }

    /// <summary>The token's subject: the observer name Comms records.</summary>
    public string Subject => IngestToken.Trim().Split('.') is { Length: 4 } parts ? parts[2] : "";
}
