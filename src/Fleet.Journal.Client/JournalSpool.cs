using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fleet.Protocol;

namespace Fleet.Journal.Client;

/// <summary>How a media file enters the spool.</summary>
public enum SpoolMediaMode
{
    /// <summary>Inbound: the attachment directory is write-once per message, so a hardlink shares no later change.</summary>
    Hardlink,

    /// <summary>Outbound: a workspace file can be overwritten later, so the bytes are copied now.</summary>
    Copy,
}

/// <summary>One media file to keep with a record: from a path, or from bytes already in memory.</summary>
public sealed record SpoolMedia(int Ordinal, string? SourcePath, byte[]? Bytes, SpoolMediaMode Mode);

public enum SpoolWriteOutcome { Written, Full, Failed }

/// <summary>A pending or dead record on disk: its bookkeeping plus the wire record itself.</summary>
public sealed class SpoolEntry
{
    public required string Id { get; init; }
    public required string Direction { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public int Attempts { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public DateTimeOffset? FirstAttemptAt { get; set; }
    public string? LastError { get; set; }
    public IReadOnlyList<int> MediaOrdinals { get; init; } = [];

    /// <summary>The wire record, as JSON, exactly what is posted.</summary>
    public required JsonObject Record { get; set; }
}

/// <summary>
/// The agent's durable journal spool: <c>{WorkDir}/.fleet/journal-spool/</c> with
/// <c>pending/&lt;ULID&gt;.json</c>, <c>media/&lt;ULID&gt;.&lt;ordinal&gt;</c> and <c>dead/</c>.
/// </summary>
/// <remarks>
/// <para>
/// Every record file is written temp + fsync + rename, so a crash leaves either the old file or
/// the new one, never half of one. Writes run synchronously on the capture path inside the
/// caller's try/catch: a full disk loses the record, never the Telegram turn.
/// </para>
/// <para>
/// Limits are constants: <see cref="JournalOptions.MaxSpoolRecords"/> records or
/// <see cref="JournalOptions.MaxSpoolBytes"/> bytes. At the limit the NEW record is dropped, so
/// what is already queued keeps its place.
/// </para>
/// </remarks>
public sealed class JournalSpool
{
    private static JsonSerializerOptions Json => JournalRecordJson.StoredOptions;

    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private int _pendingCount;
    private long _bytes;

    public JournalSpool(string root, TimeProvider? time = null)
    {
        Root = root;
        _time = time ?? TimeProvider.System;

        Directory.CreateDirectory(PendingDir);
        Directory.CreateDirectory(MediaDir);
        Directory.CreateDirectory(DeadDir);

        // Accounting starts from what a previous run left: the drainer resumes pending/.
        foreach (var file in Directory.EnumerateFiles(PendingDir, "*.json"))
        {
            _pendingCount++;
            _bytes += SafeLength(file);
        }

        foreach (var file in Directory.EnumerateFiles(MediaDir))
            _bytes += SafeLength(file);
    }

    public string Root { get; }

    /// <summary>The record limit; a test lowers it rather than writing ten thousand files.</summary>
    internal int MaxRecords { get; init; } = JournalOptions.MaxSpoolRecords;

    /// <summary>The byte limit (records plus media).</summary>
    internal long MaxBytes { get; init; } = JournalOptions.MaxSpoolBytes;
    public string PendingDir => Path.Combine(Root, "pending");
    public string MediaDir => Path.Combine(Root, "media");
    public string DeadDir => Path.Combine(Root, "dead");

    /// <summary>Signalled on every write, so an idle drainer wakes at once.</summary>
    public SemaphoreSlim Written { get; } = new(0);

    public int PendingCount { get { lock (_gate) return _pendingCount; } }
    public long Bytes { get { lock (_gate) return _bytes; } }
    public int DeadCount => Directory.Exists(DeadDir) ? Directory.EnumerateFiles(DeadDir, "*.json").Count() : 0;

    /// <summary>
    /// Writes one record and its media. Returns <see cref="SpoolWriteOutcome.Full"/> without
    /// writing anything when the spool is at its limit.
    /// </summary>
    public SpoolWriteOutcome Write(JsonObject record, string direction, IReadOnlyList<SpoolMedia> media)
    {
        var id = Ulid.NewUlid(_time.GetUtcNow());
        var now = _time.GetUtcNow();

        lock (_gate)
        {
            if (_pendingCount >= MaxRecords || _bytes >= MaxBytes)
                return SpoolWriteOutcome.Full;
        }

        var written = new List<string>();
        try
        {
            long mediaBytes = 0;
            var ordinals = new List<int>();

            // Media first: a record never points at a file that is not there yet.
            foreach (var item in media)
            {
                var target = MediaPath(id, item.Ordinal);
                if (item.Bytes is not null)
                {
                    WriteAtomically(target, item.Bytes);
                }
                else if (item.SourcePath is not null && File.Exists(item.SourcePath))
                {
                    if (item.Mode == SpoolMediaMode.Hardlink && TryHardlink(item.SourcePath, target))
                    {
                        // Shares the attachment's bytes: write-once, so nothing can change them.
                    }
                    else
                    {
                        CopyAtomically(item.SourcePath, target);
                    }
                }
                else
                {
                    continue;
                }

                written.Add(target);
                ordinals.Add(item.Ordinal);
                mediaBytes += SafeLength(target);
            }

            var entry = new SpoolEntry
            {
                Id = id,
                Direction = direction,
                CreatedAt = now,
                NextAttemptAt = now,
                MediaOrdinals = ordinals,
                Record = record,
            };

            var path = PendingPath(id);
            var bytes = Serialize(entry);
            WriteAtomically(path, bytes);
            written.Add(path);

            lock (_gate)
            {
                _pendingCount++;
                _bytes += bytes.Length + mediaBytes;
            }

            Written.Release();
            return SpoolWriteOutcome.Written;
        }
        catch (Exception)
        {
            foreach (var file in written) TryDelete(file);
            return SpoolWriteOutcome.Failed;
        }
    }

    /// <summary>Pending record ids, oldest first (ULID order). Names only: nothing is parsed.</summary>
    public IReadOnlyList<string> PendingIds() =>
        Directory.EnumerateFiles(PendingDir, "*.json")
            .Select(f => Path.GetFileNameWithoutExtension(f))
            .Order(StringComparer.Ordinal)
            .ToArray();

    /// <summary>One pending record, or null when it is gone or unreadable.</summary>
    public SpoolEntry? Load(string id) => TryRead(PendingPath(id));

    /// <summary>Pending record ids with their bookkeeping, oldest first (ULID order).</summary>
    public IReadOnlyList<SpoolEntry> Pending()
    {
        var entries = new List<SpoolEntry>();
        foreach (var file in Directory.EnumerateFiles(PendingDir, "*.json").Order(StringComparer.Ordinal))
        {
            var entry = TryRead(file);
            if (entry is not null) entries.Add(entry);
        }

        return entries;
    }

    /// <summary>Rewrites a pending record's bookkeeping or record, atomically.</summary>
    public void Save(SpoolEntry entry)
    {
        var path = PendingPath(entry.Id);
        var before = SafeLength(path);
        var bytes = Serialize(entry);
        WriteAtomically(path, bytes);
        lock (_gate) _bytes += bytes.Length - before;
    }

    /// <summary>Delivered or dropped: the record and its media go.</summary>
    public void Delete(SpoolEntry entry)
    {
        var path = PendingPath(entry.Id);
        var freed = SafeLength(path);
        if (!TryDelete(path)) return;

        foreach (var ordinal in entry.MediaOrdinals)
        {
            var media = MediaPath(entry.Id, ordinal);
            freed += SafeLength(media);
            TryDelete(media);
        }

        lock (_gate)
        {
            _pendingCount--;
            _bytes -= freed;
        }
    }

    /// <summary>
    /// Moves a record to <c>dead/</c> with its reason. Its media stay, so an operator can redrive
    /// it by moving the file back to <c>pending/</c>.
    /// </summary>
    public void MoveToDead(SpoolEntry entry, string reason)
    {
        entry.LastError = reason;
        var from = PendingPath(entry.Id);
        var freed = SafeLength(from);
        WriteAtomically(Path.Combine(DeadDir, entry.Id + ".json"), Serialize(entry));
        if (!TryDelete(from)) return;

        lock (_gate)
        {
            _pendingCount--;
            _bytes -= freed;
        }
    }

    /// <summary>Deletes dead records (and their media) older than <paramref name="retention"/>.</summary>
    public int SweepDead(TimeSpan retention)
    {
        var cutoff = _time.GetUtcNow() - retention;
        var removed = 0;

        foreach (var file in Directory.EnumerateFiles(DeadDir, "*.json"))
        {
            var entry = TryRead(file);
            if (entry is null || entry.CreatedAt >= cutoff) continue;

            foreach (var ordinal in entry.MediaOrdinals)
            {
                var media = MediaPath(entry.Id, ordinal);
                var length = SafeLength(media);
                if (TryDelete(media)) lock (_gate) _bytes -= length;
            }

            if (TryDelete(file)) removed++;
        }

        return removed;
    }

    /// <summary>The oldest pending record's age, or null when nothing is pending.</summary>
    public TimeSpan? OldestPendingAge()
    {
        var oldest = Directory.EnumerateFiles(PendingDir, "*.json").Order(StringComparer.Ordinal).FirstOrDefault();
        if (oldest is null) return null;

        var entry = TryRead(oldest);
        return entry is null ? null : _time.GetUtcNow() - entry.CreatedAt;
    }

    public string MediaPath(string id, int ordinal) => Path.Combine(MediaDir, $"{id}.{ordinal}");

    private string PendingPath(string id) => Path.Combine(PendingDir, id + ".json");

    // ── files ────────────────────────────────────────────────────────────────

    private static byte[] Serialize(SpoolEntry entry)
    {
        var node = new JsonObject
        {
            ["id"] = entry.Id,
            ["direction"] = entry.Direction,
            ["createdAt"] = entry.CreatedAt.ToUniversalTime().ToString("O"),
            ["attempts"] = entry.Attempts,
            ["nextAttemptAt"] = entry.NextAttemptAt.ToUniversalTime().ToString("O"),
            ["firstAttemptAt"] = entry.FirstAttemptAt?.ToUniversalTime().ToString("O"),
            ["lastError"] = entry.LastError,
            ["media"] = new JsonArray(entry.MediaOrdinals.Select(o => (JsonNode)o).ToArray()),
            ["record"] = entry.Record.DeepClone(),
        };

        return JsonSerializer.SerializeToUtf8Bytes(node, Json);
    }

    private static SpoolEntry? TryRead(string file)
    {
        try
        {
            var node = JsonNode.Parse(File.ReadAllBytes(file))!.AsObject();
            return new SpoolEntry
            {
                Id = node["id"]!.GetValue<string>(),
                Direction = node["direction"]?.GetValue<string>() ?? "",
                CreatedAt = DateTimeOffset.Parse(node["createdAt"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture),
                Attempts = node["attempts"]?.GetValue<int>() ?? 0,
                NextAttemptAt = DateTimeOffset.Parse(node["nextAttemptAt"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture),
                FirstAttemptAt = node["firstAttemptAt"]?.GetValue<string>() is { } first
                    ? DateTimeOffset.Parse(first, System.Globalization.CultureInfo.InvariantCulture)
                    : null,
                LastError = node["lastError"]?.GetValue<string>(),
                MediaOrdinals = node["media"]?.AsArray().Select(n => n!.GetValue<int>()).ToArray() ?? [],
                Record = node["record"]!.AsObject().DeepClone().AsObject(),
            };
        }
        catch (Exception)
        {
            // A file an operator is editing, or one a crash truncated before its rename: skip it
            // this pass rather than fail the drainer.
            return null;
        }
    }

    /// <summary>Temp file in the same directory, fsync, then rename over the target.</summary>
    private static void WriteAtomically(string path, byte[] bytes)
    {
        var temp = Path.Combine(Path.GetDirectoryName(path)!, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    private static void CopyAtomically(string source, string path)
    {
        var temp = Path.Combine(Path.GetDirectoryName(path)!, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                input.CopyTo(output);
                output.Flush(flushToDisk: true);
            }

            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int LibcLink(string oldPath, string newPath);

    /// <summary>A hardlink on Linux and macOS; false anywhere else or on failure (the caller copies).</summary>
    private static bool TryHardlink(string source, string target)
    {
        if (!(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())) return false;

        try
        {
            TryDelete(target);
            return LibcLink(source, target) == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static long SafeLength(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private static bool TryDelete(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            File.Delete(path);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
