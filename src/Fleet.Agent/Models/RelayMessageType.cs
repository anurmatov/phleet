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
    /// <summary>Published by the orchestrator when AllowedUserIds or AllowedGroupIds change.</summary>
    public const string ConfigUpdate = "config.update";
    /// <summary>Published by an agent to a control-plane agent when an unknown DM requests access.</summary>
    public const string AccessRequest = "access.request";
}
