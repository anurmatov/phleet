using Fleet.Memory.Data;

namespace Fleet.Memory.Services;

/// <summary>A memory the index returned, after its sections and the two sides were brought together.</summary>
/// <param name="Keyword">The keyword side returned it. False: only the meaning side did.</param>
/// <param name="DenseScore">Its best section's similarity on the meaning side, when that side returned it.</param>
/// <param name="Section">The section to show: the best one on the keyword side, else on the meaning side.</param>
public sealed record FusedHit(string FilePathKey, bool Keyword, float? DenseScore, IndexMatch Section, float Fused);

/// <summary>
/// Brings the two ranked lists of a search together, in code, so that each hit can say which side
/// found it. Before, Qdrant fused them and the answer no longer said.
///
/// Reciprocal rank fusion with the constant Qdrant uses by default: a memory at zero-based position
/// <c>p</c> of a list gets <c>1 / (<see cref="K"/> + p)</c> from that list, and the two are added.
/// The lists are of memories, not points: of a memory's sections, the best ranked one stands for it.
/// </summary>
public static class SearchFusion
{
    public const int K = 2;

    /// <summary>Of each memory's points in a ranked list, the first; the order is kept.</summary>
    public static List<IndexMatch> BestPerMemory(IEnumerable<IndexMatch> ranked)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var best = new List<IndexMatch>();
        foreach (var match in ranked)
            if (seen.Add(match.FilePathKey))
                best.Add(match);
        return best;
    }

    public static List<FusedHit> Fuse(IEnumerable<IndexMatch> denseRanked, IEnumerable<IndexMatch> sparseRanked)
    {
        var dense = BestPerMemory(denseRanked);
        var sparse = BestPerMemory(sparseRanked);
        var hits = new Dictionary<string, FusedHit>(StringComparer.Ordinal);

        for (var p = 0; p < sparse.Count; p++)
            hits[sparse[p].FilePathKey] = new FusedHit(sparse[p].FilePathKey, true, null, sparse[p], 1f / (K + p));

        for (var p = 0; p < dense.Count; p++)
        {
            var match = dense[p];
            hits[match.FilePathKey] = hits.TryGetValue(match.FilePathKey, out var hit)
                ? hit with { DenseScore = match.Score, Fused = hit.Fused + 1f / (K + p) }
                : new FusedHit(match.FilePathKey, false, match.Score, match, 1f / (K + p));
        }

        return hits.Values
            .OrderByDescending(h => h.Fused)
            .ThenByDescending(h => h.DenseScore ?? float.MinValue)
            .ThenBy(h => h.FilePathKey, StringComparer.Ordinal)
            .ToList();
    }
}
