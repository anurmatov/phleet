using System.Text;
using System.Text.Json;

namespace Fleet.Temporal.Mcp;

/// <summary>
/// Default <see cref="IEpicGrantDecisionForwarder"/>: one POST on the named
/// <c>"orchestrator-epic-grants"</c> HttpClient — the same base address and admin bearer as the
/// <c>"orchestrator"</c> client, with <see cref="Timeout"/> instead of 30 s, because the decision
/// path can legitimately take longer (bounded Temporal describes and history reads, a GitHub read,
/// the database). A longer bound keeps "delivery unknown" rare (see <c>Program.cs</c>).
///
/// <para>One attempt and no retry: a retried decision could be applied to a later gate visit, and
/// the orchestrator's unique decision key already makes a repeat pointless. The client's own
/// timeout bounds the call. Every failure becomes an error result rather than an exception, so the
/// MCP tool can report it and send nothing.</para>
/// </summary>
public sealed class OrchestratorEpicGrantForwarder(IHttpClientFactory httpClientFactory) : IEpicGrantDecisionForwarder
{
    internal const string HttpClientName = "orchestrator-epic-grants";

    /// <summary>
    /// Above the orchestrator's worst case: up to six 10 s Temporal reads (driver, target, history,
    /// two parents), a 10 s GitHub read and the database, plus the one send.
    /// </summary>
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(120);

    private static readonly JsonSerializerOptions WebOptions = new(JsonSerializerDefaults.Web);

    internal static string DecisionPath(string grantId) =>
        $"api/epic-grants/{Uri.EscapeDataString(grantId)}/decisions";

    public async Task<EpicGrantDecisionResult> ForwardAsync(
        string grantId,
        EpicGrantDecisionRequest request,
        CancellationToken cancellationToken = default)
    {
        var client = httpClientFactory.CreateClient(HttpClientName);
        if (client.BaseAddress is null)
            return EpicGrantDecisionResult.Failed("the orchestrator URL is not configured");

        int statusCode;
        string body;
        try
        {
            using var content = new StringContent(
                JsonSerializer.Serialize(request, WebOptions), Encoding.UTF8, "application/json");
            using var response = await client.PostAsync(DecisionPath(grantId), content, cancellationToken);

            statusCode = (int)response.StatusCode;
            body = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // HttpClient.Timeout surfaces as a cancellation the caller did not ask for.
            return EpicGrantDecisionResult.Failed("the orchestrator did not answer before the timeout");
        }
        catch (OperationCanceledException)
        {
            return EpicGrantDecisionResult.Failed("the forward was cancelled");
        }
        catch (Exception ex)
        {
            // Only the type is reported: transport messages can carry internal addresses.
            return EpicGrantDecisionResult.Failed($"the orchestrator could not be reached ({ex.GetType().Name})");
        }

        return MapResponse(statusCode, body);
    }

    /// <summary>
    /// A JSON object with a string <c>result</c> is the orchestrator's decision, whatever the HTTP
    /// status (200 sent, 409 refused, 502 send_failed, 400 bad_request). Anything else — an empty
    /// or non-JSON body, or JSON without a decision result such as a bearer rejection — is an error.
    /// </summary>
    internal static EpicGrantDecisionResult MapResponse(int statusCode, string body)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return EpicGrantDecisionResult.Failed(
                $"the orchestrator returned a non-JSON response (HTTP {statusCode})", statusCode);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("result", out var result) ||
                result.ValueKind != JsonValueKind.String)
            {
                return EpicGrantDecisionResult.Failed(
                    $"the orchestrator response carried no decision result (HTTP {statusCode})", statusCode);
            }

            string? reason = root.TryGetProperty("reason", out var reasonElement) &&
                             reasonElement.ValueKind == JsonValueKind.String
                ? reasonElement.GetString()
                : null;

            long? decisionId = root.TryGetProperty("decisionId", out var idElement) &&
                               idElement.ValueKind == JsonValueKind.Number &&
                               idElement.TryGetInt64(out var id)
                ? id
                : null;

            return new EpicGrantDecisionResult
            {
                Result = result.GetString(),
                Reason = reason,
                DecisionId = decisionId,
                StatusCode = statusCode,
            };
        }
    }
}
