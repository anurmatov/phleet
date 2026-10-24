using System.Text;
using System.Text.Json;
using Fleet.Shared;

namespace Fleet.Temporal.Mcp;

/// <summary>
/// #476 guarded recovery dispatch. Before an existing start/cancel/signal tool acts with a
/// <c>recovery_operation_id</c>, the bridge asks the orchestrator to authorize the exact dispatch
/// (reserved→dispatching, once), and afterwards reports what happened. The bridge authenticates
/// with the host-owned admin bearer; it never holds or uses the coordinator inbox token.
/// </summary>
public interface IWorkflowRecoveryForwarder
{
    /// <summary>
    /// One <c>POST …/recovery/{operationId}/authorize-dispatch</c>. One attempt, no retry. Never
    /// throws for transport, timeout, configuration or response-shape problems.
    /// </summary>
    Task<WorkflowRecoveryAuthorization> AuthorizeDispatchAsync(
        Guid operationId, WorkflowRecoveryDispatchRequest request, CancellationToken cancellationToken = default);

    /// <summary>One <c>POST …/recovery/{operationId}/receipt</c>. One attempt, no retry. Never throws.</summary>
    Task<WorkflowRecoveryReceiptOutcome> RecordReceiptAsync(
        Guid operationId, WorkflowRecoveryReceiptRequest request, CancellationToken cancellationToken = default);

    /// <summary>The operation's <c>state</c> from <c>GET …/recovery/{operationId}</c>, or null when unreadable.</summary>
    Task<string?> ReadStateAsync(Guid operationId, CancellationToken cancellationToken = default);
}

/// <summary>
/// What authorize-dispatch answered. <see cref="Allowed"/> is true only for a 2xx carrying
/// <c>allowed: true</c>. <see cref="Unknown"/> means there was no usable answer, so the
/// orchestrator may or may not have moved the reservation to dispatching.
/// </summary>
public sealed record WorkflowRecoveryAuthorization
{
    public bool Allowed { get; init; }
    public string? ReplacementWorkflowId { get; init; }
    public string? Reason { get; init; }
    public int? StatusCode { get; init; }
    public bool Unknown { get; init; }

    public static WorkflowRecoveryAuthorization Refused(string reason, int? statusCode = null) =>
        new() { Reason = reason, StatusCode = statusCode };

    public static WorkflowRecoveryAuthorization Ambiguous(string reason, int? statusCode = null) =>
        new() { Reason = reason, StatusCode = statusCode, Unknown = true };
}

public sealed record WorkflowRecoveryReceiptOutcome(bool Recorded, int? StatusCode, string? Error)
{
    public string Describe() => Recorded ? "recorded" : $"not_recorded ({Error ?? "unknown"})";
}

/// <summary>
/// Default <see cref="IWorkflowRecoveryForwarder"/> on the named <c>"orchestrator-workflow-recovery"</c>
/// HttpClient — the same base address and admin bearer as <c>"orchestrator"</c>, like
/// <see cref="OrchestratorEpicGrantForwarder"/>, with a timeout above the orchestrator's recheck.
/// </summary>
public sealed class WorkflowRecoveryForwarder(IHttpClientFactory httpClientFactory) : IWorkflowRecoveryForwarder
{
    internal const string HttpClientName = "orchestrator-workflow-recovery";

    /// <summary>Above the orchestrator's authoritative recheck (bounded Temporal reads plus the database).</summary>
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    private static readonly JsonSerializerOptions WebOptions = new(JsonSerializerDefaults.Web);

    internal static string OperationPath(Guid operationId) =>
        $"api/workflow-outcomes/recovery/{operationId:D}";

    public async Task<WorkflowRecoveryAuthorization> AuthorizeDispatchAsync(
        Guid operationId, WorkflowRecoveryDispatchRequest request, CancellationToken cancellationToken = default)
    {
        var client = httpClientFactory.CreateClient(HttpClientName);
        if (client.BaseAddress is null)
            return WorkflowRecoveryAuthorization.Refused("the orchestrator URL is not configured");

        int statusCode;
        string body;
        try
        {
            using var content = new StringContent(JsonSerializer.Serialize(request, WebOptions), Encoding.UTF8, "application/json");
            using var response = await client.PostAsync(OperationPath(operationId) + "/authorize-dispatch", content, cancellationToken);
            statusCode = (int)response.StatusCode;
            body = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return WorkflowRecoveryAuthorization.Ambiguous("the orchestrator did not answer before the timeout");
        }
        catch (Exception ex)
        {
            // Only the type is reported: transport messages can carry internal addresses.
            return WorkflowRecoveryAuthorization.Ambiguous($"the orchestrator could not be reached ({ex.GetType().Name})");
        }

        return MapAuthorization(statusCode, body);
    }

    /// <summary>
    /// 2xx with <c>allowed: true</c> → allowed. A JSON <c>allowed: false</c>, or any 4xx, is a
    /// definitive refusal (nothing moved). Anything else — 5xx, an unparseable or shapeless 2xx —
    /// is ambiguous: the reservation may now be dispatching.
    /// </summary>
    internal static WorkflowRecoveryAuthorization MapAuthorization(int statusCode, string body)
    {
        bool? allowed = null;
        string? replacement = null;
        string? reason = null;
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in root.EnumerateObject())
                {
                    if (string.Equals(property.Name, "allowed", StringComparison.OrdinalIgnoreCase) &&
                        property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                        allowed = property.Value.GetBoolean();
                    else if (string.Equals(property.Name, "replacementWorkflowId", StringComparison.OrdinalIgnoreCase) &&
                             property.Value.ValueKind == JsonValueKind.String)
                        replacement = property.Value.GetString();
                    else if (string.Equals(property.Name, "reason", StringComparison.OrdinalIgnoreCase) &&
                             property.Value.ValueKind == JsonValueKind.String)
                        reason = property.Value.GetString();
                }
            }
        }
        catch (JsonException)
        {
            // Treated below by status code.
        }

        var success = statusCode is >= 200 and < 300;
        if (success && allowed == true)
            return new WorkflowRecoveryAuthorization { Allowed = true, ReplacementWorkflowId = replacement, StatusCode = statusCode };
        if (allowed == false || statusCode is >= 400 and < 500)
            return WorkflowRecoveryAuthorization.Refused(reason ?? $"HTTP {statusCode}", statusCode);
        return WorkflowRecoveryAuthorization.Ambiguous($"the orchestrator returned no usable decision (HTTP {statusCode})", statusCode);
    }

    public async Task<WorkflowRecoveryReceiptOutcome> RecordReceiptAsync(
        Guid operationId, WorkflowRecoveryReceiptRequest request, CancellationToken cancellationToken = default)
    {
        var client = httpClientFactory.CreateClient(HttpClientName);
        if (client.BaseAddress is null)
            return new(false, null, "orchestrator_url_missing");

        try
        {
            using var content = new StringContent(JsonSerializer.Serialize(request, WebOptions), Encoding.UTF8, "application/json");
            using var response = await client.PostAsync(OperationPath(operationId) + "/receipt", content, cancellationToken);
            var code = (int)response.StatusCode;
            return new(response.IsSuccessStatusCode, code, response.IsSuccessStatusCode ? null : $"http_{code}");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(false, null, "timeout");
        }
        catch (Exception ex)
        {
            return new(false, null, ex.GetType().Name);
        }
    }

    public async Task<string?> ReadStateAsync(Guid operationId, CancellationToken cancellationToken = default)
    {
        var client = httpClientFactory.CreateClient(HttpClientName);
        if (client.BaseAddress is null) return null;

        try
        {
            using var response = await client.GetAsync(OperationPath(operationId), cancellationToken);
            if (!response.IsSuccessStatusCode) return null;
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty("state", out var state) &&
                   state.ValueKind == JsonValueKind.String
                ? state.GetString()
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
