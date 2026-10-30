using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Services;
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
            var runtime = http.RequestServices.GetService<StockOpenShellRuntime>();
            if (runtime is not { Enabled: true }) return Results.Conflict(new { error = StockOpenShellRuntime.Unavailable });
            try
            {
                if (await runtime.KindsAsync(name, ct) is not { } kinds) return Results.NotFound();
                if (kinds.Desired != "openshell" && kinds.Applied != "openshell")
                    return Results.Conflict(new { error = "managed_apply_not_applicable" });
            }
            catch (Exception ex) when (ex is DbException or InvalidOperationException)
            { return Results.Json(new { error = "runtime_state_unavailable" }, statusCode: 503); }
            // Reprovision runs the managed apply and, for OpenShell→Docker, the ordinary Docker path after it.
            var result = await http.RequestServices.GetRequiredService<ContainerProvisioningService>().ReprovisionAsync(name, ct: ct);
            return result.Success ? Results.Ok(new { message = result.Message }) : Results.Conflict(new { error = result.Message });
        });
        app.MapPost("/api/agents/{name}/revoke", async (string name, HttpContext http, CancellationToken ct) =>
        {
            if (Authenticate(http, app.Configuration) is { } denied) return denied;
            var runtime = http.RequestServices.GetService<StockOpenShellRuntime>();
            if (runtime is not { Enabled: true }) return Results.Conflict(new { error = StockOpenShellRuntime.Unavailable });
            var result = await runtime.RevokeAsync(name, ct);
            return result.Ok ? Results.Ok(new { message = result.Code })
                : Results.Json(new { error = result.Code, errors = result.Errors }, statusCode: result.Status);
        });
        // The rollout guard's input: refuses replacement unless every count is zero. Never anonymous.
        app.MapGet("/api/managed-runtime/rollback-preflight", async (HttpContext http, CancellationToken ct) =>
        {
            if (Authenticate(http, app.Configuration) is { } denied) return denied;
            var runtime = http.RequestServices.GetService<StockOpenShellRuntime>();
            if (runtime is null) return Results.Json(new { error = "runtime_state_unavailable" }, statusCode: 503);
            try { return Results.Ok(await runtime.RollbackPreflightAsync(ct)); }
            catch (Exception ex) when (ex is DbException or InvalidOperationException)
            { return Results.Json(new { error = "runtime_state_unavailable" }, statusCode: 503); }
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
            if (ParseRoot(text, out var structureFault) is not { } root) return structureFault;
            Require(PlainScalar(root, "version") == "1", "Only policy version 1 is supported.");
            if (Get(root, "filesystem_policy") is { } fs)
            {
                var map = Map(fs, "include_workdir", "read_only", "read_write");
                if (Get(map, "include_workdir") is { } flag) Require(flag is YamlScalarNode { Style: ScalarStyle.Plain } && Value(flag) is "true" or "false", "include_workdir must be a boolean, not a quoted string.");
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
                        var endpoint = Map(e, "host", "port", "protocol", "tls", "enforcement", "access", "credential_binding", "rules");
                        Require(Regex.IsMatch(Scalar(endpoint, "host"), @"^[A-Za-z0-9*_.:-]{1,253}\z"), "Invalid endpoint host.");
                        Require(int.TryParse(PlainScalar(endpoint, "port"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) && port is >= 1 and <= 65535, "Invalid endpoint port.");
                        Option(endpoint, "protocol", "tcp", "rest", "mcp", "json-rpc", "websocket", "graphql", "sql");
                        Option(endpoint, "tls", ""); // stock automatic TLS is the empty/omitted value, not "auto"
                        Option(endpoint, "enforcement", "enforce", "audit");
                        Option(endpoint, "access", "read-only", "read-write", "full");
                        if (Scalar(endpoint, "protocol") is "" or "tcp")
                            Require(Get(endpoint, "access") is null && Get(endpoint, "enforcement") is null,
                                "L7 access/enforcement requires an explicit inspected protocol.");
                        if (Scalar(endpoint, "protocol") == "mcp")
                        {
                            // #503 W5: pinned stock rejects every MCP access preset (l7_validate.rs:273–281).
                            Require(Get(endpoint, "access") is null, "protocol mcp does not support access presets; use the exact MCP rules.");
                            McpRules(endpoint);
                        }
                        else Require(Get(endpoint, "rules") is null, "Rules are supported only on protocol: mcp endpoints.");
                        if (Get(endpoint, "credential_binding") is { } binding)
                        {
                            var profile = Scalar(Map(binding, "provider"), "provider");
                            Require(profiles?.ContainsKey(profile) == true, "Each credential binding needs its pinned provider profile hash.");
                        }
                    }
                }
            }
            // #503: only the fixed stock-memory signer subset; managed apply separately requires it to match the agent's routes.
            if (Get(root, "network_middlewares") is { } middlewares) StockManagedSignerPolicy.ValidateShape(middlewares);
            return null;
        }
        catch (Exception ex) when (ex is YamlException or ArgumentException or InvalidOperationException)
        { return "Invalid or unsupported stock YAML. " + (ex is ArgumentException ? ex.Message : "Check mapping keys, duplicates and value types."); }
    }

    /// <summary>
    /// The one bounded parse shared by save and managed admission (#503): byte/event/depth limits, alias/anchor/tag refusal
    /// before any graph is built, one document and the supported root fields. Null with <paramref name="fault"/> for a
    /// size or structure refusal; malformed YAML or unsupported fields throw.
    /// </summary>
    internal static YamlMappingNode? ParseRoot(string text, out string? fault)
    {
        fault = null;
        if (string.IsNullOrWhiteSpace(text) || Encoding.UTF8.GetByteCount(text) > 65536) { fault = "YAML must be 1–65536 UTF-8 bytes."; return null; }
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
            { fault = "YAML aliases, anchors, tags or oversized structures are unsupported."; return null; }
        }
        var yaml = new YamlStream();
        yaml.Load(new StringReader(text));
        Require(yaml.Documents.Count == 1, "Use one policy document.");
        return Map(yaml.Documents[0].RootNode, "version", "filesystem_policy", "process", "landlock", "network_policies", "network_middlewares");
    }

    /// <summary>The only MCP methods a saved rule may allow besides <see cref="McpToolCall"/>, which names one exact tool.</summary>
    internal static readonly string[] McpControlMethods = ["initialize", "notifications/initialized", "tools/list"];
    internal const string McpToolCall = "tools/call";
    // Plain lowercase names only, so no tool selector can carry a stock glob character.
    private static readonly Regex McpToolName = new(@"\A[a-z][a-z0-9_]{0,63}\z", RegexOptions.CultureInvariant);

    /// <summary>
    /// #503 W5: the one supported MCP rule form, in the pinned stock shape (openshell-policy lib.rs:4630–4678): every
    /// control method in <see cref="McpControlMethods"/>, then <c>tools/call</c> once per exact tool. No wildcard,
    /// <c>any</c> selector, parameter matcher, deny rule, access preset or <c>allow_all_known_mcp_methods</c>.
    /// Returns the allowed tool names; throws <see cref="ArgumentException"/> for any other form.
    /// </summary>
    internal static IReadOnlySet<string> McpRules(YamlMappingNode endpoint)
    {
        const string form = "MCP rules allow exactly initialize, notifications/initialized and tools/list, plus tools/call once per exact tool name.";
        var rules = List(Get(endpoint, "rules") ?? throw new ArgumentException("protocol mcp needs explicit rules. " + form)).ToList();
        Require(rules.Count <= 32, form);
        var methods = new HashSet<string>(StringComparer.Ordinal);
        var tools = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in rules)
        {
            var rule = Map(item, "allow");
            var allow = Map(Get(rule, "allow") ?? throw new ArgumentException(form), "method", "tool");
            var method = Get(allow, "method") is { } m ? Value(m) : "";
            if (method == McpToolCall)
            {
                var tool = Get(allow, "tool") is { } t ? Value(t) : "";
                Require(McpToolName.IsMatch(tool), "tools/call names one exact tool (lowercase letters, digits, underscore; no wildcard or list).");
                Require(tools.Add(tool), "Duplicate MCP rule.");
            }
            else
            {
                Require(McpControlMethods.Contains(method) && Get(allow, "tool") is null, form);
                Require(methods.Add(method), "Duplicate MCP rule.");
            }
        }
        Require(methods.Count == McpControlMethods.Length && tools.Count > 0, form);
        return tools;
    }

    private static void Option(YamlMappingNode map, string key, params string[] choices)
    { if (Get(map, key) is { } value) Require(choices.Contains(Value(value)), $"Unsupported {key}."); }
    internal static YamlMappingNode Map(YamlNode node, params string[] keys)
    {
        Require(node is YamlMappingNode, "Expected a mapping.");
        var map = (YamlMappingNode)node;
        foreach (var key in map.Children.Keys) Require(keys.Length == 0 || keys.Contains(Value(key)), "Unsupported policy field.");
        return map;
    }
    internal static IEnumerable<YamlNode> List(YamlNode node)
    { Require(node is YamlSequenceNode, "Expected a list."); return ((YamlSequenceNode)node).Children; }
    internal static string Value(YamlNode node)
    { Require(node is YamlScalarNode { Value: not null }, "Expected a scalar."); return ((YamlScalarNode)node).Value!; }
    internal static YamlNode? Get(YamlMappingNode map, string key) => map.Children.TryGetValue(new YamlScalarNode(key), out var value) ? value : null;
    // YamlScalarNode.Value is text even for quoted strings. Preserve the parsed scalar style
    // at numeric/boolean fields so strings cannot masquerade as stock integer/bool values.
    internal static string PlainScalar(YamlMappingNode map, string key)
    {
        var node = Get(map, key);
        Require(node is YamlScalarNode { Style: ScalarStyle.Plain }, $"{key} must be an unquoted typed scalar.");
        return Value(node!);
    }
    internal static string Scalar(YamlMappingNode map, string key) => Get(map, key) is { } node ? Value(node) : "";
    private static void Require(bool condition, string message) { if (!condition) throw new ArgumentException(message); }
}
