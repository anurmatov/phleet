using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Fleet.Orchestrator.Data;
using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Fleet.Orchestrator.Endpoints;

public static class SandboxPolicyEndpoints
{
    private static IResult? Authenticate(HttpContext http, IConfiguration config)
    {
        var token = config["Orchestrator:AuthToken"];
        if (string.IsNullOrWhiteSpace(token)) return Results.Json(new { error = "operator_auth_unconfigured" }, statusCode: 503);
        return http.Request.Headers.Authorization.ToString().Equals("Bearer " + token, StringComparison.Ordinal)
            ? null : Results.Unauthorized();
    }

    public static WebApplication MapSandboxPolicyEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/sandbox-policies");
        // Unlike legacy read APIs, these operator documents are never anonymous, even in dev mode.
        group.AddEndpointFilter(async (context, next) =>
        {
            if (Authenticate(context.HttpContext, app.Configuration) is { } denied) return denied;
            if (context.HttpContext.RequestServices.GetService<OrchestratorDbContext>() is null)
                return Results.Json(new { error = "policy_database_unavailable" }, statusCode: 503);
            try { return await next(context); }
            catch (DbUpdateException ex) when (ex.InnerException is MySqlException { Number: 1062 })
            { return Results.Conflict(new { error = "policy_version_conflict" }); }
            catch (Exception ex) when (ex is DbException or DbUpdateException or Microsoft.EntityFrameworkCore.Storage.RetryLimitExceededException)
            { return Results.Json(new { error = "policy_database_unavailable" }, statusCode: 503); }
        });
        group.MapGet("", async (HttpContext http, CancellationToken ct) =>
        {
            var db = http.RequestServices.GetRequiredService<OrchestratorDbContext>();
            return Results.Ok(await db.SandboxPolicies.AsNoTracking().OrderBy(p => p.Name)
                .Select(p => new { p.Id, p.Name }).ToListAsync(ct));
        });
        group.MapPost("", async (PolicyCreate body, HttpContext http, CancellationToken ct) =>
        {
            var db = http.RequestServices.GetRequiredService<OrchestratorDbContext>();
            if (body.Name is null || !Regex.IsMatch(body.Name, @"^[A-Za-z0-9][A-Za-z0-9_. -]{0,99}\z"))
                return Results.BadRequest(new { error = "Invalid policy name (1–100 letters, digits, spaces, dot, underscore or hyphen)." });
            if (SandboxPolicyValidation.DescribeFault(body.Yaml, body.ProviderProfileHashes) is { } fault)
                return Results.BadRequest(new { error = fault });
            if (await db.SandboxPolicies.AnyAsync(p => p.Name == body.Name, ct))
                return Results.Conflict(new { error = "policy_name_conflict" });
            var policy = new SandboxPolicy { Name = body.Name, Versions = [NewVersion(1, body.Yaml!, body.ProviderProfileHashes)] };
            db.SandboxPolicies.Add(policy);
            await db.SaveChangesAsync(ct); // one transaction, including the first version
            return Results.Created($"/api/sandbox-policies/{policy.Id}/versions", new { policy.Id, policy.Name });
        });
        group.MapGet("/{id:int}/versions", async (int id, HttpContext http, CancellationToken ct) =>
        {
            var db = http.RequestServices.GetRequiredService<OrchestratorDbContext>();
            if (!await db.SandboxPolicies.AnyAsync(p => p.Id == id, ct)) return Results.NotFound();
            return Results.Ok(await db.SandboxPolicyVersions.AsNoTracking().Where(v => v.PolicyId == id).OrderBy(v => v.Version).ToListAsync(ct));
        });
        group.MapGet("/{id:int}/versions/{version:int}", async (int id, int version, HttpContext http, CancellationToken ct) =>
        {
            var db = http.RequestServices.GetRequiredService<OrchestratorDbContext>();
            return await db.SandboxPolicyVersions.AsNoTracking().FirstOrDefaultAsync(v => v.PolicyId == id && v.Version == version, ct)
                is { } row ? Results.Ok(row) : Results.NotFound();
        });
        group.MapPost("/{id:int}/versions", async (int id, PolicyAppend body, HttpContext http, CancellationToken ct) =>
        {
            var db = http.RequestServices.GetRequiredService<OrchestratorDbContext>();
            if (!await db.SandboxPolicies.AnyAsync(p => p.Id == id, ct)) return Results.NotFound();
            if (body.Version < 1) return Results.BadRequest(new { error = "Version must be positive." });
            if (SandboxPolicyValidation.DescribeFault(body.Yaml, body.ProviderProfileHashes) is { } fault)
                return Results.BadRequest(new { error = fault });
            if (await db.SandboxPolicyVersions.AnyAsync(v => v.PolicyId == id && v.Version == body.Version, ct))
                return Results.Conflict(new { error = "policy_version_conflict" });
            var row = NewVersion(body.Version, body.Yaml!, body.ProviderProfileHashes);
            row.PolicyId = id;
            db.SandboxPolicyVersions.Add(row);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/sandbox-policies/{id}/versions/{row.Version}", row);
        });
        app.MapPost("/api/agents/{name}/runtime/apply", async (string name, HttpContext http, CancellationToken ct) =>
        {
            if (Authenticate(http, app.Configuration) is { } denied) return denied;
            var db = http.RequestServices.GetService<OrchestratorDbContext>();
            if (db is null) return Results.Json(new { error = "policy_database_unavailable" }, statusCode: 503);
            try
            {
                var agent = await db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Name == name, ct);
                return agent is null ? Results.NotFound() : Results.Conflict(new { error = "managed_runtime_unavailable", message = "Selection is saved only; runtime apply is unavailable in this release." });
            }
            catch (Exception ex) when (ex is DbException or Microsoft.EntityFrameworkCore.Storage.RetryLimitExceededException)
            { return Results.Json(new { error = "policy_database_unavailable" }, statusCode: 503); }
        });
        return app;
    }

    internal static async ValueTask<object?> DatabaseErrors(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try { return await next(context); }
        catch (Exception ex) when (ex is DbException or DbUpdateException or Microsoft.EntityFrameworkCore.Storage.RetryLimitExceededException)
        { return Results.Json(new { error = "policy_database_unavailable" }, statusCode: 503); }
    }

    private static SandboxPolicyVersion NewVersion(int version, string yaml, Dictionary<string, string>? hashes) => new()
    {
        Version = version, Yaml = yaml, Sha256 = SandboxPolicyValidation.Hash(yaml),
        ProviderProfileHashes = JsonSerializer.Serialize((hashes ?? []).OrderBy(p => p.Key, StringComparer.Ordinal).ToDictionary(p => p.Key, p => p.Value)),
    };
    internal sealed record PolicyCreate(string? Name, string? Yaml, Dictionary<string, string>? ProviderProfileHashes);
    internal sealed record PolicyAppend(int Version, string? Yaml, Dictionary<string, string>? ProviderProfileHashes);
}

/// <summary>A bounded save-supported subset of OpenShell bdb956f openshell-policy-schema; not an execution policy engine.</summary>
internal static class SandboxPolicyValidation
{
    internal static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    internal static string? DescribeFault(string? text, Dictionary<string, string>? profiles)
    {
        if (string.IsNullOrWhiteSpace(text) || Encoding.UTF8.GetByteCount(text) > 65536) return "YAML must be 1–65536 UTF-8 bytes.";
        if (profiles?.Count > 32 || profiles?.Any(p => !Regex.IsMatch(p.Key, @"^[A-Za-z0-9_.-]{1,100}\z") || p.Value is null || !Regex.IsMatch(p.Value, @"^[a-f0-9]{64}\z")) == true)
            return "Provider profile bindings require names and lowercase SHA256 content hashes, never credentials.";
        try
        {
            // Reject aliases, anchors and tags before constructing a graph (no expansion or cycles).
            var parser = new Parser(new StringReader(text));
            var count = 0;
            var depth = 0;
            while (parser.MoveNext())
            {
                if (parser.Current is YamlDotNet.Core.Events.MappingStart or YamlDotNet.Core.Events.SequenceStart) depth++;
                if (parser.Current is YamlDotNet.Core.Events.MappingEnd or YamlDotNet.Core.Events.SequenceEnd) depth--;
                if (++count > 8192 || depth > 32 || parser.Current is YamlDotNet.Core.Events.AnchorAlias
                    || parser.Current is YamlDotNet.Core.Events.NodeEvent n && (!n.Anchor.IsEmpty || !n.Tag.IsEmpty))
                    return "YAML aliases, anchors, tags or oversized structures are unsupported.";
            }
            var yaml = new YamlStream();
            yaml.Load(new StringReader(text));
            Require(yaml.Documents.Count == 1, "Use one policy document.");
            var root = Map(yaml.Documents[0].RootNode, "version", "filesystem_policy", "process", "landlock", "network_policies");
            Require(Scalar(root, "version") == "1", "Only policy version 1 is supported.");
            if (Get(root, "filesystem_policy") is { } fs)
            {
                var map = Map(fs, "include_workdir", "read_only", "read_write");
                if (Get(map, "include_workdir") is { } flag) Require(Value(flag) is "true" or "false", "include_workdir must be boolean.");
                foreach (var key in new[] { "read_only", "read_write" })
                    if (Get(map, key) is { } paths) foreach (var path in List(paths)) Require(Value(path).StartsWith('/'), "Filesystem paths must be absolute.");
            }
            if (Get(root, "process") is { } process)
            {
                var map = Map(process, "run_as_user", "run_as_group");
                foreach (var key in new[] { "run_as_user", "run_as_group" })
                    Require(Regex.IsMatch(Scalar(map, key), @"^[a-z_][a-z0-9_-]{0,31}\z"), "Process user/group must be explicit names.");
            }
            if (Get(root, "landlock") is { } ll)
                Require(Scalar(Map(ll, "compatibility"), "compatibility") is "best_effort" or "hard_requirement", "Unsupported Landlock compatibility.");
            if (Get(root, "network_policies") is { } rules)
            {
                var networks = Map(rules); // dynamic rule names
                foreach (var pair in networks.Children)
                {
                    Require(Regex.IsMatch(Value(pair.Key), @"^[A-Za-z0-9_.-]{1,100}\z"), "Invalid network rule name.");
                    var rule = Map(pair.Value, "name", "endpoints", "binaries");
                    if (Get(rule, "name") is { } name) Value(name);
                    if (Get(rule, "binaries") is { } bins) foreach (var b in List(bins))
                    {
                        var binary = Map(b, "path");
                        Require(Scalar(binary, "path").StartsWith('/'), "Binary path must be absolute.");
                    }
                    if (Get(rule, "endpoints") is { } endpoints) foreach (var e in List(endpoints))
                    {
                        var endpoint = Map(e, "host", "port", "protocol", "tls", "enforcement", "access", "credential_binding");
                        Require(Regex.IsMatch(Scalar(endpoint, "host"), @"^[A-Za-z0-9*_.:-]{1,253}\z"), "Invalid endpoint host.");
                        Require(int.TryParse(Scalar(endpoint, "port"), out var port) && port is >= 1 and <= 65535, "Invalid endpoint port.");
                        Option(endpoint, "protocol", "tcp", "rest", "mcp", "json-rpc", "websocket", "graphql", "sql");
                        Option(endpoint, "tls", ""); // stock automatic TLS is the empty/omitted value, not "auto"
                        Option(endpoint, "enforcement", "enforce", "audit");
                        Option(endpoint, "access", "read-only", "read-write", "full");
                        if (Scalar(endpoint, "protocol") is "" or "tcp")
                            Require(Get(endpoint, "access") is null && Get(endpoint, "enforcement") is null,
                                "L7 access/enforcement requires an explicit inspected protocol.");
                        if (Get(endpoint, "credential_binding") is { } binding)
                        {
                            var profile = Scalar(Map(binding, "provider"), "provider");
                            Require(profiles?.ContainsKey(profile) == true, "Each credential binding needs its pinned provider profile hash.");
                        }
                    }
                }
            }
            return null;
        }
        catch (Exception ex) when (ex is YamlException or ArgumentException or InvalidOperationException)
        { return "Invalid or unsupported stock YAML. " + (ex is ArgumentException ? ex.Message : "Check mapping keys, duplicates and value types."); }
    }

    private static void Option(YamlMappingNode map, string key, params string[] choices)
    { if (Get(map, key) is { } value) Require(choices.Contains(Value(value)), $"Unsupported {key}."); }
    private static YamlMappingNode Map(YamlNode node, params string[] keys)
    {
        Require(node is YamlMappingNode, "Expected a mapping.");
        var map = (YamlMappingNode)node;
        foreach (var key in map.Children.Keys) Require(keys.Length == 0 || keys.Contains(Value(key)), "Unsupported policy field.");
        return map;
    }
    private static IEnumerable<YamlNode> List(YamlNode node)
    { Require(node is YamlSequenceNode, "Expected a list."); return ((YamlSequenceNode)node).Children; }
    private static string Value(YamlNode node)
    { Require(node is YamlScalarNode { Value: not null }, "Expected a scalar."); return ((YamlScalarNode)node).Value!; }
    private static YamlNode? Get(YamlMappingNode map, string key) => map.Children.TryGetValue(new YamlScalarNode(key), out var value) ? value : null;
    private static string Scalar(YamlMappingNode map, string key) => Get(map, key) is { } node ? Value(node) : "";
    private static void Require(bool condition, string message) { if (!condition) throw new ArgumentException(message); }
}
