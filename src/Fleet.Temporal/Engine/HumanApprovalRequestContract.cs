using System.Text.Json;
using System.Text.RegularExpressions;
using Temporalio.Converters;

namespace Fleet.Temporal.Engine;

/// <summary>New-type-only admission, not a new workflow primitive or approval authority.</summary>
public static class HumanApprovalRequestContract
{
    public const string WorkflowType = "UweHumanApprovalRequestWorkflow";
    public const string Signal = "request-approval";
    public const string RequesterMemo = "ApprovalRequester";
    public const int MaxInputCharacters = 1800;

    public static bool IsWorkflowType(string value) => string.Equals(value.Trim(), WorkflowType, StringComparison.OrdinalIgnoreCase);

    public static JsonElement Enrich(JsonElement input, string? requester, string? id, string ns, string queue, IPayloadConverter converter)
    {
        ValidateIdentity(requester, id, ns, queue);
        ValidateFields(input, ["Action", "Scope"]);
        var enriched = JsonSerializer.SerializeToElement(new
        {
            Action = input.GetProperty("Action").GetString(),
            Scope = input.GetProperty("Scope").GetString(),
            RequesterAgent = requester
        });
        ValidateBounds(enriched, converter);
        return enriched;
    }

    public static void ValidateStart(JsonElement input, string? memoRequester, string? id, string ns, string queue, IPayloadConverter converter)
    {
        ValidateIdentity(memoRequester, id, ns, queue);
        ValidateFields(input, ["Action", "Scope", "RequesterAgent"]);
        if (input.GetProperty("RequesterAgent").GetString() != memoRequester)
            throw new ArgumentException("Approval requester memo/input mismatch.");
        ValidateBounds(input, converter);
    }

    private static void ValidateIdentity(string? requester, string? id, string ns, string queue)
    {
        if (string.IsNullOrWhiteSpace(requester)) throw new ArgumentException("Attributed requester is required.");
        if (id is null || !Regex.IsMatch(id, @"\Ahuman-approval-[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}\z"))
            throw new ArgumentException("Supply workflow_id=human-approval-<lowercase RFC 4122 UUID>; no ID is generated.");
        if (ns != "fleet" || queue != "fleet") throw new ArgumentException("Human approval requests require namespace/task queue fleet.");
    }

    private static void ValidateFields(JsonElement input, string[] fields)
    {
        if (input.ValueKind != JsonValueKind.Object) throw new ArgumentException("Input must be an object.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in input.EnumerateObject())
            if (!fields.Contains(field.Name, StringComparer.Ordinal) || !seen.Add(field.Name) ||
                field.Value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(field.Value.GetString()))
                throw new ArgumentException("Input requires exact, unique, nonblank string fields: " + string.Join(", ", fields));
        if (seen.Count != fields.Length) throw new ArgumentException("Missing required approval input.");
    }

    private static void ValidateBounds(JsonElement input, IPayloadConverter converter)
    {
        var payload = converter.ToPayload(input);
        if (!payload.Metadata.TryGetValue("encoding", out var encoding) || encoding.ToStringUtf8() != "json/plain" ||
            payload.Data.ToStringUtf8().Length > MaxInputCharacters)
            throw new ArgumentException("Enriched approval input must be JSON and at most 1800 serialized characters.");
    }
}
