using System.Runtime.ExceptionServices;
using Fleet.Memory.Data;
using Fleet.Memory.Models;

namespace Fleet.Memory.Services;

/// <summary>How a search hit matched, strongest first. The values are the wire names.</summary>
public static class MemoryMatch
{
    public const string Id = "id";
    public const string Title = "title";
    public const string Tag = "tag";
    public const string Keyword = "keyword";
    public const string Meaning = "meaning";
}

/// <summary>One memory a search found.</summary>
/// <param name="Match">One of <see cref="MemoryMatch"/>.</param>
/// <param name="Score">The similarity of the best section, for a <see cref="MemoryMatch.Meaning"/> hit only.</param>
public sealed record MemorySearchHit(MemoryDocument Document, string Match, SearchSnippet Snippet, float? Score)
{
    /// <summary>The project the readers' access rule is checked against.</summary>
    public string Project => Document.Project;
}

/// <summary>
/// Search. A memory is returned once, under the strongest way it matched, in this order:
///
/// 1. id: the query is an id or the start of one (<see cref="MemoryIdQuery"/>);
/// 2. title: the query is part of the title, whatever the case;
/// 3. tag: the query is one of the tags, whatever the case;
/// 4. keyword and meaning: the index's two sides, fused (<see cref="SearchFusion"/>). A hit the
///    keyword side returned is a keyword hit; one only the meaning side returned is a meaning hit
///    and carries its similarity.
///
/// The first three come from the catalogue and need neither the index nor the embedder. When the
/// query names a memory by id, the index is not asked at all: the nearest neighbours of a string of
/// hexadecimal digits are noise. Filters apply to every kind.
/// </summary>
public sealed partial class MemoryService
{
    // Points asked of each side per memory wanted, and its bounds: several points can be sections
    // of one memory, and only the best of them counts.
    private const int MinSideFetch = 64;
    private const int MaxSideFetch = 400;

    public async Task<List<MemorySearchHit>> SearchAsync(
        string query, int limit = 10, string? type = null, string? project = null, string? agent = null,
        string? tag = null, DateTimeOffset? since = null, CancellationToken ct = default)
    {
        var text = query.Trim();
        if (text.Length == 0 || limit <= 0)
            return [];

        var normalizedProject = project is null ? null : MemoryDocument.NormalizeProject(project);
        var sinceTs = since?.ToUnixTimeSeconds();

        // ── 1 to 3: from the catalogue ───────────────────────────────────────
        var entries = _catalogue.Entries
            .Where(e => (type is null || e.Type == type)
                && (normalizedProject is null || e.Project == normalizedProject)
                && (agent is null || e.Agent == agent)
                && (tag is null || e.Tags.Contains(tag))
                && (sinceTs is null || e.Updated.ToUnixTimeSeconds() >= sinceTs))
            .ToList();

        var candidates = new List<Candidate>();
        var taken = new HashSet<string>(StringComparer.Ordinal);

        var namesAnId = false;
        if (MemoryIdQuery.TryParse(text, out var idPrefix))
        {
            foreach (var e in entries.Where(e => e.IdKey.StartsWith(idPrefix, StringComparison.Ordinal))
                         .OrderBy(e => e.IdKey, StringComparer.Ordinal).ThenBy(e => e.FilePath, StringComparer.Ordinal))
            {
                namesAnId = true;
                if (taken.Add(e.FilePath)) candidates.Add(new Candidate(e.FilePath, MemoryMatch.Id));
            }
        }

        foreach (var e in entries.Where(e => e.Title.Contains(text, StringComparison.OrdinalIgnoreCase))
                     .OrderBy(e => TitleRank(e.Title, text)).ThenByDescending(e => e.Updated).ThenBy(e => e.FilePath, StringComparer.Ordinal))
        {
            if (taken.Add(e.FilePath)) candidates.Add(new Candidate(e.FilePath, MemoryMatch.Title));
        }

        foreach (var e in entries.Where(e => e.Tags.Any(t => t.Equals(text, StringComparison.OrdinalIgnoreCase)))
                     .OrderByDescending(e => e.Updated).ThenBy(e => e.FilePath, StringComparer.Ordinal))
        {
            if (taken.Add(e.FilePath)) candidates.Add(new Candidate(e.FilePath, MemoryMatch.Tag));
        }

        // ── 4: from the index ────────────────────────────────────────────────
        Exception? failure = null;
        if (!namesAnId && candidates.Count < limit)
        {
            var keywords = new Dictionary<string, string>();
            if (type is not null) keywords[IndexPayload.MemoryType] = type;
            if (normalizedProject is not null) keywords[IndexPayload.Project] = normalizedProject;
            if (agent is not null) keywords[IndexPayload.AgentName] = agent;
            if (tag is not null) keywords[IndexPayload.Tags] = tag;
            var filter = new IndexFilter(keywords, sinceTs);
            var fetch = Math.Clamp(limit * SectionOverfetch, MinSideFetch, MaxSideFetch);

            // Each side by itself: the keyword side needs no embedder, so it still answers when the
            // embedder does not.
            List<IndexMatch> dense = [], sparse = [];
            try
            {
                var embedding = await embeddingService.EmbedAsync(text, ct);
                dense = await vectorStore.SearchDenseAsync(embedding, fetch, filter, ct);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
            {
                failure = ex;
                logger.LogWarning("Search ran without the meaning side: {ExceptionType}", ex.GetType().Name);
            }

            try
            {
                sparse = await vectorStore.SearchSparseAsync(text, fetch, filter, ct);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
            {
                failure ??= ex;
                logger.LogWarning("Search ran without the keyword side: {ExceptionType}", ex.GetType().Name);
            }

            foreach (var hit in SearchFusion.Fuse(dense, sparse))
            {
                if (!hit.Keyword && _settings.MinMeaningScore > 0 && hit.DenseScore < _settings.MinMeaningScore)
                    continue;
                if (taken.Add(hit.FilePathKey))
                    candidates.Add(new Candidate(hit.FilePathKey, hit.Keyword ? MemoryMatch.Keyword : MemoryMatch.Meaning, hit));
            }
        }

        // ── The hits, from the files ─────────────────────────────────────────
        var hits = new List<MemorySearchHit>();
        foreach (var candidate in candidates)
        {
            if (hits.Count >= limit) break;

            MemoryDocument? doc;
            try
            {
                doc = await fileStore.ParseFileAsync(candidate.FilePath);
            }
            catch (InvalidDataException ex)
            {
                logger.LogError(ex, "Corrupt memory file skipped during search result assembly: {Path}", candidate.FilePath);
                continue;
            }
            if (doc is null) continue;

            var section = candidate.Fused?.Section;
            var snippet = candidate.Match switch
            {
                MemoryMatch.Keyword => SearchSnippets.AroundFirstMatch(doc.Content, text, section!.SectionStart, section.SectionLength),
                MemoryMatch.Meaning => SearchSnippets.Start(doc.Content, text, section!.SectionStart, section.SectionLength),
                _ => SearchSnippets.Start(doc.Content, text),
            };
            hits.Add(new MemorySearchHit(doc, candidate.Match, snippet,
                candidate.Match == MemoryMatch.Meaning ? candidate.Fused!.DenseScore : null));
        }

        // "Nothing found" is not said when part of the search could not run.
        if (hits.Count == 0 && failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();

        return hits;
    }

    private sealed record Candidate(string FilePath, string Match, FusedHit? Fused = null);

    /// <summary>The title is the query, begins with it, or only contains it.</summary>
    private static int TitleRank(string title, string query) =>
        title.Equals(query, StringComparison.OrdinalIgnoreCase) ? 0
        : title.StartsWith(query, StringComparison.OrdinalIgnoreCase) ? 1
        : 2;
}
