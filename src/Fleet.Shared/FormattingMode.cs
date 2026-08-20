namespace Fleet.Shared;

/// <summary>
/// Controls how outbound Telegram messages are formatted and sent.
/// Stored as TINYINT (0/1/2) in the agents table.
/// </summary>
public enum FormattingMode : byte
{
    /// <summary>
    /// Legacy dumb-split path: raw text, no parse_mode, no HTML escaping by formatter.
    /// Byte-identical to the pre-formatter UseFormatter=false behavior.
    /// </summary>
    PlainText = 0,

    /// <summary>
    /// Formatter path: Markdown-like subset (bold, inline code, fenced code, links)
    /// converted to Telegram HTML and sent with ParseMode.Html.
    /// Byte-identical to the pre-tri-state UseFormatter=true behavior.
    /// </summary>
    LegacyHtml = 1,

    /// <summary>
    /// Rich message path: same recognized syntax as LegacyHtml, but emitted as
    /// InputRichBlock/RichText structures via sendRichMessage (Bot API 10.1+).
    /// Falls back to LegacyHtml on any sendRichMessage error, then to PlainText
    /// on any subsequent HTML error. Neither fallback silently drops the message.
    /// </summary>
    Rich = 2,
}
