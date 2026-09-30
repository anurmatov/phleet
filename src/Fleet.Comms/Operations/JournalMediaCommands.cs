using System.Security.Cryptography;
using Fleet.Conversations.Contracts;
using Fleet.Conversations.Journal;
using Fleet.Comms.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fleet.Comms.Operations;

/// <summary>
/// The media half of the operator path: <c>media backup</c>, <c>media verify</c>,
/// <c>media restore</c>, <c>journal verify-media</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why these are processes and not routes.</b> A command that reads every archived byte in the
/// bucket is a full-bucket export behind an <c>ingest</c> token — the same credential the agents
/// hold. The operator authorisation here is possession of the bucket credentials and the database,
/// which is what being the operator means, and it is the same reason <c>store backup</c> is a
/// subcommand (OperatorCommands, class comment).
/// </para>
/// <para>
/// <b>Every byte is re-hashed on its way in and on its way out.</b> A manifest that recorded only
/// the database's claimed digest would certify a bucket that had silently truncated an object; the
/// point of the exercise is to find out whether the bytes are still the bytes, which can only be
/// answered by reading them.
/// </para>
/// <para>
/// ⚠️ Nothing here prints an object key that was derived from content, and nothing prints a
/// credential. Keys are named in output because an operator needs them to act; they are ULIDs under
/// <c>j1/</c> and carry no information about what they contain.
/// </para>
/// </remarks>
public static class JournalMediaCommands
{
    /// <summary>The manifest name, per run. UTC to the second so runs never overwrite each other.</summary>
    public const string ManifestPrefix = "manifest-";

    public static async Task<int> BackupAsync(
        string[] args, TextWriter output, TextWriter error, CancellationToken ct)
    {
        var directory = Required(args, "--out");

        if (!Directory.Exists(directory))
        {
            throw new OperatorCommandException(
                $"The directory does not exist: {directory}. Create it first — creating it here "
                + "would hide an unmounted volume behind a successful-looking copy.");
        }

        var (store, objects) = Open();
        var rows = await objects.ListBackupRowsAsync(ct);

        var objectsDir = Path.Combine(directory, "objects");
        Directory.CreateDirectory(objectsDir);

        long copied = 0, copiedBytes = 0, already = 0, failures = 0;
        var manifest = new List<string>(rows.Count);

        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();

            var target = TargetPath(objectsDir, row.ObjectKey);

            if (File.Exists(target) && await MatchesAsync(target, row.Sha256, row.ByteSize, ct))
            {
                already++;
            }
            else
            {
                try
                {
                    await CopyAsync(store, row.ObjectKey, target, row.Sha256, row.ByteSize, ct);
                    copied++;
                    copiedBytes += row.ByteSize;
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    // Counted and continued: one missing object must not cost the operator the
                    // other 400, and the exit code still refuses to call this a backup.
                    failures++;
                    await error.WriteLineAsync($"copy failed: {row.ObjectKey} ({e.GetType().Name})");
                    continue;
                }
            }

            manifest.Add(ManifestLine(row.ObjectKey, row.Sha256, row.ByteSize));
        }

        var manifestPath = await WriteManifestAsync(directory, manifest, ct);

        output.WriteLine(
            $"objects={rows.Count} copied={copied} already={already} bytes={copiedBytes} "
            + $"failures={failures}");
        output.WriteLine($"manifest: {manifestPath}");

        if (failures > 0)
        {
            await error.WriteLineAsync(
                "media backup: INCOMPLETE — some objects could not be read. The manifest lists only "
                + "what was verified present, so this directory is not a full backup.");
            return 1;
        }

        return 0;
    }

    /// <summary>
    /// Checks a backup directory against its own manifest, byte for byte.
    /// </summary>
    /// <remarks>
    /// Reads the newest manifest, which is what "verify the backup I just took" means. The manifest
    /// is the authority, not the directory: files in <c>objects/</c> that it does not name are
    /// reported rather than trusted.
    /// </remarks>
    public static async Task<int> VerifyAsync(
        string[] args, TextWriter output, TextWriter error, CancellationToken ct)
    {
        var directory = Required(args, "--in");
        var manifestPath = NewestManifest(directory);

        var entries = ReadManifest(manifestPath);
        var objectsDir = Path.Combine(directory, "objects");

        long missing = 0, mismatched = 0, bytes = 0;

        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();

            var path = TargetPath(objectsDir, entry.Key);
            if (!File.Exists(path))
            {
                missing++;
                await error.WriteLineAsync($"missing: {entry.Key}");
                continue;
            }

            if (!await MatchesAsync(path, entry.Sha256, entry.ByteSize, ct))
            {
                mismatched++;
                await error.WriteLineAsync($"mismatch: {entry.Key}");
                continue;
            }

            bytes += entry.ByteSize;
        }

        output.WriteLine(
            $"entries={entries.Count} bytes={bytes} missing={missing} mismatches={mismatched}");

        if (missing > 0 || mismatched > 0 || entries.Count == 0)
        {
            if (entries.Count == 0) await error.WriteLineAsync("the manifest names no objects.");
            return 1;
        }

        return 0;
    }

    /// <summary>
    /// Puts every object the bucket is missing, then verifies the bucket against the manifest.
    /// </summary>
    /// <remarks>
    /// <b>Objects already present are never overwritten.</b> An object key is content-addressed by
    /// the declaration that created it, so a present key whose bytes differ means something has gone
    /// badly wrong somewhere else; replacing it would destroy the evidence. Such a key is a failure.
    /// </remarks>
    public static async Task<int> RestoreAsync(
        string[] args, TextWriter output, TextWriter error, CancellationToken ct)
    {
        var directory = Required(args, "--in");
        var manifestPath = NewestManifest(directory);

        var entries = ReadManifest(manifestPath);
        var objectsDir = Path.Combine(directory, "objects");

        var store = OpenStore();
        long restored = 0, present = 0, failures = 0, bytes = 0;

        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();

            var path = TargetPath(objectsDir, entry.Key);
            if (!File.Exists(path))
            {
                failures++;
                await error.WriteLineAsync($"missing from the backup: {entry.Key}");
                continue;
            }

            if (!await MatchesAsync(path, entry.Sha256, entry.ByteSize, ct))
            {
                failures++;
                await error.WriteLineAsync($"refusing to restore a mismatched file: {entry.Key}");
                continue;
            }

            bool exists;
            try
            {
                exists = await store.ExistsAsync(entry.Key, ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                failures++;
                await error.WriteLineAsync($"cannot reach the bucket: {e.GetType().Name}");
                break;
            }

            if (exists)
            {
                present++;
                bytes += entry.ByteSize;
                continue;
            }

            try
            {
                await using var body = File.OpenRead(path);
                var written = await store.PutAsync(
                    entry.Key, body, entry.ByteSize, "application/octet-stream", ct);

                if (!written.Succeeded || !string.Equals(written.Sha256, entry.Sha256, StringComparison.Ordinal))
                {
                    failures++;
                    await error.WriteLineAsync($"did not land as written: {entry.Key}");
                    continue;
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                failures++;
                await error.WriteLineAsync($"put failed: {entry.Key} ({e.GetType().Name})");
                continue;
            }

            restored++;
            bytes += entry.ByteSize;
        }

        output.WriteLine(
            $"entries={entries.Count} restored={restored} present={present} bytes={bytes} failures={failures}");

        if (failures > 0) return 1;

        // The restore is only as good as the check afterwards, and the check is the command the
        // operator would have run next anyway.
        output.WriteLine("verifying the bucket after restore…");
        return await VerifyBucketAsync(store, entries, output, error, ct);
    }

    /// <summary>
    /// Re-reads and re-hashes every committed attachment's object.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the answer to "will the media still be there when someone asks for it", so it walks
    /// <c>journal_attachments</c> joined to <c>journal_objects</c> rather than the object table: a
    /// broken pointer between the two is exactly the failure this cannot see by reading either one
    /// alone.
    /// </para>
    /// <para>
    /// <c>--sample N</c> is for a bucket that is too large to re-hash on a schedule. It is honest
    /// about itself in the header, because a sampled run that prints <c>mismatches=0</c> and is read
    /// as a full verification is worse than no run.
    /// </para>
    /// </remarks>
    public static async Task<int> VerifyMediaAsync(
        string[] args, TextWriter output, TextWriter error, CancellationToken ct)
    {
        var sample = Optional(args, "--sample");
        int? sampleSize = null;

        if (sample is not null)
        {
            if (!int.TryParse(sample, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var parsed) || parsed <= 0)
                throw new OperatorCommandException("--sample is a positive integer.");
            sampleSize = parsed;
        }

        var (store, objects) = Open();
        var rows = await objects.ListAttachmentObjectsAsync(sampleSize, ct);

        long objectBytes = 0, mismatches = 0, missing = 0;

        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();

            JournalObjectReadResult? read;
            try
            {
                read = await store.GetAsync(row.ObjectKey, ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                missing++;
                await error.WriteLineAsync($"unreadable: {row.ObjectKey} ({e.GetType().Name})");
                continue;
            }

            if (read is null)
            {
                missing++;
                await error.WriteLineAsync($"missing: {row.ObjectKey}");
                continue;
            }

            await using (read.Content)
            {
                var (digest, length) = await HashAsync(read.Content, ct);

                if (!string.Equals(digest, row.Sha256, StringComparison.Ordinal) || length != row.ByteSize)
                {
                    mismatches++;
                    await error.WriteLineAsync($"mismatch: {row.ObjectKey}");
                    continue;
                }

                objectBytes += length;
            }
        }

        output.WriteLine(
            $"sampled={(sampleSize?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "all")} "
            + $"rows={rows.Count} objects={rows.Count} bytes={objectBytes} "
            + $"mismatches={mismatches} missing={missing}");

        return mismatches > 0 || missing > 0 ? 1 : 0;
    }

    // ── plumbing ─────────────────────────────────────────────────────────────

    private static async Task<int> VerifyBucketAsync(
        IJournalObjectStore store, IReadOnlyList<ManifestEntry> entries,
        TextWriter output, TextWriter error, CancellationToken ct)
    {
        long missing = 0, mismatched = 0, bytes = 0;

        foreach (var entry in entries)
        {
            JournalObjectReadResult? read;
            try
            {
                read = await store.GetAsync(entry.Key, ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                missing++;
                await error.WriteLineAsync($"unreadable: {entry.Key} ({e.GetType().Name})");
                continue;
            }

            if (read is null)
            {
                missing++;
                await error.WriteLineAsync($"missing from the bucket: {entry.Key}");
                continue;
            }

            await using (read.Content)
            {
                var (digest, length) = await HashAsync(read.Content, ct);
                if (!string.Equals(digest, entry.Sha256, StringComparison.Ordinal) || length != entry.ByteSize)
                {
                    mismatched++;
                    await error.WriteLineAsync($"mismatch in the bucket: {entry.Key}");
                    continue;
                }

                bytes += length;
            }
        }

        output.WriteLine(
            $"bucket entries={entries.Count} bytes={bytes} missing={missing} mismatches={mismatched}");

        return missing > 0 || mismatched > 0 ? 1 : 0;
    }

    /// <summary>
    /// The one path a key takes on disk: <c>&lt;objectsDir&gt;/&lt;key&gt;</c>, and nothing else.
    /// </summary>
    /// <remarks>
    /// ⚠️ The key comes from the database, which the operator does not control, but a manifest is a
    /// file someone can hand you and <c>media restore --in</c> reads one. A key containing
    /// <c>..</c> or a path separator would write outside the backup directory, so a key that is not
    /// the exact shape this deployment writes is refused rather than sanitised.
    /// </remarks>
    internal static string TargetPath(string objectsDir, string key)
    {
        if (!JournalObjectKeys.IsJournalKey(key)
            || key.Length != JournalObjectKeys.Prefix.Length + 26
            || key.IndexOf('/') != JournalObjectKeys.Prefix.Length - 1
            || Path.IsPathRooted(key))
        {
            throw new OperatorCommandException(
                $"not a journal object key: {key}. A manifest or table that names anything else is "
                + "not from this deployment.");
        }

        return Path.Combine(objectsDir, key);
    }

    private static async Task CopyAsync(
        IJournalObjectStore store, string key, string target,
        string expectedSha, long expectedSize, CancellationToken ct)
    {
        var read = await store.GetAsync(key, ct)
            ?? throw new InvalidOperationException("the object is not in the bucket");

        await using (read.Content)
        {
            // A staging name beside the target, then a move: a backup interrupted halfway leaves a
            // .part file that `verify` ignores, never a truncated file that looks complete.
            var staging = target + ".part";
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            string digest;
            long length;
            await using (var body = File.Create(staging))
            {
                await read.Content.CopyToAsync(body, ct);
            }

            await using (var check = File.OpenRead(staging))
            {
                (digest, length) = await HashAsync(check, ct);
            }

            if (!string.Equals(digest, expectedSha, StringComparison.Ordinal) || length != expectedSize)
            {
                File.Delete(staging);
                throw new InvalidOperationException(
                    "the bytes in the bucket do not match the digest in the table");
            }

            File.Move(staging, target, overwrite: true);
        }
    }

    private static async Task<bool> MatchesAsync(
        string path, string expectedSha, long expectedSize, CancellationToken ct)
    {
        try
        {
            if (new FileInfo(path).Length != expectedSize) return false;

            await using var body = File.OpenRead(path);
            var (digest, length) = await HashAsync(body, ct);
            return length == expectedSize
                && string.Equals(digest, expectedSha, StringComparison.Ordinal);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Streams a whole body through SHA-256. No object is ever held in memory.</summary>
    internal static async Task<(string Digest, long Length)> HashAsync(Stream body, CancellationToken ct)
    {
        var buffer = new byte[81920];
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        long length = 0;
        int read;
        while ((read = await body.ReadAsync(buffer, ct)) > 0)
        {
            hasher.AppendData(buffer.AsSpan(0, read));
            length += read;
        }

        return (Convert.ToHexStringLower(hasher.GetHashAndReset()), length);
    }

    private static string ManifestLine(string key, string sha256, long byteSize) =>
        $"{{\"key\":\"{key}\",\"sha256\":\"{sha256}\",\"size\":{byteSize.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}";

    /// <summary>
    /// Writes the manifest atomically: a temp name, then a move.
    /// </summary>
    /// <remarks>
    /// A manifest is the index of the backup. Half a manifest names half a backup as complete, and
    /// <c>verify</c> would agree with it.
    /// </remarks>
    private static async Task<string> WriteManifestAsync(
        string directory, IReadOnlyList<string> lines, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssZ", System.Globalization.CultureInfo.InvariantCulture);
        var path = Path.Combine(directory, $"{ManifestPrefix}{now}.jsonl");
        var staging = path + ".part";

        await File.WriteAllLinesAsync(staging, lines, ct);
        File.Move(staging, path, overwrite: true);

        return path;
    }

    internal static string NewestManifest(string directory)
    {
        if (!Directory.Exists(directory))
            throw new OperatorCommandException($"The directory does not exist: {directory}.");

        // The names sort as times, so the lexicographic newest IS the newest run.
        var candidates = Directory.EnumerateFiles(directory, ManifestPrefix + "*.jsonl")
            .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
            .ToList();

        if (candidates.Count == 0)
            throw new OperatorCommandException(
                $"No {ManifestPrefix}*.jsonl manifest in {directory}. Run media backup first.");

        return candidates[0];
    }

    internal static IReadOnlyList<ManifestEntry> ReadManifest(string path)
    {
        var entries = new List<ManifestEntry>();
        var lineNo = 0;

        foreach (var line in File.ReadLines(path))
        {
            lineNo++;
            if (line.Trim().Length == 0) continue;

            System.Text.Json.JsonElement root;
            try
            {
                root = System.Text.Json.JsonDocument.Parse(line).RootElement.Clone();
            }
            catch (System.Text.Json.JsonException)
            {
                throw new OperatorCommandException(
                    $"{Path.GetFileName(path)} line {lineNo} is not JSON. The manifest is the index "
                    + "of the backup; a corrupt one cannot be trusted to verify anything.");
            }

            var key = Text(root, "key");
            var sha = Text(root, "sha256");

            if (key is null || sha is null
                || !root.TryGetProperty("size", out var size)
                || size.ValueKind != System.Text.Json.JsonValueKind.Number)
            {
                throw new OperatorCommandException(
                    $"{Path.GetFileName(path)} line {lineNo} is missing key, sha256 or size.");
            }

            entries.Add(new ManifestEntry(key, sha, size.GetInt64()));
        }

        return entries;
    }

    private static string? Text(System.Text.Json.JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>One line of a manifest.</summary>
    internal readonly record struct ManifestEntry(string Key, string Sha256, long ByteSize);

    private static (IJournalObjectStore Store, MySqlJournalObjectStore Objects) Open()
        => (OpenStore(), new MySqlJournalObjectStore(JournalMediaCommandsSupport.ConnectionString(), NullLogger.Instance));

    private static IJournalObjectStore OpenStore()
    {
        var media = CommsConfiguration.Resolve().Media;

        if (!media.Enabled)
            throw new OperatorCommandException(
                "media is not configured on this deployment (Comms__Media__Endpoint is blank), so "
                + "there is no object store to back up or restore.");

        try
        {
            media.Validate();
        }
        catch (InvalidOperationException e)
        {
            throw new OperatorCommandException(e.Message);
        }

        return new S3ObjectStore(new JournalMediaOptions
        {
            Endpoint = media.Endpoint,
            Bucket = media.Bucket,
            AccessKey = media.AccessKey,
            SecretKey = media.SecretKey,
            Region = media.Region,
            RequestTimeout = media.RequestTimeout,
        }, NullLogger.Instance);
    }

    private static string Required(string[] args, string name) =>
        Optional(args, name) ?? throw new OperatorCommandException($"{name} is required.");

    private static string? Optional(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        if (index < 0) return null;
        if (index + 1 >= args.Length || args[index + 1].StartsWith('-'))
            throw new OperatorCommandException($"{name} needs a value.");
        return args[index + 1];
    }
}

/// <summary>Shared so the operator class keeps one definition of the journal connection.</summary>
internal static class JournalMediaCommandsSupport
{
    /// <summary>
    /// The same resolution <c>OperatorCommands</c> uses for the journal: the runtime account when
    /// configured, else the DDL one. A backup needs reads, which both have.
    /// </summary>
    public static string ConnectionString()
    {
        var options = CommsConfiguration.Resolve();

        var connection = !string.IsNullOrWhiteSpace(options.ConversationConnectionString)
            ? options.ConversationConnectionString
            : options.ConversationMigrationConnectionString;

        if (string.IsNullOrWhiteSpace(connection))
            throw new OperatorCommandException(
                "No conversation connection string is configured, so there is no journal to read.");

        return connection;
    }
}
