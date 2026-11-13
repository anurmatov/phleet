using System.Text.RegularExpressions;
using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Auth;
using Fleet.Orchestrator.Services;
using Fleet.Orchestrator.Services.Push;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Fleet.Orchestrator.Endpoints;

/// <summary>
/// <c>/api/push</c>: a phone registers its device token to be told when something needs a person.
/// </summary>
/// <remarks>
/// Registering and removing are writes, so the administrative gate asks for the operator token
/// (<see cref="OrchestratorAuth"/>): a phone with a read-only connection cannot register. The read
/// says only whether the feature is on and how many devices are registered. No route returns a
/// device token.
/// </remarks>
public static partial class PushEndpoints
{
    /// <summary>More than a person's phones and tablets is a mistake or an abuse, not a fleet.</summary>
    public const int MaxDevices = 10;

    [GeneratedRegex(@"\A[A-Za-z0-9_-]{8,64}\z")]
    private static partial Regex DeviceIdShape();

    public sealed record RegisterBody(string? Token);

    /// <summary>What the chat service says when an agent replied and no client was attached.</summary>
    public sealed record ChatReplyBody(string? Agent, string? ConversationId, ulong? Seq);

    [GeneratedRegex(@"\A[0-9A-HJKMNP-TV-Z]{26}\z")]
    private static partial Regex ConversationIdShape();

    public static IEndpointRouteBuilder MapPushEndpoints(this IEndpointRouteBuilder app)
    {
        // ── the chat service: an agent replied while nobody was looking (#530 step 9) ──
        //
        // Internal, never proxied, and carries its own Comms-only token. Identifiers only: which
        // agent and which conversation, never what was said. Nothing is sent when push is off, and
        // an agent this orchestrator does not know is ignored rather than named.
        app.MapPost(Fleet.Shared.SignIn.ChatReplyPushPath, async (HttpContext http, [FromBody] ChatReplyBody? body,
            [FromServices] JournalTokenService tokens, [FromServices] PushReadiness readiness,
            [FromServices] ChatReplyPushes replies, [FromServices] IServiceScopeFactory scopes, CancellationToken ct) =>
        {
            var presented = Fleet.Orchestrator.Auth.CallerAuthenticator.BearerOf(http.Request.Headers.Authorization.FirstOrDefault());
            if (!tokens.TryVerify(presented, Fleet.Shared.SignIn.ChatReplyPushTokenPurpose, out var subject)
                || !string.Equals(subject, Fleet.Shared.SignIn.CheckTokenSubject, StringComparison.Ordinal))
                return Results.Json(new { error = "push_token_invalid" }, statusCode: StatusCodes.Status403Forbidden);

            if (body?.Seq is not { } seq || !ChatAgents.IsUsableName(body.Agent)
                || body.ConversationId is null || !ConversationIdShape().IsMatch(body.ConversationId))
                return Results.BadRequest(new { error = "body_invalid" });

            if (!readiness.IsReady) return Results.NoContent();

            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetService<OrchestratorDbContext>();
            if (db is null) return Results.NoContent();

            // Ordinal after the query: the database compares names without regard to case.
            var agent = (await db.Agents.AsNoTracking()
                    .Where(a => a.Name == body.Agent)
                    .Select(a => new { a.Name, a.DisplayName })
                    .ToListAsync(ct))
                .FirstOrDefault(a => string.Equals(a.Name, body.Agent, StringComparison.Ordinal));
            if (agent is null) return Results.NoContent();

            var queued = replies.Enqueue(PushMessages.ChatReply(
                agent.Name, string.IsNullOrWhiteSpace(agent.DisplayName) ? agent.Name : agent.DisplayName,
                body.ConversationId, seq));

            return queued
                ? Results.Accepted()
                : Results.Json(new { error = "too_many_requests" }, statusCode: StatusCodes.Status429TooManyRequests);
        });

        app.MapGet("/api/push", async ([FromServices] PushReadiness readiness, [FromServices] IServiceScopeFactory scopes, CancellationToken ct) =>
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetService<OrchestratorDbContext>();
            return Results.Ok(new
            {
                enabled = readiness.IsReady,
                devices = db is null ? 0 : await db.PushDevices.CountAsync(ct),
            });
        });

        app.MapPut("/api/push/devices/{deviceId}", async (HttpContext http, string deviceId, [FromBody] RegisterBody? body,
            [FromServices] PushReadiness readiness, [FromServices] IServiceScopeFactory scopes, CancellationToken ct) =>
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetService<OrchestratorDbContext>();
            if (db is null) return Results.Problem("Database is not configured");
            // Said before anything is stored: an app that registered against an orchestrator
            // that will never send would otherwise tell its owner that notifications are on.
            if (!readiness.IsReady) return Results.Json(new { error = "push_disabled" }, statusCode: StatusCodes.Status503ServiceUnavailable);
            if (!DeviceIdShape().IsMatch(deviceId)) return Results.BadRequest(new { error = "device_id_invalid" });
            var token = body?.Token?.Trim();
            if (!ApnsClient.IsDeviceToken(token)) return Results.BadRequest(new { error = "token_invalid" });

            var now = DateTime.UtcNow;
            var device = await db.PushDevices.FirstOrDefaultAsync(d => d.DeviceId == deviceId, ct);
            var caller = CallerIdentity.Of(http);
            if (caller.Kind == CallerKind.App && device is not null
                && !string.Equals(device.AuthDeviceId, caller.DeviceId, StringComparison.Ordinal))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (caller.Kind is CallerKind.App or CallerKind.Browser)
            {
                // The identity was verified earlier in the request. Refuse a concurrent revoke
                // here too; delivery independently filters revoked owners before sending.
                var owner = await db.AuthDevices.AsNoTracking().FirstOrDefaultAsync(d => d.DeviceId == caller.DeviceId, ct);
                if (owner is null || owner.RevokedAt is not null || owner.DeviceId != caller.DeviceId)
                    return Results.Unauthorized();
            }
            if (device is null)
            {
                if (await db.PushDevices.CountAsync(ct) >= MaxDevices)
                    return Results.Json(new { error = "device_limit" }, statusCode: StatusCodes.Status409Conflict);
                db.PushDevices.Add(new PushDevice { DeviceId = deviceId, AuthDeviceId = caller.DeviceId, Token = token!, CreatedAt = now, UpdatedAt = now });
            }
            else
            {
                // Administrative updates preserve an existing binding instead of unbinding it.
                device.AuthDeviceId ??= caller.DeviceId;
                device.Token = token!;
                device.UpdatedAt = now;
            }
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { registered = true });
        });

        app.MapDelete("/api/push/devices/{deviceId}", async (HttpContext http, string deviceId, [FromServices] IServiceScopeFactory scopes, CancellationToken ct) =>
        {
            if (!DeviceIdShape().IsMatch(deviceId)) return Results.BadRequest(new { error = "device_id_invalid" });
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetService<OrchestratorDbContext>();
            if (db is null) return Results.Problem("Database is not configured");
            var device = await db.PushDevices.FirstOrDefaultAsync(d => d.DeviceId == deviceId, ct);
            if (CallerIdentity.Of(http) is { Kind: CallerKind.App } caller
                && device is not null && !string.Equals(device.AuthDeviceId, caller.DeviceId, StringComparison.Ordinal))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (device is not null)
            {
                db.PushDevices.Remove(device);
                await db.SaveChangesAsync(ct);
            }
            // Removing a device that is not there is still "it is not registered".
            return Results.Ok(new { registered = false });
        });

        return app;
    }
}
