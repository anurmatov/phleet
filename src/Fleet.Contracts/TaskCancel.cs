namespace Fleet.Contracts;

/// <summary>
/// The names on the path a workflow's cancel takes: temporal bridge →
/// <c>POST /api/agents/{name}/cancel/{taskId}</c> on the orchestrator → <c>POST /cancel/{taskId}</c>
/// on the agent, and the agent's answer back the same way.
/// </summary>
/// <remarks>
/// A task id is reused by a later attempt of the same workflow step, so a durable delegation's
/// cancel also names the execution it is for. The orchestrator passes the parameter on and returns
/// the agent's answer unread; the agent decides whether the cancel applies, and the bridge reads
/// what it decided. A request without the parameter is forwarded and answered as it was before the
/// parameter existed.
/// </remarks>
public static class TaskCancel
{
    /// <summary>
    /// The optional query parameter that carries <see cref="RelayMessage.ExecutionId"/> on both
    /// hops. A query parameter, so an orchestrator or an agent that does not know it ignores it and
    /// matches by task id.
    /// </summary>
    public const string ExecutionIdParameter = "executionId";

    /// <summary>
    /// The <c>outcome</c> member of the agent's answer to a cancel that named an execution. The
    /// answer also echoes the id as <c>executionId</c>, which is how the bridge knows the agent
    /// compared it: an agent from before the id answers without either.
    /// </summary>
    public static class Outcome
    {
        /// <summary>The task was held under that execution id and is stopped, removed or never to start.</summary>
        public const string Cancelled = "cancelled";

        /// <summary>No task is held under the task id: it has finished, or it never arrived.</summary>
        public const string NotFound = "not_found";

        /// <summary>The task id is held by a different execution, which was left alone.</summary>
        public const string OtherExecution = "other_execution";

        /// <summary>The task id is held by a task whose directive named no execution; left alone.</summary>
        public const string NoExecutionId = "no_execution_id";
    }
}
