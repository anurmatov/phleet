using System.ComponentModel;
using Fleet.Bridge.Services;
using ModelContextProtocol.Server;

namespace Fleet.Bridge.Tools;

[McpServerToolType]
public sealed class CheckStatusTool(BridgeRelayService relay)
{
    [McpServerTool(Name = "check_acto_status")]
    [Description("Check if Acto (fleet CTO) is idle or busy. Use before sending a long question to avoid waiting on a busy agent.")]
    public async Task<string> CheckAsync(CancellationToken cancellationToken = default)
    {
        return await relay.CheckStatusAsync(cancellationToken);
    }
}
