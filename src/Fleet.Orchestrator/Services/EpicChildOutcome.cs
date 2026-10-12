using System.Globalization;
using System.Text.Json;

namespace Fleet.Orchestrator.Services;

/// <summary>Strictly parses a closed design's returned payload, never its internal sentinels.</summary>
public sealed record EpicChildOutcome(string Outcome, long? Issue)
{
    public static EpicChildOutcome Parse(string type, string status, string? encoding, string? json,
        long? issueAttr, string? existingIssueInput)
    {
        if (status == "Running") return new("running", null);
        if (type == "UweDesignToPrWorkflow") return new("no_result", null);
        if (type != "UweDesignWorkflow") return new("n/a", null);
        if (status is "Failed" or "Terminated" or "Canceled" or "TimedOut") return new("failed", null);
        if (status != "Completed") return new("unknown", null);
        if (encoding == "binary/null") return new("not_approved", null);
        if (encoding != "json/plain" || json is null) return new("unknown", null);
        try
        {
            using var outer = JsonDocument.Parse(json);
            using var inner = outer.RootElement.ValueKind == JsonValueKind.String
                ? JsonDocument.Parse(outer.RootElement.GetString()!.Trim()) : null;
            var value = inner?.RootElement ?? outer.RootElement;
            if (value.ValueKind != JsonValueKind.Object) return new("invalid_result", null);
            var properties = value.EnumerateObject().ToArray();
            if (properties.Length != 1 || properties[0].Name != "IssueNumber"
                || properties[0].Value.ValueKind != JsonValueKind.Number
                || !properties[0].Value.TryGetInt64(out var issue) || issue < 1 || issueAttr != issue)
                return new("invalid_result", null);
            if (!string.IsNullOrEmpty(existingIssueInput)
                && (!long.TryParse(existingIssueInput, NumberStyles.None, CultureInfo.InvariantCulture, out var expected) || expected != issue))
                return new("invalid_result", null);
            return new("approved", issue);
        }
        catch (JsonException) { return new("invalid_result", null); }
    }
}
