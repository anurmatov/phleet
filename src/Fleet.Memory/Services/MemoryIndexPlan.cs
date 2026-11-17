using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Fleet.Memory.Data;
using Fleet.Memory.Models;

namespace Fleet.Memory.Services;

/// <summary>
/// What one memory becomes in the index: its sections, the text each is embedded and matched as,
/// the payload each point carries, and the hash that says whether the index already holds exactly
/// this.
/// </summary>
public sealed class MemoryIndexPlan
{
    /// <summary>
    /// The index format. A point written in another format (or in none: before the field existed)
    /// is replaced at the next start. Raise it when what is written for the same memory changes:
    /// the tokenizer, the sparse text, the sections, the payload.
    /// </summary>
    public const int Format = 2;

    private readonly MemoryDocument _doc;
    private readonly string _filePath;

    private MemoryIndexPlan(MemoryDocument doc, string filePath, string hash, List<MemorySection> sections, int found)
    {
        _doc = doc;
        _filePath = filePath;
        Hash = hash;
        Sections = sections;
        SectionsFound = found;
    }

    public string Hash { get; }

    /// <summary>The sections that are indexed: at most <see cref="MemoryIndexSettings.MaxSections"/>.</summary>
    public IReadOnlyList<MemorySection> Sections { get; }

    /// <summary>How many sections the content has; more than <see cref="Sections"/> when the cap cut it.</summary>
    public int SectionsFound { get; }

    public static MemoryIndexPlan Build(MemoryDocument doc, string filePath, MemoryIndexSettings settings)
    {
        var all = MemorySections.Split(doc.Content, settings.SectionChars);
        var kept = all.Count > settings.MaxSections ? all.GetRange(0, settings.MaxSections) : all;
        return new MemoryIndexPlan(doc, filePath, HashOf(doc, filePath, settings), kept, all.Count);
    }

    /// <summary>
    /// The text one section is embedded as: the title, a blank line, the section. A memory of one
    /// section is embedded as <see cref="MemoryService.EmbeddingText"/>, the string it always was.
    /// </summary>
    public string DenseText(MemorySection section) =>
        SectionsFound == 1
            ? MemoryService.EmbeddingText(_doc)
            : $"{_doc.Title}\n\n{MemorySections.Text(_doc.Content, section)}";

    /// <summary>
    /// The text one section's keywords come from. The title and the tags are the memory's, not a
    /// section's, so they are in the first section only: a word of the title matches the memory
    /// once, not once per section.
    /// </summary>
    public string SparseText(MemorySection section)
    {
        var text = SectionsFound == 1 ? _doc.Content : MemorySections.Text(_doc.Content, section);
        return section.Index == 0
            ? $"{_doc.Title}\n{string.Join("\n", _doc.Tags)}\n{text}"
            : text;
    }

    public Dictionary<string, object> Payload(MemorySection section) => new()
    {
        [IndexPayload.FilePath] = _filePath,
        [IndexPayload.MemoryId] = _doc.Id,
        [IndexPayload.MemoryType] = _doc.Type,
        [IndexPayload.AgentName] = _doc.Agent,
        [IndexPayload.Project] = MemoryDocument.NormalizeProject(_doc.Project),
        [IndexPayload.Title] = _doc.Title,
        [IndexPayload.Tags] = _doc.Tags,
        [IndexPayload.Created] = _doc.Created.ToString("o"),
        [IndexPayload.Updated] = _doc.Updated.ToString("o"),
        [IndexPayload.UpdatedTs] = _doc.Updated.ToUnixTimeSeconds(),
        [IndexPayload.SectionIndex] = section.Index,
        [IndexPayload.SectionCount] = Sections.Count,
        [IndexPayload.SectionStart] = section.Start,
        [IndexPayload.SectionLength] = section.Length,
        [IndexPayload.IndexHash] = Hash,
        [IndexPayload.IndexFormat] = Format,
    };

    /// <summary>
    /// A hash of everything the points of a memory are made from: the format, the embedding model,
    /// the section settings, the file path and every field of the document that reaches a vector or
    /// the payload. Equal hashes mean the index would be written with exactly what it holds.
    /// </summary>
    public static string HashOf(MemoryDocument doc, string filePath, MemoryIndexSettings settings)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        void Add(string value)
        {
            // Length first, so no two different field lists give the same bytes.
            var bytes = Encoding.UTF8.GetBytes(value);
            hash.AppendData(Encoding.ASCII.GetBytes(bytes.Length.ToString(CultureInfo.InvariantCulture) + ":"));
            hash.AppendData(bytes);
        }

        Add(Format.ToString(CultureInfo.InvariantCulture));
        Add(settings.EmbedderId);
        Add(settings.SectionChars.ToString(CultureInfo.InvariantCulture));
        Add(settings.MaxSections.ToString(CultureInfo.InvariantCulture));
        Add(filePath);
        Add(doc.Id);
        Add(doc.Type);
        Add(doc.Agent);
        Add(MemoryDocument.NormalizeProject(doc.Project) ?? "");
        Add(doc.Created.ToString("o"));
        Add(doc.Updated.ToString("o"));
        Add(doc.Title);
        Add(doc.Tags.Count.ToString(CultureInfo.InvariantCulture));
        foreach (var tag in doc.Tags) Add(tag);
        Add(doc.Content);

        return Convert.ToHexStringLower(hash.GetHashAndReset().AsSpan(0, 16));
    }

    /// <summary>
    /// From what the points say, the hash the index holds for each file, or null where the points
    /// of a file are not one complete memory in the current format (a point from before the format,
    /// a write that stopped half-way, sections of two versions). A null is indexed again.
    /// </summary>
    public static Dictionary<string, string?> IndexedHashes(IEnumerable<IndexPointInfo> points)
    {
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var group in points.GroupBy(p => p.FilePathKey, StringComparer.Ordinal))
        {
            var rows = group.ToList();
            var first = rows[0];
            var complete = first.Hash.Length > 0
                && rows.All(r => r.Format == Format && r.Hash == first.Hash && r.SectionCount == first.SectionCount)
                && rows.Count == first.SectionCount
                && rows.Select(r => r.SectionIndex).Order().SequenceEqual(Enumerable.Range(0, first.SectionCount));
            result[group.Key] = complete ? first.Hash : null;
        }
        return result;
    }
}
