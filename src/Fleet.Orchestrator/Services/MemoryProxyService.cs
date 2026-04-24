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

    public async Task<HttpResponseMessage> SearchAsync(string q, CancellationToken ct = default) =>
        await CreateClient().GetAsync($"{BaseUrl}/internal/memory/search?q={Uri.EscapeDataString(q)}", ct);

    public async Task<HttpResponseMessage> GetAsync(string id, CancellationToken ct = default) =>
        await CreateClient().GetAsync($"{BaseUrl}/internal/memory/{Uri.EscapeDataString(id)}", ct);

    public async Task<HttpResponseMessage> UpdateAsync(string id, object body, CancellationToken ct = default) =>
        await CreateClient().PutAsJsonAsync($"{BaseUrl}/internal/memory/{Uri.EscapeDataString(id)}", body, ct);

    public async Task<HttpResponseMessage> DeleteAsync(string id, CancellationToken ct = default) =>
        await CreateClient().DeleteAsync($"{BaseUrl}/internal/memory/{Uri.EscapeDataString(id)}", ct);

    public async Task<HttpResponseMessage> GetReadStatsAsync(CancellationToken ct = default) =>
        await CreateClient().GetAsync($"{BaseUrl}/internal/stats/reads", ct);
}
