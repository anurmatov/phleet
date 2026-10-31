using System.ComponentModel;
using Fleet.Orchestrator.Services;
using ModelContextProtocol.Server;

namespace Fleet.Orchestrator.Tools;

[McpServerToolType]
public sealed class AgentLogsTool(AgentRegistry registry, DockerService docker, StockRuntimeMutationHold runtimeHold, StockOpenShellRuntime? managed = null)
{
    [McpServerTool(Name = "agent_logs")]
    [Description("Tail recent container logs for a fleet agent. Returns the last N lines (default 100, max 500).")]
    public async Task<string> GetAgentLogsAsync(
        [Description("Agent name (e.g. my-agent)")] string name,
        [Description("Number of log lines to tail (default 100, max 500)")] int tail = 100)
    {
        if (runtimeHold.Denies(name)) return StockRuntimeMutationHold.Reason;
        (string Desired, string Applied)? kinds = null;
        if (managed is { Enabled: true })
        {
            try { kinds = await managed.KindsAsync(name, default); }
            catch (Exception ex) when (ex is System.Data.Common.DbException or InvalidOperationException) { return "runtime_state_unavailable"; }
        }
        if (kinds is { Applied: "openshell" })
        {
            var (result, text) = await managed!.LogsAsync(name, tail, default);
            return result.Ok ? text ?? "" : result.Message;
        }
        if (await runtimeHold.DescribeAppliedFaultAsync(name) is { } appliedFault) return appliedFault;

        tail = Math.Clamp(tail, 1, 500);

        var agent = registry.Get(name);
        var containerName = agent?.ContainerName ?? name;

        var logs = await docker.GetContainerLogsAsync(containerName, tail);
        if (logs is null)
            return $"Could not fetch logs for agent '{name}' (container: {containerName}). Docker socket may be unavailable.";

        return $"## Logs: {name} (container: {containerName}, last {tail} lines)\n\n{logs}";
    }
}
