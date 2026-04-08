namespace Fleet.Shared;

/// <summary>
/// Static registry mapping Temporal search attribute names to their types.
/// Single source of truth — used by Fleet.Temporal (UpsertTypedSearchAttributes) and
/// Fleet.Orchestrator (GET /api/search-attributes for the dashboard).
/// Keep in sync with SearchAttributeInitializer when adding new attributes.
/// </summary>
public static class SearchAttributeTypeRegistry
{
    public enum AttributeType { Int, Keyword, DateTime }

    private static readonly Dictionary<string, AttributeType> Registry = new(StringComparer.Ordinal)
    {
        ["IssueNumber"] = AttributeType.Int,
        ["PrNumber"]    = AttributeType.Int,
        ["PositionId"]  = AttributeType.Int,
        ["Phase"]       = AttributeType.Keyword,
        ["Repo"]        = AttributeType.Keyword,
        ["DocPrs"]      = AttributeType.Keyword,
        ["ReviewDate"]  = AttributeType.Keyword,
    };

    /// <summary>Returns the registered type for the attribute name, or null if unknown.</summary>
    public static AttributeType? GetType(string name)
        => Registry.TryGetValue(name, out var type) ? type : null;

    /// <summary>Returns all registered attribute names.</summary>
    public static IReadOnlyList<string> GetNames() => [.. Registry.Keys];
}
