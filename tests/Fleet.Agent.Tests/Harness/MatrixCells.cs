using Fleet.Agent.Models;
using Fleet.Protocol;

namespace Fleet.Agent.Tests.Harness;

/// <summary>
/// Renders an observation into the fixed capability-matrix cell grammar (D6).
///
/// <para>The grammar is fixed, not stylistic: <c>ProviderCapabilityMatrixTests</c> parses the
/// committed document and every scenario asserts its observation against the parsed row, so two
/// implementers building the same scenario produce byte-identical cells.</para>
/// </summary>
internal static class MatrixCells
{
    /// <summary>The literal used for a cell with nothing in it. An L2-only row has no frames.</summary>
    public const string Empty = "—";

    /// <summary><c>+</c>-joined, backtick-quoted frame discriminators in emission order.</summary>
    public static string Frames(IEnumerable<string> discriminators)
    {
        var rendered = discriminators.Select(d => $"`{d}`").ToList();
        return rendered.Count == 0 ? Empty : string.Join(" + ", rendered);
    }

    /// <summary>
    /// <c>+</c>-joined <see cref="AgentProgress.EventType"/> values in emission order, each
    /// optionally suffixed with a parenthesised discriminator drawn from a CLOSED set:
    /// <c>sig</c>, <c>tool=&lt;name&gt;</c>, <c>final</c>, <c>err</c>, <c>exit</c>. No free text.
    /// </summary>
    public static string Progress(IEnumerable<AgentProgress> progress)
    {
        var rendered = progress.Select(RenderProgress).ToList();
        return rendered.Count == 0 ? Empty : string.Join(" + ", rendered);
    }

    private static string RenderProgress(AgentProgress p)
    {
        var discriminators = new List<string>();
        if (p.IsSignificant) discriminators.Add("sig");
        if (p.ToolName is not null) discriminators.Add($"tool={p.ToolName}");
        if (p.FinalResult is not null) discriminators.Add("final");
        if (p.IsErrorResult) discriminators.Add("err");
        if (p.IsProcessExit) discriminators.Add("exit");

        return discriminators.Count == 0
            ? $"`{p.EventType}`"
            : $"`{p.EventType}`({string.Join(",", discriminators)})";
    }

    /// <summary>
    /// Separates the two independently-ordered groups of a <c>client events</c> cell.
    /// </summary>
    public const string GroupSeparator = " ‖ ";

    /// <summary>
    /// The cell is TWO independently-ordered groups, separated by <see cref="GroupSeparator"/>:
    /// the dispatch dispositions, then the turn's own events. Within each group the order is
    /// <c>→</c>-joined <see cref="ConversationEventKind"/> wire values with payload discriminators
    /// drawn from the enum's own wire values.
    ///
    /// <para><b>Why two groups, measured the hard way.</b> D6 specified delivery order; the first
    /// revision weakened that to emission order (<c>seq</c>); CI then failed on <c>codex/S9</c>
    /// with <c>turn.final</c> holding a LOWER <c>seq</c> than the <c>submission.accepted</c> for
    /// the very submission it answered. The cause is structural: <c>StartTaskCore</c> publishes
    /// <c>turn.started</c>, hands the turn to <c>Task.Run</c>, and only THEN reports the dispatch
    /// disposition — so the turn thread can run to completion before the caller thread gets back
    /// to <c>ReportDisposition</c>. <b>A disposition is not ordered against the turn it
    /// dispatched</b>, and no amount of ordering by <c>seq</c> changes that.</para>
    ///
    /// <para>So the cell stops pretending there is one sequence. Each group IS internally ordered
    /// and deterministic — dispositions by sequential dispatch, turn events by the single turn
    /// thread, with <c>turn.started</c> ahead of them because it is published before
    /// <c>Task.Run</c>. The cell teaches the hazard instead of encoding a race.</para>
    ///
    /// <para><b>The periodic typing heartbeat is excluded entirely.</b> It is emitted at turn start
    /// and then every four seconds for as long as the turn runs, so its multiplicity is a function
    /// of elapsed time, not of provider behaviour. Each scenario asserts it separately.</para>
    /// </summary>
    public static string ClientEvents(IEnumerable<ConversationEvent> events)
    {
        var ordered = events.Where(e => !IsTypingHeartbeat(e)).OrderBy(e => e.Seq).ToList();

        var dispositions = ordered
            .Where(e => e.Kind == ConversationEventKind.SubmissionAccepted)
            .Select(RenderEvent)
            .ToList();
        var turnEvents = ordered
            .Where(e => e.Kind != ConversationEventKind.SubmissionAccepted)
            .Select(RenderEvent)
            .ToList();

        if (dispositions.Count == 0 && turnEvents.Count == 0) return Empty;
        if (dispositions.Count == 0) return string.Join(" → ", turnEvents);
        if (turnEvents.Count == 0) return string.Join(" → ", dispositions);
        return string.Join(" → ", dispositions) + GroupSeparator + string.Join(" → ", turnEvents);
    }

    /// <summary>
    /// The ordering guarantee the bus ACTUALLY makes — which is weaker than it first looks, and
    /// weaker than the first revision of this helper asserted.
    ///
    /// <para><b>Why the stronger form was wrong.</b> The first revision asserted that delivered
    /// non-terminals were in <c>seq</c> order among themselves, and likewise for terminals. That is
    /// not a guarantee the bus offers: <c>seq</c> is assigned inside <c>Publish</c> and the channel
    /// write happens afterwards, so two CONCURRENT publishers — the caller thread reporting a
    /// disposition, the turn thread emitting progress, and the four-second typing loop — can take
    /// <c>seq</c> 5 and 6 and then write 6 before 5. The assertion passed four consecutive local
    /// runs and failed on the CI runner, which is exactly the shape of a test that pins a race
    /// rather than a contract.</para>
    ///
    /// <para>What is genuinely guaranteed, and asserted here:</para>
    /// <list type="number">
    /// <item><b>Every delivered event carries a distinct <c>seq</c>.</b> Assignment goes through an
    /// atomic per-conversation counter, so a duplicate would mean two events claiming one slot.</item>
    /// <item><b>Every event is delivered at most once.</b> The bus hands each accepted event to one
    /// bounded structure with a single reader; a repeated <c>eventId</c> would mean a double
    /// delivery, which no client dedupe could distinguish from a redelivery.</item>
    /// <item><b>A turn's terminal is assigned a later <c>seq</c> than its own
    /// <c>turn.started</c>.</b> Per turn id, not globally: the turn thread only begins after
    /// registration has published <c>turn.started</c>, so this ordering is causal rather than
    /// racy. It is the one cross-kind ordering fact the runtime really does promise.</item>
    /// </list>
    ///
    /// <para>Everything else about arrival order is deliberately NOT asserted, because the runtime
    /// does not promise it. See the ordering findings in the matrix and scenario docs.</para>
    /// </summary>
    public static void AssertDeliveryOrderIsLawful(IReadOnlyList<ConversationEvent> delivered)
    {
        // (1) seq is unique per conversation.
        foreach (var conversation in delivered.GroupBy(e => e.Identity.ConversationId, StringComparer.Ordinal))
        {
            var seqs = conversation.Select(e => e.Seq).ToList();
            Assert.Equal(seqs.Count, seqs.Distinct().Count());
        }

        // (2) no event is delivered twice.
        var eventIds = delivered.Select(e => e.EventId).ToList();
        Assert.Equal(eventIds.Count, eventIds.Distinct(StringComparer.Ordinal).Count());

        // (3) each turn's terminal is sequenced after that turn's own turn.started.
        foreach (var turn in delivered.Where(e => e.Identity.TurnId is not null)
                     .GroupBy(e => e.Identity.TurnId!, StringComparer.Ordinal))
        {
            var started = turn.FirstOrDefault(e => e.Kind == ConversationEventKind.TurnStarted);
            if (started is null) continue;

            foreach (var terminal in turn.Where(e => e.IsTerminal))
            {
                Assert.True(
                    terminal.Seq > started.Seq,
                    $"Turn {turn.Key}: terminal {terminal.Kind} took seq {terminal.Seq}, "
                    + $"which is not after its own turn.started at seq {started.Seq}.");
            }
        }
    }

    /// <summary>True for the time-driven typing heartbeat, which is excluded from ordered cells.</summary>
    public static bool IsTypingHeartbeat(ConversationEvent evt) =>
        evt.Kind == ConversationEventKind.TurnProgress
        && evt.PayloadAs<TurnProgressPayload>()?.Activity == ProgressActivity.Typing;

    private static string RenderEvent(ConversationEvent evt)
    {
        var discriminator = evt.Kind switch
        {
            ConversationEventKind.SubmissionAccepted =>
                Wire(evt.PayloadAs<SubmissionAcceptedPayload>()?.Disposition),
            ConversationEventKind.TurnProgress =>
                Wire(evt.PayloadAs<TurnProgressPayload>()?.Activity),
            ConversationEventKind.TurnFinal =>
                Wire(evt.PayloadAs<TurnFinalPayload>()?.Completion),
            ConversationEventKind.TurnCanceled =>
                Wire(evt.PayloadAs<TurnCanceledPayload>()?.Reason),
            ConversationEventKind.TurnError =>
                Wire(evt.PayloadAs<TurnErrorPayload>()?.Code),
            ConversationEventKind.TurnOutcomeUnknown =>
                Wire(evt.PayloadAs<TurnOutcomeUnknownPayload>()?.Reason),
            ConversationEventKind.ControlAck =>
                Wire(evt.PayloadAs<ControlAckPayload>()?.Target),
            ConversationEventKind.ProtocolRejected =>
                Wire(evt.PayloadAs<ProtocolRejectedPayload>()?.Code),
            _ => null,
        };

        return discriminator is null ? evt.Kind : $"{evt.Kind}({discriminator})";
    }

    /// <summary>
    /// The enum's WIRE value, taken from the protocol's own serializer rather than
    /// <c>ToString()</c>. <c>SubmissionDisposition.QueueFull</c> is <c>queue_full</c> on the wire,
    /// and a cell that said <c>QueueFull</c> would document something the client never receives.
    /// </summary>
    private static string? Wire<TEnum>(TEnum? value) where TEnum : struct, Enum =>
        value is null ? null : FleetProtocolJson.Serialize(value.Value).Trim('"');
}
