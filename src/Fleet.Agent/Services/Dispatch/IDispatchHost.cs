using Fleet.Agent.Models;
using Fleet.Protocol;

namespace Fleet.Agent.Services.Dispatch;

/// <summary>
/// What the parts split out of <see cref="TaskManager"/> still need from it: the running set, the
/// one place that publishes conversation events, the queue drain, the three public events and the
/// test hooks. <see cref="TaskManager"/> is the only implementation; nothing outside dispatch uses it.
/// </summary>
internal interface IDispatchHost
{
    ChatTaskState GetChatState(long chatId);

    void PublishEvent<TPayload>(long chatId, string kind, ConversationIdentity? identity, TPayload? payload)
        where TPayload : class;

    void PublishTerminal<TPayload>(long chatId, int taskId, string kind, ConversationIdentity? identity, TPayload? payload)
        where TPayload : class;

    void PublishTerminalError(long chatId, int taskId, ConversationIdentity? identity, ProtocolErrorCode code);

    TaskDispatchOutcome ReportDisposition(long chatId, ConversationIdentity? identity, TaskDispatchOutcome outcome, int? queuePosition = null);

    /// <summary>Claims and starts the next queued entry unless a turn is running or being handed over.</summary>
    void DrainQueue(bool reserved);

    void RaiseTaskCompleted(long chatId, string result, string? relaySender, TaskSource source, bool isPartial,
        string? correlationId, string? taskId, CompletionKind kind);

    void RaiseToolUse(long chatId, string toolName, string description);

    void RaiseStatusChanged();

    /// <summary>True while the warmup ping or a <c>/run</c> command holds the executor.</summary>
    bool ExecutorBusyElsewhere { get; }

    /// <summary>Reads and clears the late steering-answer guard (#406). True when it was set.</summary>
    bool TakeSteeringResidue();

    /// <summary>Sets the late steering-answer guard for the next executor turn (#406).</summary>
    void LeaveSteeringResidue();

    Action? SteerAttemptStartingForTest { get; }
    Action<string>? DispatchCheckpointForTest { get; }
    Action? SteeringNoticePendingForTest { get; }
    Action? NoticeLockWaitThrowsForTest { get; }
}
