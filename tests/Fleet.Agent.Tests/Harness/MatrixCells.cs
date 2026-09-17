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
    /// <c>→</c>-joined <see cref="ConversationEventKind"/> wire values in EMISSION order (the
    /// envelope's own <c>seq</c>), each optionally suffixed with a parenthesised payload
    /// discriminator drawn from the enum's own wire values.
    ///
    /// <para><b>Measured correction to D6, which specified delivery order.</b> Delivery order is
    /// not deterministic for a scenario spanning more than one turn. The pump drains every
    /// terminal outbox BEFORE the shared progress channel on every pass (D11), deliberately, so a
    /// terminal event can and does overtake still-queued progress — a second turn's
    /// <c>turn.final</c> was observed arriving ahead of that same turn's <c>turn.started</c>. That
    /// is a real client-visible hazard, not a harness artifact: <c>ConversationEventBus</c>
    /// documents it, and <c>seq</c> exists precisely to make it detectable. Pinning a cell to
    /// delivery order would have pinned a race.</para>
    ///
    /// <para>So the cell records emission order, and each scenario ALSO asserts that the observed
    /// delivery order is lawful under D11 — see <see cref="AssertDeliveryOrderIsLawful"/>.</para>
    ///
    /// <para><b>The periodic typing heartbeat is excluded.</b> <c>turn.progress(typing)</c> is
    /// emitted immediately at turn start and then every four seconds for as long as the turn runs
    /// (<c>TaskManager.RunTypingLoopAsync</c>), so its multiplicity is a function of elapsed time,
    /// not of provider behaviour. Including it would make every ordered cell a timing assertion.
    /// Each scenario asserts it separately.</para>
    /// </summary>
    public static string ClientEvents(IEnumerable<ConversationEvent> events)
    {
        var rendered = events
            .Where(e => !IsTypingHeartbeat(e))
            .OrderBy(e => e.Seq)
            .Select(RenderEvent)
            .ToList();
        return rendered.Count == 0 ? Empty : string.Join(" → ", rendered);
    }

    /// <summary>
    /// The ordering guarantee the runtime actually makes (D11): a terminal event may move AHEAD of
    /// queued progress, but nothing may move backwards within its own class. So the delivered
    /// non-terminals must be in <c>seq</c> order among themselves, and so must the delivered
    /// terminals — and every <c>seq</c> must be unique.
    ///
    /// <para>This is the assertion that would catch a genuine reordering defect, which a
    /// literal delivery-order cell could not: that cell would go red on a timing race long before
    /// it went red on a bug.</para>
    /// </summary>
    public static void AssertDeliveryOrderIsLawful(IReadOnlyList<ConversationEvent> delivered)
    {
        var seqs = delivered.Select(e => e.Seq).ToList();
        Assert.Equal(seqs.Count, seqs.Distinct().Count());

        AssertAscending(delivered.Where(e => !e.IsTerminal).Select(e => e.Seq).ToList(), "non-terminal");
        AssertAscending(delivered.Where(e => e.IsTerminal).Select(e => e.Seq).ToList(), "terminal");
    }

    private static void AssertAscending(IReadOnlyList<long> seqs, string className)
    {
        for (var i = 1; i < seqs.Count; i++)
        {
            Assert.True(
                seqs[i] > seqs[i - 1],
                $"Delivered {className} events went backwards in seq: {seqs[i - 1]} then {seqs[i]}.");
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
