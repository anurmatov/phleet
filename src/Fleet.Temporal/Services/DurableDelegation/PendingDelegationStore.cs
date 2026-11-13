using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Fleet.Temporal.Services;

/// <summary>
/// The pending delegations, in memory and in one JSON file that is rewritten after every change
/// that matters (temp file, then rename, so a crash leaves the old file or the new one, never half
/// of either).
///
/// <para>
/// An empty path, or a directory that cannot be written, keeps the records in memory and says so:
/// delegations still work, they just do not survive a restart. A file that exists but cannot be
/// read is moved aside, never overwritten, and the store starts empty.
/// </para>
///
/// <para>Not thread-safe by itself: <see cref="DurableDelegationService"/> serialises access.</para>
/// </summary>
public sealed class PendingDelegationStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly string? _path;
    private readonly ILogger _logger;
    private readonly Dictionary<string, PendingDelegation> _records = new(StringComparer.Ordinal);
    private bool _writable;

    public PendingDelegationStore(string? path, ILogger logger)
    {
        _path = string.IsNullOrWhiteSpace(path) ? null : path;
        _logger = logger;
        _writable = _path is not null;
    }

    /// <summary>True while the last write reached the file.</summary>
    public bool Durable => _writable;

    public int Count => _records.Count;

    public IReadOnlyList<string> TaskIds => [.. _records.Keys.OrderBy(k => k, StringComparer.Ordinal)];

    public IReadOnlyList<PendingDelegation> All =>
        [.. _records.Values.OrderBy(r => r.TaskId, StringComparer.Ordinal)];

    public bool TryGet(string taskId, out PendingDelegation record) => _records.TryGetValue(taskId, out record!);

    /// <summary>
    /// Reads the file. A missing file is an empty store. Records that cannot be acted on (no task
    /// id, no token) are skipped with their count logged; a file that is not a record list at all
    /// is moved to <c>{path}.unreadable-{timestamp}</c> so it can be looked at and is not lost to
    /// the next write.
    /// </summary>
    public void Load()
    {
        _records.Clear();
        if (_path is null || !File.Exists(_path)) return;

        try
        {
            var loaded = JsonSerializer.Deserialize<List<PendingDelegation>>(File.ReadAllText(_path), Json)
                ?? throw new JsonException("not a record list");

            var skipped = 0;
            foreach (var record in loaded)
            {
                if (record is null || string.IsNullOrEmpty(record.TaskId) || string.IsNullOrEmpty(record.TaskToken)
                    || string.IsNullOrEmpty(record.Namespace) || string.IsNullOrEmpty(record.Agent)
                    || !IsBase64(record.TaskToken))
                {
                    skipped++;
                    continue;
                }

                if (string.IsNullOrEmpty(record.CurrentTaskId)) record.CurrentTaskId = record.TaskId;
                // A cancel that was sent was asked for, whichever version wrote the record.
                if (record.CancelSent) record.CancelRequested = true;
                _records[record.TaskId] = record;
            }

            if (skipped > 0)
                _logger.LogError("Pending delegation state {Path}: {Skipped} unusable record(s) skipped", _path, skipped);
            _logger.LogInformation("Pending delegation state {Path}: {Count} delegation(s) resumed", _path, _records.Count);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _records.Clear();
            var aside = $"{_path}.unreadable-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
            try
            {
                File.Move(_path, aside, overwrite: true);
                // It may be a file someone restored by hand; what it holds is still payload.
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(aside, OwnerOnlyFile);
                _logger.LogError(
                    "Pending delegation state {Path} unreadable ({Reason}); moved to {Aside}. Delegations it held will not be resumed",
                    _path, ex.GetType().Name, aside);
            }
            catch (Exception moveEx) when (moveEx is IOException or UnauthorizedAccessException)
            {
                _logger.LogError(
                    "Pending delegation state {Path} unreadable ({Reason}) and could not be moved aside ({MoveReason})",
                    _path, ex.GetType().Name, moveEx.GetType().Name);
            }
        }
    }

    /// <summary>Adds or replaces the record for its task id and writes the file.</summary>
    public void Put(PendingDelegation record)
    {
        _records[record.TaskId] = record;
        Save();
    }

    /// <summary>Removes the record and writes the file. False when there was none.</summary>
    public bool Remove(string taskId)
    {
        if (!_records.Remove(taskId)) return false;
        Save();
        return true;
    }

    /// <summary>Writes the file after a record was changed in place.</summary>
    public void Save()
    {
        if (_path is null) return;
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(_path))!;
            // A directory this store has to create is the owner's alone. One that is already there
            // (the bridge's mounted state directory, shared with the failure notifier) is left as
            // it is: its mode is the operator's.
            if (!Directory.Exists(directory))
            {
                if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
                else Directory.CreateDirectory(directory, OwnerOnlyDirectory);
            }

            var temp = _path + ".tmp";
            // A leftover from a crash keeps whatever mode it was given; ours is created anew.
            File.Delete(temp);
            WriteOwnerOnly(temp, JsonSerializer.Serialize(All, Json));
            // The rename keeps the mode of the file being moved into place.
            File.Move(temp, _path, overwrite: true);
            if (!_writable) _logger.LogInformation("Pending delegation state {Path} is writable again", _path);
            _writable = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (_writable)
                _logger.LogError(
                    "Pending delegation state {Path} not writable ({Error}); delegations are in memory only and will not survive a restart",
                    _path, ex.GetType().Name);
            _writable = false;
        }
    }

    private const UnixFileMode OwnerOnlyFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private const UnixFileMode OwnerOnlyDirectory =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>
    /// Creates the file owner-only and then writes it. The mode is given to the call that creates
    /// the file, so there is no moment at which the task tokens, instructions and partial answers
    /// in it are readable by anyone else: writing first and narrowing the mode afterwards left one.
    /// </summary>
    private static void WriteOwnerOnly(string path, string content)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
        };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = OwnerOnlyFile;

        using var stream = new FileStream(path, options);
        using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content);
    }

    private static bool IsBase64(string value)
    {
        var buffer = new byte[value.Length];
        return Convert.TryFromBase64String(value, buffer, out _);
    }
}
