using System.Text.Json;
using System.Text.RegularExpressions;
using Fleet.Shared;
using Temporalio.Converters;
using Temporalio.Workflows;

namespace Fleet.Temporal.Engine;

/// <summary>Type-specific trusted origin and Temporal converter admission.</summary>
public static class HumanInputRequestContract
{
    public const string WorkflowType = "UweHumanInputWorkflow";
    public const string Signal = GateSignal.RequestInput;
    public const string OriginMemo = "HumanInputOrigin";
    public const int MaxInputCharacters = 1800;
    public static bool IsWorkflowType(string value) => string.Equals(value.Trim(), WorkflowType, StringComparison.OrdinalIgnoreCase);
    public static JsonElement Enrich(JsonElement input, string? requester, string? id, string ns, string queue, IPayloadConverter converter)
    {
        if (string.IsNullOrWhiteSpace(requester)) throw new ArgumentException("Attributed requester is required.");
        return EnrichOrigin(input, JsonSerializer.SerializeToElement(new { Kind = "agent", RequesterAgent = requester }), id, ns, queue, converter);
    }
    public static JsonElement EnrichParent(JsonElement input, string id, string ns, string queue, string parentId, string parentRun, IPayloadConverter converter) =>
        EnrichOrigin(input, JsonSerializer.SerializeToElement(new { Kind = "parent", Namespace = ns, WorkflowId = parentId, RunId = parentRun }), id, ns, queue, converter);
    private static JsonElement EnrichOrigin(JsonElement input, JsonElement origin, string? id, string ns, string queue, IPayloadConverter converter)
    {
        HumanInputContract.ValidateRequest(input);
        var fields = input.EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
        fields.Add("Origin", origin);
        var enriched = JsonSerializer.SerializeToElement(fields);
        ValidateIdentity(id, ns, queue); HumanInputContract.ValidateRequest(enriched, true); ValidateBounds(enriched, converter);
        return enriched;
    }
    public static void ValidateStart(JsonElement input, JsonElement? memoOrigin, string? id, string ns, string queue, IPayloadConverter converter, WorkflowInfo.ParentInfo? parent = null)
    {
        ValidateIdentity(id, ns, queue); HumanInputContract.ValidateRequest(input, true); ValidateBounds(input, converter);
        var origin = input.GetProperty("Origin");
        if (memoOrigin is null || !JsonElement.DeepEquals(origin, memoOrigin.Value)) throw new ArgumentException("Origin memo/input mismatch.");
        if (origin.GetProperty("Kind").GetString() == "agent")
        { if (parent is not null) throw new ArgumentException("Agent origin cannot have a parent."); }
        else if (parent is null || origin.GetProperty("Namespace").GetString() != parent.Namespace || parent.Namespace != ns ||
            origin.GetProperty("WorkflowId").GetString() != parent.WorkflowId || origin.GetProperty("RunId").GetString() != parent.RunId)
            throw new ArgumentException("Parent metadata/origin mismatch.");
    }
    private static void ValidateIdentity(string? id, string ns, string queue)
    {
        if (id is null || !Regex.IsMatch(id, @"\Ahuman-input-[0-9a-f]{8}-[0-9a-f]{4}-[1-5][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}\z"))
            throw new ArgumentException("Supply workflow_id=human-input-<lowercase RFC 4122 UUID>.");
        if (ns != "fleet" || queue != "fleet") throw new ArgumentException("Human input requires namespace/task queue fleet.");
    }
    private static void ValidateBounds(JsonElement input, IPayloadConverter converter)
    {
        var payload = converter.ToPayload(input);
        if (!payload.Metadata.TryGetValue("encoding", out var encoding) || encoding.ToStringUtf8() != "json/plain" || payload.Data.ToStringUtf8().Length > MaxInputCharacters)
            throw new ArgumentException("Enriched input must be JSON and at most 1800 serialized UTF-16 units.");
    }
}
