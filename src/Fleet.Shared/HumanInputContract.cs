using System.Text.Json;
using System.Text.RegularExpressions;

namespace Fleet.Shared;

/// <summary>Pure, shared JSON validation. All user text remains literal and untrimmed.</summary>
public static class HumanInputContract
{
    public static void Fields(JsonElement value, string[] required, params string[] optional)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new ArgumentException("Expected an object.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in value.EnumerateObject())
            if (!seen.Add(p.Name) || (!required.Contains(p.Name, StringComparer.Ordinal) && !optional.Contains(p.Name, StringComparer.Ordinal)))
                throw new ArgumentException("Unknown or duplicate field: " + p.Name);
        if (required.Any(k => !seen.Contains(k))) throw new ArgumentException("Missing required field.");
    }
    public static string Text(JsonElement value, string key, bool nonblank = true, int maximum = int.MaxValue)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(key, out var p) || p.ValueKind != JsonValueKind.String ||
            (nonblank && string.IsNullOrWhiteSpace(p.GetString())) || p.GetString()!.Length > maximum)
            throw new ArgumentException("Invalid string: " + key);
        return p.GetString()!;
    }
    public static void ValidateRequest(JsonElement input, bool enriched = false)
    {
        Fields(input, enriched ? ["Question", "Context", "AnswerMode", "Origin"] : ["Question", "Context", "AnswerMode"], "Choices");
        Text(input, "Question"); Text(input, "Context");
        var mode = Text(input, "AnswerMode");
        if (mode is not ("choice" or "text" or "approve-reject")) throw new ArgumentException("Unknown answer mode.");
        if (mode == "choice")
        {
            if (!input.TryGetProperty("Choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() is < 2 or > 8)
                throw new ArgumentException("Supply 2–8 named choices.");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var choice in choices.EnumerateArray())
            {
                Fields(choice, ["Id", "Label"]);
                var id = Text(choice, "Id"); Text(choice, "Label");
                if (!Regex.IsMatch(id, @"\A[a-z][a-z0-9_-]{0,31}\z") || !ids.Add(id)) throw new ArgumentException("Invalid or duplicate choice ID.");
            }
        }
        else if (input.TryGetProperty("Choices", out _)) throw new ArgumentException("Choices are only allowed for choice mode.");
        if (enriched) ValidateOrigin(input.GetProperty("Origin"));
    }
    public static void ValidateOrigin(JsonElement origin)
    {
        var kind = Text(origin, "Kind");
        if (kind == "agent") { Fields(origin, ["Kind", "RequesterAgent"]); Text(origin, "RequesterAgent"); }
        else if (kind == "parent") { Fields(origin, ["Kind", "Namespace", "WorkflowId", "RunId"]); Text(origin, "Namespace"); Text(origin, "WorkflowId"); Text(origin, "RunId"); }
        else throw new ArgumentException("Invalid origin.");
    }
    public static JsonElement ValidateAnswer(JsonElement request, JsonElement answer, bool bound = false)
    {
        ValidateRequest(request, request.ValueKind == JsonValueKind.Object && request.TryGetProperty("Origin", out _));
        Fields(answer, bound ? ["Value", "Ref", "VisitId"] : ["Value"], "Comment");
        var value = Text(answer, "Value", maximum: 2000);
        if (answer.TryGetProperty("Comment", out _)) Text(answer, "Comment", false, 2000);
        var mode = Text(request, "AnswerMode");
        if (mode == "approve-reject" && value is not ("approved" or "rejected") ||
            mode == "choice" && !request.GetProperty("Choices").EnumerateArray().Any(c => c.GetProperty("Id").GetString() == value))
            throw new ArgumentException("Answer must be a declared ID.");
        var fields = new Dictionary<string, object?> { ["Value"] = value };
        if (answer.TryGetProperty("Comment", out var comment)) fields["Comment"] = comment.GetString();
        return JsonSerializer.SerializeToElement(fields);
    }
}
