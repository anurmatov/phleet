using System.Threading.Channels;

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
}
