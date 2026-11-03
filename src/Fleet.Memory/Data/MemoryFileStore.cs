using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Fleet.Memory.Configuration;
using Fleet.Memory.Models;
using Microsoft.Extensions.Options;
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

    /// <summary>What a no-follow lookup observed at a path; anything not proven is <see cref="Unobservable"/>.</summary>
    internal enum EntryKind { Absent, Directory, File, Link, Special, Unobservable }

    private const string EntryRefused = "memory_stats: linked, special or unobservable entry";

    // Explicit options: the defaults of EnumerationOptions would skip inaccessible and hidden entries.
    private static readonly EnumerationOptions ScanOptions = new()
    {
        IgnoreInaccessible = false, AttributesToSkip = 0, MatchType = MatchType.Simple, RecurseSubdirectories = false,
    };

    /// <summary>
    /// #518: per-type active counts and the archived count (key <c>_archived</c>) of the parsed memories whose project
    /// <paramref name="canRead"/> allows, for ACL-enabled <c>memory_stats</c>; every type row is present, zero included.
    /// Active records count under their directory's type. Only an observed-absent type or archive directory counts as
    /// zero. A file without frontmatter or with malformed YAML is not a classifiable record and is left out unnamed and
    /// uncounted. A linked or special entry is refused before it is followed or opened; that refusal and any lookup,
    /// enumeration or read failure throw <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/>, so the
    /// caller reports no totals. Cancellation throws. Reads files only, never the index.
    /// </summary>
    public Task<Dictionary<string, int>> GetScopedStatsAsync(Func<string?, bool> canRead, CancellationToken ct) =>
        GetScopedStatsAsync(canRead, ct, ProbeEntry);

    /// <param name="probe">The no-follow lookup; tests substitute it to reproduce failures a root test user cannot.</param>
    internal async Task<Dictionary<string, int>> GetScopedStatsAsync(Func<string?, bool> canRead, CancellationToken ct, Func<string, EntryKind> probe)
    {
        var stats = new Dictionary<string, int>();
        foreach (var type in MemoryDocument.ValidTypes)
            stats[type] = await CountReadableAsync(Path.Combine(_basePath, type), canRead, probe, ct);
        stats["_archived"] = await CountReadableAsync(Path.Combine(_basePath, "_archived"), canRead, probe, ct);
        return stats;
    }

    private static async Task<int> CountReadableAsync(string dir, Func<string?, bool> canRead, Func<string, EntryKind> probe, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        switch (probe(dir))
        {
            case EntryKind.Absent: return 0;
            case EntryKind.Directory: break;
            default: throw new IOException(EntryRefused);
        }

        var count = 0;
        foreach (var path in Directory.EnumerateFileSystemEntries(dir, "*.md", ScanOptions))
        {
            ct.ThrowIfCancellationRequested();
            var kind = probe(path);
            // A subdirectory is not a memory file; every other scan enumerates files only.
            if (kind == EntryKind.Directory) continue;
            if (kind != EntryKind.File) throw new IOException(EntryRefused);

            var text = await File.ReadAllTextAsync(path, ct);
            MemoryDocument? doc;
            // Malformed frontmatter can also fail slicing (ArgumentException) before the YAML parse wraps its error.
            try { doc = ParseMarkdown(text, path); }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException) { continue; }
            if (doc is not null && canRead(doc.Project)) count++;
        }
        return count;
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
    /// #518: Linux <c>statx</c> with <c>AT_SYMLINK_NOFOLLOW</c>, so a link is reported as a link and never followed (.NET
    /// file attributes cannot tell a FIFO, socket or device from a regular file). Only ENOENT proves absence; any other
    /// error, a missing type field or another platform is unobservable, never absence.
    /// </summary>
    internal static EntryKind ProbeEntry(string path)
    {
        try
        {
            if (!OperatingSystem.IsLinux()) return EntryKind.Unobservable;
            if (StatxCall(-100, path, 0x100, 0x1, out var buffer) != 0)
                return Marshal.GetLastPInvokeError() == 2 ? EntryKind.Absent : EntryKind.Unobservable;
            if ((buffer.Mask & 0x1) == 0) return EntryKind.Unobservable;
            return (buffer.Mode & 0xF000) switch
            {
                0x4000 => EntryKind.Directory,
                0x8000 => EntryKind.File,
                0xA000 => EntryKind.Link,
                _ => EntryKind.Special,
            };
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException or MarshalDirectiveException)
        {
            return EntryKind.Unobservable;
        }
    }

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
