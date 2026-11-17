using System.Collections.Concurrent;
using Fleet.Memory.Data;
using Fleet.Memory.Models;

namespace Fleet.Memory.Services;

/// <summary>
/// Writing a memory to the index, and knowing what the index already holds.
///
/// A memory is one point per section. Before anything is embedded, the hash of what its points
/// would be made from (<see cref="MemoryIndexPlan.HashOf"/>) is compared with the hash the index
/// holds for the file; when they are equal nothing is embedded and nothing is written. What the
/// index holds is read from the points' payload once, at start (<see cref="LoadIndexStateAsync"/>),
/// and kept in step afterwards: a hash is recorded only after every point of the memory was written
/// and its older points removed, and forgotten before a write or a removal begins. So a write that
/// stops half-way leaves no hash, and the file is indexed again by the retry that follows.
///
/// One file is written or removed by one caller at a time (<see cref="_paths"/>). The API indexes a
/// write itself and the watcher then reports the same file; without the turn-taking the two could
/// each remove the other's points as stale.
/// </summary>
public sealed partial class MemoryService
{
    // How many memories the store-time duplicate hint looks at, and how many points are asked for
    // per memory wanted, because several points can be sections of one memory.
    private const int SimilarCandidates = 3;
    private const int SectionOverfetch = 8;

    private readonly ConcurrentDictionary<string, string> _indexed = new(StringComparer.Ordinal);
    private readonly PathLocks _paths = new();

    // While the start-up read of the index is running, the files written or removed meanwhile.
    // What the read saw of them is older than what was done, so it is not recorded for them.
    private readonly object _loadGate = new();
    private HashSet<string>? _touchedDuringLoad;

    private long _embeddedMemories;
    private long _embeddedSections;
    private long _unchangedMemories;

    /// <summary>The catalogue this service keeps in step with indexing and removal.</summary>
    public MemoryCatalogue Catalogue => _catalogue;

    /// <summary>
    /// Since the process started: memories embedded, the sections that took, and memories left alone
    /// because the index already held exactly them.
    /// </summary>
    public (long Embedded, long Sections, long Unchanged) IndexCounts =>
        (Interlocked.Read(ref _embeddedMemories), Interlocked.Read(ref _embeddedSections), Interlocked.Read(ref _unchangedMemories));

    /// <summary>
    /// Enters every memory the start-up scan read into the catalogue, before any of them is
    /// embedded: a search by id, title or tag works from then on.
    /// </summary>
    public void SeedCatalogue(IEnumerable<MemoryDocument> documents)
    {
        foreach (var doc in documents)
            if (!string.IsNullOrEmpty(doc.FilePath))
                _catalogue.Set(doc, doc.FilePath);
    }

    /// <summary>
    /// Reads once what the index holds, so that unchanged files are not embedded again. Returns the
    /// path of every file that has a point, current or not, for the removal of orphans.
    /// </summary>
    public async Task<HashSet<string>> LoadIndexStateAsync(CancellationToken ct = default)
    {
        lock (_loadGate) _touchedDuringLoad = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            var hashes = MemoryIndexPlan.IndexedHashes(await vectorStore.ScrollIndexInfoAsync(ct));
            lock (_loadGate)
            {
                var touched = _touchedDuringLoad!;
                foreach (var (path, hash) in hashes)
                    if (hash is not null && !touched.Contains(path))
                        _indexed.TryAdd(path, hash);
            }
            return new HashSet<string>(hashes.Keys, StringComparer.Ordinal);
        }
        finally
        {
            lock (_loadGate) _touchedDuringLoad = null;
        }
    }

    /// <summary>
    /// Indexes a memory file into Qdrant.
    /// Throws <see cref="InvalidDataException"/> if the file cannot be parsed (bad YAML or not a memory file).
    /// Other exceptions indicate infrastructure failures (Qdrant, embedding) and propagate as-is.
    /// </summary>
    public Task IndexFileAsync(string filePath, CancellationToken ct = default) =>
        IndexCurrentFileAsync(filePath, ct);

    private async Task<MemoryDocument> ParseForIndexAsync(string filePath)
    {
        // ParseFileAsync throws InvalidDataException for bad YAML (corrupt file).
        // Returns null only for files without a frontmatter header (not a memory file).
        MemoryDocument? doc;
        try
        {
            doc = await fileStore.ParseFileAsync(filePath);
        }
        catch (InvalidDataException)
        {
            // A file that cannot be read as a memory is not one a search may return.
            _catalogue.Remove(filePath);
            throw;
        }

        if (doc is null)
        {
            _catalogue.Remove(filePath);
            throw new InvalidDataException($"File is not a parseable memory file: {filePath}");
        }
        return doc;
    }

    private async Task<MemoryDocument> IndexCurrentFileAsync(string filePath, CancellationToken ct)
    {
        using var turn = await _paths.EnterAsync(filePath, ct);
        // A queued caller must not retain a document read before an earlier writer or delete finished.
        var doc = await ParseForIndexAsync(filePath);

        // The file is the memory: it is in the catalogue as parsed, whether or not the index can be written.
        _catalogue.Set(doc, filePath);

        var plan = MemoryIndexPlan.Build(doc, filePath, _settings);
        if (_indexed.TryGetValue(filePath, out var held) && held == plan.Hash)
        {
            Interlocked.Increment(ref _unchangedMemories);
            logger.LogDebug("Memory {Id} is unchanged in the index; not embedded again", doc.Id);
            return doc;
        }

        Forget(filePath);

        if (plan.SectionsFound > plan.Sections.Count)
        {
            logger.LogWarning("Memory {Id} has {Found} sections; only the first {Indexed} are indexed",
                doc.Id, plan.SectionsFound, plan.Sections.Count);
        }

        // Everything is embedded before anything is written, so an embedder that fails half-way
        // leaves the index as it was.
        var points = new List<IndexPoint>(plan.Sections.Count);
        try
        {
            foreach (var section in plan.Sections)
            {
                var embedding = await embeddingService.EmbedAsync(plan.DenseText(section), ct);
                points.Add(new IndexPoint(filePath, section.Index, embedding, plan.SparseText(section), plan.Payload(section)));
            }

            await vectorStore.UpsertAsync(points, ct);
            // The points of an earlier version that the new ones did not overwrite.
            await vectorStore.DeleteStaleAsync(filePath, plan.Hash, ct);
        }
        catch
        {
            // Whatever a start-up read recorded meanwhile, the index is no longer known to hold it.
            Forget(filePath);
            throw;
        }

        _indexed[filePath] = plan.Hash;
        Interlocked.Increment(ref _embeddedMemories);
        Interlocked.Add(ref _embeddedSections, points.Count);
        logger.LogInformation("Indexed memory {Id} ({Type}) in {Sections} section(s): {Title}", doc.Id, doc.Type, points.Count, doc.Title);
        return doc;
    }

    public async Task RemoveFromIndexAsync(string filePath, CancellationToken ct = default)
    {
        await RemoveIndexEntryAsync(filePath, ct);
        logger.LogInformation("Removed {Path} from index", filePath);
    }

    private async Task RemoveIndexEntryAsync(string filePath, CancellationToken ct)
    {
        using var turn = await _paths.EnterAsync(filePath, ct);
        // Remove after earlier indexers finish, or a queued indexer could put it back in the catalogue.
        _catalogue.Remove(filePath);
        Forget(filePath);
        try
        {
            await vectorStore.DeleteAsync(filePath, ct);
        }
        finally
        {
            Forget(filePath);
        }
    }

    /// <summary>The index is about to change for a file: what it held is no longer known.</summary>
    private void Forget(string filePath)
    {
        lock (_loadGate) _touchedDuringLoad?.Add(filePath);
        _indexed.TryRemove(filePath, out _);
    }

    /// <summary>One caller at a time per path; the entry is dropped when the last one leaves.</summary>
    private sealed class PathLocks
    {
        private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

        private sealed class Entry
        {
            public readonly SemaphoreSlim Turn = new(1, 1);
            public int Users;
        }

        public async Task<IDisposable> EnterAsync(string path, CancellationToken ct)
        {
            Entry entry;
            lock (_entries)
            {
                if (!_entries.TryGetValue(path, out entry!)) _entries[path] = entry = new Entry();
                entry.Users++;
            }
            try
            {
                await entry.Turn.WaitAsync(ct);
            }
            catch
            {
                Leave(path, entry, held: false);
                throw;
            }
            return new Held(this, path, entry);
        }

        private void Leave(string path, Entry entry, bool held)
        {
            if (held) entry.Turn.Release();
            lock (_entries)
                if (--entry.Users == 0) _entries.Remove(path);
        }

        private sealed class Held(PathLocks owner, string path, Entry entry) : IDisposable
        {
            private int _done;
            public void Dispose()
            {
                if (Interlocked.Exchange(ref _done, 1) == 0) owner.Leave(path, entry, held: true);
            }
        }
    }
}
