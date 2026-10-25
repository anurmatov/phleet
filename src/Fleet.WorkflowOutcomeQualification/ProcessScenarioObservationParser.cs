using System.Text.Json;

namespace Fleet.WorkflowOutcomeQualification;

public static class ProcessScenarioObservationParser
{
    public const int SchemaVersion = 1;
    public const string Kind = "workflow-outcome-process-observations";

    private static readonly string[] RootKeys = ["schemaVersion", "kind", "observations"];
    private static readonly string[] ObservationKeys =
    [
        "scenario", "inboxItems", "modelTurns", "fsyncs", "acks", "holds", "humanEmissions",
        "brokerConfirms", "wrongTokenDenied", "wrongOwnerDenied", "disabledDenied",
        "crashAfterAdmission", "secondTurn", "lostPostReconciled", "lateConfirmBeforeAck",
        "outageObserved", "handoffAccepted", "databaseRowsPreserved", "keyringRestored",
        "batchId", "itemId", "version", "generation", "turnOutputSha256", "nonce", "processAncestry"
    ];

    public static IReadOnlyList<ProcessScenarioObservation> Parse(Stream stream)
    {
        using var document = JsonDocument.Parse(stream, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow
        });
        var root = document.RootElement;
        Object(root, RootKeys, "observations");
        if (root.GetProperty("schemaVersion").GetInt32() != SchemaVersion)
            throw new QualificationException("observations schemaVersion must be 1");
        if (root.GetProperty("kind").GetString() != Kind)
            throw new QualificationException("observations kind is wrong");
        var array = root.GetProperty("observations");
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() == 0)
            throw new QualificationException("observations must be a nonempty array");
        var observations = new List<ProcessScenarioObservation>();
        foreach (var item in array.EnumerateArray())
        {
            Object(item, ObservationKeys, "observation");
            observations.Add(new ProcessScenarioObservation(
                RequiredString(item.GetProperty("scenario"), "scenario"),
                NonnegativeInt(item.GetProperty("inboxItems"), "inboxItems"),
                NonnegativeInt(item.GetProperty("modelTurns"), "modelTurns"),
                NonnegativeInt(item.GetProperty("fsyncs"), "fsyncs"),
                NonnegativeInt(item.GetProperty("acks"), "acks"),
                NonnegativeInt(item.GetProperty("holds"), "holds"),
                NonnegativeInt(item.GetProperty("humanEmissions"), "humanEmissions"),
                NonnegativeInt(item.GetProperty("brokerConfirms"), "brokerConfirms"),
                RequiredBool(item.GetProperty("wrongTokenDenied"), "wrongTokenDenied"),
                RequiredBool(item.GetProperty("wrongOwnerDenied"), "wrongOwnerDenied"),
                RequiredBool(item.GetProperty("disabledDenied"), "disabledDenied"),
                RequiredBool(item.GetProperty("crashAfterAdmission"), "crashAfterAdmission"),
                RequiredBool(item.GetProperty("secondTurn"), "secondTurn"),
                RequiredBool(item.GetProperty("lostPostReconciled"), "lostPostReconciled"),
                RequiredBool(item.GetProperty("lateConfirmBeforeAck"), "lateConfirmBeforeAck"),
                RequiredBool(item.GetProperty("outageObserved"), "outageObserved"),
                RequiredBool(item.GetProperty("handoffAccepted"), "handoffAccepted"),
                RequiredBool(item.GetProperty("databaseRowsPreserved"), "databaseRowsPreserved"),
                RequiredBool(item.GetProperty("keyringRestored"), "keyringRestored"),
                OptionalString(item.GetProperty("batchId")),
                OptionalString(item.GetProperty("itemId")),
                OptionalInt(item.GetProperty("version")),
                OptionalLong(item.GetProperty("generation")),
                OptionalString(item.GetProperty("turnOutputSha256")),
                OptionalString(item.GetProperty("nonce")),
                OptionalString(item.GetProperty("processAncestry"))));
        }
        return observations;
    }

    private static void Object(JsonElement element, string[] keys, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new QualificationException(name + " must be an object");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!seen.Add(property.Name)) throw new QualificationException(name + " has duplicate key '" + property.Name + "'");
            if (!keys.Contains(property.Name, StringComparer.Ordinal))
                throw new QualificationException(name + " has unknown key '" + property.Name + "'");
        }
        foreach (var key in keys)
            if (!seen.Contains(key)) throw new QualificationException(name + " is missing '" + key + "'");
    }

    private static string RequiredString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(element.GetString())
            ? element.GetString()!
            : throw new QualificationException(name + " must be a nonempty string");

    private static string? OptionalString(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Null => null,
        JsonValueKind.String => element.GetString(),
        _ => throw new QualificationException("optional text must be a string or null")
    };

    private static bool RequiredBool(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.True || element.ValueKind == JsonValueKind.False
            ? element.GetBoolean()
            : throw new QualificationException(name + " must be a boolean");

    private static int NonnegativeInt(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out var value) || value < 0)
            throw new QualificationException(name + " must be a nonnegative integer");
        return value;
    }

    private static int? OptionalInt(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Null) return null;
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var value) && value > 0) return value;
        throw new QualificationException("version must be a positive integer or null");
    }

    private static long? OptionalLong(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Null) return null;
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var value) && value > 0) return value;
        throw new QualificationException("generation must be a positive integer or null");
    }
}
