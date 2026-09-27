using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace Fleet.Orchestrator.Services;

/// <summary>Fetches the internal Comms journal status without exposing its status token.</summary>
public sealed class CommsJournalStatusProxy(
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    JournalTokenService tokens,
    ILogger<CommsJournalStatusProxy> logger)
{
    public async Task<JsonObject> GetAsync(CancellationToken ct = default)
    {
        string token;
        try
        {
            token = tokens.Mint(JournalTokenService.PurposeStatus, "orchestrator");
        }
        catch (JournalConfigurationException ex)
        {
            return Unavailable(ex.Code);
        }

        var url = configuration["Journal:StatusUrl"]
            ?? "http://fleet-comms:8083/journal/v1/status";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));

            var client = httpClientFactory.CreateClient();
            using var response = await client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
                return Unavailable($"Http{(int)response.StatusCode}");

            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            var result = JsonNode.Parse(body)?.AsObject()
                ?? throw new System.Text.Json.JsonException("journal status is not an object");
            result["status"] = "available";
            return result;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                   or System.Text.Json.JsonException or InvalidOperationException)
        {
            var errorClass = ex is TaskCanceledException && !ct.IsCancellationRequested
                ? "TimeoutException"
                : ex.GetType().Name;
            logger.LogWarning("Comms journal status unavailable: {ErrorClass}", errorClass);
            return Unavailable(errorClass);
        }
    }

    private static JsonObject Unavailable(string errorClass) => new()
    {
        ["status"] = "unavailable",
        ["errorClass"] = errorClass,
    };
}
