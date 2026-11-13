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

    /// <summary>The agent has already been told to stop; only Temporal still needs to hear it.</summary>
    public bool CancelSent { get; set; }

    /// <summary>
    /// The activity that started this delegation is publishing its first directive right now.
    /// In memory only: after a restart nobody is, and a pass sends whatever was not sent.
    /// </summary>
    [JsonIgnore]
    public bool Publishing { get; set; }

    [JsonIgnore]
    public bool HasResult => ResultStatus is not null;

    [JsonIgnore]
    public TimeSpan Budget => TimeSpan.FromSeconds(BudgetSeconds);

    [JsonIgnore]
    public DateTimeOffset Deadline => WaitStartedAt + Budget;

    public PendingDelegation Clone() => (PendingDelegation)MemberwiseClone();
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
    byte[] TaskToken);

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
}
