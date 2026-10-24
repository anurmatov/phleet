using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Fleet.Shared;

namespace Fleet.Agent.Services;

/// <summary>Values of the local inbox state fields (#476). Exact lowercase; nothing else is written.</summary>
public static class WorkflowOutcomeLocalStates
{
    // Delivery: the delivered receipt is posted only after the batch was fsynced here.
    public const string DeliveryPending = "pending";
    public const string DeliveryDelivered = "delivered";
    /// <summary>The server refused the batch for this generation (409). It never gets a turn.</summary>
    public const string DeliverySuperseded = "superseded";

    // Turn: admitted is written (and fsynced) BEFORE the task is started.
    public const string TurnPending = "pending";
    public const string TurnAdmitted = "admitted";
    /// <summary>Admitted, but whether the model ran is unknown (crash/restart/lost task). Never rerun.</summary>
    public const string TurnUnknown = "turn_unknown";
    public const string TurnCompleted = "completed";

    // Ack: the parsed ack set is persisted before it is posted, so a retry never reruns the model.
    public const string AckNone = "none";
    public const string AckPending = "pending";
    public const string AckAcked = "acked";
    /// <summary>The server refused the acknowledgement (stale/not delivered/mismatch). Never guessed.</summary>
    public const string AckRejected = "rejected";

    public const string DecisionPending = "pending";
    public const string DecisionAccepted = "accepted";
    public const string DecisionStale = "stale";
    public const string DecisionRejected = "rejected";
}

/// <summary>One parsed decision and its submission state.</summary>
public sealed record WorkflowOutcomeLocalDecision(WorkflowOutcomeTurnDecision Decision, string State, int Attempts);

/// <summary>
/// One fetched failure batch and everything the runtime did with it. Survives restart; the numeric
/// conversation key does not (it is process-local), so only the <see cref="Conversation"/> ref is kept.
/// </summary>
public sealed record WorkflowOutcomeLocalBatch
{
    public required WorkflowOutcomeInboxBatch Batch { get; init; }
    public required ConversationRef Conversation { get; init; }
    public required string TaskId { get; init; }

    public string Delivery { get; init; } = WorkflowOutcomeLocalStates.DeliveryPending;
    /// <summary>Delivered POSTs sent. Above zero, a receipt GET reconciles before any resend.</summary>
    public int DeliveryAttempts { get; init; }
    public WorkflowOutcomeReceipt? Receipt { get; init; }

    public string Turn { get; init; } = WorkflowOutcomeLocalStates.TurnPending;
    /// <summary><c>parsed</c>, <c>rejected:&lt;parser error&gt;</c>, <c>failed:&lt;kind&gt;</c> or <c>no_failure_members</c>.</summary>
    public string? TurnResult { get; init; }
    public WorkflowOutcomeTurnOutput? Output { get; init; }

    public string Ack { get; init; } = WorkflowOutcomeLocalStates.AckNone;
    /// <summary>Ack POSTs sent. Above zero, a receipt GET reconciles before any resend.</summary>
    public int AckAttempts { get; init; }
    public IReadOnlyList<WorkflowOutcomeLocalDecision> Decisions { get; init; } = [];

    /// <summary>Nothing more for the runtime to do. Server-side the batch may still need an operator.</summary>
    public bool Completed { get; init; }

    public DateTimeOffset StoredAt { get; init; }
    /// <summary>Last time a complete inbox listing included the batch.</summary>
    public DateTimeOffset LastListedAt { get; init; }

    /// <summary>No further runtime work: completed, turn ambiguous, or superseded.</summary>
    [JsonIgnore]
    public bool IsTerminal => Completed
        || Turn == WorkflowOutcomeLocalStates.TurnUnknown
        || Delivery == WorkflowOutcomeLocalStates.DeliverySuperseded;
}

/// <summary>A local inbox file that cannot be read. Intake stays disabled; the file is left untouched.</summary>
public sealed class WorkflowOutcomeStoreException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// The coordinator's durable local failure inbox (#476): one JSON document at
/// <c>{WorkDir}/.fleet/workflow-outcome-inbox.json</c>, directory 0700, file 0600.
/// </summary>
/// <remarks>
/// <para>
/// Every change is a whole-document commit: temp file in the same directory, fsync, atomic rename,
/// then a directory fsync where the platform allows it. A commit that fails leaves the previous
/// state both on disk and in memory, and the caller then does nothing that depends on it — no
/// delivered receipt, no model task.
/// </para>
/// <para>
/// A file that exists but does not parse is never overwritten or reset: starting empty would
/// refetch batches that may already have had a turn and run them again.
/// </para>
/// </remarks>
public sealed class WorkflowOutcomeLocalStore(string path)
{
    public const int SchemaVersion = 1;

    private const UnixFileMode DirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode FileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly object _gate = new();
    private IReadOnlyList<WorkflowOutcomeLocalBatch> _batches = [];
    private bool _loaded;

    public string Path { get; } = System.IO.Path.GetFullPath(path);

    /// <summary>Fault injection: thrown after the temp file is written and before the rename.</summary>
    internal Func<Exception?>? FaultBeforeCommitForTest { get; set; }

    /// <summary>The last commit failure, as an exception type name. Never file content.</summary>
    public string? LastError { get; private set; }

    public bool IsLoaded { get { lock (_gate) return _loaded; } }

    /// <summary>Loads the file, or starts empty when it does not exist. Throws on an unreadable file.</summary>
    public void Load()
    {
        lock (_gate)
        {
            if (!File.Exists(Path))
            {
                _batches = [];
                _loaded = true;
                return;
            }

            Document? document;
            try
            {
                document = JsonSerializer.Deserialize<Document>(File.ReadAllBytes(Path), Json);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                throw new WorkflowOutcomeStoreException($"workflow outcome inbox is unreadable ({ex.GetType().Name})", ex);
            }

            if (document is null || document.SchemaVersion != SchemaVersion || document.Batches is null
                || document.Batches.Any(b => b?.Batch is null || b.Conversation is null || string.IsNullOrEmpty(b.TaskId)))
                throw new WorkflowOutcomeStoreException("workflow outcome inbox has an unknown or incomplete shape");

            if (document.Batches.GroupBy(b => b.Batch.BatchId).Any(g => g.Count() > 1))
                throw new WorkflowOutcomeStoreException("workflow outcome inbox lists a batch twice");

            _batches = document.Batches;
            _loaded = true;
        }
    }

    public IReadOnlyList<WorkflowOutcomeLocalBatch> Snapshot()
    {
        lock (_gate) return _batches;
    }

    public WorkflowOutcomeLocalBatch? Get(Guid batchId)
    {
        lock (_gate) return _batches.FirstOrDefault(b => b.Batch.BatchId == batchId);
    }

    /// <summary>Durably adds a batch. False when it already exists or the commit failed.</summary>
    public bool TryAdd(WorkflowOutcomeLocalBatch batch)
    {
        lock (_gate)
        {
            EnsureLoaded();
            if (_batches.Any(b => b.Batch.BatchId == batch.Batch.BatchId)) return false;
            return Commit([.. _batches, batch]);
        }
    }

    /// <summary>
    /// Durably replaces one batch. The update runs under the store lock against the current value,
    /// so a concurrent writer cannot be lost. False when absent, unchanged or the commit failed.
    /// </summary>
    public bool TryUpdate(Guid batchId, Func<WorkflowOutcomeLocalBatch, WorkflowOutcomeLocalBatch?> update)
    {
        lock (_gate)
        {
            EnsureLoaded();
            var index = IndexOf(batchId);
            if (index < 0) return false;
            var next = update(_batches[index]);
            if (next is null || next == _batches[index]) return next is not null;
            var list = _batches.ToList();
            list[index] = next;
            return Commit(list);
        }
    }

    /// <summary>Durably rewrites the whole list (restart recovery, listing bookkeeping, pruning).</summary>
    public bool TryReplaceAll(Func<IReadOnlyList<WorkflowOutcomeLocalBatch>, IReadOnlyList<WorkflowOutcomeLocalBatch>> update)
    {
        lock (_gate)
        {
            EnsureLoaded();
            var next = update(_batches);
            if (next.SequenceEqual(_batches)) return true;
            return Commit(next);
        }
    }

    private int IndexOf(Guid batchId)
    {
        for (var i = 0; i < _batches.Count; i++)
            if (_batches[i].Batch.BatchId == batchId) return i;
        return -1;
    }

    private void EnsureLoaded()
    {
        if (!_loaded) throw new InvalidOperationException("workflow outcome inbox used before Load");
    }

    private bool Commit(IReadOnlyList<WorkflowOutcomeLocalBatch> next)
    {
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new Document(SchemaVersion, next), Json);
            WriteAtomically(bytes);
            _batches = next;
            LastError = null;
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.GetType().Name;
            return false;
        }
    }

    private void WriteAtomically(byte[] bytes)
    {
        var directory = System.IO.Path.GetDirectoryName(Path)!;
        if (OperatingSystem.IsWindows())
            Directory.CreateDirectory(directory);
        else
        {
            Directory.CreateDirectory(directory, DirectoryMode);
            File.SetUnixFileMode(directory, DirectoryMode);
        }

        var temp = System.IO.Path.Combine(directory,
            "." + System.IO.Path.GetFileName(Path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            var options = new FileStreamOptions { Mode = System.IO.FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = FileMode;
            using (var stream = new FileStream(temp, options))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            if (FaultBeforeCommitForTest?.Invoke() is { } fault) throw fault;

            File.Move(temp, Path, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* the original error wins */ }
            throw;
        }

        FsyncDirectory(directory);
    }

    /// <summary>Makes the rename itself durable. Best effort: not every platform can open a directory.</summary>
    private static void FsyncDirectory(string directory)
    {
        if (!(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())) return;
        try
        {
            var fd = LibcOpen(directory, 0 /* O_RDONLY */);
            if (fd < 0) return;
            try { _ = LibcFsync(fd); }
            finally { _ = LibcClose(fd); }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int LibcOpen(string path, int flags);

    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static extern int LibcFsync(int fd);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int LibcClose(int fd);

    private sealed record Document(int SchemaVersion, IReadOnlyList<WorkflowOutcomeLocalBatch> Batches);
}
