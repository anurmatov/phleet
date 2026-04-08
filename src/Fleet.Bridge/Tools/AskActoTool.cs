using System.ComponentModel;
using Fleet.Bridge.Services;
using ModelContextProtocol.Server;

namespace Fleet.Bridge.Tools;

[McpServerToolType]
public sealed class AskActoTool(BridgeRelayService relay, TelegramNotifier notifier)
{
    [McpServerTool(Name = "ask_acto")]
    [Description("Send a question to Acto (fleet CTO) and wait for a synchronous response. Use this to get architectural guidance, cross-project context, or fleet knowledge.")]
    public async Task<string> AskAsync(
        [Description("The question or request for Acto")] string question,
        [Description("Your agent identifier (e.g. 'reader-agent', 'pipeline-service')")] string agent_name,
        [Description("Project context (e.g. 'kyrgyz-news', 'reading-drops')")] string? project = null,
        [Description("Max seconds to wait for response (default 300)")] int? timeout_seconds = null,
        CancellationToken cancellationToken = default)
    {
        await notifier.NotifyQuestionAsync(agent_name, project, question);
        return await relay.AskAsync(question, agent_name, project, timeout_seconds, cancellationToken);
    }
}
