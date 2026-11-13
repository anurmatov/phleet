namespace Fleet.Orchestrator.Services;

/// <summary>
/// The deployment's <c>.env</c> file: the one place the orchestrator parses it and the one place
/// it rewrites it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Reading.</b> A line is <c>KEY=value</c>, split on the first <c>=</c>. Blank lines, lines
/// starting with <c>#</c> and lines with no <c>=</c> or no key are skipped; a key given twice keeps
/// its last value. The value is trimmed, then unquoted by <see cref="ParseValue"/>.
/// </para>
/// <para>
/// <b>Writing.</b> Every rewrite goes through <see cref="EditAsync"/> or holds
/// <see cref="LockAsync"/> itself, so two writers in this process never interleave: each reads the
/// file, changes its lines and replaces it (temp file, then rename) before the next one starts.
/// The lock is process-wide because the file is. It does not guard against another process
/// (<c>setup.sh</c>, an editor).
/// </para>
/// </remarks>
public static class EnvFile
{
    public const string DefaultPath = "/app/deploy/.env";

    /// <summary>The configured path of the file (<c>Provisioning:EnvFilePath</c>).</summary>
    public static string PathFrom(IConfiguration configuration) =>
        configuration["Provisioning:EnvFilePath"] ?? DefaultPath;

    // ── Reading ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The value of one line, as written after the <c>=</c>: trimmed, and without its quotes.
    /// </summary>
    /// <remarks>
    /// Double quotes at either end are removed. Otherwise, a value wholly inside one pair of single
    /// quotes loses that pair, as Docker Compose reads the same file. A single quote that is not
    /// part of such a pair belongs to the value and stays.
    /// </remarks>
    public static string ParseValue(string raw)
    {
        var trimmed = raw.Trim();
        var value = trimmed.Trim('"');
        if (value.Length != trimmed.Length)
            return value;
        return value.Length >= 2 && value[0] == '\'' && value[^1] == '\''
            ? value[1..^1]
            : value;
    }

    public static Dictionary<string, string> Parse(IEnumerable<string> lines)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith('#')) continue;
            var idx = trimmed.IndexOf('=');
            if (idx < 0) continue;
            var key = trimmed[..idx].Trim();
            if (key.Length == 0) continue;
            result[key] = ParseValue(trimmed[(idx + 1)..]);
        }
        return result;
    }

    /// <summary>
    /// The file's keys and values. A file that is missing or cannot be read is an empty result:
    /// callers treat the two alike, and a key that is not there is simply not configured.
    /// </summary>
    public static Dictionary<string, string> Read(string path)
    {
        try
        {
            return ReadOrThrow(path);
        }
        catch (Exception)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    /// <summary><see cref="Read"/> for a caller that reports a file it could not read. A missing file is still empty.</summary>
    public static Dictionary<string, string> ReadOrThrow(string path) =>
        File.Exists(path) ? Parse(File.ReadAllLines(path)) : new Dictionary<string, string>(StringComparer.Ordinal);

    // ── Writing ───────────────────────────────────────────────────────────────

    private static readonly SemaphoreSlim WriteLock = new(1, 1);

    /// <summary>How long a writer waits for another one before giving up.</summary>
    public static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Takes the write lock. Dispose the result to release it. Hold it across a read that a write
    /// depends on, then write with <see cref="EditWhileLockedAsync"/>.
    /// </summary>
    /// <exception cref="TimeoutException">Another writer held the lock for <see cref="LockTimeout"/>.</exception>
    public static async Task<IDisposable> LockAsync(CancellationToken ct = default)
    {
        if (!await WriteLock.WaitAsync(LockTimeout, ct))
            throw new TimeoutException("Another config write is in progress. Try again shortly.");
        return new Held();
    }

    /// <summary>Reads the file's lines, applies <paramref name="edit"/> and replaces the file, under the write lock.</summary>
    /// <param name="backup">Copy the current file to <c>.env.bak</c> first. A failed copy does not stop the write.</param>
    /// <param name="backupFailed">Told when that copy failed.</param>
    public static async Task EditAsync(
        string path, Func<string[], string[]> edit, CancellationToken ct = default,
        bool backup = false, Action<Exception>? backupFailed = null)
    {
        using var held = await LockAsync(ct);
        await EditWhileLockedAsync(path, edit, ct, backup, backupFailed);
    }

    /// <summary><see cref="EditAsync"/> for a caller that already holds <see cref="LockAsync"/>.</summary>
    public static async Task EditWhileLockedAsync(
        string path, Func<string[], string[]> edit, CancellationToken ct = default,
        bool backup = false, Action<Exception>? backupFailed = null)
    {
        var lines = File.Exists(path) ? await File.ReadAllLinesAsync(path, ct) : [];
        var updated = edit(lines);

        if (backup && File.Exists(path))
        {
            try { File.Copy(path, path + ".bak", overwrite: true); }
            catch (Exception ex) { backupFailed?.Invoke(ex); }
        }

        var tmp = path + ".tmp";
        try
        {
            await File.WriteAllLinesAsync(tmp, updated, ct);
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            // A half-written copy of the secrets file is not left beside it.
            try { File.Delete(tmp); } catch { /* best effort */ }
            throw;
        }
    }

    /// <summary>
    /// <paramref name="lines"/> with each key in <paramref name="updates"/> set: replaced where it
    /// is (the last occurrence, earlier duplicates dropped), appended otherwise. Comments, blank
    /// lines and other keys are kept as they are.
    /// </summary>
    /// <exception cref="ArgumentException">A value contains a line break.</exception>
    public static string[] Upsert(string[] lines, Dictionary<string, string> updates)
    {
        // Validate: no newlines in values
        foreach (var (k, v) in updates)
        {
            if (v.Contains('\n') || v.Contains('\r'))
                throw new ArgumentException($"Value for {k} contains a newline character — not allowed in .env files");
        }

        var result = new List<string>(lines.Length + updates.Count);
        var written = new HashSet<string>(StringComparer.Ordinal);

        // Track last index of each key to handle duplicate-key case (keep last)
        var lastIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.StartsWith('#') || !trimmed.Contains('=')) continue;
            var key = trimmed[..trimmed.IndexOf('=')].Trim();
            lastIndex[key] = i;
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.StartsWith('#') || !trimmed.Contains('='))
            {
                result.Add(lines[i]);
                continue;
            }

            var eqIdx = trimmed.IndexOf('=');
            var key = trimmed[..eqIdx].Trim();

            // Skip duplicate keys that will be overwritten by the last occurrence
            if (lastIndex.TryGetValue(key, out var last) && last != i)
                continue;

            if (updates.TryGetValue(key, out var newVal))
            {
                result.Add($"{key}={newVal}");
                written.Add(key);
            }
            else
            {
                result.Add(lines[i]);
            }
        }

        // Append any keys from updates not yet written
        foreach (var (k, v) in updates)
        {
            if (!written.Contains(k))
                result.Add($"{k}={v}");
        }

        return [.. result];
    }

    /// <summary><paramref name="lines"/> without any line that sets <paramref name="key"/>.</summary>
    public static string[] Remove(string[] lines, string key) =>
        lines.Where(line =>
        {
            var t = line.Trim();
            if (t.StartsWith('#') || !t.Contains('=')) return true;
            var k = t[..t.IndexOf('=')].Trim();
            return !k.Equals(key, StringComparison.Ordinal);
        }).ToArray();

    private sealed class Held : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                WriteLock.Release();
        }
    }
}

/// <summary>
/// The deployment's CTO agent (<c>FLEET_CTO_AGENT</c>), read the same way by everything that asks.
/// </summary>
/// <remarks>
/// The <c>.env</c> file comes first. Creating the first co-cto agent writes the name there while
/// the orchestrator is running, and the orchestrator's own environment is a copy of the file taken
/// when its container was created, so it still holds the old value, usually blank. The
/// configuration value is the fallback for a deployment that sets the name in the environment only.
/// </remarks>
public static class CtoAgentSetting
{
    public const string Key = "FLEET_CTO_AGENT";

    /// <summary>The CTO agent's name, or null when none is configured.</summary>
    public static string? Resolve(IConfiguration configuration) =>
        Resolve(EnvFile.Read(EnvFile.PathFrom(configuration)), configuration);

    /// <summary><see cref="Resolve(IConfiguration)"/> for a caller that has already read the file.</summary>
    public static string? Resolve(IReadOnlyDictionary<string, string> envFile, IConfiguration configuration)
    {
        if (envFile.TryGetValue(Key, out var fromFile) && !string.IsNullOrWhiteSpace(fromFile))
            return fromFile.Trim();
        return configuration[Key] is { } fromConfiguration && !string.IsNullOrWhiteSpace(fromConfiguration)
            ? fromConfiguration.Trim()
            : null;
    }
}
