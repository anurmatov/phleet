using System.Security.Cryptography;
using System.Text;
using Fleet.Orchestrator.Services;

namespace Fleet.Orchestrator.Endpoints;

/// <summary>
/// <c>GET /api/operator-token</c>: says whether the bearer on this request is the operator token,
/// and changes nothing.
/// </summary>
/// <remarks>
/// <para>
/// The administrative gate (<see cref="OrchestratorAuth"/>) is method-based: reads never need the
/// token, every write does. That left a client with no way to learn whether the token it holds is
/// right except to make a write and see. A phone that stores the token at setup would find out it
/// was mistyped only at the moment it tried to approve something.
/// </para>
/// <para>
/// So this is the one read that checks the token itself, the way the credential-file download
/// does. It answers with a status and nothing else: no token, no hint of its length, no identity.
/// </para>
/// </remarks>
public static class OperatorTokenEndpoints
{
    public const string Path = "/api/operator-token";

    /// <summary>The bearer matches <c>Orchestrator:AuthToken</c>.</summary>
    public const string Accepted = "accepted";

    /// <summary>
    /// No <c>Orchestrator:AuthToken</c> is configured, so writes are not gated at all. Reported
    /// as its own status rather than as <see cref="Accepted"/>: a client must not conclude that
    /// the token it sent was checked.
    /// </summary>
    public const string NotRequired = "not_required";

    public static IEndpointRouteBuilder MapOperatorTokenEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(Path, (HttpRequest request, IConfiguration configuration) =>
            Describe(request.Headers.Authorization.FirstOrDefault(), configuration["Orchestrator:AuthToken"]));
        return app;
    }

    /// <summary>
    /// 200 with a status when the bearer is the operator token or none is configured; otherwise
    /// the same 401 body the administrative gate answers with.
    /// </summary>
    public static IResult Describe(string? authorization, string? configuredToken)
    {
        if (string.IsNullOrWhiteSpace(configuredToken))
            return Results.Ok(new { status = NotRequired });

        var presented = authorization?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true
            ? authorization["Bearer ".Length..].Trim()
            : null;

        return presented is not null && Matches(presented, configuredToken)
            ? Results.Ok(new { status = Accepted })
            : Results.Json(new { error = "Unauthorized" }, statusCode: StatusCodes.Status401Unauthorized);
    }

    // This route exists to answer "is this the token", so it compares without an early exit.
    private static bool Matches(string presented, string configured) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented), Encoding.UTF8.GetBytes(configured));
}
