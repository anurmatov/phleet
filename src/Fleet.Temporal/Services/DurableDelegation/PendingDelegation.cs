using System.Text.Json.Serialization;

namespace Fleet.Temporal.Services;

/// <summary>
/// One delegation that is waiting for its agent, as it is written to the state file. Everything
/// the bridge needs to keep the activity alive, re-send the directive, enforce the budget, continue
/// an incomplete answer and finally complete the activity is here, so a bridge that has just
/// started can carry on from the file alone.
///
/// <para>
/// The instruction and the accumulated answer are payload: they are stored, never logged.
/// </para>
/// </summary>
public sealed class PendingDelegation
{
    /// <summary>The task id the workflow passed. The key: one record per delegation.</summary>
    public string TaskId { get; set; } = "";

    /// <summary>
    /// The id the agent is working under right now: <see cref="TaskId"/>, or
    /// <c>{TaskId}/incomplete-retry-N</c> once an incomplete answer has been continued.
    /// </summary>
    public string CurrentTaskId { get; set; } = "";

    public string Namespace { get; set; } = "";
    public string WorkflowId { get; set; } = "";
    public string RunId { get; set; } = "";
    public string WorkflowType { get; set; } = "";
    public string ActivityId { get; set; } = "";
    public int Attempt { get; set; }

    /// <summary>
    /// The activity attempt's task token, base64. It names exactly one attempt, so a record that
    /// outlived its attempt can only ever be told "not found", never complete a later one.
    /// </summary>
    public string TaskToken { get; set; } = "";

    public string Agent { get; set; } = "";

    /// <summary>
    /// Names this delegation to the agent, on every directive published for it (the first, a
    /// re-send, a continuation) and on every cancel issued for it. Minted once, when the record is
    /// created, and kept in the file, so it is the same after a restart. The task id cannot do
    /// this job: a later attempt of the same step has the same task id.
    /// <para>
    /// Empty in a record written before the field existed. Such a record sends what it always
    /// sent, and its cancel is matched by task id.
    /// </para>
    /// </summary>
    public string ExecutionId { get; set; } = "";

    /// <summary>The directive being waited on: the original, or the current continuation prompt.</summary>
    public string CurrentInstruction { get; set; } = "";

    public string? Repo { get; set; }

    /// <summary>How long each wait (the first, and every continuation) may take.</summary>
    public int BudgetSeconds { get; set; }

    public bool RetryOnIncomplete { get; set; }
    public int MaxIncompleteRetries { get; set; }

    /// <summary>Continuations sent so far. Zero while the original directive is outstanding.</summary>
    public int IncompleteAttempt { get; set; }

    /// <summary>The partial answers so far, joined the way the activity always joined them.</summary>
    public string AccumulatedText { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When the current wait began; the budget runs from here.</summary>
    public DateTimeOffset WaitStartedAt { get; set; }

    /// <summary>
    /// When <see cref="CurrentInstruction"/> last reached the broker. <see cref="DateTimeOffset.MinValue"/>
    /// means it has not yet, and the next pass sends it.
    /// </summary>
    public DateTimeOffset LastPublishedAt { get; set; }

    /// <summary>
    /// The agent's final answer, kept only while Temporal could not be told. The next pass
    /// completes the activity with it.
    /// </summary>
    public string? ResultStatus { get; set; }

    public string? ResultText { get; set; }

    /// <summary>
    /// Set when the budget ran out, so the failure message keeps the elapsed time measured then
    /// and the escalation target is told once even if Temporal has to be asked twice.
    /// </summary>
    public string? TimeoutMessage { get; set; }

    /// <summary>
    /// The timeout notice for <see cref="TimeoutMessage"/> has been handed to the relay (or given
    /// up on). Until then the record is kept, whatever Temporal has been told, so the notice is
    /// sent once and is not lost to a restart.
    /// </summary>
    public bool TimeoutReported { get; set; }

    /// <summary>
    /// The agent has to be told to stop: the workflow cancelled the activity, or Temporal no
    /// longer has it. Recorded when that is learnt; the cancel itself is sent off the pass.
    /// </summary>
    public bool CancelRequested { get; set; }

    /// <summary>The agent has been told to stop (or the attempt was given up).</summary>
    public bool CancelSent { get; set; }

    /// <summary>
    /// Cancels owed on behalf of the records this one replaced under the same task id, oldest
    /// first. They are sent, and have ended, before this record's directive is published: the
    /// agent matches a cancel by task id alone, so a cancel meant for an earlier execution that
    /// arrived after this one's directive would stop this one.
    /// </summary>
    public List<PriorCancel> PriorCancels { get; set; } = [];

    /// <summary>
    /// The directive is not published before this. Set when a cancel for the same task id ended
    /// without the bridge knowing whether it will still arrive.
    /// </summary>
    public DateTimeOffset PublishNotBefore { get; set; }

    /// <summary>
    /// Temporal needs nothing more for this activity: it has been failed, reported cancelled, or
    /// is gone. The record is no longer heartbeated and stays only until what it still owes the
    /// relay (a cancel, a timeout notice) has been sent.
    /// </summary>
    public bool TemporalSettled { get; set; }

    /// <summary>
    /// The delegation was started with <c>noRedelivery</c>: the bridge makes at most one attempt
    /// to publish its directive. No five-minute re-send, no continuation of an incomplete answer,
    /// and nothing from a process that only read the record from the file.
    /// <para>
    /// Written to the file only when true, like <see cref="PublishAttempted"/>, so a record
    /// without it is the same bytes it was before the field existed.
    /// </para>
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool PublishOnce { get; set; }

    /// <summary>
    /// The one publish attempt of a <see cref="PublishOnce"/> record has been made, or is about
    /// to be: set, and confirmed in the file, BEFORE the directive is handed to the relay. From
    /// then on nothing publishes for the record, whether that attempt delivered, failed or ended
    /// without the bridge knowing which. Never set for any other record.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool PublishAttempted { get; set; }

    /// <summary>
    /// The record was read from the state file by this process, not created by it. In memory
    /// only. It matters for a <see cref="PublishOnce"/> record alone: a restored one never
    /// initiates a directive.
    /// </summary>
    [JsonIgnore]
    public bool Restored { get; set; }

    /// <summary>
    /// The attempt marker of a <see cref="PublishOnce"/> record could not be confirmed in the
    /// file when its held directive came due, so the directive was not sent and never will be.
    /// In memory only; after a restart the record is a restored, unattempted one, which ends the
    /// same way.
    /// </summary>
    [JsonIgnore]
    public bool MarkerWriteFailed { get; set; }

    /// <summary>
    /// A <see cref="PublishOnce"/> record whose directive was never handed to the relay and now
    /// cannot be: the pass fails its activity, so the workflow can go on to read back what
    /// happened instead of waiting out a budget for an answer nobody was asked for.
    /// </summary>
    [JsonIgnore]
    public bool DirectiveWillNeverBeSent => PublishOnce && !PublishAttempted && (Restored || MarkerWriteFailed);

    /// <summary>
    /// The activity that started this delegation is publishing its first directive right now.
    /// In memory only: after a restart nobody is, and a pass sends whatever was not sent.
    /// </summary>
    [JsonIgnore]
    public bool Publishing { get; set; }

    /// <summary>
    /// Not before this: the last attempt to publish the directive failed. In memory only, so a
    /// restart tries at once.
    /// </summary>
    [JsonIgnore]
    public DateTimeOffset PublishRetryAfter { get; set; }

    /// <summary>
    /// True once nothing is owed to Temporal or to the relay, which is when the record goes.
    /// </summary>
    [JsonIgnore]
    public bool Finished =>
        TemporalSettled
        && PriorCancels.Count == 0
        && (!CancelRequested || CancelSent)
        && (TimeoutMessage is null || TimeoutReported);

    [JsonIgnore]
    public bool HasResult => ResultStatus is not null;

    [JsonIgnore]
    public TimeSpan Budget => TimeSpan.FromSeconds(BudgetSeconds);

    [JsonIgnore]
    public DateTimeOffset Deadline => WaitStartedAt + Budget;

    public PendingDelegation Clone()
    {
        var copy = (PendingDelegation)MemberwiseClone();
        copy.PriorCancels = [.. PriorCancels];
        return copy;
    }
}

/// <summary>A cancel still owed to an execution that a later record has replaced.</summary>
public sealed class PriorCancel
{
    public string Agent { get; set; } = "";

    /// <summary>The id the agent was working under: the task id, or a continuation's.</summary>
    public string TaskId { get; set; } = "";

    /// <summary>The predecessor's <see cref="PendingDelegation.ExecutionId"/>; empty if it had none.</summary>
    public string ExecutionId { get; set; } = "";
}

/// <summary>What the activity knows when it hands a delegation over.</summary>
public sealed record DelegationStart(
    string TaskId,
    string Agent,
    string Instruction,
    string? Repo,
    TimeSpan Budget,
    bool RetryOnIncomplete,
    int MaxIncompleteRetries,
    string Namespace,
    string WorkflowId,
    string RunId,
    string WorkflowType,
    string ActivityId,
    int Attempt,
    byte[] TaskToken,
    bool NoRedelivery = false);

/// <summary>What became of an agent reply offered to the pending delegations.</summary>
public enum DurableReplyOutcome
{
    /// <summary>No pending delegation has this task id. The caller handles it as before.</summary>
    NotPending,

    /// <summary>The activity was completed with it (or had already gone) and the record is removed.</summary>
    Completed,

    /// <summary>It was incomplete and a continuation was sent to the agent.</summary>
    Continued,

    /// <summary>Temporal could not be told. The answer is in the record and the next pass delivers it.</summary>
    Stored,

    /// <summary>A repeat, or an answer to a directive this delegation has moved past. Nothing changed.</summary>
    Ignored,

    /// <summary>
    /// An answer from another execution of the same task id: a predecessor's, arriving late.
    /// Nothing changed. It is settled all the same: nothing else is waiting for it either.
    /// </summary>
    OtherExecution,
}
