namespace Fleet.Memory.Data;

/// <summary>
/// What the memory service needs from the search index. <see cref="VectorStore"/> is the Qdrant
/// implementation; the tests use one that lives in memory, so indexing, sectioning, search and the
/// start-up scan are tested without a Qdrant.
///
/// A memory is one or more points, one per section, all carrying the memory's file path.
/// </summary>
public interface IVectorIndex
{
    Task EnsureCollectionAsync(CancellationToken ct = default);

    /// <summary>Writes the points of one memory. A point replaces the one written for the same file and section before.</summary>
    Task UpsertAsync(IReadOnlyList<IndexPoint> points, CancellationToken ct = default);

    /// <summary>
    /// Removes the points of a file that do not carry <paramref name="keepHash"/>: the sections of an
    /// earlier, longer version, and a point from before memories were indexed in sections.
    /// </summary>
    Task DeleteStaleAsync(string filePathKey, string keepHash, CancellationToken ct = default);

    /// <summary>Removes every point of a file.</summary>
    Task DeleteAsync(string filePathKey, CancellationToken ct = default);

    /// <summary>The best section of each distinct memory by meaning, best first; limit counts memories.</summary>
    Task<List<IndexMatch>> SearchDenseAsync(float[] queryEmbedding, int limit, IndexFilter? filter = null, CancellationToken ct = default);

    /// <summary>
    /// The best keyword section of each distinct memory, best first; limit counts memories.
    /// None when the query has no keyword.
    /// </summary>
    Task<List<IndexMatch>> SearchSparseAsync(string queryText, int limit, IndexFilter? filter = null, CancellationToken ct = default);

    /// <summary>The payload of every memory matching the filters: one entry per memory, whatever its number of sections.</summary>
    Task<List<Dictionary<string, string>>> ScrollFilteredAsync(Dictionary<string, string>? filters = null, CancellationToken ct = default);

    /// <summary>What every point says about the state it was indexed from. Read once, at start.</summary>
    Task<List<IndexPointInfo>> ScrollIndexInfoAsync(CancellationToken ct = default);
}

/// <summary>One section of one memory, ready to be written to the index.</summary>
public sealed record IndexPoint(
    string FilePathKey,
    int SectionIndex,
    float[] Dense,
    string SparseText,
    Dictionary<string, object> Payload);

/// <summary>
/// A point a search returned. <see cref="SectionLength"/> is -1 for a point from before memories
/// were indexed in sections: it stands for the whole memory.
/// </summary>
public sealed record IndexMatch(string FilePathKey, float Score, int SectionStart = 0, int SectionLength = -1);

/// <summary>Exact-match filters on the payload, and a lower bound on the updated time (Unix seconds).</summary>
public sealed record IndexFilter(IReadOnlyDictionary<string, string> Keywords, long? UpdatedSince = null)
{
    public bool IsEmpty => Keywords.Count == 0 && UpdatedSince is null;
}

/// <summary>
/// The indexing facts of one point. <see cref="Hash"/> is empty and <see cref="Format"/> 0 for a
/// point written before they existed.
/// </summary>
public sealed record IndexPointInfo(string FilePathKey, string Hash, int Format, int SectionIndex, int SectionCount);

/// <summary>The names of the payload fields, in one place for the index and its readers.</summary>
public static class IndexPayload
{
    public const string FilePath = "file_path";
    public const string MemoryId = "memory_id";
    public const string MemoryType = "memory_type";
    public const string AgentName = "agent_name";
    public const string Project = "project";
    public const string Title = "title";
    public const string Tags = "tags";
    public const string Created = "created";
    public const string Updated = "updated";
    /// <summary>The updated time as Unix seconds, so "since" is a range filter.</summary>
    public const string UpdatedTs = "updated_ts";
    public const string SectionIndex = "section_index";
    public const string SectionCount = "section_count";
    public const string SectionStart = "section_start";
    public const string SectionLength = "section_length";
    /// <summary>A hash of everything the memory's points were made from.</summary>
    public const string IndexHash = "index_hash";
    /// <summary>The <see cref="Fleet.Memory.Services.MemoryIndexPlan.Format"/> the point was written in.</summary>
    public const string IndexFormat = "index_format";
}
