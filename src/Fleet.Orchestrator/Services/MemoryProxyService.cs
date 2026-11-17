using System.Net.Http.Json;
using System.Text.Json;

namespace Fleet.Orchestrator.Services;

/// <summary>
/// Thin HTTP client that calls fleet-memory's /internal/* REST endpoints.
/// The orchestrator proxies these to the dashboard via /api/memory/* endpoints.
/// No auth required — fleet-memory's internal API relies on Docker-network trust.
/// </summary>
public sealed class MemoryProxyService(IHttpClientFactory httpClientFactory, IConfiguration config)
{
    private static readonly JsonSerializerOptions JsonOpts =
        new() { PropertyNameCaseInsensitive = true };

    private string BaseUrl => (config["FleetMemory:Url"] ?? "http://fleet-memory:3100").TrimEnd('/');

    private HttpClient CreateClient() => httpClientFactory.CreateClient("fleet-memory");

    public async Task<HttpResponseMessage> GetListAsync(CancellationToken ct = default) =>
        await CreateClient().GetAsync($"{BaseUrl}/internal/memory", ct);

    public async Task<HttpResponseMessage> GetIdsAsync(CancellationToken ct = default) =>
        await CreateClient().GetAsync($"{BaseUrl}/internal/memory/ids", ct);

    /// <summary>
    /// The search parameters fleet-memory accepts besides <c>q</c>, in the order they are sent.
    /// Nothing else a caller puts on <c>/api/memory/search</c> is forwarded.
    /// </summary>
    public static readonly IReadOnlyList<string> SearchFilterNames = ["project", "type", "tag", "since", "limit"];

    /// <summary>
    /// <c>q</c> and each named filter that has a value, escaped. A value is passed as it was typed:
    /// fleet-memory is the one that says a <c>since</c> or a <c>limit</c> is wrong (a 400).
    /// </summary>
    public static string BuildSearchQuery(string q, Func<string, string?> filter)
    {
        var query = new System.Text.StringBuilder("q=").Append(Uri.EscapeDataString(q));
        foreach (var name in SearchFilterNames)
        {
            var value = filter(name);
            if (!string.IsNullOrEmpty(value))
                query.Append('&').Append(name).Append('=').Append(Uri.EscapeDataString(value));
        }
        return query.ToString();
    }

    public async Task<HttpResponseMessage> SearchAsync(string q, Func<string, string?> filter, CancellationToken ct = default) =>
        await CreateClient().GetAsync($"{BaseUrl}/internal/memory/search?{BuildSearchQuery(q, filter)}", ct);

    public async Task<HttpResponseMessage> GetAsync(string id, CancellationToken ct = default) =>
        await CreateClient().GetAsync($"{BaseUrl}/internal/memory/{Uri.EscapeDataString(id)}", ct);

    public async Task<HttpResponseMessage> UpdateAsync(string id, object body, CancellationToken ct = default) =>
        await CreateClient().PutAsJsonAsync($"{BaseUrl}/internal/memory/{Uri.EscapeDataString(id)}", body, ct);

    public async Task<HttpResponseMessage> DeleteAsync(string id, CancellationToken ct = default) =>
        await CreateClient().DeleteAsync($"{BaseUrl}/internal/memory/{Uri.EscapeDataString(id)}", ct);

    public async Task<HttpResponseMessage> GetReadStatsAsync(CancellationToken ct = default) =>
        await CreateClient().GetAsync($"{BaseUrl}/internal/stats/reads", ct);
}
