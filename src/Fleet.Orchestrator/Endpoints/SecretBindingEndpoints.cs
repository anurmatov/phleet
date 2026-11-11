using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Services;
using Fleet.Shared;
using Microsoft.EntityFrameworkCore;

namespace Fleet.Orchestrator.Endpoints;

/// <summary>
/// The <c>/api/secret-bindings</c> surface: which <c>.env</c> keys agents hold only as placeholders,
/// and where <c>fleet-secrets</c> may send each one.
/// </summary>
/// <remarks>
/// <para>
/// A binding is the whole trust decision: it names the hosts a secret may reach. So it can only be
/// written here, over REST, behind the operator's bearer token (the method-based rule in
/// <see cref="OrchestratorAuth"/>). No MCP tool creates, changes or deletes a binding.
/// </para>
/// <para>
/// The GET returns no secret value and no placeholder: key names, hosts and which agents hold a
/// grant. Whether <c>.env</c> has a value is a boolean.
/// </para>
/// </remarks>
public static class SecretBindingEndpoints
{
    public sealed record SecretBindingBody(
        string? Kind,
        string[]? Hosts,
        string? HeaderName,
        string[]? ExposeAs,
        string? AppIdEnvKey,
        bool? AgentAttachable,
        string[]? KnownHosts = null);

    /// <summary>
    /// Turns <see cref="SecretStatePublicationException"/> from any REST handler into a 503 that
    /// names the fault. The change is in the database but not in force at <c>fleet-secrets</c>, so no
    /// handler may answer 200 for it, whichever route saved it (a binding, an agent's Env Refs, an
    /// agent delete).
    /// </summary>
    public static IApplicationBuilder UseSecretStateFaults(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            try
            {
                await next(context);
            }
            catch (SecretStatePublicationException ex) when (!context.Response.HasStarted)
            {
                context.Response.Clear();
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await context.Response.WriteAsJsonAsync(new { error = ex.Message, fault = ex.Fault, saved = true, enforced = false });
            }
        });

    public static IEndpointRouteBuilder MapSecretBindingEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/secret-bindings", async (
            IServiceScopeFactory scopeFactory, SecretProxySettings settings, SecretProxyStateWriter state,
            IConfiguration config, CancellationToken ct) =>
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetService<OrchestratorDbContext>();
            if (db is null)
                return Results.Problem("Database is not configured on this orchestrator");

            var bindings = await db.SecretBindings.AsNoTracking().OrderBy(b => b.EnvKeyName).ToListAsync(ct);
            var keys = bindings.Select(b => b.EnvKeyName).ToList();
            var references = await db.AgentEnvRefs.AsNoTracking()
                .Where(r => keys.Contains(r.EnvKeyName))
                .Select(r => new { r.EnvKeyName, Agent = r.Agent.Name, Held = r.PlaceholderSha256 != null })
                .ToListAsync(ct);
            var env = ContainerProvisioningService.LoadEnvFile(config["Provisioning:EnvFilePath"] ?? "/app/deploy/.env");

            return Results.Ok(new
            {
                settings.Enabled,
                settings.Fault,
                Subnet = settings.Network?.Subnet,
                Address = settings.Network?.ProxyAddress.ToString(),
                CaPresent = state.CaPresent,
                Bindings = bindings.Select(b => Describe(b, env,
                    references.Where(r => string.Equals(r.EnvKeyName, b.EnvKeyName, StringComparison.Ordinal))
                        .OrderBy(r => r.Agent, StringComparer.Ordinal)
                        .Select(r => new AgentGrant(r.Agent, r.Held)))),
            });
        });

        // Create or replace one binding. A new host only reaches an agent's container on its next
        // provision; a removed host stops being served at once.
        app.MapPut("/api/secret-bindings/{key}", async (
            string key, SecretBindingBody body, IServiceScopeFactory scopeFactory, IConfiguration config, CancellationToken ct) =>
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetService<OrchestratorDbContext>();
            if (db is null)
                return Results.Problem("Database is not configured on this orchestrator");

            var kind = string.IsNullOrWhiteSpace(body.Kind) ? SecretBindingRules.KindStatic : body.Kind.Trim();
            var hosts = (body.Hosts ?? []).Select(h => (h ?? "").Trim()).ToArray();
            var header = string.IsNullOrWhiteSpace(body.HeaderName) ? SecretBindingRules.DefaultHeader : body.HeaderName.Trim();
            var exposeAs = (body.ExposeAs ?? []).Select(n => (n ?? "").Trim()).Where(n => n.Length > 0).ToArray();
            var appIdKey = string.IsNullOrWhiteSpace(body.AppIdEnvKey) ? null : body.AppIdEnvKey.Trim();

            // Host key lines as ssh-keyscan prints them: blank lines and comment lines are dropped,
            // runs of blanks become one. Anything else that is not a plain line is refused below.
            var knownHosts = (body.KnownHosts ?? [])
                .SelectMany(text => (text ?? "").Split('\n'))
                .Select(line => string.Join(' ', line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)))
                .Where(line => line.Length > 0 && !line.StartsWith('#'))
                .ToArray();
            if (kind == SecretBindingRules.KindSshKey)
                header = SecretBindingRules.DefaultHeader;

            if (SecretGrantPlanner.DescribeBindingFault(key, kind, hosts, header, exposeAs, appIdKey, knownHosts) is { } fault)
                return Results.BadRequest(new { error = fault });

            var all = await db.SecretBindings.ToListAsync(ct);
            var existing = all.FirstOrDefault(b => string.Equals(b.EnvKeyName, key, StringComparison.Ordinal));
            if (existing is null && all.Any(b => string.Equals(b.EnvKeyName, key, StringComparison.OrdinalIgnoreCase)))
                return Results.Conflict(new { error = "secret_binding_key_conflict: a binding exists for the same name in another letter case" });

            var created = existing is null;
            if (existing is null)
            {
                existing = new SecretBinding { EnvKeyName = key, Hosts = "" };
                db.SecretBindings.Add(existing);
            }

            existing.Kind = kind;
            existing.Hosts = string.Join(",", hosts);
            existing.HeaderName = header;
            existing.ExposeAs = exposeAs.Length == 0 ? null : string.Join(",", exposeAs);
            existing.AppIdEnvKey = appIdKey;
            existing.SshKnownHosts = kind == SecretBindingRules.KindSshKey ? string.Join('\n', knownHosts) : null;
            existing.AgentAttachable = body.AgentAttachable ?? false;
            existing.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);

            var agents = await db.AgentEnvRefs.AsNoTracking()
                .Where(r => r.EnvKeyName == key)
                .OrderBy(r => r.Agent.Name)
                .Select(r => new AgentGrant(r.Agent.Name, r.PlaceholderSha256 != null))
                .ToListAsync(ct);
            var env = ContainerProvisioningService.LoadEnvFile(config["Provisioning:EnvFilePath"] ?? "/app/deploy/.env");

            return Results.Ok(new
            {
                Created = created,
                Binding = Describe(existing, env, agents),
                // Every agent that references the key: a container started before this save still holds
                // what it was given then (the value itself, or the old host list).
                Reprovision = agents.Select(a => a.Agent),
            });
        });

        // Refused while any agent references the key: without the binding, the next provision of
        // that agent would write the value itself into its container. `?allowPlainDelivery=true` is
        // the operator saying exactly that is intended (the way back to plain delivery): the grants
        // die at once, and each listed agent gets the value itself on its next provision.
        app.MapDelete("/api/secret-bindings/{key}", async (
            string key, bool? allowPlainDelivery, IServiceScopeFactory scopeFactory, CancellationToken ct) =>
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetService<OrchestratorDbContext>();
            if (db is null)
                return Results.Problem("Database is not configured on this orchestrator");

            var binding = await db.SecretBindings.FirstOrDefaultAsync(b => b.EnvKeyName == key, ct);
            if (binding is null || !string.Equals(binding.EnvKeyName, key, StringComparison.Ordinal))
                return Results.NotFound(new { error = $"No secret binding for '{key}'" });

            var agents = await db.AgentEnvRefs.AsNoTracking()
                .Where(r => r.EnvKeyName == key)
                .OrderBy(r => r.Agent.Name)
                .Select(r => r.Agent.Name)
                .ToListAsync(ct);
            if (agents.Count > 0 && allowPlainDelivery != true)
                return Results.Conflict(new
                {
                    error = "secret_binding_in_use: remove this Env Ref from every agent first. Deleting the binding now "
                        + "would deliver the value itself to these agents on their next provision. To do exactly that, "
                        + "repeat the request with ?allowPlainDelivery=true.",
                    agents,
                });

            // The placeholders die with the binding: no container may keep a hash for a key that is
            // no longer bound.
            foreach (var reference in await db.AgentEnvRefs.Where(r => r.EnvKeyName == key && r.PlaceholderSha256 != null).ToListAsync(ct))
                reference.PlaceholderSha256 = null;
            db.SecretBindings.Remove(binding);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new
            {
                message = $"Secret binding '{key}' deleted",
                // Non-empty only with allowPlainDelivery: these agents hold a dead placeholder until
                // they are reprovisioned, and then receive the value itself.
                Reprovision = agents,
            });
        });

        return app;
    }

    public sealed record AgentGrant(string Agent, bool PlaceholderHeld);

    private static object Describe(SecretBinding binding, IReadOnlyDictionary<string, string> env, IEnumerable<AgentGrant> agents)
    {
        var list = agents.ToList();
        return new
        {
            binding.EnvKeyName,
            binding.Kind,
            Hosts = binding.HostList,
            binding.HeaderName,
            ExposeAs = binding.ExposeAsList,
            binding.AppIdEnvKey,
            // Host keys are public; listing them lets the operator check what the key is bound to.
            KnownHosts = binding.KnownHostList ?? [],
            binding.AgentAttachable,
            ValuePresent = Present(env, binding.EnvKeyName)
                && (binding.Kind != SecretBindingRules.KindGitHubApp || (binding.AppIdEnvKey is { } id && Present(env, id))),
            Agents = list,
            // No placeholder yet: the agent's container predates the binding (or was never provisioned)
            // and may still hold the value itself.
            PendingReprovision = list.Where(a => !a.PlaceholderHeld).Select(a => a.Agent),
        };
    }

    private static bool Present(IReadOnlyDictionary<string, string> env, string key) =>
        env.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) && value != "<secret>";
}
