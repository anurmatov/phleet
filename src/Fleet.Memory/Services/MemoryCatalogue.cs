using System.Collections.Concurrent;
using Fleet.Memory.Models;

namespace Fleet.Memory.Services;

/// <summary>What a search needs to know about a memory without opening its file.</summary>
public sealed record CatalogueEntry(
    string FilePath,
    string Id,
    string Title,
    IReadOnlyList<string> Tags,
    string Project,
    string Type,
    string Agent,
    DateTimeOffset Updated)
{
    /// <summary>The id as <see cref="MemoryIdQuery"/> compares it: lower case, no dashes.</summary>
    public string IdKey { get; } = MemoryIdQuery.Key(Id);

    public static CatalogueEntry From(MemoryDocument doc, string filePath) => new(
        filePath, doc.Id, doc.Title, doc.Tags.ToArray(), MemoryDocument.NormalizeProject(doc.Project) ?? "",
        doc.Type, doc.Agent, doc.Updated);
}

/// <summary>
/// The memories that exist, by file, held in memory so that a search by id, title or tag reads no
/// file and needs neither the index nor the embedder.
///
/// It is built from the files and lives as long as the process: filled when the start-up scan has
/// read them (before anything is embedded), and after that it follows the same two events the
/// index follows. A file that is indexed is entered as it was parsed, whether or not the index
/// could be written; a file that is removed from the index, or cannot be parsed, is taken out. It
/// holds no content and is never written to disk. Reading it applies no access rule: the callers
/// filter what they return, as they do for every other read.
/// </summary>
public sealed class MemoryCatalogue
{
    private readonly ConcurrentDictionary<string, CatalogueEntry> _byPath = new(StringComparer.Ordinal);

    public int Count => _byPath.Count;

    public void Set(MemoryDocument doc, string filePath) => _byPath[filePath] = CatalogueEntry.From(doc, filePath);

    public void Remove(string filePath) => _byPath.TryRemove(filePath, out _);

    /// <summary>A snapshot of every entry, in no particular order.</summary>
    public IReadOnlyCollection<CatalogueEntry> Entries => _byPath.Values.ToArray();
}
