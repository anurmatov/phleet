using System.ComponentModel;
using System.Data.Common;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Fleet.Orchestrator.Data;
using Fleet.Shared.Security;
using Microsoft.EntityFrameworkCore;

namespace Fleet.Orchestrator.Services;

// ── Stock CLI process seam ──────────────────────────────────────────────────

public sealed record StockCliResult(int ExitCode, string Stdout, bool TimedOut = false, bool Missing = false);

public interface IStockCli
{
    /// <summary>Runs the pinned stock CLI with an argument array. Secrets travel only in <paramref name="secretEnv"/>.</summary>
    Task<StockCliResult> RunAsync(IReadOnlyList<string> args, IReadOnlyDictionary<string, string>? secretEnv, TimeSpan timeout, CancellationToken ct);
}

/// <summary>
/// Argument arrays only, never a shell. Client trust comes from the private ConfigDir. TLS-skip and
/// alternate endpoint variables are removed from the child. Stderr is drained and discarded, because
/// stock output may echo request content; callers report typed codes, not raw output.
/// </summary>
public sealed class ProcessStockCli(IConfiguration config) : IStockCli
{
    public async Task<StockCliResult> RunAsync(IReadOnlyList<string> args, IReadOnlyDictionary<string, string>? secretEnv,
        TimeSpan timeout, CancellationToken ct)
    {
        var path = config["StockOpenShell:CliPath"];
        var psi = new ProcessStartInfo(string.IsNullOrWhiteSpace(path) ? "openshell" : path)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        foreach (var name in new[] { "OPENSHELL_GATEWAY_INSECURE", "OPENSHELL_GATEWAY_ENDPOINT", "OPENSHELL_GATEWAY" })
            psi.Environment.Remove(name);
        if (config["StockOpenShell:ConfigDir"] is { Length: > 0 } dir) psi.Environment["XDG_CONFIG_HOME"] = dir;
        if (secretEnv is not null)
            foreach (var (key, value) in secretEnv) psi.Environment[key] = value;

        Process? process;
        try { process = Process.Start(psi); }
        catch (Win32Exception) { return new(-1, "", Missing: true); }
        if (process is null) return new(-1, "", Missing: true);
        using (process)
        {
            process.StandardInput.Close();
            var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(timeout);
            try { await process.WaitForExitAsync(deadline.Token); }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return new(-1, "", TimedOut: true);
            }
            await stderr;
            var output = await stdout;
            return new(process.ExitCode, output.Length > 1_000_000 ? "" : output);
        }
    }
}

// ── Host observation seam (gateway generation, workload/supervisor quiescence) ──

public interface IStockHostView
{
    /// <summary>Gateway container ID plus StartedAt, or null when unobservable.</summary>
    Task<string?> GatewayGenerationAsync(CancellationToken ct);

    /// <summary>Full IDs of workload/native-supervisor containers still running for a stock UUID; null when unobservable.</summary>
    Task<IReadOnlyList<string>?> RunningSandboxContainersAsync(string sandboxId, CancellationToken ct);
}

public sealed class DockerStockHostView(DockerService docker, IConfiguration config) : IStockHostView
{
    public async Task<string?> GatewayGenerationAsync(CancellationToken ct)
    {
        var name = config["StockOpenShell:GatewayContainer"];
        var json = await docker.InspectContainerAsync(string.IsNullOrWhiteSpace(name) ? "openshell-gateway" : name, ct);
        if (json is null) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var state = root.GetProperty("State");
            if (!state.GetProperty("Running").GetBoolean()) return null;
            return root.GetProperty("Id").GetString() + "@" + state.GetProperty("StartedAt").GetString();
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { return null; }
    }

    public async Task<IReadOnlyList<string>?> RunningSandboxContainersAsync(string sandboxId, CancellationToken ct)
    {
        // The stock driver labels both the workload and its native supervisor with the sandbox UUID.
        var containers = await docker.ListContainersByLabelAsync("openshell.ai/sandbox-id=" + sandboxId, ct);
        return containers?.Where(c => c.State is "running" or "restarting" or "paused" or "created").Select(c => c.Id).ToList();
    }
}

// ── Persisted binding and compare-and-set store ─────────────────────────────

public sealed record ManagedBinding(string SandboxName, string SandboxId, Guid Epoch, string Image, int PolicyVersionId,
    string PolicySha256, string AuthMode, string KeyRef, string Provider, string GatewayGeneration);

/// <summary>JSON stored in <c>agents.AppliedRuntimeBinding</c>. Heartbeats never write it.</summary>
public sealed record ManagedRecord
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    public int V { get; init; } = 1;
    /// <summary>none · applying · applied · stopped · revoked · needs_attention</summary>
    public string State { get; init; } = "none";
    /// <summary>Kind and owning orchestrator instance of the one in-flight operation.</summary>
    public string? Operation { get; init; }
    public ManagedBinding? Applied { get; init; }
    public ManagedBinding? Candidate { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = [];
    public DateTimeOffset UpdatedAt { get; init; }

    public static ManagedRecord Parse(string? json) =>
        string.IsNullOrEmpty(json) ? new() : JsonSerializer.Deserialize<ManagedRecord>(json, Options) ?? throw new JsonException();
    public string Serialize() => JsonSerializer.Serialize(this with { UpdatedAt = DateTimeOffset.UtcNow }, Options);
    public bool Unresolved => State is "applying" or "needs_attention" || Operation is not null || Candidate is not null;
}

public interface IManagedBindingStore
{
    Task<Agent?> LoadAsync(string name, CancellationToken ct);
    Task<SandboxPolicyVersion?> LoadPolicyAsync(int id, CancellationToken ct);
    Task<IReadOnlyList<Agent>> ListAsync(CancellationToken ct);
    /// <summary>
    /// Per-agent compare-and-set on the stored binding. When <paramref name="appliedKind"/> is set the
    /// applied runtime/policy fields change in the same statement. False: another writer won (409).
    /// </summary>
    Task<bool> SwapAsync(int agentId, string? expected, string next, string? appliedKind, int? appliedPolicyVersionId, CancellationToken ct);
}

public sealed class EfManagedBindingStore(IServiceScopeFactory scopes) : IManagedBindingStore
{
    public async Task<Agent?> LoadAsync(string name, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        return await db.Agents.Include(a => a.Tools).Include(a => a.McpEndpoints).Include(a => a.EnvRefs)
            .AsSplitQuery().AsNoTracking().FirstOrDefaultAsync(a => a.Name == name, ct);
    }

    public async Task<SandboxPolicyVersion?> LoadPolicyAsync(int id, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        return await db.SandboxPolicyVersions.AsNoTracking().FirstOrDefaultAsync(v => v.Id == id, ct);
    }

    public async Task<IReadOnlyList<Agent>> ListAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        return await db.Agents.AsNoTracking().ToListAsync(ct);
    }

    public async Task<bool> SwapAsync(int agentId, string? expected, string next, string? appliedKind, int? appliedPolicyVersionId, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var rows = db.Agents.Where(a => a.Id == agentId && a.AppliedRuntimeBinding == expected);
        var changed = appliedKind is null
            ? await rows.ExecuteUpdateAsync(s => s.SetProperty(a => a.AppliedRuntimeBinding, next), ct)
            : await rows.ExecuteUpdateAsync(s => s
                .SetProperty(a => a.AppliedRuntimeBinding, next)
                .SetProperty(a => a.AppliedRuntimeKind, appliedKind)
                .SetProperty(a => a.AppliedSandboxPolicyVersionId, appliedPolicyVersionId), ct);
        return changed == 1;
    }
}

// ── The thin adapter behind every existing lifecycle entry point ────────────

/// <summary>
/// Managed stock OpenShell lifecycle (#489/#492). One adapter for REST and MCP: apply transitions
/// toward the desired runtime after the old one is quiescent; Start/Stop/cancel/logs/revoke act on
/// the persisted applied binding. One operation per agent at a time (compare-and-set), unknown
/// outcomes become <c>needs_attention</c> and are never replayed automatically.
/// </summary>
public sealed class StockOpenShellRuntime(
    IConfiguration config,
    IStockCli cli,
    StockBrokerProvisioner broker,
    IManagedBindingStore store,
    IStockHostView host,
    ICredentialsReader credentials,
    IServiceProvider services,
    ILogger<StockOpenShellRuntime> logger)
{
    public const string PinnedCliVersion = "0.1.5";
    public const string StockCommit = "bdb956f2cd65a635f1294fccf1d138d263bf650f";
    public const string Unavailable = "managed_runtime_unavailable";
    internal static readonly string Instance = Guid.NewGuid().ToString("N");
    internal static readonly string[] MemoryTools = ["memory_get", "memory_search", "memory_list"];

    internal TimeSpan LegTimeout { get; init; } = TimeSpan.FromSeconds(30);
    internal TimeSpan FanoutTimeout { get; init; } = TimeSpan.FromSeconds(120);
    internal TimeSpan StopSettle { get; init; } = TimeSpan.FromSeconds(60);
    internal TimeSpan Poll { get; init; } = TimeSpan.FromSeconds(2);
    /// <summary>Test seams; production uses the ordinary generator and Docker service.</summary>
    internal Func<string, CancellationToken, Task<string>>? GenerateInputs { get; init; }
    internal Func<Agent, CancellationToken, Task<string?>>? StopDocker { get; init; }

    public sealed record Result(bool Ok, int Status, string Code, IReadOnlyList<string> Errors)
    {
        public static Result Done(string code) => new(true, 200, code, []);
        public static Result Refused(int status, string code, IReadOnlyList<string>? errors = null) => new(false, status, code, errors ?? []);
        public string Message => Errors.Count == 0 ? Code : Code + ": " + string.Join(", ", Errors);
    }

    public bool Enabled => config.GetValue<bool>("StockOpenShell:ManagedEnabled");

    /// <summary>True when apply/lifecycle for this agent belongs to the managed adapter rather than Docker.</summary>
    public static bool Owns(Agent agent) => agent.RuntimeKind == "openshell" || agent.AppliedRuntimeKind == "openshell";

    private string ConfigDir => config["StockOpenShell:ConfigDir"] ?? "";
    private string AuthorityDir => Path.Combine(ConfigDir, "managed", "authority");
    private string AuthorityPath(string agent) => Path.Combine(AuthorityDir, agent + ".json");

    /// <summary>Desired/applied kinds for routing; null when the agent is absent; throws on DB failure.</summary>
    public async Task<(string Desired, string Applied)?> KindsAsync(string name, CancellationToken ct)
    {
        var agent = await store.LoadAsync(name, ct);
        return agent is null ? null : (agent.RuntimeKind, agent.AppliedRuntimeKind);
    }

    /// <summary>
    /// Existing MCP lifecycle tools: null leaves the Docker path in charge (applied Docker binding);
    /// otherwise the managed outcome. Pending desired changes never redirect Start/Stop.
    /// </summary>
    public async Task<string?> DispatchToolAsync(string name, string action, CancellationToken ct = default)
    {
        if (!Enabled) return null;
        (string Desired, string Applied)? kinds;
        try { kinds = await KindsAsync(name, ct); }
        catch (Exception ex) when (ex is DbException or InvalidOperationException) { return "runtime_state_unavailable"; }
        if (kinds is not { Applied: "openshell" }) return null;
        var result = action switch
        {
            "start" => await StartAsync(name, ct),
            "stop" => await StopAsync(name, ct),
            "restart" => await RestartAsync(name, ct),
            _ => Result.Refused(400, "unsupported_managed_action"),
        };
        return result.Message;
    }

    // ── public lifecycle ───────────────────────────────────────────────────

    public Task<Result> ApplyAsync(string name, CancellationToken ct) => RunAsync(name, "apply", ApplyCoreAsync, ct);
    public Task<Result> StartAsync(string name, CancellationToken ct) => RunAsync(name, "start", StartCoreAsync, ct);
    public Task<Result> StopAsync(string name, CancellationToken ct) => RunAsync(name, "stop", StopCoreAsync, ct);
    public Task<Result> RevokeAsync(string name, CancellationToken ct) => RunAsync(name, "revoke", RevokeCoreAsync, ct);

    public async Task<Result> RestartAsync(string name, CancellationToken ct)
    {
        var stopped = await StopAsync(name, ct);
        return stopped.Ok ? await StartAsync(name, ct) : stopped;
    }

    /// <summary>
    /// Stock-authenticated, exact-UUID-checked <c>sandbox exec</c> to the agent's managed loopback
    /// cancel route. A timeout or other failure is an unconfirmed cancellation, not success.
    /// </summary>
    public async Task<Result> CancelAsync(string name, string? taskId, CancellationToken ct)
    {
        if (!Enabled) return Result.Refused(409, Unavailable);
        var (agent, record, fault) = await ReadAsync(name, ct);
        if (fault is not null) return fault;
        if (agent!.AppliedRuntimeKind != "openshell" || record!.Applied is not { } binding) return Result.Refused(409, "managed_binding_missing");
        var path = taskId is null ? "/cancel" : "/cancel/" + Uri.EscapeDataString(taskId);
        if (!Regex.IsMatch(path, @"\A/cancel(/[A-Za-z0-9%._~-]{1,256})?\z")) return Result.Refused(400, "invalid_task_id");
        if (await VerifyIdentityAsync(binding, ct) is { } identity) return identity;
        const string script = "exec 3<>/dev/tcp/127.0.0.1/8080 || exit 2; "
            + "printf 'POST %s HTTP/1.0\\r\\nContent-Length: 0\\r\\n\\r\\n' \"$1\" >&3; "
            + "IFS= read -r status <&3; case \"$status\" in *' 200 '*) exit 0;; *' 404 '*) exit 4;; esac; exit 1";
        var run = await cli.RunAsync(["sandbox", "exec", "-n", binding.SandboxName, "--", "bash", "-c", script, "cancel", path], null, LegTimeout, ct);
        return run switch
        {
            { Missing: true } => Result.Refused(503, "stock_cli_missing"),
            { TimedOut: true } => Result.Refused(504, "cancel_unconfirmed"),
            { ExitCode: 0 } => Result.Done("cancel_requested"),
            { ExitCode: 4 } => Result.Refused(404, "task_not_found"),
            _ => Result.Refused(502, "cancel_unconfirmed"),
        };
    }

    public async Task<(Result Result, string? Logs)> LogsAsync(string name, int lines, CancellationToken ct)
    {
        if (!Enabled) return (Result.Refused(409, Unavailable), null);
        var (agent, record, fault) = await ReadAsync(name, ct);
        if (fault is not null) return (fault, null);
        if (agent!.AppliedRuntimeKind != "openshell" || record!.Applied is not { } binding) return (Result.Refused(409, "managed_binding_missing"), null);
        if (await VerifyIdentityAsync(binding, ct) is { } identity) return (identity, null);
        var run = await cli.RunAsync(["logs", binding.SandboxName, "-n", Math.Clamp(lines, 1, 1000).ToString()], null, LegTimeout, ct);
        return run.ExitCode == 0 && !run.TimedOut ? (Result.Done("logs"), run.Stdout) : (Result.Refused(502, "stock_logs_unavailable"), null);
    }

    /// <summary>
    /// Counts used by the rollout guard before an orchestrator without the managed capability label
    /// replaces this one. Any nonzero count must refuse replacement.
    /// </summary>
    public async Task<object> RollbackPreflightAsync(CancellationToken ct)
    {
        var agents = await store.ListAsync(ct);
        var unresolved = 0;
        foreach (var agent in agents)
        {
            try { if (ManagedRecord.Parse(agent.AppliedRuntimeBinding).Unresolved) unresolved++; }
            catch (JsonException) { unresolved++; }
        }
        return new
        {
            desiredOpenShell = agents.Count(a => a.RuntimeKind == "openshell"),
            appliedOpenShell = agents.Count(a => a.AppliedRuntimeKind == "openshell"),
            unresolvedBindings = unresolved,
        };
    }

    /// <summary>Named blocker before any external action, or null when apply may proceed.</summary>
    public async Task<string?> DescribePreflightFaultAsync(Agent agent, CancellationToken ct)
    {
        if (!Enabled) return "managed_runtime_disabled";
        if (agent.RuntimeKind != "openshell") return null; // OpenShell→Docker needs no managed prerequisites beyond stop.
        if (agent.Provider != "codex" || agent.NativeAuthMode != "api_key" || string.IsNullOrEmpty(agent.ProviderApiKeyEnvRef)
            || agent.MountDockerSock || agent.LocalBaseUrl is not null)
            return "openshell_requires_codex_api_key_policy_and_no_socket";
        if (agent.Name != agent.ShortName || agent.Name != agent.Name.ToLowerInvariant() || !Regex.IsMatch(agent.Name, @"\A[a-z0-9][a-z0-9-]{0,40}\z"))
            return "managed_agent_name_unsupported";
        if (agent.SandboxPolicyVersionId is null || await store.LoadPolicyAsync(agent.SandboxPolicyVersionId.Value, ct) is null)
            return "sandbox_policy_version_not_found";
        if (string.IsNullOrEmpty(credentials.Get(agent.ProviderApiKeyEnvRef))) return "provider_key_reference_unresolved";
        // Ordinary-service routes are not yet available in managed mode; refuse rather than drop them.
        if (ContainerProvisioningService.HasTelegramBot(agent)) return "managed_route_unsupported:telegram";
        if (agent.JournalEnabled) return "managed_route_unsupported:journal";
        if (agent.McpEndpoints.FirstOrDefault(e => e.McpName != "memory") is { } extra) return "managed_route_unsupported:mcp:" + extra.McpName;
        if (!agent.Tools.Any(t => t.IsEnabled && MemoryTools.Any(m => t.ToolName == "mcp__memory__" + m))) return "managed_memory_grant_missing";
        if (string.IsNullOrWhiteSpace(config["StockOpenShell:AgentImage"])) return "managed_agent_image_missing";
        if (DescribeConfigDirFault() is { } dirFault) return dirFault;
        if (LoadTemplate() is null) return "managed_authority_template_invalid";
        if (broker.DescribePreflightFault() is { } brokerFault) return brokerFault;
        var version = await cli.RunAsync(["--version"], null, LegTimeout, ct);
        if (version.Missing) return "stock_cli_missing";
        var tokens = version.Stdout.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (version.ExitCode != 0 || tokens.Length < 2 || tokens[0] != "openshell"
            || !(tokens[1] == PinnedCliVersion || tokens[1].StartsWith(PinnedCliVersion + "+", StringComparison.Ordinal)))
            return "stock_cli_version_mismatch";
        return await host.GatewayGenerationAsync(ct) is null ? "gateway_unavailable" : null;
    }

    // ── operation envelope: one writer per agent ───────────────────────────

    private delegate Task<(ManagedRecord Next, string? AppliedKind, int? AppliedPolicy, Result Result)> Step(
        Agent agent, ManagedRecord record, CancellationToken ct);

    private async Task<Result> RunAsync(string name, string kind, Step step, CancellationToken ct)
    {
        if (!Enabled) return Result.Refused(409, Unavailable);
        var (agent, record, fault) = await ReadAsync(name, ct);
        if (fault is not null) return fault;
        var claimed = record! with { Operation = kind + ":" + Instance };
        var claimedJson = claimed.Serialize();
        try
        {
            if (!await store.SwapAsync(agent!.Id, agent.AppliedRuntimeBinding, claimedJson, null, null, ct))
                return Result.Refused(409, "managed_operation_conflict");
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException) { return Result.Refused(503, "runtime_state_unavailable"); }

        (ManagedRecord Next, string? AppliedKind, int? AppliedPolicy, Result Result) outcome;
        try
        {
            using var overall = CancellationTokenSource.CreateLinkedTokenSource(ct);
            outcome = await step(agent, claimed, overall.Token);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogWarning("Managed {Kind} for {Agent} ended unknown ({Type})", kind, name, ex.GetType().Name);
            outcome = (claimed with { State = "needs_attention", Errors = [.. claimed.Errors, kind + "_unknown"] }, null, null,
                Result.Refused(500, kind + "_unknown"));
        }

        var final = outcome.Next with { Operation = null };
        try
        {
            if (!await store.SwapAsync(agent.Id, claimedJson, final.Serialize(), outcome.AppliedKind, outcome.AppliedPolicy, CancellationToken.None))
                return Result.Refused(409, "managed_state_changed_during_" + kind, outcome.Result.Errors);
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException)
        {
            // Post-action DB failure: the claimed operation stays recorded and is reported as interrupted next time.
            return Result.Refused(503, kind + "_state_unknown", outcome.Result.Errors);
        }
        logger.LogInformation("Managed {Kind} for {Agent}: {Code}", kind, name, outcome.Result.Code);
        return outcome.Result;
    }

    private async Task<(Agent? Agent, ManagedRecord? Record, Result? Fault)> ReadAsync(string name, CancellationToken ct)
    {
        Agent? agent;
        try { agent = await store.LoadAsync(name, ct); }
        catch (Exception ex) when (ex is DbException or InvalidOperationException) { return (null, null, Result.Refused(503, "runtime_state_unavailable")); }
        if (agent is null) return (null, null, Result.Refused(404, "agent_not_found"));
        ManagedRecord record;
        try { record = ManagedRecord.Parse(agent.AppliedRuntimeBinding); }
        catch (JsonException) { return (agent, null, Result.Refused(409, "managed_binding_unreadable")); }
        if (record.Operation is { } op)
        {
            // An operation owned by an earlier orchestrator process was interrupted: hold it, never replay.
            if (!op.EndsWith(":" + Instance, StringComparison.Ordinal))
            {
                var held = record with { Operation = null, State = "needs_attention", Errors = [.. record.Errors, "interrupted_" + op.Split(':')[0]] };
                try { await store.SwapAsync(agent.Id, agent.AppliedRuntimeBinding, held.Serialize(), null, null, ct); }
                catch (Exception ex) when (ex is DbException or InvalidOperationException) { }
                return (agent, null, Result.Refused(409, "needs_attention", held.Errors));
            }
            return (agent, null, Result.Refused(409, "managed_operation_in_progress"));
        }
        return (agent, record, null);
    }

    // ── steps ──────────────────────────────────────────────────────────────

    private async Task<(ManagedRecord, string?, int?, Result)> ApplyCoreAsync(Agent agent, ManagedRecord record, CancellationToken ct)
    {
        if (record.State == "needs_attention")
            return (record, null, null, Result.Refused(409, "needs_attention", record.Errors));

        if (agent.RuntimeKind == "docker")
        {
            // OpenShell→Docker: revoke and stop every verified binding first; any unresolved leg holds.
            var errors = await RevokeFanoutAsync(agent.Name, record, ct);
            if (errors.Count > 0)
                return (record with { State = "needs_attention", Errors = errors }, null, null, Result.Refused(409, "revoke_unresolved", errors));
            // The previous binding is retained for custody; the Docker path continues in the caller.
            return (record with { State = "revoked", Candidate = null, Errors = [] }, "docker", null, Result.Done("applied_docker"));
        }

        if (await DescribePreflightFaultAsync(agent, ct) is { } preflight)
            return (record, null, null, Result.Refused(409, preflight));

        // Quiesce the old runtime before any new admission.
        if (agent.AppliedRuntimeKind == "docker")
        {
            if (await StopDockerAsync(agent, ct) is { } dockerFault)
                return (record, null, null, Result.Refused(409, dockerFault));
        }
        else if (record.Applied is not null)
        {
            var errors = await RevokeFanoutAsync(agent.Name, record, ct);
            if (errors.Count > 0)
                return (record with { State = "needs_attention", Errors = errors }, null, null, Result.Refused(409, "revoke_unresolved", errors));
        }

        var policy = (await store.LoadPolicyAsync(agent.SandboxPolicyVersionId!.Value, ct))!;
        var generation = await host.GatewayGenerationAsync(ct);
        if (generation is null) return (record, null, null, Result.Refused(503, "gateway_unavailable"));
        var epoch = Guid.NewGuid();
        var candidate = new ManagedBinding($"fleet-{agent.Name}-{epoch.ToString("N")[..8]}", "", epoch,
            config["StockOpenShell:AgentImage"]!, policy.Id, policy.Sha256, "api_key", agent.ProviderApiKeyEnvRef!,
            $"fleet-{agent.Name}-openai", generation);
        var applying = record with { State = "applying", Candidate = candidate, Errors = [] };

        // 1. Broker epoch resources; partials are held, never activated.
        var created = await broker.CreateEpochAsync(epoch, agent.ShortName, ct);
        if (!created.Ok)
            return (applying with { State = "needs_attention", Errors = [created.Code!, .. created.OwnedPartials] }, null, null,
                Result.Refused(502, created.Code!, created.OwnedPartials));

        // 2. Revoked allocation outside, then the workload bundle and stock provider.
        var template = LoadTemplate()!;
        var epochDir = Path.Combine(ConfigDir, "managed", agent.Name, epoch.ToString("N"));
        string bundle;
        try
        {
            WriteBrokerPasswords(agent.Name, created.Passwords!);
            WriteRecord(AuthorityPath(agent.Name), Authority(template, agent.Name, "pending", epoch, policy.Sha256, "revoked"));
            bundle = await BuildBundleAsync(agent, template, epochDir, created.Passwords!["workload"], epoch, policy, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or JsonException)
        {
            return (applying with { State = "needs_attention", Errors = ["managed_files_failed"] }, null, null, Result.Refused(500, "managed_files_failed"));
        }

        var key = credentials.Get(agent.ProviderApiKeyEnvRef!);
        if (string.IsNullOrEmpty(key)) return (applying with { State = "needs_attention", Errors = ["provider_key_reference_unresolved"] }, null, null, Result.Refused(409, "provider_key_reference_unresolved"));
        var secret = new Dictionary<string, string> { ["OPENAI_API_KEY"] = key };
        var exists = await cli.RunAsync(["provider", "get", candidate.Provider], null, LegTimeout, ct);
        var provider = await cli.RunAsync(exists.ExitCode == 0
            ? ["provider", "update", candidate.Provider, "--credential", "OPENAI_API_KEY"]
            : ["provider", "create", "--name", candidate.Provider, "--type", "openai", "--credential", "OPENAI_API_KEY"], secret, LegTimeout, ct);
        if (provider.ExitCode != 0 || provider.TimedOut)
            return (applying with { State = "needs_attention", Errors = ["provider_setup_unknown"] }, null, null, Result.Refused(502, "provider_setup_unknown"));

        // 3. Create once; the revoked copy makes the workload refuse before intake.
        var create = await cli.RunAsync([
            "sandbox", "create", "--name", candidate.SandboxName, "--from", candidate.Image,
            "--policy", Path.Combine(epochDir, "policy.yaml"), "--provider", candidate.Provider, "--no-auto-providers",
            "--restart-policy", "never", "--label", "fleet.agent=" + agent.Name, "--label", "fleet.epoch=" + epoch.ToString("N"),
            "--upload", Path.Combine(bundle, "app") + ":/app", "--upload", Path.Combine(bundle, "trust") + ":/trust",
            "--upload", Path.Combine(bundle, "workspace") + ":/workspace", "--detach"], null, TimeSpan.FromMinutes(5), ct);
        var sandbox = await GetAsync(candidate.SandboxName, ct);
        if (create.ExitCode != 0 || create.TimedOut || sandbox is null)
            return (applying with { State = "needs_attention", Errors = ["sandbox_create_unknown"] }, null, null, Result.Refused(502, "sandbox_create_unknown"));
        candidate = candidate with { SandboxId = sandbox.Value.Id };
        applying = applying with { Candidate = candidate };

        // 4. Wait for the refused allocation to settle, bind the actual UUID, activate, then one Start.
        if (await SettleStoppedAsync(candidate, ct) is { } settle)
            return (applying with { State = "needs_attention", Errors = [settle] }, null, null, Result.Refused(502, settle));
        try
        {
            WriteRecord(AuthorityPath(agent.Name), Authority(template, agent.Name, candidate.SandboxId, epoch, policy.Sha256, "active"));
            WriteRecord(Path.Combine(bundle, "trust", "enrollment.json"), Authority(template, agent.Name, candidate.SandboxId, epoch, policy.Sha256, "active"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (applying with { State = "needs_attention", Errors = ["managed_files_failed"] }, null, null, Result.Refused(500, "managed_files_failed"));
        }
        var upload = await cli.RunAsync(["sandbox", "upload", candidate.SandboxName, Path.Combine(bundle, "trust", "enrollment.json"), "/trust/enrollment.json"],
            null, LegTimeout, ct);
        if (upload.ExitCode != 0 || upload.TimedOut)
            return (applying with { State = "needs_attention", Errors = ["enrollment_upload_unknown"] }, null, null, Result.Refused(502, "enrollment_upload_unknown"));
        if (await LifecycleAsync("start", candidate, ct) is { } start)
            return (applying with { State = "needs_attention", Errors = [start] }, null, null, Result.Refused(502, start));

        return (record with { State = "applied", Applied = candidate, Candidate = null, Errors = [] }, "openshell", policy.Id, Result.Done("applied_openshell"));
    }

    private async Task<(ManagedRecord, string?, int?, Result)> StartCoreAsync(Agent agent, ManagedRecord record, CancellationToken ct)
    {
        if (agent.AppliedRuntimeKind != "openshell" || record.Applied is not { } binding)
            return (record, null, null, Result.Refused(409, "managed_binding_missing"));
        if (record.State is "needs_attention" or "revoked")
            return (record, null, null, Result.Refused(409, record.State, record.Errors));
        if (await host.GatewayGenerationAsync(ct) is not { } generation) return (record, null, null, Result.Refused(503, "gateway_unavailable"));
        if (generation != binding.GatewayGeneration)
        {
            // Unexpected gateway restart/resume: hold and stop verified owned bindings once; no Start.
            var errors = await RevokeFanoutAsync(agent.Name, record, ct);
            return (record with { State = "needs_attention", Errors = ["gateway_generation_changed", .. errors] }, null, null,
                Result.Refused(409, "gateway_generation_changed", errors));
        }
        if (await LifecycleAsync("start", binding, ct) is { } fault)
            return (record with { State = "needs_attention", Errors = [fault] }, null, null, Result.Refused(502, fault));
        return (record with { State = "applied", Errors = [] }, null, null, Result.Done("started"));
    }

    private async Task<(ManagedRecord, string?, int?, Result)> StopCoreAsync(Agent agent, ManagedRecord record, CancellationToken ct)
    {
        if (agent.AppliedRuntimeKind != "openshell" || record.Applied is not { } binding)
            return (record, null, null, Result.Refused(409, "managed_binding_missing"));
        var errors = new List<string>();
        if (await LifecycleAsync("stop", binding, ct) is { } fault) errors.Add(fault);
        else if (await QuiescentAsync(binding, ct) is { } quiet) errors.Add(quiet);
        return errors.Count == 0
            ? (record with { State = "stopped", Errors = [] }, null, null, Result.Done("stopped"))
            : (record with { State = "needs_attention", Errors = errors }, null, null, Result.Refused(502, "stop_unresolved", errors));
    }

    private async Task<(ManagedRecord, string?, int?, Result)> RevokeCoreAsync(Agent agent, ManagedRecord record, CancellationToken ct)
    {
        if (record.Applied is null && record.Candidate is null) return (record, null, null, Result.Refused(409, "managed_binding_missing"));
        var errors = await RevokeFanoutAsync(agent.Name, record, ct);
        return errors.Count == 0
            ? (record with { State = "revoked", Candidate = null, Errors = [] }, null, null, Result.Done("revoked"))
            : (record with { State = "needs_attention", Errors = errors }, null, null, Result.Refused(502, "revoke_unresolved", errors));
    }

    // ── shared legs ────────────────────────────────────────────────────────

    /// <summary>
    /// Admission revoke, provider detach, broker revoke/connection close and one normal Stop for each
    /// verified binding, as independent legs with their own deadline so a hung leg never suppresses
    /// Stop. Returns every unresolved error; an empty list is the only clean result.
    /// </summary>
    internal async Task<List<string>> RevokeFanoutAsync(string agentName, ManagedRecord record, CancellationToken ct)
    {
        var errors = new List<string>();
        var targets = new[] { record.Applied, record.Candidate }.OfType<ManagedBinding>().DistinctBy(b => b.SandboxName).ToList();
        using var overall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        overall.CancelAfter(FanoutTimeout);
        var legs = new List<Task<string?>>
        {
            Leg("admission", _ =>
            {
                var path = AuthorityPath(agentName);
                if (!File.Exists(path)) return Task.FromResult<string?>(null);
                var current = JsonNode.Parse(StockEnrollmentAuthority.ReadBounded(path))!.AsObject();
                current["State"] = "revoked";
                WriteRecord(path, current);
                return Task.FromResult<string?>(null);
            }),
        };
        foreach (var target in targets)
        {
            legs.Add(Leg("broker:" + target.SandboxName, async token =>
            {
                var revoked = await broker.RevokeEpochAsync(target.Epoch, token);
                return revoked.Ok ? null : string.Join(",", revoked.OwnedPartials.DefaultIfEmpty(revoked.Code ?? "broker_revoke_unresolved"));
            }));
            if (string.IsNullOrEmpty(target.SandboxId)) { errors.Add("unverified_target:" + target.SandboxName); continue; }
            legs.Add(Leg("detach:" + target.SandboxName, async token =>
            {
                if (await VerifyIdentityAsync(target, token) is { } identity) return identity.Code;
                var run = await cli.RunAsync(["sandbox", "provider", "detach", target.SandboxName, target.Provider], null, LegTimeout, token);
                return run.ExitCode == 0 && !run.TimedOut ? null : "provider_detach_unknown";
            }));
            legs.Add(Leg("stop:" + target.SandboxName, async token =>
                await LifecycleAsync("stop", target, token) ?? await QuiescentAsync(target, token)));
        }
        foreach (var leg in await Task.WhenAll(legs))
            if (leg is not null) errors.Add(leg);
        return errors;

        async Task<string?> Leg(string label, Func<CancellationToken, Task<string?>> action)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(overall.Token);
            deadline.CancelAfter(LegTimeout);
            try
            {
                var work = action(deadline.Token);
                var finished = await Task.WhenAny(work, Task.Delay(Timeout.Infinite, deadline.Token).ContinueWith(_ => (string?)"timeout", TaskScheduler.Default));
                var error = await finished;
                return error is null ? null : label + ":" + error;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { return label + ":" + ex.GetType().Name; }
        }
    }

    /// <summary>Name-based stock control with exact-UUID checks before and after; a changed target is unknown.</summary>
    private async Task<string?> LifecycleAsync(string verb, ManagedBinding binding, CancellationToken ct)
    {
        if (await VerifyIdentityAsync(binding, ct) is { } before) return before.Code;
        var run = await cli.RunAsync(["sandbox", verb, binding.SandboxName], null, LegTimeout, ct);
        if (run.Missing) return "stock_cli_missing";
        if (run.TimedOut || run.ExitCode != 0) return verb + "_unknown";
        return await VerifyIdentityAsync(binding, ct) is { } after ? after.Code : null;
    }

    private async Task<Result?> VerifyIdentityAsync(ManagedBinding binding, CancellationToken ct)
    {
        var sandbox = await GetAsync(binding.SandboxName, ct);
        if (sandbox is null) return Result.Refused(502, "sandbox_unobservable");
        return sandbox.Value.Id == binding.SandboxId ? null : Result.Refused(409, "sandbox_identity_changed");
    }

    private async Task<(string Id, string Phase)?> GetAsync(string name, CancellationToken ct)
    {
        var run = await cli.RunAsync(["sandbox", "get", name, "-o", "json"], null, LegTimeout, ct);
        if (run.ExitCode != 0 || run.TimedOut) return null;
        try
        {
            using var document = JsonDocument.Parse(run.Stdout);
            var id = document.RootElement.GetProperty("id").GetString();
            var phase = document.RootElement.GetProperty("phase").GetString();
            return string.IsNullOrEmpty(id) || document.RootElement.GetProperty("name").GetString() != name ? null : (id, phase ?? "Unknown");
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { return null; }
    }

    private async Task<string?> SettleStoppedAsync(ManagedBinding binding, CancellationToken ct)
    {
        var until = DateTimeOffset.UtcNow + StopSettle;
        while (DateTimeOffset.UtcNow < until)
        {
            var sandbox = await GetAsync(binding.SandboxName, ct);
            if (sandbox is null) return "sandbox_unobservable";
            if (sandbox.Value.Id != binding.SandboxId) return "sandbox_identity_changed";
            if (sandbox.Value.Phase is "Stopped" or "Completed" or "Error") return await QuiescentAsync(binding, ct);
            await Task.Delay(Poll, ct);
        }
        // The revoked allocation is still running: stop it explicitly rather than activating under it.
        return await LifecycleAsync("stop", binding, ct) ?? await QuiescentAsync(binding, ct);
    }

    /// <summary>No workload or native-supervisor container left running for the UUID, by exact label query.</summary>
    private async Task<string?> QuiescentAsync(ManagedBinding binding, CancellationToken ct)
    {
        var running = await host.RunningSandboxContainersAsync(binding.SandboxId, ct);
        return running is null ? "quiescence_unobservable" : running.Count == 0 ? null : "quiescence_pending";
    }

    private async Task<string?> StopDockerAsync(Agent agent, CancellationToken ct)
    {
        if (StopDocker is { } stop) return await stop(agent, ct);
        var docker = services.GetRequiredService<DockerService>();
        var status = await docker.GetContainerStatusAsync(agent.ContainerName);
        if (status is null or "exited" or "created" or "dead") return null; // absent or not running
        if (!await docker.StopContainerAsync(agent.ContainerName)) return "docker_stop_failed";
        var after = await docker.GetContainerStatusAsync(agent.ContainerName);
        return after is null or "exited" or "dead" ? null : "docker_quiescence_pending";
    }

    // ── files: private ConfigDir only, atomic replacement, 0700/0600 ───────

    private string? DescribeConfigDirFault()
    {
        if (string.IsNullOrEmpty(ConfigDir) || !Path.IsPathFullyQualified(ConfigDir) || !Directory.Exists(ConfigDir)) return "managed_config_dir_invalid";
        if (!OperatingSystem.IsWindows())
        {
            var info = new DirectoryInfo(ConfigDir);
            if (info.LinkTarget is not null || (File.GetUnixFileMode(ConfigDir) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite
                | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
                return "managed_config_dir_not_private";
        }
        return null;
    }

    /// <summary>Operator-owned trust fields; the orchestrator fills only identity, epoch and state.</summary>
    internal JsonObject? LoadTemplate()
    {
        try
        {
            var path = Path.Combine(ConfigDir, "managed-authority.json");
            var node = JsonNode.Parse(StockEnrollmentAuthority.ReadBounded(path))?.AsObject();
            if (node is null) return null;
            foreach (var field in new[] { "GatewayIssuer", "GatewayAudience", "GatewayJwksPath", "AssertionPrivateKeyPath", "AssertionPublicKeyPath", "MemoryHost", "BrokerHost" })
                if (node[field]?.GetValue<string>() is not { Length: > 0 }) return null;
            return node;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException
            or ArgumentException or StockIdentityDenied) { return null; }
    }

    internal static JsonObject Authority(JsonObject template, string agent, string sandboxId, Guid epoch, string policySha, string state)
    {
        var record = new JsonObject
        {
            ["State"] = state,
            ["Agent"] = agent,
            ["SandboxId"] = sandboxId,
            ["Epoch"] = epoch.ToString(),
            ["ExpiresAt"] = DateTimeOffset.UtcNow.AddDays(365).ToString("O"),
        };
        foreach (var field in new[] { "GatewayIssuer", "GatewayAudience", "GatewayJwksPath", "AssertionPrivateKeyPath", "AssertionPublicKeyPath", "MemoryHost", "BrokerHost" })
            record[field] = template[field]!.GetValue<string>();
        record["StockCommit"] = StockCommit;
        record["PolicySha256"] = policySha;
        return record;
    }

    private void WriteBrokerPasswords(string agent, IReadOnlyDictionary<string, string> passwords)
    {
        foreach (var role in new[] { "control", "observer" })
            WriteText(Path.Combine(AuthorityDir, $"{agent}.{role}.password"), passwords[role]);
    }

    private async Task<string> BuildBundleAsync(Agent agent, JsonObject template, string epochDir, string workloadPassword,
        Guid epoch, SandboxPolicyVersion policy, CancellationToken ct)
    {
        var generated = GenerateInputs is { } generate ? await generate(agent.Name, ct)
            : await services.GetRequiredService<ContainerProvisioningService>().GenerateManagedInputsAsync(agent.Name, ct);
        var bundle = Path.Combine(epochDir, "bundle");
        PrivateDirectory(Path.Combine(bundle, "app"));
        PrivateDirectory(Path.Combine(bundle, "trust"));
        PrivateDirectory(Path.Combine(bundle, "workspace"));
        WriteText(Path.Combine(epochDir, "policy.yaml"), policy.Yaml);

        var settings = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(generated, "appsettings.json"), ct))!.AsObject();
        settings["StockOpenShell"] = new JsonObject
        {
            ["Enabled"] = true,
            ["EnrollmentFile"] = "/trust/enrollment.json",
            ["AuthorityFile"] = "/trust/enrollment.json",
            ["BrokerPasswordFile"] = "/trust/workload-broker-password",
        };
        var agentNode = settings["Agent"]?.AsObject() ?? throw new InvalidOperationException("generated_agent_section_missing");
        agentNode["NativeAuthMode"] = "api_key";
        agentNode["ProviderApiKeyEnvRef"] = agent.ProviderApiKeyEnvRef;
        agentNode["WorkDir"] = "/workspace";
        (settings["RabbitMq"] ??= new JsonObject())["Host"] = template["BrokerHost"]!.GetValue<string>();
        WriteText(Path.Combine(bundle, "app", "appsettings.json"), settings.ToJsonString());
        var root = Path.Combine(generated, Fleet.Shared.HarnessDefaults.CodexRootTomlFileName);
        if (!File.Exists(root)) throw new InvalidOperationException("codex_root_missing");
        File.Copy(root, Path.Combine(bundle, "app", Fleet.Shared.HarnessDefaults.CodexRootTomlFileName), overwrite: true);
        foreach (var dir in new[] { "roles", "projects" })
            if (Directory.Exists(Path.Combine(generated, dir))) CopyTree(Path.Combine(generated, dir), Path.Combine(bundle, "app", dir));
        WriteText(Path.Combine(bundle, "workspace", ".mcp.json"), new JsonObject
        {
            ["mcpServers"] = new JsonObject { ["memory"] = new JsonObject { ["url"] = $"https://{template["MemoryHost"]!.GetValue<string>()}/mcp" } },
        }.ToJsonString());
        WriteRecord(Path.Combine(bundle, "trust", "enrollment.json"), Authority(template, agent.Name, "pending", epoch, policy.Sha256, "revoked"));
        WriteText(Path.Combine(bundle, "trust", "workload-broker-password"), workloadPassword);
        return bundle;
    }

    private static void CopyTree(string source, string target)
    {
        PrivateDirectory(target);
        foreach (var dir in Directory.GetDirectories(source)) CopyTree(dir, Path.Combine(target, Path.GetFileName(dir)));
        foreach (var file in Directory.GetFiles(source))
        {
            if (new FileInfo(file).LinkTarget is not null) throw new IOException("generated_symlink_refused");
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        }
    }

    private static void PrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(path);
        else Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static void WriteRecord(string path, JsonObject record) => WriteText(path, record.ToJsonString());

    private static void WriteText(string path, string content)
    {
        PrivateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var stream = new FileStream(temp, options))
        using (var writer = new StreamWriter(stream)) writer.Write(content);
        File.Move(temp, path, overwrite: true);
    }
}
