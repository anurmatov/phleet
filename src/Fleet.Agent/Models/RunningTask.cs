using System.Threading.Channels;
using Fleet.Protocol;

namespace Fleet.Agent.Models;

public sealed class RunningTask
{
    public required int Id { get; init; }
    public required string Description { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required CancellationTokenSource Cts { get; init; }
    public required bool IsSessionTask { get; init; }
    public long UserId { get; init; }
    /// <summary>Bridge taskId (format: {workflowId}/{step}) for Temporal-delegated tasks. Null for Telegram-originated tasks.</summary>
    public string? BridgeTaskId { get; init; }
    /// <summary>Where this task came from; a Relay or Bridge task is not its chat's own work (#369).</summary>
    public TaskSource Source { get; init; } = TaskSource.UserMessage;

    /// <summary>Mid-task inbox: messages appended while this task is running.</summary>
    public Channel<MidTurnMessage> Inbox { get; } = Channel.CreateUnbounded<MidTurnMessage>(
        new UnboundedChannelOptions { SingleReader = true });

    /// <summary>Serializes the live/closed decision against turn completion.</summary>
    public SemaphoreSlim TurnDispatchLock { get; } = new(1, 1);

    /// <summary>Set while holding TurnDispatchLock once no more live injection is possible.</summary>
    public bool Closed { get; set; }

    /// <summary>Number of successful live injections accepted into this turn.</summary>
    public int InjectionCount { get; set; }

    /// <summary>Injected messages to redeliver if the process dies before the turn completes cleanly.</summary>
    public List<MidTurnMessage> InjectedMessagesForResume { get; } = [];

    /// <summary>
    /// Routing identity for this turn, minted once at registration. Null for turns started before
    /// the seam existed or by callers that supply no identity.
    /// </summary>
    public ConversationIdentity? Identity { get; set; }

    /// <summary>
    /// Why this turn was cancelled. Set by the CANCELLER, before <c>Cts.Cancel()</c>, so the
    /// catch block can report it. There is deliberately no <c>shutdown</c> member — see
    /// <see cref="TurnCancelReason"/>.
    /// </summary>
    public TurnCancelReason CancelReason { get; set; } = TurnCancelReason.Unknown;

    /// <summary>
    /// Set the moment a terminal event is handed to the outbox — NOT when it is delivered.
    /// The reaper reads this on every exit path; a turn that published a terminal event must not
    /// also produce <c>turn.outcome_unknown</c>.
    ///
    /// Delivery is deliberately not the trigger: an adapter that is down would otherwise turn
    /// every completed turn into a fabricated unknown outcome.
    /// </summary>
    public bool TerminalPublished { get; set; }

    /// <summary>
    /// Submission ids this turn is answering beyond its own, accumulated from coalesced queue
    /// parts and mid-turn injections. Carried on the single terminal event.
    /// </summary>
    public List<string> MergedSubmissionIds { get; } = [];
}
