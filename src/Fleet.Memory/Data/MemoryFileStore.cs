using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Fleet.Memory.Configuration;
using Fleet.Memory.Models;
using Microsoft.Extensions.Options;
using Microsoft.Win32.SafeHandles;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Fleet.Memory.Data;

public sealed partial class MemoryFileStore(IOptions<StorageOptions> storageOptions, ILogger<MemoryFileStore> logger)
{
    private readonly string _basePath = storageOptions.Value.Path;

    private static readonly IDeserializer YamlDeserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    private static readonly ISerializer YamlSerializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitDefaults)
        .Build();

    public void EnsureDirectories()
    {
        foreach (var type in MemoryDocument.ValidTypes)
            Directory.CreateDirectory(Path.Combine(_basePath, type));

        Directory.CreateDirectory(Path.Combine(_basePath, "_archived"));
    }

    /// <summary>
    /// Deletes any stale .tmp.* files left behind by crashed write operations.
    /// Safe to call at startup before FullIndexAsync runs.
    /// </summary>
    public void CleanStaleTempFiles()
    {
        var cleaned = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(_basePath, "*.tmp.*", SearchOption.AllDirectories))
            {
                try
                {
                    File.Delete(file);
                    cleaned++;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to delete stale temp file: {Path}", file);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error scanning for stale temp files in {Path}", _basePath);
        }

        if (cleaned > 0)
            logger.LogInformation("Cleaned {Count} stale temp file(s) on startup", cleaned);
    }

    public async Task<MemoryDocument> SaveAsync(MemoryDocument doc)
    {
        var dir = Path.Combine(_basePath, doc.Type);
        Directory.CreateDirectory(dir);

        var slug = Slugify(doc.Title);
        var shortId = doc.Id[..8];
        var date = doc.Created.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var fileName = $"{date}_{shortId}_{slug}.md";
        var finalPath = Path.Combine(dir, fileName);

        var content = SerializeToMarkdown(doc);

        // Pre-write validation: round-trip parse to catch YAML issues before touching disk.
        // Throws InvalidDataException if the serialized content would be unparseable.
        _ = ParseMarkdown(content, finalPath);

        var tmpPath = $"{finalPath}.tmp.{Guid.NewGuid():N}";
        await File.WriteAllTextAsync(tmpPath, content);

        try
        {
            File.Move(tmpPath, finalPath, overwrite: false);
        }
        catch
        {
            try { File.Delete(tmpPath); } catch { /* best effort */ }
            throw;
        }

        doc.FilePath = finalPath;
        logger.LogInformation("Saved memory {Id} to {Path}", doc.Id, finalPath);
        return doc;
    }

    public async Task<MemoryDocument> UpdateAsync(string filePath, Action<MemoryDocument> modify)
    {
        var doc = await ParseFileAsync(filePath)
            ?? throw new FileNotFoundException($"Memory file not found: {filePath}");

        modify(doc);
        doc.Updated = DateTimeOffset.UtcNow;

        var content = SerializeToMarkdown(doc);

        // Pre-write validation: catch YAML issues before overwriting the existing file.
        // Throws InvalidDataException if the new serialized content would be unparseable.
        _ = ParseMarkdown(content, filePath);

        var tmpPath = $"{filePath}.tmp.{Guid.NewGuid():N}";
        await File.WriteAllTextAsync(tmpPath, content);

        try
        {
            File.Move(tmpPath, filePath, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmpPath); } catch { /* best effort */ }
            throw;
        }

        logger.LogInformation("Updated memory {Id} at {Path}", doc.Id, filePath);
        return doc;
    }

    public void Delete(string filePath, bool permanent)
    {
        if (!File.Exists(filePath))
            return;

        if (permanent)
        {
            File.Delete(filePath);
            logger.LogInformation("Permanently deleted {Path}", filePath);
        }
        else
        {
            var archiveDir = Path.Combine(_basePath, "_archived");
            Directory.CreateDirectory(archiveDir);
            var dest = Path.Combine(archiveDir, Path.GetFileName(filePath));
            File.Move(filePath, dest, overwrite: true);
            logger.LogInformation("Archived {Path} to {Dest}", filePath, dest);
        }
    }

    public async Task<MemoryDocument?> ParseFileAsync(string filePath)
    {
        if (!File.Exists(filePath))
            return null;

        var text = await File.ReadAllTextAsync(filePath);
        return ParseMarkdown(text, filePath);
    }

    /// <summary>
    /// Every parsed memory under the type directories, plus how many enumerated <c>.md</c> files were
    /// skipped as corrupt — unparseable YAML, or no frontmatter at all (not a memory file). So the
    /// files enumerated are exactly <c>Documents.Count + Corrupt</c>.
    /// </summary>
    public async Task<(List<MemoryDocument> Documents, int Corrupt)> ListAllAsync()
    {
        var results = new List<MemoryDocument>();
        var corrupt = 0;

        foreach (var type in MemoryDocument.ValidTypes)
        {
            var dir = Path.Combine(_basePath, type);
            if (!Directory.Exists(dir))
                continue;

            foreach (var file in Directory.EnumerateFiles(dir, "*.md"))
            {
                try
                {
                    var doc = await ParseFileAsync(file);
                    if (doc is not null)
                        results.Add(doc);
                    else
                        corrupt++;
                }
                catch (InvalidDataException ex)
                {
                    corrupt++;
                    logger.LogError(ex, "Skipping corrupt memory file during scan: {Path}", file);
                }
            }
        }

        return (results, corrupt);
    }

    public async Task<List<MemoryDocument>> ListFilteredAsync(string? type = null, string? project = null, string? agent = null, string? tag = null)
    {
        var types = type is not null ? [type] : MemoryDocument.ValidTypes;
        var results = new List<MemoryDocument>();

        foreach (var t in types)
        {
            var dir = Path.Combine(_basePath, t);
            if (!Directory.Exists(dir))
                continue;

            foreach (var file in Directory.EnumerateFiles(dir, "*.md"))
            {
                MemoryDocument? doc;
                try
                {
                    doc = await ParseFileAsync(file);
                }
                catch (InvalidDataException ex)
                {
                    logger.LogError(ex, "Skipping corrupt memory file during filtered scan: {Path}", file);
                    continue;
                }

                if (doc is null) continue;

                if (project is not null && !MemoryDocument.NormalizeProject(doc.Project).Equals(MemoryDocument.NormalizeProject(project), StringComparison.Ordinal))
                    continue;
                if (agent is not null && !doc.Agent.Equals(agent, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (tag is not null && !doc.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
                    continue;

                results.Add(doc);
            }
        }

        return results;
    }

    /// <summary>
    /// Finds a memory file by full UUID or 8-character short ID. Only the ID segment of the file name
    /// is compared, never the slug, which can contain another memory's short ID (#469). An ID longer
    /// than 8 characters must also prefix the file's front-matter <c>id</c> (a full UUID must equal it).
    /// Returns null when nothing matches; throws <see cref="AmbiguousMemoryIdException"/> when several do.
    /// </summary>
    public string? FindFileById(string id)
    {
        var shortId = id.Length >= 8 ? id[..8] : id;
        var matches = new List<string>();

        foreach (var type in MemoryDocument.ValidTypes)
        {
            var dir = Path.Combine(_basePath, type);
            if (!Directory.Exists(dir))
                continue;

            foreach (var file in Directory.EnumerateFiles(dir, "*.md"))
            {
                // ID prefix is in the filename: {date}_{shortId}_{slug}.md
                var segments = Path.GetFileName(file).Split('_');
                if (segments.Length < 3 || !segments[1].Equals(shortId, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (id.Length > 8 && !FrontMatterIdStartsWith(file, id))
                    continue;

                matches.Add(file);
            }
        }

        return matches.Count switch
        {
            0 => null,
            1 => matches[0],
            _ => throw new AmbiguousMemoryIdException(id, matches.Count, fullId: id.Length > 8),
        };
    }

    private bool FrontMatterIdStartsWith(string file, string id)
    {
        try
        {
            var doc = ParseMarkdown(File.ReadAllText(file), file);
            return doc is not null && doc.Id.StartsWith(id, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            logger.LogWarning(ex, "Cannot confirm the id of memory file {Path}; skipping it for id {Id}", file, id);
            return false;
        }
    }

    public Dictionary<string, int> GetStats()
    {
        var stats = new Dictionary<string, int>();

        foreach (var type in MemoryDocument.ValidTypes)
        {
            var dir = Path.Combine(_basePath, type);
            var count = Directory.Exists(dir)
                ? Directory.EnumerateFiles(dir, "*.md").Count()
                : 0;
            stats[type] = count;
        }

        var archiveDir = Path.Combine(_basePath, "_archived");
        stats["_archived"] = Directory.Exists(archiveDir)
            ? Directory.EnumerateFiles(archiveDir, "*.md").Count()
            : 0;

        return stats;
    }

    private const string EntryRefused = "memory_stats: linked, special or unreadable entry";

    /// <summary>
    /// #518: per-type active counts and the archived count (key <c>_archived</c>) of the parsed memories whose project
    /// <paramref name="canRead"/> allows, for ACL-enabled <c>memory_stats</c>; every type row is present, zero included.
    /// Active records count under their directory's type. The scan holds no-follow handles: each type and archive
    /// directory is opened once below the storage root and listed and read only through that handle, and each entry is
    /// opened without following a link and without blocking, then checked to be a regular file on the opened handle.
    /// Replacing a checked path cannot redirect the scan. Only an observed-absent type or archive directory counts as zero.
    /// A file without frontmatter or with malformed YAML is not a classifiable record and is left out unnamed. A link or
    /// special entry, and any lookup, listing or read failure, throws <see cref="IOException"/> or
    /// <see cref="UnauthorizedAccessException"/>, so the caller reports no totals. Cancellation throws. Files only, never
    /// the index.
    /// </summary>
    public Task<Dictionary<string, int>> GetScopedStatsAsync(Func<string?, bool> canRead, CancellationToken ct) =>
        GetScopedStatsAsync(canRead, ct, null);

    /// <param name="afterOpen">
    /// Test seam, null in production: called with the relative path after a directory handle is open (<c>learning</c>)
    /// and after an entry is listed, before it is opened (<c>learning/NAME.md</c>), so tests can replace it there.
    /// </param>
    internal async Task<Dictionary<string, int>> GetScopedStatsAsync(Func<string?, bool> canRead, CancellationToken ct, Action<string>? afterOpen)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            using var root = ScanDirectory.OpenRoot(_basePath);
            var stats = new Dictionary<string, int>();
            foreach (var type in MemoryDocument.ValidTypes)
                stats[type] = await CountReadableAsync(root, type, canRead, afterOpen, ct);
            stats["_archived"] = await CountReadableAsync(root, "_archived", canRead, afterOpen, ct);
            return stats;
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException or MarshalDirectiveException)
        {
            throw new IOException(EntryRefused, ex);
        }
    }

    private static async Task<int> CountReadableAsync(ScanDirectory root, string name, Func<string?, bool> canRead,
        Action<string>? afterOpen, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var dir = root.OpenChildDirectory(name);
        if (dir is null) return 0;
        afterOpen?.Invoke(name);

        var count = 0;
        foreach (var entry in dir.MarkdownEntries())
        {
            ct.ThrowIfCancellationRequested();
            var relative = name + "/" + Encoding.UTF8.GetString(entry);
            afterOpen?.Invoke(relative);
            using var file = dir.OpenRegularFile(entry);
            // A subdirectory is not a memory file; every other scan enumerates files only.
            if (file is null) continue;

            var text = await ReadTextAsync(file, ct);
            MemoryDocument? doc;
            // Malformed frontmatter can also fail slicing (ArgumentException) before the YAML parse wraps its error.
            try { doc = ParseMarkdown(text, relative); }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException) { continue; }
            if (doc is not null && canRead(doc.Project)) count++;
        }
        return count;
    }

    /// <summary>The whole regular file behind <paramref name="file"/>, decoded as <see cref="File.ReadAllText(string)"/> does.</summary>
    private static async Task<string> ReadTextAsync(SafeFileHandle file, CancellationToken ct)
    {
        var length = RandomAccess.GetLength(file);
        if (length > int.MaxValue) throw new IOException(EntryRefused);
        var buffer = new byte[length];
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await RandomAccess.ReadAsync(file, buffer.AsMemory(read), read, ct);
            if (n == 0) break;
            read += n;
        }
        using var reader = new StreamReader(new MemoryStream(buffer, 0, read), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(ct);
    }

    /// <summary>
    /// #518: one open directory for the stats scan (Linux x86-64 or arm64 only): children are opened relative to this
    /// handle with <c>O_NOFOLLOW</c>, listed with <c>readdir</c> on it, and typed with <c>statx</c> on the opened
    /// descriptor, never by path. Any other platform or architecture refuses.
    /// </summary>
    private sealed class ScanDirectory : IDisposable
    {
        private const int ReadOnly = 0, NoControllingTty = 0x100, NonBlocking = 0x800, CloseOnExec = 0x80000;
        private const int CurrentDirectory = -100, EmptyPath = 0x1000, StatxType = 0x1, NoEntry = 2;
        private IntPtr _dir;

        private ScanDirectory(IntPtr dir) => _dir = dir;

        // O_DIRECTORY / O_NOFOLLOW are architecture-specific; the other flags are the generic Linux values.
        private static (int Directory, int NoFollow) Flags() =>
            !OperatingSystem.IsLinux() ? throw new IOException(EntryRefused) : RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => (0x10000, 0x20000),
                Architecture.Arm64 => (0x4000, 0x8000),
                _ => throw new IOException(EntryRefused),
            };

        /// <summary>The configured storage root (an operator path, followed as configured); it must exist.</summary>
        public static ScanDirectory OpenRoot(string path) =>
            FromDescriptor(OpenAt(CurrentDirectory, Bytes(path), ReadOnly | Flags().Directory | CloseOnExec))
                ?? throw new IOException(EntryRefused);

        /// <summary>A child directory without following a link; null only when it is observed absent.</summary>
        public ScanDirectory? OpenChildDirectory(string name)
        {
            var (directory, noFollow) = Flags();
            var fd = OpenAt(DirFd(_dir), Bytes(name), ReadOnly | directory | noFollow | NonBlocking | CloseOnExec);
            if (fd < 0 && Marshal.GetLastPInvokeError() == NoEntry) return null;
            return FromDescriptor(fd) ?? throw new IOException(EntryRefused);
        }

        /// <summary>
        /// Opens an entry without following a link and without blocking (a FIFO opens immediately, a socket fails), then
        /// types the opened descriptor: a regular file is returned, a directory is null, anything else refuses.
        /// </summary>
        public SafeFileHandle? OpenRegularFile(byte[] name)
        {
            var fd = OpenAt(DirFd(_dir), Terminated(name), ReadOnly | Flags().NoFollow | NonBlocking | NoControllingTty | CloseOnExec);
            if (fd < 0) throw new IOException(EntryRefused);
            var file = new SafeFileHandle(fd, ownsHandle: true);
            if (StatxCall(fd, "", EmptyPath, StatxType, out var status) != 0 || (status.Mask & StatxType) == 0)
            {
                file.Dispose();
                throw new IOException(EntryRefused);
            }
            switch (status.Mode & 0xF000)
            {
                case 0x8000: return file;
                case 0x4000: file.Dispose(); return null;
                default: file.Dispose(); throw new IOException(EntryRefused);
            }
        }

        /// <summary>The raw names ending in <c>.md</c>, read through this handle; a listing error refuses.</summary>
        public List<byte[]> MarkdownEntries()
        {
            var names = new List<byte[]>();
            while (true)
            {
                var entry = ReadDir(_dir);
                if (entry == IntPtr.Zero)
                {
                    // readdir signals both the end and an error with null; only errno tells them apart.
                    if (Marshal.GetLastPInvokeError() != 0) throw new IOException(EntryRefused);
                    return names;
                }
                // struct dirent on 64-bit Linux (glibc and musl): d_name starts at byte 19, NUL-terminated.
                var name = new List<byte>();
                for (var i = 0; i < 256 && Marshal.ReadByte(entry, 19 + i) is var b && b != 0; i++) name.Add(b);
                if (name.Count > 3 && name[^3] == '.' && name[^2] == 'm' && name[^1] == 'd') names.Add([.. name]);
            }
        }

        private static ScanDirectory? FromDescriptor(int fd)
        {
            if (fd < 0) return null;
            var dir = FdOpenDir(fd);
            if (dir != IntPtr.Zero) return new ScanDirectory(dir);
            Close(fd);
            return null;
        }

        public void Dispose()
        {
            if (_dir == IntPtr.Zero) return;
            CloseDir(_dir);
            _dir = IntPtr.Zero;
        }

        private static byte[] Bytes(string value) => Terminated(Encoding.UTF8.GetBytes(value));
        private static byte[] Terminated(byte[] value) => [.. value, 0];

        [DllImport("libc", EntryPoint = "openat", SetLastError = true)]
        private static extern int OpenAt(int directory, byte[] path, int flags);

        [DllImport("libc", EntryPoint = "fdopendir", SetLastError = true)]
        private static extern IntPtr FdOpenDir(int fd);

        [DllImport("libc", EntryPoint = "readdir", SetLastError = true)]
        private static extern IntPtr ReadDir(IntPtr dir);

        [DllImport("libc", EntryPoint = "dirfd")]
        private static extern int DirFd(IntPtr dir);

        [DllImport("libc", EntryPoint = "closedir")]
        private static extern int CloseDir(IntPtr dir);

        [DllImport("libc", EntryPoint = "close")]
        private static extern int Close(int fd);
    }

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct Statx
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(28)] public ushort Mode;
    }

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int StatxCall(int directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, out Statx buffer);

    /// <summary>
    /// Parses a memory markdown string.
    /// Returns null if the text has no frontmatter header (not a memory file).
    /// Throws <see cref="InvalidDataException"/> if the text has frontmatter but YAML parsing fails.
    /// </summary>
    internal static MemoryDocument? ParseMarkdown(string text, string filePath)
    {
        if (!text.StartsWith("---"))
            return null;

        var endIndex = text.IndexOf("\n---", 3, StringComparison.Ordinal);
        if (endIndex < 0)
            return null;

        var yamlBlock = text[(3 + Environment.NewLine.Length)..endIndex];
        var content = text[(endIndex + 4)..].TrimStart('\r', '\n');

        try
        {
            var frontmatter = YamlDeserializer.Deserialize<Dictionary<string, object>>(yamlBlock)
                ?? new Dictionary<string, object>();

            var tags = new List<string>();
            if (frontmatter.TryGetValue("tags", out var tagsObj) && tagsObj is List<object> tagList)
                tags = tagList.Select(t => t.ToString() ?? "").Where(t => t.Length > 0).ToList();

            var type = InferTypeFromPath(filePath);

            return new MemoryDocument
            {
                Id = frontmatter.GetValueOrDefault("id")?.ToString() ?? Guid.NewGuid().ToString(),
                Type = type,
                Title = frontmatter.GetValueOrDefault("title")?.ToString() ?? Path.GetFileNameWithoutExtension(filePath),
                Agent = frontmatter.GetValueOrDefault("agent")?.ToString() ?? "",
                Project = frontmatter.GetValueOrDefault("project")?.ToString() ?? "",
                Tags = tags,
                Source = frontmatter.GetValueOrDefault("source")?.ToString() ?? "",
                Created = ParseDateTime(frontmatter.GetValueOrDefault("created")?.ToString()),
                Updated = ParseDateTime(frontmatter.GetValueOrDefault("updated")?.ToString()),
                Content = content,
                FilePath = filePath
            };
        }
        catch (Exception ex)
        {
            throw new InvalidDataException($"YAML parse error in {filePath}: {ex.Message}", ex);
        }
    }

    private static string InferTypeFromPath(string filePath)
    {
        var dir = Path.GetFileName(Path.GetDirectoryName(filePath)) ?? "";
        return MemoryDocument.ValidTypes.Contains(dir) ? dir : "reference";
    }

    private static DateTimeOffset ParseDateTime(string? value)
    {
        if (value is null) return DateTimeOffset.UtcNow;
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dt)
            ? dt
            : DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Serializes a memory document to markdown with frontmatter.
    /// Tag values are double-quoted to prevent YAML injection from special characters.
    /// </summary>
    internal static string SerializeToMarkdown(MemoryDocument doc)
    {
        var sb = new StringBuilder();
        sb.AppendLine("---");
        sb.AppendLine($"id: {doc.Id}");
        sb.AppendLine($"agent: {doc.Agent}");
        sb.AppendLine($"title: \"{EscapeYamlString(doc.Title)}\"");
        if (!string.IsNullOrEmpty(doc.Project))
            sb.AppendLine($"project: {doc.Project}");
        if (doc.Tags.Count > 0)
            sb.AppendLine($"tags: [{string.Join(", ", doc.Tags.Select(t => $"\"{EscapeYamlString(t)}\""))}]");
        if (!string.IsNullOrEmpty(doc.Source))
            sb.AppendLine($"source: {doc.Source}");
        sb.AppendLine($"created: {doc.Created:yyyy-MM-ddTHH:mm:ssZ}");
        sb.AppendLine($"updated: {doc.Updated:yyyy-MM-ddTHH:mm:ssZ}");
        sb.AppendLine("---");
        sb.AppendLine();
        sb.Append(doc.Content);

        return sb.ToString();
    }

    private static string EscapeYamlString(string value)
        => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static string Slugify(string text)
    {
        var slug = text.ToLowerInvariant();
        slug = SlugRegex().Replace(slug, "-");
        slug = slug.Trim('-');
        return slug.Length > 60 ? slug[..60].TrimEnd('-') : slug;
    }

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex SlugRegex();
}
