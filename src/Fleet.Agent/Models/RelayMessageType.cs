namespace Fleet.Agent.Models;

public static class RelayMessageType
{
    public const string Directive = "directive";
    public const string Response = "response";
    public const string TokenUpdate = "token-update";
    public const string PartialResponse = "partial-response";
    public const string StatusCheck = "status-check";
    public const string StatusResponse = "status-response";
    public const string BridgeRequest = "bridge-request";
    public const string BridgeResponse = "bridge-response";
    public const string WorkflowSignal = "workflow-signal";
}
