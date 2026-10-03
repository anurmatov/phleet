using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Services;
using Microsoft.EntityFrameworkCore;

namespace Fleet.Orchestrator.Endpoints;

/// <summary>Live, uncached revocation gate. No agent token can call this endpoint.</summary>
public static class JournalCrossChatAuthorizationEndpoints
{
    public const string Path = "/internal/journal/cross-chat-authorization";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        PropertyNameCaseInsensitive = false,
    };
    private static readonly Regex Subject = new("^[A-Za-z0-9_-]{1,128}$", RegexOptions.CultureInvariant);

    public static WebApplication MapJournalCrossChatAuthorization(this WebApplication app)
    {
        app.MapPost(Path, async (HttpRequest request, IServiceScopeFactory scopes,
            JournalTokenService tokens, TelegramMembershipClient telegram, IConfiguration configuration) =>
        {
            var started = Stopwatch.GetTimestamp();
            var auth = request.Headers.Authorization.ToString();
            if (!auth.StartsWith("Bearer ", StringComparison.Ordinal)
                || !tokens.TryVerify(auth[7..], JournalTokenService.PurposeCrossChatAuthz, out var caller)
                || caller != "fleet-comms")
                return Results.Unauthorized();

            AuthorizationRequest? body;
            try
            {
                if (request.ContentLength > 1024) return Results.BadRequest(new { error = "invalid_request" });
                var bytes = new byte[1025];
                var count = 0;
                while (count < bytes.Length)
                {
                    var n = await request.Body.ReadAsync(bytes.AsMemory(count), request.HttpContext.RequestAborted);
                    if (n == 0) break;
                    count += n;
                }
                if (count > 1024) return Results.BadRequest(new { error = "invalid_request" });
                body = JsonSerializer.Deserialize<AuthorizationRequest>(bytes.AsSpan(0, count), Json);
            }
            catch (JsonException) { return Results.BadRequest(new { error = "invalid_request" }); }
            if (body is null || !Subject.IsMatch(body.Subject ?? "") || body.BotId <= 0
                || body.ChatId.HasValue != body.UserId.HasValue
                || body.ChatId == 0 || body.UserId <= 0)
                return Results.BadRequest(new { error = "invalid_request" });

            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            var agent = await db.Agents.Include(a => a.Tools).Include(a => a.EnvRefs)
                .AsSplitQuery().AsNoTracking().SingleOrDefaultAsync(a => a.Name == body.Subject, request.HttpContext.RequestAborted);
            var effective = agent is not null && JournalGrants.CrossChatEffective(agent);
            string? member = null;
            if (effective && body.ChatId is long chat && body.UserId is long user)
            {
                var env = ContainerProvisioningService.LoadEnvFile(configuration["Provisioning:EnvFilePath"] ?? "/app/deploy/.env");
                var token = agent!.EnvRefs.Where(e => e.EnvKeyName.StartsWith("TELEGRAM_", StringComparison.OrdinalIgnoreCase)
                    && e.EnvKeyName.EndsWith("_BOT_TOKEN", StringComparison.OrdinalIgnoreCase))
                    .Select(e => env.GetValueOrDefault(e.EnvKeyName)).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
                member = await telegram.CheckAsync(token, body.BotId, chat, user, request.HttpContext.RequestAborted);
            }
            app.Logger.LogInformation("Journal cross-chat authorization: subject={subject} switch={s} member={m} elapsed_ms={t}",
                body.Subject, effective ? "effective" : "off", member, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return effective ? Results.Json(new { @switch = "effective", member }) : Results.Json(new { @switch = "off" });
        });
        return app;
    }

    private sealed record AuthorizationRequest(string? Subject, long BotId, long? ChatId = null, long? UserId = null);
}
