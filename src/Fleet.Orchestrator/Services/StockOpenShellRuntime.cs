using System.ComponentModel;
using System.Data.Common;
using System.Diagnostics;
using System.Text;
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
/// alternate endpoint variables are removed from the child. Output is read with a fixed cap and the
/// child plus both readers settle inside the deadline; stderr is discarded because stock output may
/// echo request content, so callers report typed codes, not raw output.
/// </summary>
public sealed class ProcessStockCli(IConfiguration config) : IStockCli
{
    internal const int OutputLimit = 1_000_000;

    public async Task<StockCliResult> RunAsync(IReadOnlyList<string> args, IReadOnlyDictionary<string, string>? secretEnv,
        TimeSpan timeout, CancellationToken ct)
    {
        var path = config["StockOpenShell:CliPath"];
        var cliPath = string.IsNullOrWhiteSpace(path) ? "openshell" : path;
        // Own session/process group, so a timeout reaps reparented descendants too.
        var leader = !OperatingSystem.IsWindows() && File.Exists(SetsidPath);
        var psi = new ProcessStartInfo(leader ? SetsidPath : cliPath)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (leader) psi.ArgumentList.Add(cliPath);
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
            var stdout = ReadBoundedAsync(process.StandardOutput);
            var stderr = ReadBoundedAsync(process.StandardError);
            // One deadline for the child AND both output drains: an inherited pipe never extends it.
            var clock = Stopwatch.StartNew();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(timeout);
            try
            {
                await process.WaitForExitAsync(deadline.Token);
                var remaining = timeout - clock.Elapsed;
                if (remaining <= TimeSpan.Zero) throw new TimeoutException();
                await Task.WhenAll(stdout, stderr).WaitAsync(remaining, ct);
            }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
            {
                if (leader) _ = KillGroup(-process.Id, 9);
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return new(-1, "", TimedOut: true);
            }
            var output = await stdout;
            // Over-limit output is never parsed as a partial document.
            return output is null ? new(-1, "") : new(process.ExitCode, output);
        }
    }

    private const string SetsidPath = "/usr/bin/setsid";

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int KillGroup(int pid, int signal);

    private static async Task<string?> ReadBoundedAsync(StreamReader reader)
    {
        var buffer = new char[8192];
        var text = new StringBuilder();
        var overflow = false;
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), CancellationToken.None)) > 0)
        {
            if (overflow) continue; // keep draining so the child never blocks on a full pipe
            if (text.Length + read > OutputLimit) { overflow = true; text.Clear(); continue; }
            text.Append(buffer, 0, read);
        }
        return overflow ? null : text.ToString();
    }
}

// ── Host observation seam (gateway generation, workload/supervisor quiescence) ──

public interface IStockHostView
{
    /// <summary>Gateway container ID plus StartedAt, or null when unobservable.</summary>
    Task<string?> GatewayGenerationAsync(CancellationToken ct);

    /// <summary>Full IDs of workload/native-supervisor containers still running for a stock UUID; null when unobservable.</summary>
    Task<IReadOnlyList<string>?> RunningSandboxContainersAsync(string sandboxId, CancellationToken ct);

    /// <summary>States of containers with exactly this name; empty when absent, null when unobservable.</summary>
    Task<IReadOnlyList<string>?> DockerContainerStatesAsync(string containerName, CancellationToken ct);
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

    public async Task<IReadOnlyList<string>?> DockerContainerStatesAsync(string containerName, CancellationToken ct)
    {
        var containers = await docker.ListContainersByNameAsync(containerName, ct);
        return containers?.Select(c => c.State).ToList();
    }
}

// ── Persisted binding and compare-and-set store ─────────────────────────────

public sealed record ManagedBinding(string SandboxName, string SandboxId, Guid Epoch, string Image, int PolicyVersionId,
    string PolicySha256, string AuthMode, string KeyRef, string Provider, string GatewayGeneration)
{
    /// <summary>Further attached stock providers: the agent's own Telegram bot and journal capture (#492).</summary>
    public IReadOnlyList<string> ServiceProviders { get; init; } = [];

    /// <summary>Every provider attached to this binding, native API key first.</summary>
    public IReadOnlyList<string> AllProviders() => [Provider, .. ServiceProviders];
}

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
/// toward the desired runtime after the old one is verified quiescent; Start/Stop/cancel/logs/revoke
/// act on the persisted applied binding. One operation per agent at a time (compare-and-set); the
/// candidate's epoch/name/UUID is checkpointed before each external action; unknown outcomes become
/// <c>needs_attention</c> and are never replayed or cleared by a later successful Stop.
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
    public const string Entrypoint = "/app/stock-entrypoint.sh";
    /// <summary>Operator-imported stock profile IDs for the agent's own bot and journal capture (#492).</summary>
    public const string TelegramProfile = "telegram";
    public const string JournalProfile = "fleet-journal";
    /// <summary>Separate profile for the comms-journal read MCP token, so its binary scope is the native Codex child only.</summary>
    public const string JournalReadProfile = "fleet-journal-read";
    /// <summary>Profile for the derived cross-chat read token (cross-chat sends from the agent process).</summary>
    public const string JournalCrossChatProfile = "fleet-journal-cross-chat";
    internal static readonly string Instance = Guid.NewGuid().ToString("N");
    internal static readonly string[] MemoryTools = ["memory_get", "memory_search", "memory_list"];
    /// <summary>Ordinary MCP endpoint name → first-party receiving service that verifies the outside assertion.</summary>
    internal static readonly IReadOnlyDictionary<string, string> ServiceByMcpName =
        StockServices.McpNames.ToDictionary(p => p.Value, p => p.Key, StringComparer.Ordinal);
    /// <summary>Account/control state that never crosses a runtime switch with the workspace.</summary>
    internal static readonly string[] PrivateWorkspaceEntries = [".generated", "claude", ".claude", "codex", ".codex", ".codex-host", "gemini", ".gemini", ".ssh", ".mcp.json"];

    internal TimeSpan LegTimeout { get; init; } = TimeSpan.FromSeconds(30);
    internal TimeSpan FanoutTimeout { get; init; } = TimeSpan.FromSeconds(120);
    internal TimeSpan StopSettle { get; init; } = TimeSpan.FromSeconds(60);
    internal TimeSpan Poll { get; init; } = TimeSpan.FromSeconds(2);
    /// <summary>Test seams; production uses the ordinary generator, Docker service and provisioning path.</summary>
    internal Func<string, CancellationToken, Task<string>>? GenerateInputs { get; init; }
    internal Func<string, Task<bool>>? DockerStop { get; init; }
    internal Func<string, CancellationToken, Task<(bool Ok, string Message)>>? ProvisionDocker { get; init; }
    internal Func<Agent, string>? DockerWorkspace { get; init; }

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
    /// Stock-authenticated <c>sandbox exec</c> to the agent's managed loopback cancel route, with
    /// generation and exact-UUID checks before and after. A changed generation or identity is
    /// persisted as a hold. A timeout or other failure is an unconfirmed cancellation.
    /// </summary>
    public async Task<Result> CancelAsync(string name, string? taskId, CancellationToken ct)
    {
        if (!Enabled) return Result.Refused(409, Unavailable);
        var (agent, record, fault) = await ReadAsync(name, ct);
        if (fault is not null) return fault;
        if (agent!.AppliedRuntimeKind != "openshell" || record!.Applied is not { } binding) return Result.Refused(409, "managed_binding_missing");
        if (record.State is "needs_attention" or "revoked") return Result.Refused(409, record.State, record.Errors);
        var path = taskId is null ? "/cancel" : "/cancel/" + Uri.EscapeDataString(taskId);
        if (!Regex.IsMatch(path, @"\A/cancel(/[A-Za-z0-9%._~-]{1,256})?\z")) return Result.Refused(400, "invalid_task_id");
        if (await GuardAsync(agent, binding, ct) is { } before) return before;
        const string script = "exec 3<>/dev/tcp/127.0.0.1/8080 || exit 2; "
            + "printf 'POST %s HTTP/1.0\\r\\nContent-Length: 0\\r\\n\\r\\n' \"$1\" >&3; "
            + "IFS= read -r status <&3; case \"$status\" in *' 200 '*) exit 0;; *' 404 '*) exit 4;; esac; exit 1";
        var run = await cli.RunAsync(["sandbox", "exec", "-n", binding.SandboxName, "--", "bash", "-c", script, "cancel", path], null, LegTimeout, ct);
        if (await GuardAsync(agent, binding, ct) is { } after) return after;
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
        if (await GuardAsync(agent, binding, ct) is { } before) return (before, null);
        var run = await cli.RunAsync(["logs", binding.SandboxName, "-n", Math.Clamp(lines, 1, 1000).ToString()], null, LegTimeout, ct);
        // Output read across a UUID swap belongs to another sandbox: discard it and hold.
        if (await GuardAsync(agent, binding, ct) is { } after) return (after, null);
        return run.ExitCode == 0 && !run.TimedOut ? (Result.Done("logs"), run.Stdout) : (Result.Refused(502, "stock_logs_unavailable"), null);
    }

    /// <summary>
    /// Generation and exact-UUID guard for status/action paths outside the operation envelope. A
    /// changed generation or identity is persisted as <c>needs_attention</c>; unobservable is refused.
    /// </summary>
    private async Task<Result?> GuardAsync(Agent agent, ManagedBinding binding, CancellationToken ct)
    {
        var fault = await GenerationFaultAsync(binding, ct);
        if (fault is null && await VerifyIdentityAsync(binding, ct) is { } identity) fault = identity.Code;
        if (fault is null) return null;
        if (fault == "gateway_generation_changed")
        {
            await HandleUnexpectedRestartAsync(agent.Name, ct);
            return Result.Refused(409, fault);
        }
        if (fault == "sandbox_identity_changed")
        {
            try
            {
                if (await store.LoadAsync(agent.Name, ct) is { } fresh && ManagedRecord.Parse(fresh.AppliedRuntimeBinding) is { Operation: null } current
                    && current.State != "needs_attention"
                    && await store.SwapAsync(fresh.Id, fresh.AppliedRuntimeBinding, (current with { State = "needs_attention", Errors = [.. current.Errors, fault] }).Serialize(), null, null, ct))
                    await PublishAsync(agent.Name);
            }
            catch (Exception ex) when (ex is DbException or InvalidOperationException or JsonException) { }
        }
        return Result.Refused(409, fault);
    }

    /// <summary>
    /// Status refresh: an applied binding whose gateway generation changed is held and its verified
    /// owned sandbox gets one bounded revoke/Stop fanout. Unobservable or busy states are left alone.
    /// </summary>
    public async Task CheckGenerationAsync(string name, CancellationToken ct)
    {
        if (!Enabled) return;
        Agent? agent;
        try { agent = await store.LoadAsync(name, ct); }
        catch (Exception ex) when (ex is DbException or InvalidOperationException) { return; }
        if (agent is not { AppliedRuntimeKind: "openshell" }) return;
        ManagedRecord record;
        try { record = ManagedRecord.Parse(agent.AppliedRuntimeBinding); }
        catch (JsonException) { return; }
        if (record is not { Operation: null, Applied: { } binding } || record.State is "needs_attention" or "revoked") return;
        if (await host.GatewayGenerationAsync(ct) is { } current && current != binding.GatewayGeneration)
            await HandleUnexpectedRestartAsync(name, ct);
    }

    private Task<Result> HandleUnexpectedRestartAsync(string name, CancellationToken ct) =>
        RunAsync(name, "restart_hold", async (agent, record, _, token) => record.Applied is null
            ? (record, null, null, Result.Refused(409, "managed_binding_missing"))
            : await HoldForGenerationAsync(agent, record, "gateway_generation_changed", token), ct);

    /// <summary>Pushes the persisted runtime state to the registry/WebSocket so dashboards update live.</summary>
    private async Task PublishAsync(string name)
    {
        try
        {
            if (services.GetService<AgentRegistry>() is { } registry && await store.LoadAsync(name, CancellationToken.None) is { } fresh)
                registry.UpdateRuntimeSelection(fresh);
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException) { }
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

    /// <summary>
    /// Named blocker before any external mutation or old-runtime Stop, or null when apply may
    /// proceed: identity/auth selection, routes, trust/config, broker HTTPS auth, pinned provider
    /// profile hashes, CLI version and gateway observation.
    /// </summary>
    public async Task<string?> DescribePreflightFaultAsync(Agent agent, CancellationToken ct)
    {
        if (!Enabled) return "managed_runtime_disabled";
        if (agent.RuntimeKind != "openshell") return null; // OpenShell→Docker needs no managed prerequisites beyond stop.
        if (agent.Provider != "codex" || agent.NativeAuthMode != "api_key" || string.IsNullOrEmpty(agent.ProviderApiKeyEnvRef)
            || agent.MountDockerSock || agent.LocalBaseUrl is not null)
            return "openshell_requires_codex_api_key_policy_and_no_socket";
        if (agent.Name != agent.ShortName || agent.Name != agent.Name.ToLowerInvariant() || !Regex.IsMatch(agent.Name, @"\A[a-z0-9][a-z0-9-]{0,40}\z"))
            return "managed_agent_name_unsupported";
        var policy = agent.SandboxPolicyVersionId is { } id ? await store.LoadPolicyAsync(id, ct) : null;
        if (policy is null) return "sandbox_policy_version_not_found";
        if (string.IsNullOrEmpty(credentials.Get(agent.ProviderApiKeyEnvRef))) return "provider_key_reference_unresolved";
        // The agent's own bot token and derived journal ingest token stay outside in stock providers; the
        // workload gets only the public bot id and stock placeholders. Missing values refuse here.
        if (ContainerProvisioningService.HasTelegramBot(agent) && TelegramCredential(agent).Fault is { } telegramFault) return telegramFault;
        // Tool-send receipts (#394) arrive on the ordinary broker, which the epoch identity cannot read; with
        // capture on, Telegram MCP sends would go unjournaled, so the combination refuses instead.
        if (JournalCapture(agent) && agent.McpEndpoints.Any(e => e.McpName == "fleet-telegram"))
            return "managed_route_unsupported:journal_tool_send_receipts";
        if ((JournalCapture(agent) || JournalRead(agent)) && services.GetService<JournalTokenService>()?.DescribeKeyFault() is { } journalKeyFault) return journalKeyFault;
        // Only first-party HTTP MCP receivers that verify the outside assertion, and the comms-journal read
        // MCP (Comms verifies the derived read token resolved outside), are routed; others refuse.
        if (agent.McpEndpoints.FirstOrDefault(e => e.McpName != "memory" && e.McpName != StockJournalReadRoute.McpName
            && !ServiceByMcpName.ContainsKey(e.McpName)) is { } extra)
            return "managed_route_unsupported:mcp:" + extra.McpName;
        if (!agent.Tools.Any(t => t.IsEnabled && MemoryTools.Any(m => t.ToolName == "mcp__memory__" + m))) return "managed_memory_grant_missing";
        if (string.IsNullOrWhiteSpace(config["StockOpenShell:AgentImage"])) return "managed_agent_image_missing";
        if (DescribeConfigDirFault() is { } dirFault) return dirFault;
        if (LoadTemplate() is not { } template) return "managed_authority_template_invalid";
        // A supported service without an operator-configured receiving host has no route in this installation.
        if (agent.McpEndpoints.FirstOrDefault(e => ServiceByMcpName.TryGetValue(e.McpName, out var service) && TemplateHost(template, service) is null) is { } unrouted)
            return "managed_route_unsupported:mcp:" + unrouted.McpName;
        // Journal capture needs the operator's journal listener host as its route.
        if (JournalCapture(agent) && JournalHost(template) is null) return "managed_route_unsupported:journal";
        // The read MCP targets the same journal listener host, at the ordinary endpoint's path.
        if (JournalRead(agent) && (JournalHost(template) is null || JournalReadPath(agent) is null))
            return "managed_route_unsupported:mcp:" + StockJournalReadRoute.McpName;
        if (broker.DescribePreflightFault() is { } brokerFault) return brokerFault;
        if (await broker.VerifyAccessAsync(ct) is { } accessFault) return accessFault;
        var version = await cli.RunAsync(["--version"], null, LegTimeout, ct);
        if (version.Missing) return "stock_cli_missing";
        var tokens = version.Stdout.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (version.ExitCode != 0 || tokens.Length < 2 || tokens[0] != "openshell"
            || !(tokens[1] == PinnedCliVersion || tokens[1].StartsWith(PinnedCliVersion + "+", StringComparison.Ordinal)))
            return "stock_cli_version_mismatch";
        // Authenticated gateway client trust (mTLS/JWT material in ConfigDir), not only a running container.
        var probe = await cli.RunAsync(["sandbox", "list", "-o", "json", "--page-size", "1"], null, LegTimeout, ct);
        if (probe.Missing || probe.TimedOut || probe.ExitCode != 0 || !IsJson(probe.Stdout)) return "gateway_client_unverified";
        if (await ProfileHashFaultAsync(policy, ct) is { } profileFault) return profileFault;
        return await host.GatewayGenerationAsync(ct) is null ? "gateway_unavailable" : null;
    }

    private static bool IsJson(string text)
    {
        try { using var _ = JsonDocument.Parse(text); return true; }
        catch (JsonException) { return false; }
    }

    /// <summary>Each pinned provider profile's exported YAML must hash to the version's saved value.</summary>
    private async Task<string?> ProfileHashFaultAsync(SandboxPolicyVersion policy, CancellationToken ct)
    {
        Dictionary<string, string>? pinned;
        try { pinned = JsonSerializer.Deserialize<Dictionary<string, string>>(string.IsNullOrWhiteSpace(policy.ProviderProfileHashes) ? "{}" : policy.ProviderProfileHashes); }
        catch (JsonException) { return "provider_profile_hashes_invalid"; }
        foreach (var (profile, hash) in pinned ?? [])
        {
            var export = await cli.RunAsync(["profile", "export", profile, "-o", "yaml"], null, LegTimeout, ct);
            if (export.Missing || export.TimedOut || export.ExitCode != 0) return "provider_profile_unverifiable:" + profile;
            var actual = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(export.Stdout)));
            if (!string.Equals(actual, hash, StringComparison.Ordinal)) return "provider_profile_hash_mismatch:" + profile;
        }
        return null;
    }

    // ── operation envelope: one writer per agent, checkpointed progress ────

    private delegate Task<(ManagedRecord Next, string? AppliedKind, int? AppliedPolicy, Result Result)> Step(
        Agent agent, ManagedRecord record, Func<ManagedRecord, Task> checkpoint, CancellationToken ct);

    private async Task<Result> RunAsync(string name, string kind, Step step, CancellationToken ct)
    {
        if (!Enabled) return Result.Refused(409, Unavailable);
        var (agent, record, fault) = await ReadAsync(name, ct);
        if (fault is not null) return fault;
        var operation = kind + ":" + Instance;
        var current = record! with { Operation = operation };
        var saved = current.Serialize();
        try
        {
            if (!await store.SwapAsync(agent!.Id, agent.AppliedRuntimeBinding, saved, null, null, ct))
                return Result.Refused(409, "managed_operation_conflict");
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException) { return Result.Refused(503, "runtime_state_unavailable"); }

        // Progress (candidate epoch/name/UUID) is durable before the external action it enables.
        async Task Checkpoint(ManagedRecord next)
        {
            var json = (next with { Operation = operation }).Serialize();
            if (!await store.SwapAsync(agent.Id, saved, json, null, null, CancellationToken.None))
                throw new InvalidOperationException("managed_checkpoint_conflict");
            saved = json;
            current = next with { Operation = operation };
            await PublishAsync(agent.Name);
        }

        (ManagedRecord Next, string? AppliedKind, int? AppliedPolicy, Result Result) outcome;
        try { outcome = await step(agent, current, Checkpoint, ct); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogWarning("Managed {Kind} for {Agent} ended unknown ({Type})", kind, name, ex.GetType().Name);
            outcome = (current with { State = "needs_attention", Errors = [.. current.Errors, kind + "_unknown"] }, null, null,
                Result.Refused(500, kind + "_unknown"));
        }

        var final = outcome.Next with { Operation = null };
        try
        {
            if (!await store.SwapAsync(agent.Id, saved, final.Serialize(), outcome.AppliedKind, outcome.AppliedPolicy, CancellationToken.None))
                return Result.Refused(409, "managed_state_changed_during_" + kind, outcome.Result.Errors);
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException)
        {
            // Post-action DB failure: the claimed operation stays recorded and is reported as interrupted next time.
            return Result.Refused(503, kind + "_state_unknown", outcome.Result.Errors);
        }
        await PublishAsync(name);
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

    private static (ManagedRecord, string?, int?, Result) Hold(ManagedRecord record, int status, string code, params string[] extra) =>
        (record with { State = "needs_attention", Errors = [code, .. extra] }, null, null, Result.Refused(status, code, extra));

    // ── steps ──────────────────────────────────────────────────────────────

    private async Task<(ManagedRecord, string?, int?, Result)> ApplyCoreAsync(Agent agent, ManagedRecord record,
        Func<ManagedRecord, Task> checkpoint, CancellationToken ct)
    {
        if (record.State == "needs_attention") return (record, null, null, Result.Refused(409, "needs_attention", record.Errors));
        if (record.Candidate is not null) return (record, null, null, Result.Refused(409, "unresolved_candidate"));
        return agent.RuntimeKind == "docker"
            ? await ApplyDockerAsync(agent, record, checkpoint, ct)
            : await ApplyOpenShellAsync(agent, record, checkpoint, ct);
    }

    /// <summary>
    /// OpenShell→Docker: cut off, copy the workspace out while running, stop and verify, then run the
    /// ordinary Docker provisioning while this operation still holds the agent. Applied Docker is
    /// published only after that Docker transition succeeded.
    /// </summary>
    private async Task<(ManagedRecord, string?, int?, Result)> ApplyDockerAsync(Agent agent, ManagedRecord record,
        Func<ManagedRecord, Task> checkpoint, CancellationToken ct)
    {
        if (record.Applied is { } applied)
        {
            // Cut off (unless already revoked), quiesce every writer, start into staging (no daemon),
            // copy the workspace out, then stop and verify. Any failure still attempts a bounded Stop.
            var errors = new List<string>();
            if (record.State != "revoked") errors.AddRange(await RevokeFanoutAsync(agent.Name, record, ct, stop: false));
            if (errors.Count == 0 && await StopVerifiedAsync(applied, ct) is { } quiet) errors.Add(quiet);
            if (errors.Count == 0 && await StageAsync(applied, ct) is { } staged) errors.Add(staged);
            if (errors.Count == 0 && await DownloadWorkspaceAsync(agent, applied, DockerWorkspaceDir(agent), ct) is { } workspace) errors.Add(workspace);
            if (await StopVerifiedAsync(applied, ct) is { } stopped) errors.Add(stopped);
            if (errors.Count > 0) return Hold(record, 502, "openshell_to_docker_unresolved", [.. errors]);
            record = record with { State = "revoked" };
            await checkpoint(record);
        }
        var provisioned = ProvisionDocker is { } provision ? await provision(agent.Name, ct)
            : await services.GetRequiredService<ContainerProvisioningService>().ProvisionDockerTransitionAsync(agent.Name, ct);
        if (!provisioned.Ok) return Hold(record, 502, "docker_transition_failed", provisioned.Message);
        return (record with { State = "revoked", Candidate = null, Errors = [] }, "docker", null, Result.Done("applied_docker"));
    }

    private async Task<(ManagedRecord, string?, int?, Result)> ApplyOpenShellAsync(Agent agent, ManagedRecord record,
        Func<ManagedRecord, Task> checkpoint, CancellationToken ct)
    {
        // Every prerequisite, including gateway client trust and generated inputs, precedes any old-runtime action.
        if (await DescribePreflightFaultAsync(agent, ct) is { } preflight) return (record, null, null, Result.Refused(409, preflight));
        string generated;
        try { generated = await GeneratedInputsAsync(agent, ct); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or JsonException)
        { return (record, null, null, Result.Refused(409, "managed_inputs_invalid")); }
        var (serviceSecrets, botId, credentialFault) = ServiceSecrets(agent, generated);
        if (credentialFault is not null) return (record, null, null, Result.Refused(409, credentialFault));
        IReadOnlyList<string> serviceProviders = [.. serviceSecrets.Select(s => s.Provider)];
        var policy = (await store.LoadPolicyAsync(agent.SandboxPolicyVersionId!.Value, ct))!;
        var generation = await host.GatewayGenerationAsync(ct);
        if (generation is null) return (record, null, null, Result.Refused(503, "gateway_unavailable"));
        var image = config["StockOpenShell:AgentImage"]!;

        // Reprovision and stopped/revoked rollback keep the verified sandbox and its persistent workspace.
        var reuse = agent.AppliedRuntimeKind == "openshell" && record.Applied is not null;
        if (reuse)
        {
            var old = record.Applied!;
            if (old.GatewayGeneration != generation) return (record, null, null, Result.Refused(409, "gateway_generation_changed"));
            if (old.Image != image) return (record, null, null, Result.Refused(409, "managed_image_change_requires_revoke"));
            if (await VerifyIdentityAsync(old, ct) is { } identity) return (record, null, null, identity);
        }
        var epoch = Guid.NewGuid();
        var candidate = reuse
            ? record.Applied! with { Epoch = epoch, PolicyVersionId = policy.Id, PolicySha256 = policy.Sha256, KeyRef = agent.ProviderApiKeyEnvRef!, GatewayGeneration = generation, ServiceProviders = serviceProviders }
            : new ManagedBinding($"fleet-{agent.Name}-{epoch.ToString("N")[..8]}", "", epoch, image,
                policy.Id, policy.Sha256, "api_key", agent.ProviderApiKeyEnvRef!, $"fleet-{agent.Name}-openai", generation) { ServiceProviders = serviceProviders };
        var applying = record with { State = "applying", Candidate = candidate, Errors = [] };
        await checkpoint(applying);

        var template = LoadTemplate()!;
        var epochDir = Path.Combine(ConfigDir, "managed", agent.Name, epoch.ToString("N"));
        var passwords = StockBrokerProvisioner.Roles.ToDictionary(r => r, _ => Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
        string bundle;
        try
        {
            WriteBrokerPasswords(agent.Name, passwords);
            WriteRecord(AuthorityPath(agent.Name), Authority(template, agent, reuse ? candidate.SandboxId : "pending", epoch, policy.Sha256, "revoked"));
            bundle = await BuildBundleAsync(agent, template, generated, epochDir, passwords["workload"], epoch, policy,
                reuse ? candidate.SandboxId : "pending", botId, serviceSecrets.Select(s => s.Value));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or JsonException)
        { return Hold(applying, 500, "managed_files_failed"); }
        var key = credentials.Get(agent.ProviderApiKeyEnvRef!);
        if (string.IsNullOrEmpty(key)) return Hold(applying, 409, "provider_key_reference_unresolved");
        var secret = new Dictionary<string, string> { ["OPENAI_API_KEY"] = key };

        // Every outside provider exists with its current value before create/attach; values only in the CLI env.
        async Task<string?> EnsureProvidersAsync()
        {
            if (await EnsureProviderAsync(candidate.Provider, "openai", secret, ct) is { } fault) return fault;
            foreach (var service in serviceSecrets)
                if (await EnsureProviderAsync(service.Provider, service.Profile, new Dictionary<string, string> { [service.Key] = service.Value }, ct) is { } serviceFault)
                    return serviceFault;
            return null;
        }

        if (reuse)
        {
            // Cut the old epoch (unless already revoked), then quiesce every writer before any input changes.
            var cut = record.State == "revoked" ? [] : await RevokeFanoutAsync(agent.Name, record, ct, stop: false);
            if (cut.Count > 0) return await HoldStoppedAsync(applying, candidate, "revoke_unresolved", cut, ct);
            if (await StopVerifiedAsync(candidate, ct) is { } quiet) return await HoldStoppedAsync(applying, candidate, quiet, [], ct);
        }
        else
        {
            if (agent.AppliedRuntimeKind == "docker")
            {
                if (await StopDockerAsync(agent) is { } dockerFault) return Hold(applying, 409, dockerFault);
                try { StageWorkspace(agent, Path.Combine(bundle, "workspace")); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Hold(applying, 500, "workspace_stage_failed"); }
            }
            if (await EnsureProvidersAsync() is { } providerFault) return Hold(applying, 502, providerFault);
            // Pinned contract: an explicit canonical launcher, staged; no --upload with a trailing command.
            var create = await cli.RunAsync([
                "sandbox", "create", "--name", candidate.SandboxName, "--from", candidate.Image,
                "--policy", Path.Combine(epochDir, "policy.yaml"), .. candidate.AllProviders().SelectMany(p => new[] { "--provider", p }),
                "--no-auto-providers",
                "--restart-policy", "never", "--label", "fleet.agent=" + agent.Name, "--label", "fleet.epoch=" + epoch.ToString("N"),
                "--env", "FLEET_STOCK_STAGED=1", "--detach", "--", Entrypoint], null, TimeSpan.FromMinutes(5), ct);
            // Whatever create reported, record any sandbox that now exists so cleanup keeps its identity.
            if (await GetAsync(candidate.SandboxName, ct) is { } made)
            {
                candidate = candidate with { SandboxId = made.Id };
                applying = applying with { Candidate = candidate };
                await checkpoint(applying);
            }
            if (create.ExitCode != 0 || create.TimedOut || string.IsNullOrEmpty(candidate.SandboxId))
                return string.IsNullOrEmpty(candidate.SandboxId) ? Hold(applying, 502, "sandbox_create_unknown")
                    : await HoldStoppedAsync(applying, candidate, "sandbox_create_unknown", [], ct);
        }

        // Staging: the launcher waits without a daemon, so transfers race no native task or file writer.
        if (await StageAsync(candidate, ct) is { } stage) return await HoldStoppedAsync(applying, candidate, stage, [], ct);
        // Reuse keeps the workspace but refreshes its MCP routes, which follow the current grants.
        foreach (var (local, remote) in reuse ? new[] { ("app", "/app"), ("trust", "/trust"), (Path.Combine("workspace", ".mcp.json"), "/workspace/.mcp.json") }
                     : [("app", "/app"), ("trust", "/trust"), ("workspace", "/workspace")])
            if (await UploadAsync(candidate, Path.Combine(bundle, local), remote, ct) is { } upload)
                return await HoldStoppedAsync(applying, candidate, upload, [], ct);
        if (reuse)
        {
            if (record.Applied!.PolicyVersionId != policy.Id)
            {
                var set = await cli.RunAsync(["policy", "set", candidate.SandboxName, "--policy", Path.Combine(epochDir, "policy.yaml"), "--wait"], null, LegTimeout, ct);
                if (set.ExitCode != 0 || set.TimedOut) return await HoldStoppedAsync(applying, candidate, "policy_update_unknown", [], ct);
            }
            if (await EnsureProvidersAsync() is { } providerFault) return await HoldStoppedAsync(applying, candidate, providerFault, [], ct);
            // The cut detached every old provider; attach exactly the current set (a dropped bot/journal stays detached).
            foreach (var provider in candidate.AllProviders())
            {
                var attach = await cli.RunAsync(["sandbox", "provider", "attach", candidate.SandboxName, provider], null, LegTimeout, ct);
                if (attach.ExitCode != 0 || attach.TimedOut) return await HoldStoppedAsync(applying, candidate, "provider_attach_unknown", [], ct);
            }
        }

        // Broker users exist only now; then the outside and guest enrollments carry the exact UUID/epoch.
        var created = await broker.CreateEpochAsync(epoch, agent.ShortName, ct, passwords);
        if (!created.Ok) return await HoldStoppedAsync(applying, candidate, created.Code!, [.. created.OwnedPartials], ct);
        try
        {
            var active = Authority(template, agent, candidate.SandboxId, epoch, policy.Sha256, "active");
            WriteRecord(AuthorityPath(agent.Name), active);
            WriteRecord(Path.Combine(bundle, "trust", "enrollment.json"), active);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return await HoldStoppedAsync(applying, candidate, "managed_files_failed", [], ct); }
        if (await UploadAsync(candidate, Path.Combine(bundle, "trust", "enrollment.json"), "/trust/enrollment.json", ct) is { } enrollment)
            return await HoldStoppedAsync(applying, candidate, enrollment, [], ct);
        if (await ActivateAsync(candidate, ct) is { } activate) return await HoldStoppedAsync(applying, candidate, activate, [], ct);
        return (record with { State = "applied", Applied = candidate, Candidate = null, Errors = [] }, "openshell", policy.Id, Result.Done("applied_openshell"));
    }

    /// <summary>Bare <c>--credential KEY</c> arguments: the CLI reads each value from its own environment.</summary>
    private async Task<string?> EnsureProviderAsync(string provider, string profile, IReadOnlyDictionary<string, string> secret, CancellationToken ct)
    {
        var exists = await cli.RunAsync(["provider", "get", provider], null, LegTimeout, ct);
        var keys = secret.Keys.Order(StringComparer.Ordinal).SelectMany(key => new[] { "--credential", key }).ToList();
        var run = await cli.RunAsync(exists.ExitCode == 0
            ? ["provider", "update", provider, .. keys]
            : ["provider", "create", "--name", provider, "--type", profile, .. keys], secret, LegTimeout, ct);
        return run.ExitCode == 0 && !run.TimedOut ? null : "provider_setup_unknown";
    }

    /// <summary>One outside service credential for its own stock provider; the value never leaves the CLI environment.</summary>
    private sealed record ServiceSecret(string Provider, string Profile, string Key, string Value)
    {
        public override string ToString() => Provider; // never the value
    }

    /// <summary>Journal capture exists exactly when ordinary provisioning would mint an ingest token.</summary>
    internal static bool JournalCapture(Agent agent) => agent.JournalEnabled && ContainerProvisioningService.HasTelegramBot(agent);

    /// <summary>Files/send exist exactly when ordinary provisioning enables them (both need capture).</summary>
    internal static bool JournalFiles(Agent agent) =>
        JournalCapture(agent) && (ContainerProvisioningService.HasFilesGrant(agent) || JournalGrants.SendEffective(agent));

    /// <summary>The agent has the ordinary comms-journal read MCP endpoint (ordinary provisioning mints its read token).</summary>
    internal static bool JournalRead(Agent agent) => agent.McpEndpoints.Any(e => e.McpName == StockJournalReadRoute.McpName);

    /// <summary>The ordinary endpoint's path (query dropped as ordinary provisioning does), or null when unroutable.</summary>
    private static string? JournalReadPath(Agent agent) =>
        agent.McpEndpoints.FirstOrDefault(e => e.McpName == StockJournalReadRoute.McpName) is { } endpoint
        && Uri.TryCreate(ContainerProvisioningService.WithoutQuery(endpoint.Url), UriKind.Absolute, out var uri)
        && Regex.IsMatch(uri.AbsolutePath, @"\A(?:/[A-Za-z0-9_-]{1,64}){1,8}\z") ? uri.AbsolutePath : null;

    private static string? JournalHost(JsonObject template) =>
        template["JournalHost"]?.GetValue<string>() is { Length: > 0 } host ? host : null;

    /// <summary>
    /// The agent's own bot token, read from the host env by its existing reference (never argv, DB or logs),
    /// and the public numeric bot id before ':' that Telegram.Bot validates. Neither is logged.
    /// </summary>
    private (string? Token, long BotId, string? Fault) TelegramCredential(Agent agent)
    {
        if (JournalGrants.BotRef(agent) is not { } reference || credentials.Get(reference) is not { Length: > 0 } token)
            return (null, 0, "telegram_bot_token_unresolved");
        var colon = token.IndexOf(':');
        return colon is >= 1 and <= 16 && colon < token.Length - 1 && long.TryParse(token.AsSpan(0, colon),
            System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id) && id > 0
            ? (token, id, null) : (null, 0, "telegram_bot_token_invalid");
    }

    /// <summary>
    /// Bot token and the same derived per-agent ingest token the ordinary path would carry (no switched
    /// identity), resolved before any old-runtime action. A missing value refuses with a named code.
    /// </summary>
    private (List<ServiceSecret> Secrets, long? BotId, string? Fault) ServiceSecrets(Agent agent, string generated)
    {
        var secrets = new List<ServiceSecret>();
        long? botId = null;
        if (ContainerProvisioningService.HasTelegramBot(agent))
        {
            var (token, id, fault) = TelegramCredential(agent);
            if (fault is not null) return ([], null, fault);
            secrets.Add(new($"fleet-{agent.Name}-telegram", TelegramProfile, StockCredentials.TelegramBotToken, token!));
            botId = id;
        }
        if (JournalCapture(agent))
        {
            string? ingest;
            try { ingest = JsonNode.Parse(File.ReadAllText(Path.Combine(generated, "appsettings.json")))?["Journal"]?["IngestToken"]?.GetValue<string>(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { ingest = null; }
            if (string.IsNullOrEmpty(ingest)) return ([], null, "journal_token_unresolved");
            secrets.Add(new($"fleet-{agent.Name}-journal", JournalProfile, StockCredentials.JournalIngestToken, ingest));
        }
        if (JournalRead(agent) || JournalFiles(agent))
        {
            // The same derived per-agent read token ordinary provisioning puts in the MCP header and, for
            // files/send, in Journal:ReadToken (no switched identity). Both must agree when both exist.
            string? header = null, files = null;
            try
            {
                if (JournalRead(agent))
                {
                    header = JsonNode.Parse(File.ReadAllText(Path.Combine(generated, ".mcp.json")))?["mcpServers"]?[StockJournalReadRoute.McpName]?["headers"]?["Authorization"]?.GetValue<string>();
                    header = header is { } value && value.StartsWith("Bearer ", StringComparison.Ordinal) ? value["Bearer ".Length..] : "";
                }
                if (JournalFiles(agent))
                    files = JsonNode.Parse(File.ReadAllText(Path.Combine(generated, "appsettings.json")))?["Journal"]?["ReadToken"]?.GetValue<string>() ?? "";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { return ([], null, "journal_token_unresolved"); }
            var read = header ?? files;
            if (string.IsNullOrWhiteSpace(read) || read.Any(char.IsWhiteSpace) || files is not null && files != read)
                return ([], null, "journal_token_unresolved");
            secrets.Add(new($"fleet-{agent.Name}-journal-read", JournalReadProfile, StockCredentials.JournalReadToken, read));
        }
        if (JournalFiles(agent) && JournalGrants.CrossChatEffective(agent))
        {
            string? cross;
            try { cross = JsonNode.Parse(File.ReadAllText(Path.Combine(generated, "appsettings.json")))?["Journal"]?["CrossChatToken"]?.GetValue<string>(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { cross = null; }
            if (string.IsNullOrWhiteSpace(cross) || cross.Any(char.IsWhiteSpace)) return ([], null, "journal_token_unresolved");
            secrets.Add(new($"fleet-{agent.Name}-journal-cross-chat", JournalCrossChatProfile, StockCredentials.JournalCrossChatToken, cross));
        }
        return (secrets, botId, null);
    }

    /// <summary>Any failure after staging still attempts one bounded, verified Stop and keeps every error.</summary>
    private async Task<(ManagedRecord, string?, int?, Result)> HoldStoppedAsync(ManagedRecord record, ManagedBinding binding,
        string code, IReadOnlyList<string> errors, CancellationToken ct)
    {
        var all = errors.ToList();
        if (await StopVerifiedAsync(binding, ct) is { } stop) all.Add("stop:" + stop);
        return Hold(record, 502, code, [.. all]);
    }

    /// <summary>Stop if needed, then confirm no workload/native-supervisor container remains.</summary>
    private async Task<string?> StopVerifiedAsync(ManagedBinding binding, CancellationToken ct)
    {
        var sandbox = await GetAsync(binding.SandboxName, ct);
        if (sandbox is null) return "sandbox_unobservable";
        if (sandbox.Value.Id != binding.SandboxId) return "sandbox_identity_changed";
        if (sandbox.Value.Phase is not ("Stopped" or "Completed" or "Error") && await LifecycleAsync("stop", binding, ct) is { } stop) return stop;
        return await QuiescentAsync(binding, ct);
    }

    /// <summary>Start into the staged launcher (no daemon) and wait until it reports staged.</summary>
    private async Task<string?> StageAsync(ManagedBinding binding, CancellationToken ct)
    {
        var sandbox = await GetAsync(binding.SandboxName, ct);
        if (sandbox is null) return "sandbox_unobservable";
        if (sandbox.Value.Id != binding.SandboxId) return "sandbox_identity_changed";
        // A just-created sandbox already runs the staged launcher; a stopped one boots into it.
        if (sandbox.Value.Phase is "Stopped" or "Completed" or "Error" && await LifecycleAsync("start", binding, ct) is { } start) return start;
        var until = DateTimeOffset.UtcNow + StopSettle;
        while (DateTimeOffset.UtcNow < until)
        {
            if (await ExecAsync(binding, "test -f \"$HOME/.fleet-staged\"", ct) == 0) return null;
            await Task.Delay(Poll, ct);
        }
        return "staging_unconfirmed";
    }

    /// <summary>
    /// The launcher writes the ready marker (its own PID) only after preparation succeeded, then execs
    /// the daemon under that PID; readiness is that PID actually running the daemon.
    /// </summary>
    internal const string DaemonReadyScript =
        "test ! -f \"$HOME/.fleet-staged\" && p=$(cat \"$HOME/.fleet-ready\") && [ -n \"$p\" ]"
        + " && grep -qa 'Fleet.Agent.dll' \"/proc/$p/cmdline\" && ! grep -qa 'prepare-stock-native' \"/proc/$p/cmdline\"";

    /// <summary>Writes this boot's activation; the launcher consumes it, prepares and starts the daemon.</summary>
    private async Task<string?> ActivateAsync(ManagedBinding binding, CancellationToken ct)
    {
        if (await ExecAsync(binding, "printf 1 > \"$HOME/.fleet-activate\"", ct) != 0) return "activation_unknown";
        var until = DateTimeOffset.UtcNow + StopSettle;
        while (DateTimeOffset.UtcNow < until)
        {
            if (await ExecAsync(binding, DaemonReadyScript, ct) == 0)
                return await VerifyIdentityAsync(binding, ct) is { } after ? after.Code : null;
            await Task.Delay(Poll, ct);
        }
        return "activation_unconfirmed";
    }

    private async Task<int> ExecAsync(ManagedBinding binding, string script, CancellationToken ct)
    {
        if (await VerifyIdentityAsync(binding, ct) is not null) return -1;
        var run = await cli.RunAsync(["sandbox", "exec", "-n", binding.SandboxName, "--", "sh", "-c", script], null, LegTimeout, ct);
        return run.TimedOut ? -1 : run.ExitCode;
    }

    private async Task<string?> UploadAsync(ManagedBinding binding, string local, string remote, CancellationToken ct)
    {
        if (await VerifyIdentityAsync(binding, ct) is { } identity) return identity.Code;
        var run = await cli.RunAsync(["sandbox", "upload", binding.SandboxName, local, remote], null, LegTimeout, ct);
        return run.ExitCode == 0 && !run.TimedOut ? null : "input_upload_unknown";
    }

    private async Task<(ManagedRecord, string?, int?, Result)> StartCoreAsync(Agent agent, ManagedRecord record,
        Func<ManagedRecord, Task> checkpoint, CancellationToken ct)
    {
        if (agent.AppliedRuntimeKind != "openshell" || record.Applied is not { } binding)
            return (record, null, null, Result.Refused(409, "managed_binding_missing"));
        // Unresolved holds survive Stop; only a clean revoke/apply reconciles them.
        if (record.State is "needs_attention" or "revoked" || record.Candidate is not null)
            return (record, null, null, Result.Refused(409, record.Candidate is not null ? "unresolved_candidate" : record.State, record.Errors));
        if (await GenerationFaultAsync(binding, ct) is { } generation) return await HoldForGenerationAsync(agent, record, generation, ct);
        // A repeat Start on an applied, running binding never re-stages or stops it: success when
        // Ready, otherwise a mutation-free refusal.
        if (record.State == "applied" && await GetAsync(binding.SandboxName, ct) is { } live && live.Id == binding.SandboxId
            && live.Phase is not ("Stopped" or "Completed" or "Error"))
            return (record, null, null, live.Phase == "Ready" ? Result.Done("started") : Result.Refused(409, "sandbox_not_stopped"));
        // Every boot is staged; the daemon starts only with this boot's activation.
        if ((await StageAsync(binding, ct) ?? await ActivateAsync(binding, ct)) is { } fault)
            return await HoldStoppedAsync(record, binding, fault, [], ct);
        return (record with { State = "applied", Errors = [] }, null, null, Result.Done("started"));
    }

    private async Task<(ManagedRecord, string?, int?, Result)> StopCoreAsync(Agent agent, ManagedRecord record,
        Func<ManagedRecord, Task> checkpoint, CancellationToken ct)
    {
        if (agent.AppliedRuntimeKind != "openshell" || record.Applied is not { } binding)
            return (record, null, null, Result.Refused(409, "managed_binding_missing"));
        if (await GenerationFaultAsync(binding, ct) is { } generation) return await HoldForGenerationAsync(agent, record, generation, ct);
        var errors = new List<string>();
        if (await LifecycleAsync("stop", binding, ct) is { } fault) errors.Add(fault);
        else if (await QuiescentAsync(binding, ct) is { } quiet) errors.Add(quiet);
        if (errors.Count > 0) return Hold(record, 502, "stop_unresolved", [.. errors]);
        // A successful Stop never clears an earlier unresolved hold or its errors.
        return record.State == "needs_attention" || record.Candidate is not null
            ? (record, null, null, Result.Done("stopped_held"))
            : (record with { State = "stopped", Errors = [] }, null, null, Result.Done("stopped"));
    }

    private async Task<(ManagedRecord, string?, int?, Result)> RevokeCoreAsync(Agent agent, ManagedRecord record,
        Func<ManagedRecord, Task> checkpoint, CancellationToken ct)
    {
        if (record.Applied is null && record.Candidate is null) return (record, null, null, Result.Refused(409, "managed_binding_missing"));
        var errors = await RevokeFanoutAsync(agent.Name, record, ct);
        return errors.Count == 0
            ? (record with { State = "revoked", Candidate = null, Errors = [] }, null, null, Result.Done("revoked"))
            : Hold(record, 502, "revoke_unresolved", [.. errors]);
    }

    /// <summary>Changed/unobservable generation: hold, then one bounded revoke/Stop fanout; never Start.</summary>
    private async Task<(ManagedRecord, string?, int?, Result)> HoldForGenerationAsync(Agent agent, ManagedRecord record, string generation, CancellationToken ct)
    {
        var errors = generation == "gateway_generation_changed" ? await RevokeFanoutAsync(agent.Name, record, ct) : [];
        return Hold(record, 409, generation, [.. errors]);
    }

    private async Task<string?> GenerationFaultAsync(ManagedBinding binding, CancellationToken ct) =>
        await host.GatewayGenerationAsync(ct) is not { } current ? "gateway_unavailable"
            : current == binding.GatewayGeneration ? null : "gateway_generation_changed";

    // ── shared legs ────────────────────────────────────────────────────────

    /// <summary>
    /// Admission revoke, provider detach, broker revoke/connection close and one normal Stop for each
    /// verified binding, as independent legs with their own deadline so a hung leg never suppresses
    /// Stop. Returns every unresolved error; an empty list is the only clean result.
    /// </summary>
    internal async Task<List<string>> RevokeFanoutAsync(string agentName, ManagedRecord record, CancellationToken ct, bool stop = true)
    {
        var errors = new List<string>();
        var targets = new[] { record.Applied, record.Candidate }.OfType<ManagedBinding>().DistinctBy(b => b.Epoch).ToList();
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
            legs.Add(Leg("broker:" + target.Epoch.ToString("N"), async token =>
            {
                var revoked = await broker.RevokeEpochAsync(target.Epoch, token);
                return revoked.Ok ? null : string.Join(",", revoked.OwnedPartials.DefaultIfEmpty(revoked.Code ?? "broker_revoke_unresolved"));
            }));
            if (string.IsNullOrEmpty(target.SandboxId)) { errors.Add("unverified_target:" + target.SandboxName); continue; }
        }
        foreach (var target in targets.Where(t => !string.IsNullOrEmpty(t.SandboxId)).DistinctBy(t => t.SandboxId))
        {
            legs.Add(Leg("detach:" + target.SandboxName, async token =>
            {
                if (await VerifyIdentityAsync(target, token) is { } identity) return identity.Code;
                // Every attached provider is detached; one failure does not skip the others.
                var unresolved = false;
                foreach (var provider in target.AllProviders())
                {
                    var run = await cli.RunAsync(["sandbox", "provider", "detach", target.SandboxName, provider], null, LegTimeout, token);
                    unresolved |= run.ExitCode != 0 || run.TimedOut;
                }
                return unresolved ? "provider_detach_unknown" : null;
            }));
            if (stop) legs.Add(Leg("stop:" + target.SandboxName, async token =>
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
            // The leg's own deadline can cancel the work before the delay continuation wins the race.
            catch (OperationCanceledException) when (deadline.IsCancellationRequested) { return label + ":timeout"; }
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

    /// <summary>Exact-name Docker observation: failure is never read as absence.</summary>
    private async Task<string?> StopDockerAsync(Agent agent)
    {
        var states = await host.DockerContainerStatesAsync(agent.ContainerName, CancellationToken.None);
        if (states is null) return "docker_observation_failed";
        if (states.All(s => s is "exited" or "created" or "dead")) return null;
        var stop = DockerStop ?? (name => services.GetRequiredService<DockerService>().StopContainerAsync(name));
        if (!await stop(agent.ContainerName)) return "docker_stop_failed";
        var after = await host.DockerContainerStatesAsync(agent.ContainerName, CancellationToken.None);
        if (after is null) return "docker_observation_failed";
        return after.All(s => s is "exited" or "created" or "dead") ? null : "docker_quiescence_pending";
    }

    // ── workspace: preserved across reprovision and runtime switches ──────

    private string DockerWorkspaceDir(Agent agent) => DockerWorkspace?.Invoke(agent)
        ?? Path.Combine(config["Provisioning:BaseDir"] ?? throw new InvalidOperationException("Provisioning:BaseDir is required"), "workspaces", agent.ContainerName);

    /// <summary>The Docker workspace without account/control state, into the bundle's /workspace.</summary>
    private void StageWorkspace(Agent agent, string target)
    {
        var source = DockerWorkspaceDir(agent);
        if (Directory.Exists(source)) CopyTree(source, target, PrivateWorkspaceEntries);
        WriteText(Path.Combine(target, ".mcp.json"), WorkloadMcp(LoadTemplate()!, agent));
    }

    /// <summary>
    /// Copies the running sandbox's /workspace into <paramref name="target"/>, control state excluded.
    /// The pinned CLI extracts the directory's contents straight into the destination; no re-rooting.
    /// </summary>
    private async Task<string?> DownloadWorkspaceAsync(Agent agent, ManagedBinding binding, string target, CancellationToken ct)
    {
        var staging = Path.Combine(ConfigDir, "managed", agent.Name, binding.Epoch.ToString("N"), "workspace-out");
        try
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            PrivateDirectory(staging);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return "workspace_stage_failed"; }
        if (await VerifyIdentityAsync(binding, ct) is { } before) return before.Code;
        var download = await cli.RunAsync(["sandbox", "download", binding.SandboxName, "/workspace", staging], null, TimeSpan.FromMinutes(5), ct);
        if (download.ExitCode != 0 || download.TimedOut) return "workspace_download_unknown";
        if (await VerifyIdentityAsync(binding, ct) is { } after) return after.Code;
        try { CopyTree(staging, target, PrivateWorkspaceEntries); return null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return "workspace_restore_failed"; }
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
            // Optional receiving hosts of non-memory first-party services and the journal listener (#492):
            // exact DNS names, distinct from memory and each other.
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { node["MemoryHost"]!.GetValue<string>() };
            if (node["ServiceHosts"] is { } hosts)
            {
                foreach (var (service, host) in hosts.AsObject())
                    if (!StockServices.McpNames.ContainsKey(service) || host?.GetValue<string>() is not { } name
                        || Uri.CheckHostName(name) != UriHostNameType.Dns || !seen.Add(name)) return null;
            }
            if (node["JournalHost"] is { } journal && (journal.GetValue<string>() is not { } journalHost
                || Uri.CheckHostName(journalHost) != UriHostNameType.Dns || !seen.Add(journalHost))) return null;
            return node;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException
            or ArgumentException or StockIdentityDenied) { return null; }
    }

    private static string? TemplateHost(JsonObject template, string service) =>
        template["ServiceHosts"]?[service]?.GetValue<string>() is { Length: > 0 } host ? host : null;

    internal static JsonObject Authority(JsonObject template, Agent agent, string sandboxId, Guid epoch, string policySha, string state)
    {
        var record = new JsonObject
        {
            ["State"] = state,
            ["Agent"] = agent.Name,
            ["SandboxId"] = sandboxId,
            ["Epoch"] = epoch.ToString(),
            ["ExpiresAt"] = DateTimeOffset.UtcNow.AddDays(365).ToString("O"),
        };
        foreach (var field in new[] { "GatewayIssuer", "GatewayAudience", "GatewayJwksPath", "AssertionPrivateKeyPath", "AssertionPublicKeyPath", "MemoryHost", "BrokerHost" })
            record[field] = template[field]!.GetValue<string>();
        record["StockCommit"] = StockCommit;
        record["PolicySha256"] = policySha;
        if (ServiceRoutes(template, agent) is { Count: > 0 } services) record["Services"] = services;
        if (JournalRead(agent) && JournalHost(template) is { } journalHost && JournalReadPath(agent) is { } path)
        {
            // Exactly the agent's enabled grants on the ordinary endpoint name; nothing implicit or widened.
            const string prefix = "mcp__" + StockJournalReadRoute.McpName + "__";
            var tools = agent.Tools.Where(t => t.IsEnabled && t.ToolName.StartsWith(prefix, StringComparison.Ordinal))
                .Select(t => t.ToolName[prefix.Length..]).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
            record["JournalRead"] = new JsonObject
            {
                ["Host"] = journalHost, ["Path"] = path, ["Tools"] = new JsonArray([.. tools.Select(t => (JsonNode)t)]),
            };
        }
        return record;
    }

    /// <summary>
    /// One route per granted first-party service endpoint: the operator's receiving host and exactly the
    /// agent's enabled DB tool grants on that endpoint (existing names, no implicit or widened tools).
    /// Telegram also carries the agent's formatting mode, which its receiver appends after attribution.
    /// </summary>
    private static JsonObject ServiceRoutes(JsonObject template, Agent agent)
    {
        var routes = new JsonObject();
        foreach (var (service, mcpName) in StockServices.McpNames.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (!agent.McpEndpoints.Any(e => e.McpName == mcpName) || TemplateHost(template, service) is not { } host) continue;
            var prefix = "mcp__" + mcpName + "__";
            var tools = agent.Tools.Where(t => t.IsEnabled && t.ToolName.StartsWith(prefix, StringComparison.Ordinal))
                .Select(t => t.ToolName[prefix.Length..]).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
            routes[service] = new JsonObject
            {
                ["Host"] = host,
                ["Tools"] = new JsonArray([.. tools.Select(t => (JsonNode)t)]),
                ["Query"] = service == "telegram" ? $"formatting_mode={Math.Clamp((int)agent.FormattingMode, 0, 2)}" : "",
            };
        }
        return routes;
    }

    /// <summary>The workload's MCP servers: memory plus each routed service under its ordinary endpoint name.</summary>
    private static string WorkloadMcp(JsonObject template, Agent agent)
    {
        var servers = new JsonObject { ["memory"] = new JsonObject { ["url"] = $"https://{template["MemoryHost"]!.GetValue<string>()}/mcp" } };
        foreach (var (service, route) in ServiceRoutes(template, agent))
            servers[StockServices.McpNames[service]] = new JsonObject { ["url"] = $"https://{route!["Host"]!.GetValue<string>()}/mcp" };
        // No header here: the agent adds only the stock placeholder from its environment to its native config.
        if (JournalRead(agent) && JournalHost(template) is { } journalHost && JournalReadPath(agent) is { } path)
            servers[StockJournalReadRoute.McpName] = new JsonObject { ["url"] = $"https://{journalHost}{path}" };
        // The agent-local files/send listener, exactly as ordinary provisioning names it.
        if (JournalFiles(agent))
            servers[StockCredentials.JournalFilesMcpName] = new JsonObject { ["url"] = StockCredentials.JournalFilesUrl };
        return new JsonObject { ["mcpServers"] = servers }.ToJsonString();
    }

    private void WriteBrokerPasswords(string agent, IReadOnlyDictionary<string, string> passwords)
    {
        foreach (var role in new[] { "control", "observer" })
            WriteText(Path.Combine(AuthorityDir, $"{agent}.{role}.password"), passwords[role]);
    }

    /// <summary>The ordinary generated inputs, validated before any old-runtime Stop.</summary>
    private async Task<string> GeneratedInputsAsync(Agent agent, CancellationToken ct)
    {
        var generated = GenerateInputs is { } generate ? await generate(agent.Name, ct)
            : await services.GetRequiredService<ContainerProvisioningService>().GenerateManagedInputsAsync(agent.Name, ct);
        RefuseLinks(generated);
        if (!File.Exists(Path.Combine(generated, Fleet.Shared.HarnessDefaults.CodexRootTomlFileName))) throw new InvalidOperationException("codex_root_missing");
        if (JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(generated, "appsettings.json"), ct))?["Agent"] is not JsonObject)
            throw new InvalidOperationException("generated_agent_section_missing");
        return generated;
    }

    private async Task<string> BuildBundleAsync(Agent agent, JsonObject template, string generated, string epochDir, string workloadPassword,
        Guid epoch, SandboxPolicyVersion policy, string sandboxId, long? botId, IEnumerable<string> outsideSecrets)
    {
        var bundle = Path.Combine(epochDir, "bundle");
        PrivateDirectory(Path.Combine(bundle, "app"));
        PrivateDirectory(Path.Combine(bundle, "trust"));
        PrivateDirectory(Path.Combine(bundle, "workspace"));
        WriteText(Path.Combine(epochDir, "policy.yaml"), policy.Yaml);

        var settings = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(generated, "appsettings.json")))!.AsObject();
        settings["StockOpenShell"] = new JsonObject
        {
            ["Enabled"] = true,
            ["EnrollmentFile"] = "/trust/enrollment.json",
            ["AuthorityFile"] = "/trust/enrollment.json",
            ["BrokerPasswordFile"] = "/trust/workload-broker-password",
        };
        var agentNode = settings["Agent"]!.AsObject();
        agentNode["NativeAuthMode"] = "api_key";
        agentNode["ProviderApiKeyEnvRef"] = agent.ProviderApiKeyEnvRef;
        agentNode["WorkDir"] = "/workspace";
        (settings["RabbitMq"] ??= new JsonObject())["Host"] = template["BrokerHost"]!.GetValue<string>();
        // #492: credential slots stay empty. The agent fills them from the placeholders stock issues for
        // the attached providers; it gets only the public bot id and the journal listener's route.
        (settings["Telegram"] as JsonObject)?.Remove("BotToken");
        if (botId is { } id) settings["StockOpenShell"]!["TelegramBotId"] = id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (settings["Journal"] is JsonObject journal)
        {
            foreach (var slot in new[] { "IngestToken", "ReadToken", "CrossChatToken" }) journal.Remove(slot);
            if (JournalCapture(agent))
            {
                journal["BaseUrl"] = $"https://{JournalHost(template)}";
                settings["StockOpenShell"]!["JournalCapture"] = true;
            }
        }
        var workloadSettings = settings.ToJsonString();
        if (outsideSecrets.Any(secret => workloadSettings.Contains(secret, StringComparison.Ordinal)))
            throw new InvalidOperationException("managed_secret_in_workload_inputs");
        WriteText(Path.Combine(bundle, "app", "appsettings.json"), workloadSettings);
        File.Copy(Path.Combine(generated, Fleet.Shared.HarnessDefaults.CodexRootTomlFileName),
            Path.Combine(bundle, "app", Fleet.Shared.HarnessDefaults.CodexRootTomlFileName), overwrite: true);
        foreach (var dir in new[] { "roles", "projects" })
            if (Directory.Exists(Path.Combine(generated, dir))) CopyTree(Path.Combine(generated, dir), Path.Combine(bundle, "app", dir), []);
        WriteText(Path.Combine(bundle, "workspace", ".mcp.json"), WorkloadMcp(template, agent));
        WriteRecord(Path.Combine(bundle, "trust", "enrollment.json"), Authority(template, agent, sandboxId, epoch, policy.Sha256, "revoked"));
        WriteText(Path.Combine(bundle, "trust", "workload-broker-password"), workloadPassword);
        return bundle;
    }

    /// <summary>Refuses any symlinked file or directory in a tree before any recursive privileged copy.</summary>
    internal static void RefuseLinks(string root)
    {
        if (new DirectoryInfo(root).LinkTarget is not null) throw new IOException("symlink_refused");
        foreach (var entry in new DirectoryInfo(root).EnumerateFileSystemInfos("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }))
            if (entry.LinkTarget is not null) throw new IOException("symlink_refused");
    }

    /// <summary>Refuses symlinks in the source tree, on either root's ancestors, or at any destination entry.</summary>
    private static void CopyTree(string source, string target, IReadOnlyCollection<string> exclude)
    {
        RefuseLinkedAncestors(source);
        RefuseLinks(source);
        RefuseLinkedAncestors(target);
        Copy(source, target, top: true);

        void Copy(string from, string to, bool top)
        {
            if (new DirectoryInfo(to).LinkTarget is not null) throw new IOException("symlink_refused");
            Directory.CreateDirectory(to);
            foreach (var dir in Directory.GetDirectories(from))
                if (!(top && exclude.Contains(Path.GetFileName(dir)))) Copy(dir, Path.Combine(to, Path.GetFileName(dir)), false);
            foreach (var file in Directory.GetFiles(from))
            {
                if (top && exclude.Contains(Path.GetFileName(file))) continue;
                var dest = Path.Combine(to, Path.GetFileName(file));
                if (new FileInfo(dest).LinkTarget is not null) throw new IOException("symlink_refused");
                File.Copy(file, dest, overwrite: true);
            }
        }
    }

    /// <summary>Every existing component of the path, up to the root, must be a real directory.</summary>
    internal static void RefuseLinkedAncestors(string path)
    {
        for (var dir = new DirectoryInfo(Path.GetFullPath(path)); dir is not null; dir = dir.Parent)
            if (dir.LinkTarget is not null) throw new IOException("symlink_refused");
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
