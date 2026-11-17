using System.Globalization;
using System.Text.Json.Serialization;
using Fleet.Memory.Services;

namespace Fleet.Memory.Endpoints;

/// <summary>
/// <c>GET /internal/memory/search</c>: what the dashboard's Memory page searches with, through the
/// orchestrator's proxy.
///
/// Parameters: <c>q</c> (required), and optionally <c>project</c>, <c>type</c>, <c>tag</c>,
/// <c>since</c> (an ISO date, see <see cref="SearchDates"/>) and <c>limit</c> (1 to
/// <see cref="MaxLimit"/>, default <see cref="DefaultLimit"/>). An empty optional parameter counts as
/// absent. A missing <c>q</c>, or a <c>since</c> or <c>limit</c> that cannot be read, is a 400.
/// The answer is an array of <see cref="MemorySearchResult"/>, strongest match first.
/// </summary>
public static class MemorySearchEndpoint
{
    public const int DefaultLimit = 20;
    public const int MaxLimit = 50;

    public static async Task<IResult> HandleAsync(
        string? q, string? project, string? type, string? tag, string? since, string? limit,
        MemoryService memoryService, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(q))
            return Results.BadRequest(new { error = "Missing query parameter 'q'" });

        var take = DefaultLimit;
        if (!string.IsNullOrWhiteSpace(limit)
            && (!int.TryParse(limit.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out take) || take < 1 || take > MaxLimit))
        {
            return Results.BadRequest(new { error = "invalid_limit", detail = $"'limit' must be a whole number from 1 to {MaxLimit}" });
        }

        DateTimeOffset? sinceDate = null;
        if (!string.IsNullOrWhiteSpace(since))
        {
            if (!SearchDates.TryParseSince(since, out var parsed))
                return Results.BadRequest(new { error = "invalid_since", detail = "'since' must be an ISO 8601 date such as 2026-03-01" });
            sinceDate = parsed;
        }

        var hits = await memoryService.SearchAsync(q, take,
            type: NullIfBlank(type), project: NullIfBlank(project), tag: NullIfBlank(tag), since: sinceDate, ct: ct);

        return Results.Ok(hits.Select(MemorySearchResult.From).ToList());
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>One search result on the wire. The names are the contract; the dashboard reads exactly these.</summary>
public sealed record MemorySearchResult(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("project")] string Project,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("tags")] IReadOnlyList<string> Tags,
    [property: JsonPropertyName("snippet")] string Snippet,
    // [start, length] pairs into snippet, in UTF-16 units; empty when no word of the query is in it.
    [property: JsonPropertyName("highlights")] IReadOnlyList<int[]> Highlights,
    // id, title, tag, keyword or meaning.
    [property: JsonPropertyName("match")] string Match,
    // The similarity, for a meaning match; null for every other.
    [property: JsonPropertyName("score")] float? Score,
    [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt)
{
    public static MemorySearchResult From(MemorySearchHit hit) => new(
        hit.Document.Id,
        hit.Document.Title,
        hit.Document.Project,
        hit.Document.Type,
        hit.Document.Tags,
        hit.Snippet.Text,
        hit.Snippet.Highlights.Select(h => new[] { h.Start, h.Length }).ToList(),
        hit.Match,
        hit.Score,
        hit.Document.Updated);
}
