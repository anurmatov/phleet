namespace Fleet.Orchestrator.Endpoints;

/// <summary>
/// The call <c>POST /api/agents/{name}/cancel/{taskId}</c> makes to the agent, and what the route
/// answers with.
///
/// <para>
/// The route is the middle of the path a workflow's cancel takes: temporal bridge → here → the
/// agent's own <c>/cancel/{taskId}</c>. It waits for the agent's answer before it gives its own,
/// so a caller that gets a 2xx knows the agent has dealt with the cancel. The bridge relies on
/// that order.
/// </para>
///
/// <para>
/// A task id is reused by a later attempt of the same workflow step, so a durable delegation's
/// cancel also names the execution it is for, in the optional query parameter
/// <see cref="ExecutionIdParameter"/>. It is passed to the agent unchanged and the agent's answer
/// is returned as it came: the agent decides whether the cancel applies, and the bridge reads
/// what it decided. A request without the parameter is forwarded and answered exactly as before.
/// </para>
/// </summary>
public static class AgentTaskCancel
{
    public const string ExecutionIdParameter = "executionId";

    /// <summary>How long the agent is given. The bridge's hold on an unanswered cancel is sized from this.</summary>
    public static readonly TimeSpan AgentTimeout = TimeSpan.FromSeconds(10);

    public static string AgentUrl(string agentBaseUrl, string taskId, string? executionId)
    {
        var url = $"{agentBaseUrl}/cancel/{Uri.EscapeDataString(taskId)}";
        return string.IsNullOrEmpty(executionId)
            ? url
            : $"{url}?{ExecutionIdParameter}={Uri.EscapeDataString(executionId)}";
    }

    public static async Task<IResult> ForwardAsync(
        HttpClient client, string agentBaseUrl, string agentName, string taskId, string? executionId)
    {
        var agentUrl = AgentUrl(agentBaseUrl, taskId, executionId);

        try
        {
            client.Timeout = AgentTimeout;
            var response = await client.PostAsync(agentUrl, null);

            // Named an execution: the agent's own answer says what became of it (cancelled, not
            // found, another execution's). An agent from before the id answers without it, and
            // the caller can tell.
            if (response.IsSuccessStatusCode && !string.IsNullOrEmpty(executionId))
                return Results.Content(await response.Content.ReadAsStringAsync(), "application/json");

            return response.IsSuccessStatusCode
                ? Results.Ok(new { message = $"Cancel requested for task '{taskId}' on agent '{agentName}'" })
                : Results.Problem($"Agent returned {(int)response.StatusCode} — cancel may have failed");
        }
        catch (Exception ex)
        {
            return Results.Problem($"Failed to reach agent '{agentName}' at {agentUrl}: {ex.Message}");
        }
    }
}
