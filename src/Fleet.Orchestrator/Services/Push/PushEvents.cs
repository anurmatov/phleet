using Fleet.Orchestrator.Models;

namespace Fleet.Orchestrator.Services.Push;

/// <summary>
/// Agents an operator stopped on purpose, so their silence is not reported as a failure.
/// </summary>
/// <remarks>
/// The orchestrator sees a stopped agent and a crashed one the same way: heartbeats end. The stop
/// route knows which it was, and says so here. Held in memory only: after an orchestrator restart
/// the detector starts from what it then sees and reports nothing that was already down.
/// </remarks>
public sealed class OperatorStops
{
    private readonly HashSet<string> _stopped = new(StringComparer.OrdinalIgnoreCase);

    public void MarkStopped(string agentName) { lock (_stopped) _stopped.Add(agentName); }
    public void Clear(string agentName) { lock (_stopped) _stopped.Remove(agentName); }
    public bool Contains(string agentName) { lock (_stopped) return _stopped.Contains(agentName); }
}

/// <summary>
/// Turns what the orchestrator currently sees into the events worth a notification, each once.
/// </summary>
/// <remarks>
/// <para>
/// Three events: a run reaches a gate that is waiting for a person, a run closes failed or timed
/// out, and an agent that was alive has been silent past the grace period.
/// </para>
/// <para>
/// The first observation after start-up reports nothing. It only records what is already true, so a
/// restart of the orchestrator does not re-announce every gate that has been waiting since
/// yesterday. The cost is stated rather than hidden: something that happened while the orchestrator
/// was down is not announced.
/// </para>
/// <para>Not thread-safe; one service calls it from one loop.</para>
/// </remarks>
public sealed class PushEventDetector(TimeSpan agentDownGrace, OperatorStops stops)
{
    private HashSet<string> _gates = new(StringComparer.Ordinal);
    private HashSet<string> _failures = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _silentSince = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _reportedDown = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _seenAlive = new(StringComparer.OrdinalIgnoreCase);
    private bool _seeded;

    public IReadOnlyList<PushMessage> Observe(
        IReadOnlyCollection<WorkflowSummary> running,
        IReadOnlyCollection<WorkflowSummary> recentlyFailed,
        IReadOnlyCollection<AgentState> agents,
        DateTimeOffset now)
    {
        var messages = new List<PushMessage>();

        // Gates. The set is replaced, not grown: a run that leaves a gate and comes back to it is
        // a new wait, and is announced again.
        var gates = new HashSet<string>(StringComparer.Ordinal);
        foreach (var run in running)
        {
            foreach (var signal in WaitingSignals(run))
            {
                var key = string.Join('\u001f', "gate", run.Namespace, run.WorkflowId, run.RunId, signal.Name, run.Phase, run.GateRef ?? "");
                gates.Add(key);
                if (_seeded && !_gates.Contains(key)) messages.Add(PushMessages.GateWaiting(run, signal, key));
            }
        }
        _gates = gates;

        // Failures. Only a run that failed or timed out: one that was cancelled or terminated was
        // stopped by someone, who already knows.
        var failures = new HashSet<string>(StringComparer.Ordinal);
        foreach (var run in recentlyFailed)
        {
            if (run.Status is not ("Failed" or "TimedOut")) continue;
            var key = string.Join('\u001f', "run", run.Namespace, run.WorkflowId, run.RunId);
            failures.Add(key);
            if (_seeded && !_failures.Contains(key)) messages.Add(PushMessages.RunFailed(run, key));
        }
        // Kept while the orchestrator still lists the run as recently failed, which bounds the set
        // to the query's window.
        _failures = failures;

        // Agents.
        var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var agent in agents)
        {
            listed.Add(agent.AgentName);
            var silent = !agent.IsDbOnly && agent.EffectiveStatus == "dead";
            if (!silent)
            {
                if (!agent.IsDbOnly)
                {
                    _seenAlive.Add(agent.AgentName);
                    // A heartbeat ends any "stopped on purpose" as well: it is running again.
                    stops.Clear(agent.AgentName);
                }
                _silentSince.Remove(agent.AgentName);
                _reportedDown.Remove(agent.AgentName);
                continue;
            }

            if (!_seeded)
            {
                // Already down when this started watching. Not news.
                _reportedDown.Add(agent.AgentName);
                continue;
            }
            if (_reportedDown.Contains(agent.AgentName) || stops.Contains(agent.AgentName)) continue;
            // Only an agent this process has seen alive can be said to have gone down.
            if (!_seenAlive.Contains(agent.AgentName)) continue;

            if (!_silentSince.TryGetValue(agent.AgentName, out var since))
            {
                // LastSeen is when it actually went quiet; fall back to now if it is not usable.
                since = agent.LastSeen > DateTimeOffset.MinValue && agent.LastSeen <= now ? agent.LastSeen : now;
                _silentSince[agent.AgentName] = since;
            }
            if (now - since < agentDownGrace) continue;

            _reportedDown.Add(agent.AgentName);
            messages.Add(PushMessages.AgentDown(agent, since));
        }
        foreach (var gone in _silentSince.Keys.Where(name => !listed.Contains(name)).ToList()) _silentSince.Remove(gone);
        _reportedDown.RemoveWhere(name => !listed.Contains(name));
        _seenAlive.RemoveWhere(name => !listed.Contains(name));

        _seeded = true;
        return messages;
    }

    /// <summary>
    /// The signals a run is waiting for: registered for its type, and naming its current phase. A
    /// signal with no phase list is accepted at any time and says nothing about anyone waiting.
    /// </summary>
    public static IEnumerable<SignalRegistry.SignalDef> WaitingSignals(WorkflowSummary run)
    {
        if (run.Status != "Running" || string.IsNullOrEmpty(run.Phase)) yield break;
        foreach (var signal in SignalRegistry.Get(run.WorkflowType) ?? [])
            if (signal.ValidPhases is not null && signal.ValidPhases.Contains(run.Phase, StringComparer.Ordinal))
                yield return signal;
    }
}

/// <summary>
/// The words of each notification.
/// </summary>
/// <remarks>
/// A notification passes through Apple and sits on a lock screen, so it carries only what
/// identifies the thing: a repository and a number, a workflow type, an agent's name. Never task
/// text, a comment, a failure message or an output.
/// </remarks>
public static class PushMessages
{
    public static PushMessage GateWaiting(WorkflowSummary run, SignalRegistry.SignalDef signal, string key) => new(
        Kind: "gate",
        Title: Capitalize(signal.Label) + " waiting",
        Body: Subject(run),
        Target: RunTarget(run, ("signal", signal.Name)),
        Key: key);

    public static PushMessage RunFailed(WorkflowSummary run, string key) => new(
        Kind: "run",
        Title: run.Status == "TimedOut" ? "Workflow timed out" : "Workflow failed",
        Body: Subject(run) == run.WorkflowId ? run.WorkflowType : $"{Subject(run)} · {run.WorkflowType}",
        Target: RunTarget(run),
        Key: key);

    public static PushMessage AgentDown(AgentState agent, DateTimeOffset since) => new(
        Kind: "agent",
        Title: "Agent down",
        Body: agent.AgentName,
        Target: new Dictionary<string, string> { ["agentName"] = agent.AgentName },
        // Includes when it went quiet, so a second outage of the same agent is its own notification.
        Key: string.Join('\u001f', "agent", agent.AgentName, since.ToUnixTimeSeconds().ToString()));

    /// <summary>The repository and the pull request or issue, else the workflow identifier.</summary>
    public static string Subject(WorkflowSummary run)
    {
        var parts = new List<string>(2);
        if (!string.IsNullOrWhiteSpace(run.Repo)) parts.Add(run.Repo.Trim());
        if (run.PrNumber is { } pr) parts.Add($"PR #{pr}");
        else if (run.IssueNumber is { } issue) parts.Add($"Issue #{issue}");
        return parts.Count == 0 ? run.WorkflowId : string.Join(" · ", parts);
    }

    private static Dictionary<string, string> RunTarget(WorkflowSummary run, params (string Key, string Value)[] extra)
    {
        var target = new Dictionary<string, string>
        {
            ["namespace"] = run.Namespace,
            ["workflowId"] = run.WorkflowId,
            ["runId"] = run.RunId,
        };
        foreach (var (k, v) in extra) target[k] = v;
        return target;
    }

    private static string Capitalize(string text) =>
        text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
