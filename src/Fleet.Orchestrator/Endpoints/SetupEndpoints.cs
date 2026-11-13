using Fleet.Orchestrator.Services;

namespace Fleet.Orchestrator.Endpoints;

/// <summary>
/// The REST surface the dashboard's setup banner uses: setup status, and validating and saving the
/// Telegram and GitHub App settings.
/// </summary>
public static class SetupEndpoints
{
    public static IEndpointRouteBuilder MapSetupEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/setup/status", (SetupService setup) =>
            Results.Ok(setup.GetStatus()));

        app.MapPost("/api/setup/telegram/validate", async (
            HttpRequest request, SetupService setup, CancellationToken ct) =>
        {
            TelegramSetupRequest? req;
            try { req = await request.ReadFromJsonAsync<TelegramSetupRequest>(ct); }
            catch { return Results.BadRequest(new { error = "invalid_json" }); }
            if (req is null) return Results.BadRequest(new { error = "empty_body" });

            var result = await setup.ValidateTelegramAsync(req, ct);
            if (!result.Valid)
                return Results.BadRequest(new { error = result.ErrorCode, detail = result.ErrorDetail });
            await setup.UpdateLastValidatedAsync("telegram");
            return Results.Ok(new { valid = true, ctoBot = result.CtoBot, notifierBot = result.NotifierBot, groupChat = result.GroupChat, warnings = result.Warnings });
        });


        app.MapPost("/api/setup/github/validate", async (
            HttpRequest request, SetupService setup, CancellationToken ct) =>
        {
            GitHubSetupRequest? req;
            try { req = await request.ReadFromJsonAsync<GitHubSetupRequest>(ct); }
            catch { return Results.BadRequest(new { error = "invalid_json" }); }
            if (req is null) return Results.BadRequest(new { error = "empty_body" });

            var result = await setup.ValidateGitHubAsync(req, ct);
            if (!result.Valid)
                return Results.BadRequest(new { error = result.ErrorCode, detail = result.ErrorDetail });
            await setup.UpdateLastValidatedAsync("github");
            return Results.Ok(new { valid = true, appId = result.AppId, appName = result.AppName, warnings = result.Warnings });
        });

        // POST /api/setup/github/save — writes GITHUB_APP_ID + GITHUB_APP_PEM to .env.
        // GITHUB_APP_PEM is on the config-API denylist (prevents general agents from reading it back),
        // so it cannot go through PUT /api/config/values. This dedicated setup endpoint bypasses the
        // denylist for the two GitHub App keys only. Auth-gated by ORCHESTRATOR_AUTH_TOKEN.
        app.MapPost("/api/setup/github/save", async (
            HttpRequest request, SetupService setup, ConfigService configService, CancellationToken ct) =>
        {
            GitHubSetupRequest? req;
            try { req = await request.ReadFromJsonAsync<GitHubSetupRequest>(ct); }
            catch { return Results.BadRequest(new { error = "invalid_json" }); }
            if (req is null) return Results.BadRequest(new { error = "empty_body" });

            try
            {
                await setup.SaveGitHubAsync(req.AppId, req.PrivateKeyPem);
                // Reload to invalidate cache and broadcast config.changed for non-denylisted changes
                // (GITHUB_APP_ID will be broadcast; GITHUB_APP_PEM is denylisted so it is never broadcast).
                var changed = await configService.ReloadAsync(ct);
                return Results.Ok(new { saved = true, changedKeys = changed });
            }
            catch (Exception ex)
            {
                return Results.Problem($"Save failed: {ex.Message}", statusCode: 500);
            }
        });

        return app;
    }
}
