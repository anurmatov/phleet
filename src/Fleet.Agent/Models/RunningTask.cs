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
    public Channel<string> Inbox { get; } = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true });
}
