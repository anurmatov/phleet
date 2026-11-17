namespace Fleet.Memory.Endpoints;

/// <summary>What <c>GET /internal/memory</c> says about one listed memory, read from its index payload.</summary>
public static class MemoryListEntry
{
    /// <summary>
    /// When the memory was last updated. The route used to answer with the created time under this
    /// name, because the payload held no other; it holds <c>updated</c> now. A point written before
    /// that (not indexed again yet) still has only <c>created</c>, which is what it gets.
    /// </summary>
    public static string UpdatedAt(IReadOnlyDictionary<string, string> payload) =>
        payload.GetValueOrDefault("updated") is { Length: > 0 } updated
            ? updated
            : payload.GetValueOrDefault("created") ?? "";
}
