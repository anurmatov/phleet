using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using ModelContextProtocol.Protocol;
namespace Fleet.Agent.Services.MessageCopy;

[McpServerToolType]
public sealed class MessageCopyTools(MessageCopyCoordinator coordinator)
{
    internal McpServerTool CreateTool()
    {
        var tool = McpServerTool.Create(typeof(MessageCopyTools).GetMethod(nameof(CopyMessageAsync))!, this);
        var schema = JsonNode.Parse(tool.ProtocolTool.InputSchema.GetRawText())!.AsObject();
        schema["additionalProperties"] = false;
        tool.ProtocolTool.InputSchema = JsonSerializer.SerializeToElement(schema);
        return tool;
    }

    [McpServerTool(Name = "copy_message", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true)]
    [Description("Copy one message from the current private human request to an allowlisted chat using this bot. Requires the requester to tap the runtime's Copy button. Never retry an ambiguous result; ask the person to check the destination first. No content or copy is journaled.")]
    public Task<string> CopyMessageAsync(int message_id, long to_chat_id,
        RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default)
    {
        // The SDK publishes schemas but does not validate additionalProperties at invocation.
        // Check the actual MCP arguments before entering the coordinator or making any API call.
        if (request.Params?.Arguments?.Keys.Any(key => key is not ("message_id" or "to_chat_id")) == true)
            throw new ArgumentException("invalid_argument: only message_id and to_chat_id are accepted");
        return coordinator.CopyAsync(message_id, to_chat_id, cancellationToken);
    }
}
