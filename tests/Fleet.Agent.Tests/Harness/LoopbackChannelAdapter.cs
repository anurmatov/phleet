using Fleet.Agent.Abstractions;
using Fleet.Protocol;

namespace Fleet.Agent.Tests.Harness;

/// <summary>
/// The L2 terminus (D2): an in-process <see cref="IChannelAdapter"/> that records the events a
/// client would receive, <b>in delivery order</b>.
///
/// <para>Order is the whole point. A terminal delivered before its own <c>turn.started</c>, or a
/// <c>turn.final</c> before the <c>submission.accepted</c> it answers, is unrecoverable on the
/// client and invisible to a set-based assertion (MUST NOT #9). Every scenario asserts against
/// <see cref="Delivered"/> as a sequence.</para>
///
/// <para>Supersedes the private <c>RecordingAdapter</c> that lived in
/// <c>ConversationEventPumpTests</c>; that file now uses this one. The delivery semantics are
/// carried over unchanged: setting <see cref="Behaviour"/> REPLACES recording rather than
/// wrapping it, which is what lets a throwing or hanging adapter be observed as having delivered
/// nothing.</para>
/// </summary>
internal sealed class LoopbackChannelAdapter : IChannelAdapter
{
    /// <summary>The channel id every public scenario opens against. Never a runtime-owned id.</summary>
    public const string DefaultChannelId = "example-adapter";

    private readonly List<ConversationEvent> _delivered = [];
    private readonly Lock _gate = new();

    public LoopbackChannelAdapter(string channelId = DefaultChannelId) => ChannelId = channelId;

    public string ChannelId { get; }

    /// <summary>
    /// Optional delivery behaviour override. When set, the event is NOT recorded — the override
    /// is the whole delivery. Used to prove that an adapter which throws or hangs is contained by
    /// the pump and cannot prevent a turn from terminating.
    /// </summary>
    public Func<ConversationEvent, CancellationToken, Task>? Behaviour { get; set; }

    /// <summary>Events in the order the pump handed them over.</summary>
    public IReadOnlyList<ConversationEvent> Delivered
    {
        get { lock (_gate) return _delivered.ToList(); }
    }

    /// <summary>The <c>kind</c> of every delivered event, in delivery order.</summary>
    public IReadOnlyList<string> DeliveredKinds
    {
        get { lock (_gate) return _delivered.Select(e => e.Kind).ToList(); }
    }

    public Task DeliverAsync(ConversationEvent evt, CancellationToken ct)
    {
        if (Behaviour is not null) return Behaviour(evt, ct);
        lock (_gate) _delivered.Add(evt);
        return Task.CompletedTask;
    }

    public void Clear()
    {
        lock (_gate) _delivered.Clear();
    }
}
