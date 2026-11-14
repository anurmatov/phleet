namespace Fleet.Orchestrator.Services;

/// <summary>
/// What a caller answers when a change is committed to the database but the running service that
/// enforces it could not be told. That is not a success: a removed Telegram user can still write
/// to the agent, and a revoked project is still readable, until the remedy named here is applied.
/// </summary>
/// <remarks>
/// The same rule as <see cref="SecretStatePublicationException"/> for bound secrets: never report
/// success for a change the enforcing side does not carry. REST answers 503 with
/// <c>saved: true, delivered: false</c>; an MCP tool answers with the sentence.
/// </remarks>
public static class LiveDelivery
{
    /// <summary>
    /// For an allowlist change on one agent. The agent reads its allowlist from its generated
    /// config at start, so a reprovision applies what is stored.
    /// </summary>
    public static string AllowlistNotDelivered(string change, string agentName) =>
        $"Saved, but not delivered: {change} is stored, and the running agent '{agentName}' could not be told, "
        + "so it still uses its previous allowlist. Reprovision the agent to apply it.";

    /// <summary>
    /// For a project-access change. fleet-memory re-reads the access list on its own timer, so this
    /// one ends without anyone acting.
    /// </summary>
    public static string AccessNotDelivered(string change) =>
        $"Saved, but not delivered: {change} is stored, and fleet-memory could not be told. "
        + "It re-reads the access list on its own within five minutes; until then the previous access applies.";

    /// <summary>The REST answer for a change that was saved and not delivered.</summary>
    public static IResult NotDelivered(string sentence) =>
        Results.Json(new { error = sentence, saved = true, delivered = false },
            statusCode: StatusCodes.Status503ServiceUnavailable);
}
