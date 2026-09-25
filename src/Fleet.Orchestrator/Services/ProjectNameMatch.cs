namespace Fleet.Orchestrator.Services;

/// <summary>
/// Project-name matching shared by provisioning and the context-removal preflight. Names are compared
/// in C# on loaded rows, never by database collation, which differs between MySQL and the SQLite test host.
/// </summary>
public static class ProjectNameMatch
{
    /// <summary>
    /// The loaded row named <paramref name="name"/>, ignoring case — an exact ordinal match first, so
    /// a (hand-made) case-variant duplicate can never shadow the row the name actually spells.
    /// </summary>
    public static T? MatchByName<T>(IReadOnlyCollection<T> rows, string name, Func<T, string> nameOf) where T : class =>
        rows.FirstOrDefault(r => string.Equals(nameOf(r), name, StringComparison.Ordinal))
        ?? rows.FirstOrDefault(r => string.Equals(nameOf(r), name, StringComparison.OrdinalIgnoreCase));
}
