using System.Text.Json.Nodes;
using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Services;
using Fleet.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Fleet.Orchestrator.Endpoints;

/// <summary>
/// <c>GET /api/agents/{name}/effective-harness</c> (#460 Part 3a): the harness settings the agent's
/// stored config produces, computed by <see cref="HarnessDefaults.Build"/> — the same call provisioning
/// uses for <c>codex-root.toml</c> — plus the plan sha the running agent reported, so a saved but not
/// yet reprovisioned change shows as pending.
/// </summary>
/// <remarks>
/// Read-only and unauthenticated like <c>GET /api/agents/{name}/config</c>. Credential env values are
/// masked (MUST NOT 10). A stored extra the running orchestrator no longer accepts is a 200 with
/// <c>extra.state = "ignored"</c> and the reason, and the plan is the mode defaults.
/// </remarks>
public static class AgentHarnessEndpoints
{
    public static WebApplication MapAgentHarnessEndpoints(this WebApplication app)
    {
        app.MapGet("/api/agents/{name}/effective-harness", async (string name, IServiceScopeFactory scopeFactory, AgentRegistry registry) =>
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetService<OrchestratorDbContext>();
            if (db is null)
                return Results.Problem("Database is not configured on this orchestrator");

            var agent = await db.Agents
                .Include(a => a.Tools)
                .AsNoTracking()
                .FirstOrDefaultAsync(a => a.Name == name);
            if (agent is null)
                return Results.NotFound(new { error = $"Agent '{name}' not found in DB" });
            if (!HarnessExtra.IsSupportedProvider(agent.Provider))
                return Results.NotFound(new { reason = HarnessExtra.UnsupportedProviderCode });

            var plan = HarnessDefaults.Build(ContainerProvisioningService.HarnessInputFor(agent));
            var started = registry.Get(agent.Name)?.StartedPlanSha256;
            return Results.Json(ToJson(agent.Name, plan, started));
        });

        return app;
    }

    /// <summary>The response body; <c>pendingReprovision</c> only when a started sha exists and differs.</summary>
    internal static JsonObject ToJson(string agentName, HarnessPlan plan, string? startedPlanSha256)
    {
        JsonObject? codex = null;
        if (plan.CodexRootToml is not null)
        {
            var thread = new JsonObject();
            foreach (var setting in plan.CodexThreadConfig!)
                thread[setting.Name] = setting.Value switch
                {
                    bool b => JsonValue.Create(b),
                    int i => JsonValue.Create(i),
                    _ => JsonValue.Create((string)setting.Value),
                };
            codex = new JsonObject { ["configToml"] = plan.CodexRootToml, ["threadConfig"] = thread };
        }

        JsonObject? claude = null;
        if (plan.ClaudeArgs is not null)
        {
            var env = new JsonArray();
            foreach (var (key, value) in plan.ClaudeEnv!)
                env.Add(new JsonObject { ["name"] = key, ["value"] = HarnessDefaults.DisplayValue(key, value) });
            claude = new JsonObject
            {
                ["args"] = new JsonArray(plan.ClaudeArgs.Select(a => (JsonNode?)JsonValue.Create(a)).ToArray()),
                ["env"] = env,
            };
        }

        return new JsonObject
        {
            ["agent"] = agentName,
            ["provider"] = plan.Provider,
            ["mode"] = plan.Mode,
            ["codex"] = codex,
            ["claude"] = claude,
            ["extra"] = new JsonObject
            {
                ["state"] = plan.ExtraState,
                ["reason"] = plan.ExtraReason,
                ["replaced"] = new JsonArray(plan.Replaced.Select(r => (JsonNode?)JsonValue.Create(r)).ToArray()),
                ["notes"] = new JsonArray(plan.Notes.Select(n => (JsonNode?)JsonValue.Create(n)).ToArray()),
            },
            ["planSha256"] = plan.PlanSha256,
            ["computedPlanSha256"] = plan.PlanSha256,
            ["startedPlanSha256"] = startedPlanSha256,
            ["pendingReprovision"] = startedPlanSha256 is not null && startedPlanSha256 != plan.PlanSha256,
        };
    }
}
