namespace Fleet.Agent.Models;

/// <summary>
/// A request to attach one project's canonical full context to the turn that carries it (#347).
///
/// Resolved at intake by <see cref="Services.ProjectContextRouter"/>, carried with the message
/// through the queue, the Inbox and mid-turn injection, and rendered only at delivery by
/// <see cref="Services.ProjectContextAttacher"/>. It is never text: the attachment itself exists
/// only in the executor input, so a redelivery re-renders instead of replaying a stale prefix.
/// </summary>
/// <param name="Project">The assignment's project name, as in <c>Agent:Projects</c>.</param>
/// <param name="FullVersion">The full-context version the routing block was written for.</param>
/// <param name="SignalKind">The level that won: <c>repo</c>, <c>workflow</c> or <c>chat</c>.</param>
public sealed record ContextAttachmentRequest(string Project, int FullVersion, string SignalKind)
{
    /// <summary>
    /// Union of several messages' requests: deduped by (project, fullVersion), ordered by
    /// ordinal-ignore-case project name so a merged turn renders each block exactly once and in a
    /// stable order. Returns null when nothing was requested, so request-free agents carry nothing.
    /// </summary>
    public static IReadOnlyList<ContextAttachmentRequest>? Union(
        IEnumerable<IReadOnlyList<ContextAttachmentRequest>?> lists)
    {
        List<ContextAttachmentRequest>? union = null;
        foreach (var list in lists)
        {
            if (list is null) continue;
            foreach (var request in list)
            {
                union ??= [];
                if (!union.Any(r => r.FullVersion == request.FullVersion
                        && string.Equals(r.Project, request.Project, StringComparison.OrdinalIgnoreCase)))
                {
                    union.Add(request);
                }
            }
        }

        return union is null
            ? null
            : union
                .OrderBy(r => r.Project, StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.FullVersion)
                .ToList();
    }
}
