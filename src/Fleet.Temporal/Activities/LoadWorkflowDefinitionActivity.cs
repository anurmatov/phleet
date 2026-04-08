namespace Fleet.Temporal.Activities;

using Fleet.Temporal.Engine;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Temporalio.Activities;

/// <summary>
/// Loads a workflow definition from the orchestrator REST API by workflow type name.
/// Called as the first activity in <see cref="Engine.UniversalWorkflow.RunAsync"/>.
/// The result is serialized into workflow history and replayed deterministically on replay.
///
/// The API returns the raw DB entity where <c>Definition</c> is a JSON string column.
/// This activity maps that to <see cref="WorkflowDefinitionModel"/> by parsing the string
/// into a <see cref="StepDefinition"/> tree. (#684)
/// </summary>
public sealed class LoadWorkflowDefinitionActivity(IHttpClientFactory httpClientFactory)
{
    private static readonly JsonSerializerOptions WebOptions =
        new(JsonSerializerDefaults.Web);

    private readonly HttpClient _client = httpClientFactory.CreateClient("orchestrator");

    [Activity("LoadWorkflowDefinition")]
    public async Task<WorkflowDefinitionModel> LoadAsync(string workflowTypeName)
    {
        var response = await _client.GetAsync($"api/workflow-definitions/{workflowTypeName}");
        response.EnsureSuccessStatusCode();

        var raw = await response.Content.ReadFromJsonAsync<WorkflowDefinitionRaw>(WebOptions)
            ?? throw new InvalidOperationException(
                $"Workflow definition '{workflowTypeName}' not found or returned null");

        // Ensure the polymorphic type discriminator ("type") is the first property in every
        // JSON object before deserializing. System.Text.Json's [JsonPolymorphic] requires the
        // discriminator to appear first; agents may write properties in any order (e.g. "name"
        // before "type"), which would otherwise throw NotSupportedException. (#699)
        var normalized = EnsureTypeFirst(raw.Definition);
        var root = JsonSerializer.Deserialize<StepDefinition>(normalized, WebOptions)
            ?? throw new InvalidOperationException(
                $"Failed to parse step tree for workflow '{workflowTypeName}'");

        return new WorkflowDefinitionModel
        {
            Name      = raw.Name,
            Namespace = raw.Namespace,
            TaskQueue = raw.TaskQueue,
            Root      = root,
            Version   = raw.Version,
        };
    }

    /// <summary>
    /// Recursively reorders each JSON object so the "type" discriminator property comes first.
    /// Required because System.Text.Json polymorphic deserialization expects the discriminator
    /// to be the first property, but authors may write properties in any order.
    /// </summary>
    private static string EnsureTypeFirst(string json)
    {
        var node = JsonNode.Parse(json);
        return JsonSerializer.Serialize(NormalizeTypeFirst(node), WebOptions);
    }

    private static JsonNode? NormalizeTypeFirst(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            var result = new JsonObject();
            if (obj.TryGetPropertyValue("type", out var typeVal))
                result["type"] = typeVal?.DeepClone();
            foreach (var (key, val) in obj)
            {
                if (key == "type") continue;
                result[key] = NormalizeTypeFirst(val);
            }
            return result;
        }
        if (node is JsonArray arr)
        {
            var result = new JsonArray();
            foreach (var item in arr)
                result.Add(NormalizeTypeFirst(item?.DeepClone()));
            return result;
        }
        return node?.DeepClone();
    }

    /// <summary>Intermediate DTO matching the raw API response (Definition is a JSON string column).</summary>
    private sealed record WorkflowDefinitionRaw(
        string Name,
        string Namespace,
        string TaskQueue,
        string Definition,
        int Version);
}
