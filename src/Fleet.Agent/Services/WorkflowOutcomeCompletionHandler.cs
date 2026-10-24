using System.Collections.Concurrent;
using Fleet.Agent.Configuration;
using Fleet.Agent.Models;
using Fleet.Protocol;
using Fleet.Shared;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fleet.Agent.Services;

/// <summary>The end of one internal <see cref="TaskSource.WorkflowOutcome"/> turn, as TaskManager reports it.</summary>
/// <param name="FinalText">The turn's last result only — never the concatenated assistant texts. Null when there was none.</param>
public sealed record WorkflowOutcomeTurnEnded(
    long ChatId, string TaskId, TaskSource Source, string? FinalText, CompletionKind Kind, bool IsPartial);

/// <summary>
/// Admits the coordinator's internal failure-review turns and turns their final output into
/// acknowledgements and decisions (#476).
/// </summary>
/// <remarks>
/// <para>
/// <b>Admission.</b> At most one turn is outstanding, and one is admitted only while the agent is
/// idle, so human work is never preempted and a busy or offline coordinator leaves the batch
/// pending. The conversation key comes from <see cref="IConversationRegistry.Resolve"/> for the
/// batch's persisted <see cref="ConversationRef"/>, inside the positive reserved band; allocation
/// failure leaves the batch pending with no fallback key. The admitted state is fsynced BEFORE the
/// task starts, so a crash can only ever produce <c>turn_unknown</c> — which is never rerun.
/// </para>
/// <para>
/// <b>Completion.</b> Only the admitted final is considered, and only when its key, source, task id
/// and registered ref all match the admitted batch. A completed turn's text is parsed strictly
/// against the delivered members; failed, partial, plain or malformed output acknowledges nothing
/// and decides nothing. A parsed result is persisted first, so posting it can be retried without
/// running the model again.
/// </para>
/// </remarks>
public sealed class WorkflowOutcomeCompletionHandler : IDisposable
{
    /// <summary>The fixed task description shown in status; carries no batch content.</summary>
    public const string DisplayText = "workflow outcome review";

    private readonly TaskManager _taskManager;
    private readonly IConversationRegistry _registry;
    private readonly WorkflowOutcomeLocalStore _store;
    private readonly WorkflowOutcomeClientOptions _options;
    private readonly ILogger<WorkflowOutcomeCompletionHandler> _logger;
    private readonly TimeProvider _time;

    // Process-local: a numeric key is never persisted, only the ref it was resolved from.
    private readonly ConcurrentDictionary<Guid, long> _keys = new();
    private readonly SemaphoreSlim _admission = new(1, 1);

    public WorkflowOutcomeCompletionHandler(
        TaskManager taskManager,
        IConversationRegistry registry,
        WorkflowOutcomeLocalStore store,
        IOptions<WorkflowOutcomeClientOptions> options,
        ILogger<WorkflowOutcomeCompletionHandler> logger,
        TimeProvider? time = null)
    {
        _taskManager = taskManager;
        _registry = registry;
        _store = store;
        _options = options.Value;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _taskManager.OnWorkflowOutcomeTurnEnded += OnTurnEnded;
        _taskManager.OnStatusChanged += OnStatusChanged;
    }

    /// <summary>Raised when there is local work to do now: a turn ended, or the agent became free.</summary>
    public event Action? Changed;

    /// <summary>The internal conversation of one batch for this coordinator and generation.</summary>
    public static ConversationRef ConversationFor(Guid batchId, string coordinator, long generation) =>
        new(WorkflowOutcomeTurnIdentity.ChannelId, batchId.ToString("D"),
            WorkflowOutcomeTurnIdentity.Principal(coordinator, generation));

    /// <summary>The key currently resolved for a batch in this process, if any.</summary>
    public long? KeyFor(Guid batchId) => _keys.TryGetValue(batchId, out var key) ? key : null;

    /// <summary>
    /// Startup recovery. A turn that was admitted before the restart may or may not have run, so it
    /// becomes <c>turn_unknown</c>: never rerun, left for the coordinator or operator (the server
    /// keeps showing it delivered and unacknowledged). False when that could not be recorded.
    /// </summary>
    public bool Recover()
    {
        var interrupted = _store.Snapshot().Count(b => b.Turn == WorkflowOutcomeLocalStates.TurnAdmitted);
        if (interrupted == 0) return true;
        var ok = _store.TryReplaceAll(batches => batches
            .Select(b => b.Turn == WorkflowOutcomeLocalStates.TurnAdmitted
                ? b with { Turn = WorkflowOutcomeLocalStates.TurnUnknown, TurnResult = "interrupted_by_restart" }
                : b)
            .ToList());
        if (ok)
            _logger.LogError("{Count} workflow outcome turn(s) were in flight at restart and are now turn_unknown; "
                + "they are not rerun and need a coordinator or operator decision", interrupted);
        return ok;
    }

    /// <summary>
    /// An admitted turn whose task is neither queued nor running and never reported an end (the
    /// queue was cleared, or the task was rejected or lost) becomes <c>turn_unknown</c>.
    /// </summary>
    public void ReconcileTurns()
    {
        foreach (var b in _store.Snapshot().Where(b => b.Turn == WorkflowOutcomeLocalStates.TurnAdmitted))
        {
            if (_taskManager.IsTaskIdActive(b.TaskId)) continue;
            // Re-read: the end may have been recorded between the snapshot and the activity check.
            if (_store.TryUpdate(b.Batch.BatchId, x => x.Turn == WorkflowOutcomeLocalStates.TurnAdmitted
                    && !_taskManager.IsTaskIdActive(x.TaskId)
                    ? x with { Turn = WorkflowOutcomeLocalStates.TurnUnknown, TurnResult = "task_lost" }
                    : x)
                && _store.Get(b.Batch.BatchId)?.Turn == WorkflowOutcomeLocalStates.TurnUnknown)
            {
                _logger.LogError("Workflow outcome turn for batch {BatchId} ended without a result; turn_unknown, not rerun",
                    b.Batch.BatchId);
            }
        }
    }

    /// <summary>
    /// Admits the next delivered batch, oldest first, when no turn is outstanding and the agent is
    /// idle. True when a task was started or queued.
    /// </summary>
    public async Task<bool> TryAdmitNextAsync(CancellationToken ct)
    {
        await _admission.WaitAsync(ct);
        try
        {
            var batches = _store.Snapshot();
            if (batches.Any(b => b.Turn == WorkflowOutcomeLocalStates.TurnAdmitted)) return false;
            if (!_taskManager.IsIdle) return false;

            var next = batches
                .Where(b => !b.Completed
                    && b.Delivery == WorkflowOutcomeLocalStates.DeliveryDelivered
                    && b.Turn == WorkflowOutcomeLocalStates.TurnPending)
                .OrderBy(b => b.Batch.CreatedAt).ThenBy(b => b.StoredAt)
                .FirstOrDefault();
            if (next is null) return false;

            var batchId = next.Batch.BatchId;
            if (WorkflowOutcomePrompt.Members(next.Batch).Count == 0)
            {
                // Nothing to review is never a model turn.
                _store.TryUpdate(batchId, b => b with
                {
                    Turn = WorkflowOutcomeLocalStates.TurnCompleted, TurnResult = "no_failure_members", Completed = true,
                });
                return false;
            }

            long key;
            try
            {
                key = _registry.Resolve(next.Conversation);
            }
            catch (InvalidOperationException)
            {
                _logger.LogError("Workflow outcome batch {BatchId} stays pending: the reserved conversation key band is exhausted", batchId);
                return false;
            }

            if (!_registry.IsReserved(key) || _registry.Lookup(key) != next.Conversation)
            {
                _logger.LogError("Workflow outcome batch {BatchId} stays pending: its conversation key is not a reserved internal key", batchId);
                return false;
            }

            // fsync admission first: from here on a crash can only mean turn_unknown, never a rerun.
            if (!_store.TryUpdate(batchId, b => b.Turn == WorkflowOutcomeLocalStates.TurnPending
                    ? b with { Turn = WorkflowOutcomeLocalStates.TurnAdmitted }
                    : null))
            {
                _logger.LogError("Workflow outcome batch {BatchId} stays pending: admission could not be stored ({Error})",
                    batchId, _store.LastError);
                return false;
            }
            _keys[batchId] = key;

            var outcome = await _taskManager.StartTask(
                chatId: key,
                task: WorkflowOutcomePrompt.Build(next.Batch),
                displayText: DisplayText,
                isSessionTask: false,
                source: TaskSource.WorkflowOutcome,
                relaySender: null,
                correlationId: null,
                taskId: next.TaskId,
                userId: 0,
                identity: IdentityFor(next.Conversation));

            switch (outcome)
            {
                case TaskDispatchOutcome.Ran or TaskDispatchOutcome.Queued:
                    _logger.LogInformation("Workflow outcome review admitted for batch {BatchId} ({Count} member(s), {Outcome})",
                        batchId, WorkflowOutcomePrompt.Members(next.Batch).Count, outcome);
                    return true;
                case TaskDispatchOutcome.QueueFull:
                    // Never queued, so nothing ran: back to pending for a later cycle.
                    _store.TryUpdate(batchId, b => b.Turn == WorkflowOutcomeLocalStates.TurnAdmitted
                        ? b with { Turn = WorkflowOutcomeLocalStates.TurnPending }
                        : null);
                    _logger.LogWarning("Workflow outcome batch {BatchId} stays pending: the agent queue is full", batchId);
                    return false;
                default:
                    // Refused or a duplicate in flight: ReconcileTurns settles it, never a second start.
                    _logger.LogWarning("Workflow outcome review for batch {BatchId} was not started ({Outcome})", batchId, outcome);
                    return false;
            }
        }
        finally
        {
            _admission.Release();
        }
    }

    /// <summary>Drops the registry entry of every finished batch whose task has ended.</summary>
    public void ReleaseFinishedConversations()
    {
        foreach (var (batchId, key) in _keys)
        {
            var batch = _store.Get(batchId);
            if (batch is not null && !batch.IsTerminal) continue;
            if (batch is not null && _taskManager.IsTaskIdActive(batch.TaskId)) continue;
            if (_keys.TryRemove(batchId, out _)) _registry.Unregister(key);
        }
    }

    internal static ConversationIdentity IdentityFor(ConversationRef conversation) => new()
    {
        PrincipalId = conversation.PrincipalId,
        Role = PrincipalRole.Owner,
        ChannelId = conversation.ChannelId,
        ConversationId = conversation.ConversationId,
        SubmissionId = conversation.ConversationId,
        Attempt = 1,
    };

    private void OnTurnEnded(WorkflowOutcomeTurnEnded ended)
    {
        var batch = MatchAdmitted(ended);
        if (batch is null)
        {
            _logger.LogWarning("Workflow outcome turn end rejected: key, source, task or conversation does not match an admitted batch");
            return;
        }

        var members = WorkflowOutcomePrompt.Members(batch.Batch)
            .Select(i => new WorkflowOutcomeItemRef(i.Id, i.Version))
            .ToList();

        string result;
        WorkflowOutcomeTurnOutput? output = null;
        if (ended.Kind != CompletionKind.Completed || ended.IsPartial)
            result = "failed:" + ended.Kind.ToString().ToLowerInvariant();
        else if (WorkflowOutcomeTurnOutputParser.TryParse(ended.FinalText, batch.Batch.BatchId, batch.Batch.Generation,
                     members, _time.GetUtcNow(), out output, out var error))
            result = "parsed";
        else
            result = "rejected:" + error;

        var hasAck = output is { Ack.Count: > 0 };
        var stored = _store.TryUpdate(batch.Batch.BatchId, b => b.Turn != WorkflowOutcomeLocalStates.TurnAdmitted ? null : b with
        {
            Turn = WorkflowOutcomeLocalStates.TurnCompleted,
            TurnResult = result,
            Output = output,
            Ack = hasAck ? WorkflowOutcomeLocalStates.AckPending : WorkflowOutcomeLocalStates.AckNone,
            Decisions = output?.Decisions
                .Select(d => new WorkflowOutcomeLocalDecision(d, WorkflowOutcomeLocalStates.DecisionPending, 0))
                .ToList() ?? [],
            Completed = !hasAck,
        });

        if (!stored)
            // Left admitted: once the task id is released, ReconcileTurns makes it turn_unknown.
            _logger.LogError("Workflow outcome turn result for batch {BatchId} could not be stored ({Error}); it will not be rerun",
                batch.Batch.BatchId, _store.LastError);
        else
            _logger.LogInformation("Workflow outcome review for batch {BatchId} ended: {Result}, {AckCount} ack(s), {DecisionCount} decision(s)",
                batch.Batch.BatchId, result, output?.Ack.Count ?? 0, output?.Decisions.Count ?? 0);

        Changed?.Invoke();
    }

    /// <summary>
    /// The admission rule, applied again at completion: internal source, reserved key, the ref
    /// registered under that key, and an admitted batch with exactly that ref and task id.
    /// </summary>
    internal WorkflowOutcomeLocalBatch? MatchAdmitted(WorkflowOutcomeTurnEnded ended)
    {
        if (ended.Source != TaskSource.WorkflowOutcome || !_registry.IsReserved(ended.ChatId)) return null;
        var reference = _registry.Lookup(ended.ChatId);
        if (reference is null) return null;
        var batch = _store.Snapshot().FirstOrDefault(b => b.Conversation == reference);
        if (batch is null
            || batch.Turn != WorkflowOutcomeLocalStates.TurnAdmitted
            || batch.Batch.Generation != _options.CoordinatorGeneration
            || !string.Equals(batch.TaskId, ended.TaskId, StringComparison.Ordinal)
            || !_keys.TryGetValue(batch.Batch.BatchId, out var key) || key != ended.ChatId)
            return null;
        return batch;
    }

    private void OnStatusChanged()
    {
        // Only worth a cycle when a review could actually be admitted now.
        if (!_taskManager.IsIdle) return;
        var batches = _store.Snapshot();
        if (batches.Any(b => b.Turn == WorkflowOutcomeLocalStates.TurnAdmitted)
            || !batches.Any(b => !b.Completed && b.Delivery == WorkflowOutcomeLocalStates.DeliveryDelivered
                                 && b.Turn == WorkflowOutcomeLocalStates.TurnPending))
            return;
        Changed?.Invoke();
    }

    public void Dispose()
    {
        _taskManager.OnWorkflowOutcomeTurnEnded -= OnTurnEnded;
        _taskManager.OnStatusChanged -= OnStatusChanged;
    }
}
