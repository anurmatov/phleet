using System.Globalization;
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

    public async Task<MemoryDocument> SaveAsync(MemoryDocument doc)
    {
        var dir = Path.Combine(_basePath, doc.Type);
        Directory.CreateDirectory(dir);

        var slug = Slugify(doc.Title);
        var shortId = doc.Id[..8];
        var date = doc.Created.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var fileName = $"{date}_{shortId}_{slug}.md";
        var filePath = Path.Combine(dir, fileName);

        var content = SerializeToMarkdown(doc);
        await File.WriteAllTextAsync(filePath, content);

        doc.FilePath = filePath;
        logger.LogInformation("Saved memory {Id} to {Path}", doc.Id, filePath);
        return doc;
    }

    public async Task<MemoryDocument> UpdateAsync(string filePath, Action<MemoryDocument> modify)
    {
        var doc = await ParseFileAsync(filePath)
            ?? throw new FileNotFoundException($"Memory file not found: {filePath}");

        modify(doc);
        doc.Updated = DateTimeOffset.UtcNow;

        var content = SerializeToMarkdown(doc);
        await File.WriteAllTextAsync(filePath, content);

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

    public async Task<List<MemoryDocument>> ListAllAsync()
    {
        var results = new List<MemoryDocument>();

        foreach (var type in MemoryDocument.ValidTypes)
        {
            var dir = Path.Combine(_basePath, type);
            if (!Directory.Exists(dir))
                continue;

            foreach (var file in Directory.EnumerateFiles(dir, "*.md"))
            {
                var doc = await ParseFileAsync(file);
                if (doc is not null)
                    results.Add(doc);
            }
        }

        return results;
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
                var doc = await ParseFileAsync(file);
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

    public string? FindFileById(string id)
    {
        foreach (var type in MemoryDocument.ValidTypes)
        {
            var dir = Path.Combine(_basePath, type);
            if (!Directory.Exists(dir))
                continue;

            // ID prefix is in the filename: {date}_{shortId}_{slug}.md
            var shortId = id.Length >= 8 ? id[..8] : id;
            foreach (var file in Directory.EnumerateFiles(dir, "*.md"))
            {
                var fileName = Path.GetFileName(file);
                if (fileName.Contains(shortId, StringComparison.OrdinalIgnoreCase))
                    return file;
            }
        }

        return null;
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

    private static MemoryDocument? ParseMarkdown(string text, string filePath)
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
        catch
        {
            return null;
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

    private static string SerializeToMarkdown(MemoryDocument doc)
    {
        var sb = new StringBuilder();
        sb.AppendLine("---");
        sb.AppendLine($"id: {doc.Id}");
        sb.AppendLine($"agent: {doc.Agent}");
        sb.AppendLine($"title: \"{EscapeYamlString(doc.Title)}\"");
        if (!string.IsNullOrEmpty(doc.Project))
            sb.AppendLine($"project: {doc.Project}");
        if (doc.Tags.Count > 0)
            sb.AppendLine($"tags: [{string.Join(", ", doc.Tags)}]");
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
