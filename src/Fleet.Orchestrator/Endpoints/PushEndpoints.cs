using System.Text.RegularExpressions;
using Fleet.Orchestrator.Data;
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

    public static IEndpointRouteBuilder MapPushEndpoints(this IEndpointRouteBuilder app)
    {
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

        app.MapPut("/api/push/devices/{deviceId}", async (string deviceId, [FromBody] RegisterBody? body,
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
            if (device is null)
            {
                if (await db.PushDevices.CountAsync(ct) >= MaxDevices)
                    return Results.Json(new { error = "device_limit" }, statusCode: StatusCodes.Status409Conflict);
                db.PushDevices.Add(new PushDevice { DeviceId = deviceId, Token = token!, CreatedAt = now, UpdatedAt = now });
            }
            else
            {
                device.Token = token!;
                device.UpdatedAt = now;
            }
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { registered = true });
        });

        app.MapDelete("/api/push/devices/{deviceId}", async (string deviceId, [FromServices] IServiceScopeFactory scopes, CancellationToken ct) =>
        {
            if (!DeviceIdShape().IsMatch(deviceId)) return Results.BadRequest(new { error = "device_id_invalid" });
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetService<OrchestratorDbContext>();
            if (db is null) return Results.Problem("Database is not configured");
            var device = await db.PushDevices.FirstOrDefaultAsync(d => d.DeviceId == deviceId, ct);
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
