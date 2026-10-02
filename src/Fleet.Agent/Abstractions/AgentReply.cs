using System.Net;

namespace Fleet.Agent.Abstractions;

/// <summary>
/// A turn's answer as the task manager hands it to a sink (#394): the body, with the footer kept
/// apart so a sink can render both exactly as before and journal the body alone.
/// </summary>
/// <param name="Content">
/// The answer as the chat shows it: the task prefix, the agent's text and any incomplete marker,
/// with its <c>[IMAGE:]</c> and <c>[reply_to:]</c> markers still in it.
/// </param>
/// <param name="StatsSuffix">
/// The stats line with its leading newline (<c>"\n" + ExecutionStats.Format()</c>), or empty when
/// stats are off. Never journaled.
/// </param>
/// <param name="ToolBlockHtml">
/// The tool-call block (<c>ExecutionStats.FormatToolBlock()</c>), or empty. Never journaled. When
/// present the reply goes out as one pre-formatted HTML text (Path T); otherwise through the
/// formatting-mode render (Path S).
/// </param>
public sealed record AgentReply(string Content, string StatsSuffix = "", string ToolBlockHtml = "")
{
    /// <summary>
    /// The bold <c>&lt;b&gt;Name:&lt;/b&gt;\n</c> header Path T puts first when <c>PrefixMessages</c>
    /// is on, else empty. It is the task manager's to compute, as it always was. Path S ignores it:
    /// the transport adds its own per-mode prefix there.
    /// </summary>
    public string HtmlPrefix { get; init; } = "";

    /// <summary>True when the reply goes out as one pre-formatted HTML text (Path T).</summary>
    public bool HasToolBlock => ToolBlockHtml.Length > 0;

    /// <summary>Path S: the text the sink renders, exactly as the task manager composed it before #394.</summary>
    public string ComposeText() => Content + StatsSuffix;

    /// <summary>Path T: the HTML the sink sends, exactly as the task manager composed it before #394.</summary>
    public string ComposeHtml() => HtmlBody() + StatsSuffix + ToolBlockHtml;

    /// <summary>Path T's body alone: the header and the encoded content, without the footer.</summary>
    public string HtmlBody() => HtmlPrefix + WebUtility.HtmlEncode(Content);
}
