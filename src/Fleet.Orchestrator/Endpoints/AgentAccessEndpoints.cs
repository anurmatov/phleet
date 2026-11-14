using Fleet.Orchestrator.Application;
using Fleet.Orchestrator.Services;

namespace Fleet.Orchestrator.Endpoints;

/// <summary>
/// The REST surface for who may reach an agent and what the agent may read: its Telegram allowlist
/// and its memory project access.
/// </summary>
/// <remarks>
/// Adapters over <see cref="AgentAllowlist"/> and <see cref="AgentProjectAccessWriter"/>, which
/// <c>manage_agent_telegram_users</c>, <c>manage_agent_telegram_groups</c> and
/// <c>manage_agent_project_access</c> call too. A change that is stored but could not be told to
/// the service that enforces it answers 503 <c>saved: true, delivered: false</c>
/// (<see cref="LiveDelivery"/>). Writes are gated by the method-based bearer middleware
/// (<see cref="OrchestratorAuth"/>); the project-access GET is open like the other agent GETs.
/// </remarks>
public static class AgentAccessEndpoints
{
    public static IEndpointRouteBuilder MapAgentAccessEndpoints(this IEndpointRouteBuilder app)
    {
        // ── Telegram allowlist ───────────────────────────────────────────────────────────────────
        // Called by the dashboard Save to apply a diff with a live allowlist update.

        app.MapPost("/api/agents/{name}/telegram-users", async (string name, HttpRequest request, AgentAllowlist allowlist, CancellationToken ct) =>
        {
            var body = await request.ReadFromJsonAsync<TelegramUserRequest>(ct);
            if (body is null) return Results.BadRequest(new { error = "Invalid request body" });

            return Answer(await allowlist.AddUserAsync(name, body.UserId, ct: ct), name, change => change == AllowlistChange.AlreadyPresent
                ? $"User {body.UserId} already allowed"
                : $"Added user {body.UserId} to agent '{name}'");
        });

        app.MapDelete("/api/agents/{name}/telegram-users/{userId}", async (string name, long userId, AgentAllowlist allowlist, CancellationToken ct) =>
            Answer(await allowlist.RemoveUserAsync(name, userId, ct), name, change => change == AllowlistChange.NotPresent
                ? $"User {userId} not in allowlist — no-op"
                : $"Removed user {userId} from agent '{name}'"));

        app.MapPost("/api/agents/{name}/telegram-groups", async (string name, HttpRequest request, AgentAllowlist allowlist, CancellationToken ct) =>
        {
            var body = await request.ReadFromJsonAsync<TelegramGroupRequest>(ct);
            if (body is null) return Results.BadRequest(new { error = "Invalid request body" });

            return Answer(await allowlist.AddGroupAsync(name, body.GroupId, ct), name, change => change == AllowlistChange.AlreadyPresent
                ? $"Group {body.GroupId} already allowed"
                : $"Added group {body.GroupId} to agent '{name}'");
        });

        app.MapDelete("/api/agents/{name}/telegram-groups/{groupId}", async (string name, long groupId, AgentAllowlist allowlist, CancellationToken ct) =>
            Answer(await allowlist.RemoveGroupAsync(name, groupId, ct), name, change => change == AllowlistChange.NotPresent
                ? $"Group {groupId} not in allowlist — no-op"
                : $"Removed group {groupId} from agent '{name}'"));

        // ── Memory project access ────────────────────────────────────────────────────────────────

        app.MapGet("/api/agents/{name}/project-access", async (string name, AgentProjectAccessWriter access, CancellationToken ct) =>
            Results.Ok(new
            {
                agent = AgentProjectAccessWriter.NormalizeAgent(name),
                projects = (await access.ListAsync(name, ct)).Select(row => row.Project),
            }));

        app.MapPut("/api/agents/{name}/project-access", async (string name, HttpRequest request, AgentProjectAccessWriter access, CancellationToken ct) =>
        {
            ProjectAccessPutRequest? body;
            try { body = await request.ReadFromJsonAsync<ProjectAccessPutRequest>(ct); }
            catch { return Results.BadRequest(new { error = "invalid_json" }); }
            if (body is null) return Results.BadRequest(new { error = "empty_payload" });

            return WriteHttp.Answer(await access.ReplaceAsync(name, body.Projects ?? [], ct),
                done => Results.Ok(new { agent = done.AgentName, projects = done.Projects }));
        });

        app.MapPost("/api/agents/{name}/project-access", async (string name, HttpRequest request, AgentProjectAccessWriter access, CancellationToken ct) =>
        {
            ProjectAccessPostRequest? body;
            try { body = await request.ReadFromJsonAsync<ProjectAccessPostRequest>(ct); }
            catch { return Results.BadRequest(new { error = "invalid_json" }); }
            if (body is null) return Results.BadRequest(new { error = "empty_payload" });

            return WriteHttp.Answer(await access.GrantAsync(name, body.Project, ct),
                done => Results.Ok(new { agent = done.AgentName, projects = done.Projects }));
        });

        app.MapDelete("/api/agents/{name}/project-access/{project}", async (string name, string project, AgentProjectAccessWriter access, CancellationToken ct) =>
            WriteHttp.Answer(await access.RevokeAsync(name, project, ct), _ => Results.NoContent()));

        return app;
    }

    /// <summary>
    /// The allowlist routes answer a missing agent as <c>Agent 'x' not found</c>, and everything
    /// that was stored with a message.
    /// </summary>
    private static IResult Answer(WriteOutcome<AllowlistChange> outcome, string name, Func<AllowlistChange, string> message)
    {
        if (outcome.Fault is { Code: "agent_not_found" })
            return Results.NotFound(new { error = $"Agent '{name}' not found" });
        return WriteHttp.Answer(outcome, change => Results.Ok(new { message = message(change) }));
    }
}

internal sealed record ProjectAccessPutRequest(List<string>? Projects);
internal sealed record ProjectAccessPostRequest(string? Project);
internal sealed record TelegramUserRequest(long UserId);
internal sealed record TelegramGroupRequest(long GroupId);
