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
        // setsid is the launched process, so its start never fails for an absent CLI: check the CLI itself.
        if (leader && !CliExists(cliPath)) return new(-1, "", Missing: true);
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
            // setsid exits 127 when it cannot exec the CLI (removed after the check above).
            if (leader && process.ExitCode == 127 && !CliExists(cliPath)) return new(-1, "", Missing: true);
            // Over-limit output is never parsed as a partial document.
            return output is null ? new(-1, "") : new(process.ExitCode, output);
        }
    }

    private const string SetsidPath = "/usr/bin/setsid";

    /// <summary>An explicit path must be an existing file; a bare name must resolve on PATH.</summary>
    internal static bool CliExists(string cliPath)
    {
        if (cliPath.Contains('/')) return File.Exists(cliPath);
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries))
            if (File.Exists(Path.Combine(dir, cliPath))) return true;
        return false;
    }

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

    /// <summary>Every container labelled with the stock UUID (any state) and its isolation role; null when unobservable.</summary>
    Task<IReadOnlyList<StockSandboxContainer>?> SandboxContainersAsync(string sandboxId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<StockSandboxContainer>?>(null);
}

/// <summary>One Docker container of a stock sandbox: full ID, Docker state and <c>openshell.ai/isolation-role</c>.</summary>
public sealed record StockSandboxContainer(string Id, string State, string? Role);

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

    public async Task<IReadOnlyList<StockSandboxContainer>?> SandboxContainersAsync(string sandboxId, CancellationToken ct)
    {
        var containers = await docker.ListLabelledContainersAsync("openshell.ai/sandbox-id=" + sandboxId, "openshell.ai/isolation-role", ct);
        return containers?.Select(c => new StockSandboxContainer(c.Id, c.State, c.LabelValue)).ToList();
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

    /// <summary>Last observed Docker identities of the workload and native supervisor (#492 §7/§11); absent in older records.</summary>
    public ManagedContainers? Containers { get; init; }

    /// <summary>
    /// #502: the named stock workspace every resource call for this sandbox carries (<c>--workspace</c>). Null for
    /// API-key and older bindings, whose calls keep the existing ambient scope.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? SandboxWorkspace { get; init; }

    /// <summary>#502: the outside subscription identity, captured before any effect; null for API-key bindings.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public ManagedSubscription? Subscription { get; init; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsSubscription => AuthMode == StockSubscriptionBinding.Mode;
}

/// <summary>
/// #502 persisted subscription identity: provider workspace/UUID, selected account, local owner generation, source digest
/// and expiry. Cleanup uses these and the binding's sandbox workspace, never the current outside file or CLI default.
/// </summary>
public sealed record ManagedSubscription(string ProviderWorkspace, string ProviderId, string AccountId, Guid OwnerGeneration,
    string SourceSha256, DateTimeOffset ExpiresAt);

/// <summary>
/// #502 inert custody reference to an atomically retired subscription binding archived under
/// <c>managed/&lt;agent&gt;/retired/&lt;epoch&gt;.json</c>. Never a dispatch, attach or start input.
/// </summary>
public sealed record RetiredSubscription(Guid Epoch, string SandboxWorkspace, string SandboxId, string ProviderWorkspace,
    string ProviderId, Guid OwnerGeneration, string ArchivePath, string ArchiveSha256, DateTimeOffset RetiredAt);

/// <summary>
/// Docker container IDs of a binding's workload and native supervisor plus their observed status:
/// running · stopped · removed (no labelled container left) · unobservable. Identifiers only.
/// </summary>
public sealed record ManagedContainers(string? WorkloadId, string? SupervisorId, string Status, DateTimeOffset ObservedAt)
{
    /// <summary><c>openshell.ai/isolation-role</c> values at the pinned stock commit (openshell-driver-docker).</summary>
    public const string WorkloadRole = "sandbox";
    public const string SupervisorRole = "supervisor";

    /// <summary>Folds one label read into the record; a failed or empty read keeps the last known IDs.</summary>
    public static ManagedContainers From(IReadOnlyList<StockSandboxContainer>? containers, ManagedContainers? previous, DateTimeOffset now)
    {
        if (containers is null) return new(previous?.WorkloadId, previous?.SupervisorId, "unobservable", now);
        if (containers.Count == 0) return new(previous?.WorkloadId, previous?.SupervisorId, "removed", now);
        // Exact role values of the pinned stock Docker driver: "sandbox" is the workload, "supervisor" the
        // native supervisor; "staging", "identity" or any other role is neither, never a workload ID.
        var ordered = containers.OrderBy(c => c.Id, StringComparer.Ordinal).ToList();
        var running = containers.Any(c => c.State is "running" or "restarting" or "paused");
        return new(ordered.FirstOrDefault(c => c.Role == WorkloadRole)?.Id ?? previous?.WorkloadId,
            ordered.FirstOrDefault(c => c.Role == SupervisorRole)?.Id ?? previous?.SupervisorId, running ? "running" : "stopped", now);
    }
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
    /// <summary>
    /// Revoked, verified-stopped earlier bindings of this agent replaced by an image-change re-apply (review A7b).
    /// They stay tracked for custody, with their sandbox UUIDs; nothing deletes their sandboxes.
    /// </summary>
    public IReadOnlyList<ManagedBinding> Superseded { get; init; } = [];
    /// <summary>#502: archive references of atomically retired subscription bindings (inert history, never rehydrated).</summary>
    public IReadOnlyList<RetiredSubscription> RetiredSubscriptions { get; init; } = [];
    public IReadOnlyList<string> Errors { get; init; } = [];

    /// <summary>#502: every nonretired subscription tuple still in this record (applied, candidate or superseded).</summary>
    public IEnumerable<ManagedBinding> SubscriptionTuples() =>
        new[] { Applied, Candidate }.OfType<ManagedBinding>().Concat(Superseded).Where(b => b.IsSubscription);
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
    /// <summary>Review A7: workspace bundle upload and download share one realistic bound (not the 30 s CLI leg).</summary>
    internal TimeSpan TransferTimeout { get; init; } = TimeSpan.FromMinutes(5);
    /// <summary>Test seams; production uses the ordinary generator, Docker service and provisioning path.</summary>
    internal Func<string, CancellationToken, Task<string>>? GenerateInputs { get; init; }
    internal Func<string, Task<bool>>? DockerStop { get; init; }
    internal Func<string, CancellationToken, Task<(bool Ok, string Message)>>? ProvisionDocker { get; init; }
    internal Func<Agent, string>? DockerWorkspace { get; init; }
    /// <summary>#502 seams: the effective uid the outside binding owner must equal, and the UTC clock for expiry/refresh windows.</summary>
    internal Func<uint>? EffectiveUid { get; init; }
    internal TimeProvider Clock { get; init; } = TimeProvider.System;
    private DateTimeOffset Now => Clock.GetUtcNow();

    public sealed record Result(bool Ok, int Status, string Code, IReadOnlyList<string> Errors)
    {
        public static Result Done(string code) => new(true, 200, code, []);
        public static Result Refused(int status, string code, IReadOnlyList<string>? errors = null) => new(false, status, code, errors ?? []);
        public string Message => Errors.Count == 0 ? Code : Code + ": " + string.Join(", ", Errors);
    }

    public bool Enabled => config.GetValue<bool>("StockOpenShell:ManagedEnabled");

    /// <summary>
    /// #502: a subscription binding must carry valid persisted sandbox and provider scopes; without them no resource command
    /// may run at all (never the ambient workspace). Null for API-key/legacy bindings and valid subscription bindings.
    /// </summary>
    internal static string? ScopeFault(ManagedBinding binding) =>
        !binding.IsSubscription ? null
        : StockSubscriptionBinding.ValidWorkspace(binding.SandboxWorkspace) && binding.Subscription is { } s
            && StockSubscriptionBinding.ValidWorkspace(s.ProviderWorkspace) ? null : StockSubscriptionBinding.Unrecoverable;

    /// <summary>
    /// #502: every resource call for a subscription binding is prefixed with its persisted <c>--workspace</c>; API-key and
    /// older bindings keep their existing ambient arguments unchanged.
    /// </summary>
    internal static IReadOnlyList<string> Scoped(ManagedBinding binding, IReadOnlyList<string> args)
    {
        if (!binding.IsSubscription) return args;
        if (ScopeFault(binding) is not null) throw new InvalidOperationException(StockSubscriptionBinding.Unrecoverable);
        return ["--workspace", binding.SandboxWorkspace!, .. args];
    }

    private static IReadOnlyList<string> InWorkspace(string workspace, IReadOnlyList<string> args) => ["--workspace", workspace, .. args];

    /// <summary>True when apply/lifecycle for this agent belongs to the managed adapter rather than Docker.</summary>
    public static bool Owns(Agent agent) => agent.RuntimeKind == "openshell" || agent.AppliedRuntimeKind == "openshell";

    private string ConfigDir => config["StockOpenShell:ConfigDir"] ?? "";

    /// <summary>
    /// The stock image's non-root HOME (uid 10001); /app and / are not writable there, so every input lands
    /// under it. Pinned 0.1.5 <c>sandbox upload NAME DIR DEST</c> extracts at <c>DEST/basename(DIR)/</c>, so
    /// the bundle's <c>app</c>/<c>trust</c> go to HOME and <c>workspace</c> to <c>/</c>.
    /// </summary>
    internal string WorkloadHome => (config["StockOpenShell:WorkloadHome"] is { Length: > 0 } home ? home : "/home/agent").TrimEnd('/');
    internal string WorkloadAppRoot => WorkloadHome + "/app";
    internal string WorkloadEnrollmentFile => WorkloadHome + "/trust/enrollment.json";
    private string AuthorityDir => Path.Combine(ConfigDir, "managed", "authority");
    /// <summary>Orchestrator-only observer credentials (review W4); never mounted into another service.</summary>
    private string ObserverDir => StockAuthoritySource.ObserverDirectory(AuthorityDir);
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
    // Review A8: Start is a lifecycle action like Stop: a client disconnect never cancels it midway.
    public Task<Result> StartAsync(string name, CancellationToken ct) => RunAsync(name, "start", Bounded(StartCoreAsync), CancellationToken.None);
    // Stop and revoke are cleanup: every leg has its own bounded deadline, and a caller (client)
    // disconnect never cancels them midway. Review A8: Start/Stop also share one overall bound across legs.
    public Task<Result> StopAsync(string name, CancellationToken ct) => RunAsync(name, "stop", Bounded(StopCoreAsync), CancellationToken.None);
    public Task<Result> RevokeAsync(string name, CancellationToken ct) => RunAsync(name, "revoke", RevokeCoreAsync, CancellationToken.None);

    /// <summary>
    /// The step's legs run under their own <see cref="FanoutTimeout"/> deadline, never the caller's token; an expiry
    /// mid-step ends as the envelope's held <c>KIND_unknown</c> (bounded staging/activation faults still Stop first).
    /// </summary>
    private Step Bounded(Step step) => async (agent, record, checkpoint, _) =>
    {
        using var deadline = new CancellationTokenSource(FanoutTimeout);
        return await step(agent, record, checkpoint, deadline.Token);
    };

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
        var run = await cli.RunAsync(Scoped(binding, ["sandbox", "exec", "-n", binding.SandboxName, "--", "bash", "-c", script, "cancel", path]), null, LegTimeout, ct);
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
        var run = await cli.RunAsync(Scoped(binding, ["logs", binding.SandboxName, "-n", Math.Clamp(lines, 1, 1000).ToString()]), null, LegTimeout, ct);
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
        if (await host.GatewayGenerationAsync(ct) is { } current && current != binding.GatewayGeneration
            // A durably stopped, quiescent binding is a planned restart: Start/apply rebinds it after its checks.
            // Review A5: an unobservable sandbox or quiescence (gateway not ready yet) is no proof of an unexpected one.
            && !(record.State == "stopped" && await StoppedQuiescentAsync(binding, ct) is null or "sandbox_unobservable" or "quiescence_unobservable"))
            await HandleUnexpectedRestartAsync(name, ct);
    }

    private Task<Result> HandleUnexpectedRestartAsync(string name, CancellationToken ct) =>
        RunAsync(name, "restart_hold", async (agent, record, _, token) => record.Applied is null
            ? (record, null, null, Result.Refused(409, "managed_binding_missing"))
            // Review A4: an already held or revoked record is never fanned out again, and the logs/cancel guard gets
            // the status refresh's exemption for a durably stopped, quiescent (or not yet observable) binding.
            : record.State is "needs_attention" or "revoked" || record.State == "stopped"
                && await StoppedQuiescentAsync(record.Applied, token) is null or "sandbox_unobservable" or "quiescence_unobservable"
                ? (record, null, null, Result.Refused(409, "gateway_generation_changed"))
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
        var subscription = 0;
        foreach (var agent in agents)
        {
            // #502: desired subscription or ANY nonretired applied/candidate/superseded subscription tuple counts; only
            // atomically retired references are inert. An unreadable record counts everywhere (never read as none).
            try
            {
                var record = ManagedRecord.Parse(agent.AppliedRuntimeBinding);
                if (record.Unresolved) unresolved++;
                if (agent.NativeAuthMode == StockSubscriptionBinding.Mode || record.SubscriptionTuples().Any()) subscription++;
            }
            catch (JsonException) { unresolved++; subscription++; }
        }
        return new
        {
            desiredOpenShell = agents.Count(a => a.RuntimeKind == "openshell"),
            appliedOpenShell = agents.Count(a => a.AppliedRuntimeKind == "openshell"),
            unresolvedBindings = unresolved,
            subscriptionBindings = subscription,
        };
    }

    /// <summary>
    /// Named blocker before any external mutation or old-runtime Stop, or null when apply may
    /// proceed: identity/auth selection, routes, trust/config, broker HTTPS auth, pinned provider
    /// profile hashes, CLI version and gateway observation.
    /// </summary>
    public async Task<string?> DescribePreflightFaultAsync(Agent agent, CancellationToken ct) => (await PreflightAsync(agent, ct)).Fault;

    private async Task<(string? Fault, StockSubscriptionSource? Source)> PreflightAsync(Agent agent, CancellationToken ct)
    {
        if (!Enabled) return ("managed_runtime_disabled", null);
        if (agent.RuntimeKind != "openshell") return (null, null); // OpenShell→Docker needs no managed prerequisites beyond stop.
        var subscription = agent.NativeAuthMode == StockSubscriptionBinding.Mode;
        if (agent.Provider != "codex" || agent.MountDockerSock || agent.LocalBaseUrl is not null
            || (subscription ? agent.ProviderApiKeyEnvRef is not null : agent.NativeAuthMode != "api_key" || string.IsNullOrEmpty(agent.ProviderApiKeyEnvRef)))
            return ("openshell_requires_codex_api_key_policy_and_no_socket", null);
        if (agent.Name != agent.ShortName || agent.Name != agent.Name.ToLowerInvariant() || !Regex.IsMatch(agent.Name, @"\A[a-z0-9][a-z0-9-]{0,40}\z"))
            return ("managed_agent_name_unsupported", null);
        var policy = agent.SandboxPolicyVersionId is { } id ? await store.LoadPolicyAsync(id, ct) : null;
        if (policy is null) return ("sandbox_policy_version_not_found", null);
        return subscription ? await SubscriptionPreflightAsync(agent, policy, ct) : (await ApiPreflightAsync(agent, policy, ct), null);
    }

    private async Task<string?> ApiPreflightAsync(Agent agent, SandboxPolicyVersion policy, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(credentials.Get(agent.ProviderApiKeyEnvRef!))) return "provider_key_reference_unresolved";
        // The agent's own bot token and derived journal ingest token stay outside in stock providers; the
        // workload gets only the public bot id and stock placeholders. Missing values refuse here.
        if (ContainerProvisioningService.HasTelegramBot(agent) && TelegramCredential(agent).Fault is { } telegramFault) return telegramFault;
        // Tool-send receipts (#394) for a managed agent travel over the epoch's existing control lane
        // (fleet.group to the agent's own queue, published by Fleet.Telegram as the control principal).
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
        if (await ProfileHashFaultAsync(policy, agent, ct) is { } profileFault) return profileFault;
        return await host.GatewayGenerationAsync(ct) is null ? "gateway_unavailable" : null;
    }

    /// <summary>
    /// #502 compatibility-only subscription admission, before any mutation or old-runtime Stop: exactly the native memory
    /// endpoint with its existing <c>memory_get</c> grant and no other route (extra selections refuse, never dropped), the
    /// outside binding file, the pinned CLI and gateway client trust in the persisted scope, the restricted profile hash,
    /// gateway generation, sole ownership, the existing provider (no create/update/import) and its refresh window.
    /// </summary>
    private async Task<(string? Fault, StockSubscriptionSource? Source)> SubscriptionPreflightAsync(Agent agent, SandboxPolicyVersion policy, CancellationToken ct)
    {
        if (ContainerProvisioningService.HasTelegramBot(agent)) return ("managed_route_unsupported:telegram", null);
        if (agent.JournalEnabled) return ("managed_route_unsupported:journal", null);
        if (agent.McpEndpoints.FirstOrDefault(e => e.McpName != "memory") is { } extra) return ("managed_route_unsupported:mcp:" + extra.McpName, null);
        if (agent.McpEndpoints.Count(e => e.McpName == "memory") != 1) return ("managed_memory_route_missing", null);
        if (!agent.Tools.Any(t => t.IsEnabled && t.ToolName == "mcp__memory__memory_get")) return ("managed_memory_grant_missing", null);
        if (string.IsNullOrWhiteSpace(config["StockOpenShell:AgentImage"])) return ("managed_agent_image_missing", null);
        if (DescribeConfigDirFault() is { } dirFault) return (dirFault, null);
        if (LoadTemplate() is null) return ("managed_authority_template_invalid", null);
        var (source, sourceFault) = ReadSource(agent.Name);
        if (sourceFault is not null) return (sourceFault, null);
        if (broker.DescribePreflightFault() is { } brokerFault) return (brokerFault, null);
        if (await broker.VerifyAccessAsync(ct) is { } accessFault) return (accessFault, null);
        var version = await cli.RunAsync(["--version"], null, LegTimeout, ct);
        if (version.Missing) return ("stock_cli_missing", null);
        var tokens = version.Stdout.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (version.ExitCode != 0 || tokens.Length < 2 || tokens[0] != "openshell"
            || !(tokens[1] == PinnedCliVersion || tokens[1].StartsWith(PinnedCliVersion + "+", StringComparison.Ordinal)))
            return ("stock_cli_version_mismatch", null);
        var probe = await cli.RunAsync(InWorkspace(source!.SandboxWorkspace, ["sandbox", "list", "-o", "json", "--page-size", "1"]), null, LegTimeout, ct);
        if (probe.Missing || probe.TimedOut || probe.ExitCode != 0 || !IsJson(probe.Stdout)) return ("gateway_client_unverified", null);
        if (await ProfileHashFaultAsync(policy, agent, ct, source) is { } profileFault) return (profileFault, null);
        var generation = await host.GatewayGenerationAsync(ct);
        if (generation is null) return ("gateway_unavailable", null);
        if (generation != source.GatewayGeneration) return (StockSubscriptionBinding.ProviderUnverified, null);
        if (await OwnershipFaultAsync(agent, source, ct) is { } ownerFault) return (ownerFault, null);
        if (await ProviderFaultAsync(source.ProviderWorkspace, source.ProviderName, source.ProviderId, ct) is { } providerFault) return (providerFault, null);
        return (null, source);
    }

    private (StockSubscriptionSource? Source, string? Fault) ReadSource(string agentName) =>
        StockSubscriptionBinding.Read(ConfigDir, agentName, Now, EffectiveUid?.Invoke() ?? CurrentEffectiveUid(), OwnerUid);

    private static uint CurrentEffectiveUid() => OperatingSystem.IsWindows() ? uint.MaxValue : GetEffectiveUid();

    /// <summary>The persisted subscription identity a source maps to (captured before any effect).</summary>
    private static ManagedSubscription Tuple(StockSubscriptionSource source) =>
        new(source.ProviderWorkspace, source.ProviderId, source.AccountId, source.OwnerGeneration, source.SourceSha256, source.ExpiresAt);

    /// <summary>
    /// Sole owner: no other agent's nonretired subscription tuple may name the same provider UUID or the same
    /// workspace/name, and this agent's own live (not revoked) tuple may not be rebound to a different identity.
    /// </summary>
    private async Task<string?> OwnershipFaultAsync(Agent agent, StockSubscriptionSource source, CancellationToken ct)
    {
        IReadOnlyList<Agent> agents;
        try { agents = await store.ListAsync(ct); }
        catch (Exception ex) when (ex is DbException or InvalidOperationException) { return "runtime_state_unavailable"; }
        foreach (var other in agents)
        {
            ManagedRecord owned;
            try { owned = ManagedRecord.Parse(other.AppliedRuntimeBinding); }
            catch (JsonException) { if (other.Name != agent.Name) return StockSubscriptionBinding.OwnerConflict; continue; }
            foreach (var tuple in owned.SubscriptionTuples())
            {
                var same = tuple.Subscription?.ProviderId == source.ProviderId
                    || tuple.Subscription?.ProviderWorkspace == source.ProviderWorkspace && tuple.Provider == source.ProviderName;
                if (other.Name != agent.Name) { if (same) return StockSubscriptionBinding.OwnerConflict; continue; }
                if (owned.State != "revoked" && (owned.Applied == tuple || owned.Candidate == tuple) && !SameIdentity(tuple, source))
                    return StockSubscriptionBinding.Changed;
            }
        }
        return null;
    }

    private static bool SameIdentity(ManagedBinding binding, StockSubscriptionSource source) =>
        binding.SandboxWorkspace == source.SandboxWorkspace && binding.Provider == source.ProviderName && binding.Subscription is { } s
        && s.ProviderWorkspace == source.ProviderWorkspace && s.ProviderId == source.ProviderId && s.AccountId == source.AccountId
        && s.OwnerGeneration == source.OwnerGeneration;

    /// <summary>
    /// The existing provider, observed only inside its persisted workspace: exactly one entry of that name across every
    /// paginated page, with the exact UUID, the restricted profile type and only the subscription credential key; then
    /// its refresh status and window. Never create/update/import/rotate/delete.
    /// </summary>
    private async Task<string?> ProviderFaultAsync(string workspace, string name, string providerId, CancellationToken ct)
    {
        var token = "";
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var matches = new List<StockProviderEntry>();
        for (var page = 0; ; page++)
        {
            if (page >= MaxListPages) return StockSubscriptionBinding.ProviderUnverified;
            var list = await cli.RunAsync(InWorkspace(workspace, token.Length == 0 ? ["provider", "list", "-o", "json"]
                : ["provider", "list", "-o", "json", "--page-token", token]), null, LegTimeout, ct);
            if (list.Missing || list.TimedOut || list.ExitCode != 0
                || StockSubscriptionBinding.ParseProviderPage(list.Stdout) is not { } parsed) return StockSubscriptionBinding.ProviderUnverified;
            if (parsed.Entries.Any(e => e.Workspace != workspace)) return StockSubscriptionBinding.ProviderUnverified;
            matches.AddRange(parsed.Entries.Where(e => e.Name == name));
            token = parsed.NextToken;
            if (token.Length == 0) break;
            if (!seen.Add(token)) return StockSubscriptionBinding.ProviderUnverified;
        }
        if (matches is not [{ } provider] || provider.Id != providerId || provider.Type != StockSubscriptionBinding.ProfileId
            || !provider.CredentialKeys.SequenceEqual([StockSubscriptionBinding.CredentialKey]))
            return StockSubscriptionBinding.ProviderUnverified;
        var status = await cli.RunAsync(InWorkspace(workspace, ["--color", "never", "provider", "refresh", "status", name,
            "--credential-key", StockSubscriptionBinding.CredentialKey]), null, LegTimeout, ct);
        if (status.Missing || status.TimedOut || status.ExitCode != 0) return StockSubscriptionBinding.ProviderUnverified;
        return StockSubscriptionBinding.RefreshFault(StockSubscriptionBinding.ParseRefreshStatus(status.Stdout, name), Now);
    }

    /// <summary>
    /// #502: the outside source and existing provider revalidated against a persisted tuple immediately before attachment
    /// or activation. Missing/changed source, expiry or provider drift is a named fault; nothing is re-imported.
    /// </summary>
    private async Task<string?> RevalidateAsync(string agentName, ManagedBinding binding, CancellationToken ct)
    {
        if (ScopeFault(binding) is { } scope) return scope;
        if (binding.Subscription!.ExpiresAt <= Now) return StockSubscriptionBinding.Expired;
        var (source, fault) = ReadSource(agentName);
        if (fault is not null) return fault;
        if (source!.SourceSha256 != binding.Subscription.SourceSha256 || !SameIdentity(binding, source)) return StockSubscriptionBinding.Changed;
        if (await host.GatewayGenerationAsync(ct) is not { } generation || generation != source.GatewayGeneration)
            return StockSubscriptionBinding.ProviderUnverified;
        return await ProviderFaultAsync(binding.Subscription.ProviderWorkspace, binding.Provider, binding.Subscription.ProviderId, ct);
    }

    /// <summary>The stock provider profiles this agent's apply attaches: the native key, then each outside service.</summary>
    internal static IEnumerable<string> SelectedProfiles(Agent agent)
    {
        // #502: a subscription agent selects only the restricted subscription profile (no service routes).
        if (agent.NativeAuthMode == StockSubscriptionBinding.Mode) { yield return StockSubscriptionBinding.ProfileId; yield break; }
        yield return "openai";
        if (ContainerProvisioningService.HasTelegramBot(agent)) yield return TelegramProfile;
        if (JournalCapture(agent)) yield return JournalProfile;
        if (JournalRead(agent) || JournalFiles(agent)) yield return JournalReadProfile;
        if (JournalFiles(agent) && JournalGrants.CrossChatEffective(agent)) yield return JournalCrossChatProfile;
    }

    private static bool IsJson(string text)
    {
        try { using var _ = JsonDocument.Parse(text); return true; }
        catch (JsonException) { return false; }
    }

    /// <summary>
    /// Every provider profile the agent selects must be pinned, and each pinned profile's exported YAML
    /// must hash to the version's saved value, before any old-runtime Stop.
    /// </summary>
    private async Task<string?> ProfileHashFaultAsync(SandboxPolicyVersion policy, Agent agent, CancellationToken ct, StockSubscriptionSource? source = null)
    {
        Dictionary<string, string>? pinned;
        try { pinned = JsonSerializer.Deserialize<Dictionary<string, string>>(string.IsNullOrWhiteSpace(policy.ProviderProfileHashes) ? "{}" : policy.ProviderProfileHashes); }
        catch (JsonException) { return "provider_profile_hashes_invalid"; }
        if (SelectedProfiles(agent).FirstOrDefault(p => pinned?.ContainsKey(p) != true) is { } unpinned) return "provider_profile_unpinned:" + unpinned;
        // #502: the subscription profile is exported only in the persisted provider workspace, and the outside binding
        // must name that same pinned hash; other pinned profiles are not selected by a subscription agent.
        if (source is not null)
        {
            var subscriptionHash = pinned![StockSubscriptionBinding.ProfileId];
            if (subscriptionHash != source.ProfileSha256) return StockSubscriptionBinding.ProviderUnverified;
            pinned = new() { [StockSubscriptionBinding.ProfileId] = subscriptionHash };
        }
        foreach (var (profile, hash) in pinned ?? [])
        {
            var export = await cli.RunAsync(source is null ? ["profile", "export", profile, "-o", "yaml"]
                : InWorkspace(source.ProviderWorkspace, ["profile", "export", profile, "-o", "yaml"]), null, LegTimeout, ct);
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
        // Review H1: marked live before the claim CAS and unmarked only after the final CAS, so a reader that sees this
        // process's claim on the record treats it as in progress for exactly the lifetime of this call.
        Running.AddOrUpdate(agent!.Name, 1, (_, n) => n + 1);
        try { return await RunClaimedAsync(name, kind, agent, record!, step, ct); }
        finally
        {
            if (Running.AddOrUpdate(agent.Name, 0, (_, n) => n - 1) <= 0)
            {
                Running.TryRemove(new(agent.Name, 0));
                // Review A8: a claim this call left behind (unfinalized handoff, unknown commit) was published while
                // still live; republish so the status shows it interrupted rather than a stale "busy".
                await RepublishLeftClaimAsync(agent.Name);
            }
        }
    }

    /// <summary>Agents with a <see cref="RunAsync"/> call live in this process (claim CAS through final CAS).</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> Running = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Exact claim tokens of <see cref="RunClaimedAsync"/> calls still running in this process. A claim is live only
    /// while its own call runs; another call for the same agent name never makes a left-behind claim look busy.
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> LiveClaims = new(StringComparer.Ordinal);

    /// <summary>True while <paramref name="operation"/> is the claim of a call still running in this process.</summary>
    internal static bool OperationLive(string agentName, string operation) => LiveClaims.ContainsKey(operation);

    private async Task RepublishLeftClaimAsync(string name)
    {
        try
        {
            if (await store.LoadAsync(name, CancellationToken.None) is not { } fresh || string.IsNullOrEmpty(fresh.AppliedRuntimeBinding)) return;
            if (ManagedRecord.Parse(fresh.AppliedRuntimeBinding).Operation is { } op && !OperationLive(name, op)
                && services.GetService<AgentRegistry>() is { } registry)
                registry.UpdateRuntimeSelection(fresh);
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException or JsonException) { }
    }

    private async Task<Result> RunClaimedAsync(string name, string kind, Agent agent, ManagedRecord record, Step step, CancellationToken ct)
    {
        // kind:process:call — the kind stays first for interrupted_<kind>; the call id scopes liveness to this call.
        var operation = kind + ":" + Instance + ":" + Guid.NewGuid().ToString("N");
        LiveClaims[operation] = 0;
        try { return await RunClaimedCoreAsync(name, kind, agent, record, step, operation, ct); }
        finally { LiveClaims.TryRemove(operation, out _); }
    }

    private async Task<Result> RunClaimedCoreAsync(string name, string kind, Agent agent, ManagedRecord record, Step step, string operation, CancellationToken ct)
    {
        var current = record with { Operation = operation };
        var saved = current.Serialize();
        try
        {
            if (!await store.SwapAsync(agent.Id, agent.AppliedRuntimeBinding, saved, null, null, ct))
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
        // #492 §7/§11: a verified outcome records the workload/supervisor identities it left behind.
        if (outcome.Result.Ok && final.Applied is { } observed && final.State is "applied" or "stopped" or "revoked")
            final = final with { Applied = observed with { Containers = await ObserveContainersAsync(observed) } };
        // Review R2 (round 6): ordinary admission stays held until the Docker binding is durably committed. The
        // handoff commits first with a pending-retirement marker (a held needs_attention record) while the authority
        // record is still revoked; only after that commit is the authority retired, and a follow-up CAS clears the
        // marker or records authority_retire_failed. A failed or ambiguous commit therefore never released traffic.
        // Review H1: the held commit also RETAINS this operation's claim, so a concurrent revoke/apply/start gets 409
        // through authority retirement; only the final CAS below clears Operation.
        var handoff = outcome.Result.Ok && outcome.AppliedKind == "docker" && !final.Unresolved;
        var clean = final;
        if (handoff) final = final with { State = "needs_attention", Errors = [.. final.Errors, RetirePending], Operation = operation };
        var finalJson = final.Serialize();
        bool committed;
        try { committed = await store.SwapAsync(agent.Id, saved, finalJson, outcome.AppliedKind, outcome.AppliedPolicy, CancellationToken.None); }
        catch (Exception ex) when (ex is DbException or InvalidOperationException)
        {
            // Review V4: the write may have committed before it threw. Resolve by re-reading; never assume either way.
            switch (await ResolveCommitAsync(name, finalJson, saved))
            {
                case true: committed = true; break;
                case false:
                    // Post-action DB failure: the claimed operation stays recorded and is reported as interrupted next time.
                    return Result.Refused(503, kind + "_state_unknown", outcome.Result.Errors);
                default:
                    logger.LogError("Managed {Kind} for {Agent}: final commit is ambiguous; the hold is kept", kind, name);
                    return Result.Refused(503, "needs_attention", [.. outcome.Result.Errors, kind + "_state_unknown", "commit_ambiguous"]);
            }
        }
        if (!committed) return Result.Refused(409, "managed_state_changed_during_" + kind, outcome.Result.Errors);
        if (handoff)
        {
            var retiredOk = RetireAuthority(name);
            var next = retiredOk ? clean : clean with { State = "needs_attention", Errors = [.. clean.Errors, "authority_retire_failed"] };
            var nextJson = next.Serialize();
            bool finalized;
            try { finalized = await store.SwapAsync(agent.Id, finalJson, nextJson, null, null, CancellationToken.None); }
            catch (Exception ex) when (ex is DbException or InvalidOperationException)
            { finalized = await ResolveCommitAsync(name, nextJson, finalJson) == true; }
            if (!retiredOk)
            {
                await PublishAsync(name);
                // The authority is unretired, so ordinary traffic stays held whether or not the follow-up committed.
                logger.LogWarning("Managed {Kind} for {Agent}: authority retirement failed after the Docker commit", kind, name);
                return Result.Refused(502, "docker_handoff_unresolved",
                    finalized ? ["authority_retire_failed"] : ["authority_retire_failed", "handoff_record_unfinalized"]);
            }
            if (!finalized)
            {
                await PublishAsync(name);
                // Docker is durably committed and the authority retired; the record still shows the pending marker (held,
                // visible) and the retained claim, which ReadAsync reports as interrupted once this call has returned;
                // revoke on the Docker agent is the supported retry that clears it.
                logger.LogWarning("Managed {Kind} for {Agent}: the retired handoff record was not finalized", kind, name);
                return Result.Refused(502, "docker_handoff_unresolved", [RetirePending, "handoff_record_unfinalized"]);
            }
        }
        await PublishAsync(name);
        logger.LogInformation("Managed {Kind} for {Agent}: {Code}", kind, name, outcome.Result.Code);
        return outcome.Result;
    }

    /// <summary>Error marker on a committed Docker handoff whose authority is not yet retired (still held).</summary>
    internal const string RetirePending = "authority_retire_pending";

    /// <summary>
    /// Review V4: after a swap threw, re-read the stored binding. True: <paramref name="written"/> is stored (the write
    /// committed); false: <paramref name="previous"/> is still stored (it did not); null: unreadable or another value.
    /// </summary>
    private async Task<bool?> ResolveCommitAsync(string name, string written, string? previous)
    {
        try
        {
            var stored = (await store.LoadAsync(name, CancellationToken.None))?.AppliedRuntimeBinding;
            if (stored == written) return true;
            if (stored == previous) return false;
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException) { }
        return null;
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
            // An operation owned by an earlier orchestrator process, or by a call in this process that has already
            // returned (e.g. an unfinalized handoff that kept its claim), was interrupted: hold it, never replay.
            if (!OperationLive(agent.Name, op))
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
        // Appends: an earlier hold's errors and partial names are never overwritten by a later one.
        (record with { State = "needs_attention", Errors = [.. record.Errors, code, .. extra] }, null, null, Result.Refused(status, code, extra));

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
            // The workspace copy-out and revoke record live under ConfigDir: refuse before any write.
            if (DescribeConfigDirFault() is { } dirFault) return (record, null, null, Result.Refused(409, dirFault));
            // Cut off (unless already revoked), quiesce every writer, start into staging (no daemon),
            // copy the workspace out, then stop and verify. Any failure still attempts a bounded Stop.
            var errors = new List<string>();
            if (record.State != "revoked") errors.AddRange(await RevokeFanoutAsync(agent.Name, record, ct, stop: false));
            if (errors.Count == 0 && await StopVerifiedAsync(applied, ct) is { } quiet) errors.Add(quiet);
            if (errors.Count == 0 && await StageAsync(applied, ct) is { } staged) errors.Add(staged);
            if (errors.Count == 0 && await DownloadWorkspaceAsync(agent, applied, DockerWorkspaceDir(agent), ct) is { } workspace) errors.Add(workspace);
            using (var cleanup = new CancellationTokenSource(FanoutTimeout))
                if (await StopVerifiedAsync(applied, cleanup.Token) is { } stopped) errors.Add(stopped);
            if (errors.Count > 0) return Hold(record, 502, "openshell_to_docker_unresolved", [.. errors]);
            record = record with { State = "revoked" };
            await checkpoint(record);
        }
        // #502: every old subscription tuple must be verified cut, detached and quiescent BEFORE the Docker handoff;
        // otherwise it stays live/countable and nothing is handed off.
        var retiring = RetirementTuples(agent, record);
        if (retiring.Count > 0 && await RetirementFaultAsync(agent.Name, retiring, ct) is { } unverified)
            return Hold(record, 502, "subscription_retirement_unverified", unverified);
        var provisioned = ProvisionDocker is { } provision ? await provision(agent.Name, ct)
            : await services.GetRequiredService<ContainerProvisioningService>().ProvisionDockerTransitionAsync(agent.Name, ct);
        if (!provisioned.Ok) return Hold(record, 502, "docker_transition_failed", provisioned.Message);
        var handedOff = record with { State = "revoked", Candidate = null, Errors = [] };
        if (retiring.Count == 0) return (handedOff, "docker", null, Result.Done("applied_docker"));
        // Archive before commit; the final CAS then publishes Docker and the retirement together (Applied=null).
        var (retired, archiveFault) = ArchiveRetirements(agent.Name, retiring, "docker");
        if (archiveFault is not null)
            return (handedOff with { State = "needs_attention", Errors = ["subscription_archive_failed", archiveFault] }, "docker", null,
                Result.Refused(502, "subscription_archive_failed", [archiveFault]));
        return (Retire(handedOff, retiring, retired!), "docker", null, Result.Done("applied_docker"));
    }

    private async Task<(ManagedRecord, string?, int?, Result)> ApplyOpenShellAsync(Agent agent, ManagedRecord record,
        Func<ManagedRecord, Task> checkpoint, CancellationToken ct)
    {
        // Every prerequisite, including gateway client trust and generated inputs, precedes any old-runtime action.
        var (preflight, source) = await PreflightAsync(agent, ct);
        if (preflight is not null) return (record, null, null, Result.Refused(409, preflight));
        var subscription = source is not null;
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
        var mode = subscription ? StockSubscriptionBinding.Mode : "api_key";

        // Reprovision and stopped/revoked rollback keep the verified sandbox and its persistent workspace.
        var reuse = agent.AppliedRuntimeKind == "openshell" && record.Applied is not null;
        // Review A7: the recovery path for managed_image_change_requires_revoke. After a clean revoke the old sandbox
        // is cut and stopped; a changed image then gets a fresh sandbox from the pinned new image, carrying the old
        // sandbox's workspace (the old sandbox itself stays stopped and revoked). #502: a changed auth mode or
        // sandbox workspace takes the same path (a subscription sandbox lives only in its persisted workspace).
        var replaced = reuse && (record.Applied!.Image != image || record.Applied.AuthMode != mode
            || subscription && record.Applied.SandboxWorkspace != source!.SandboxWorkspace);
        var retired = replaced && record.State == "revoked" ? record.Applied : null;
        if (retired is not null)
        {
            if (record.Applied!.GatewayGeneration != generation && !await PlannedRestartAsync(agent.Name, record, record.Applied, ct))
                return (record, null, null, Result.Refused(409, "gateway_generation_changed"));
            if (await VerifyIdentityAsync(retired, ct) is { } identity) return (record, null, null, identity);
            reuse = false;
        }
        if (reuse)
        {
            var old = record.Applied!;
            // #489 planned restart: a durably stopped, quiescent fleet re-applies onto the new generation.
            if (old.GatewayGeneration != generation && !await PlannedRestartAsync(agent.Name, record, old, ct))
                return (record, null, null, Result.Refused(409, "gateway_generation_changed"));
            if (old.Image != image) return (record, null, null, Result.Refused(409, "managed_image_change_requires_revoke"));
            if (replaced) return (record, null, null, Result.Refused(409, "managed_auth_change_requires_revoke"));
            if (await VerifyIdentityAsync(old, ct) is { } identity) return (record, null, null, identity);
        }
        var epoch = Guid.NewGuid();
        // #502: the subscription identity and both workspaces are captured in the candidate BEFORE any effect.
        var tuple = subscription ? Tuple(source!) : null;
        var candidate = reuse
            ? record.Applied! with
            {
                Epoch = epoch, PolicyVersionId = policy.Id, PolicySha256 = policy.Sha256, KeyRef = agent.ProviderApiKeyEnvRef ?? "",
                GatewayGeneration = generation, ServiceProviders = serviceProviders, Subscription = tuple,
                Provider = subscription ? source!.ProviderName : record.Applied!.Provider,
            }
            : new ManagedBinding($"fleet-{agent.Name}-{epoch.ToString("N")[..8]}", "", epoch, image, policy.Id, policy.Sha256, mode,
                agent.ProviderApiKeyEnvRef ?? "", subscription ? source!.ProviderName : $"fleet-{agent.Name}-openai", generation)
            {
                ServiceProviders = serviceProviders, SandboxWorkspace = source?.SandboxWorkspace, Subscription = tuple,
            };
        // #502: a reused sandbox rebound to a new outside identity keeps the old tuple as custody history.
        var history = reuse && record.Applied!.IsSubscription && subscription && !SameIdentity(record.Applied, source!)
            ? [.. record.Superseded, record.Applied] : record.Superseded;
        var applying = record with { State = "applying", Candidate = candidate, Errors = [], Superseded = history };
        await checkpoint(applying);

        // #502: a pending subscription apply runs against its expiry-linked deadline; after expiry no further positive
        // step runs and the candidate is revoked and stopped instead.
        using var expiry = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (subscription) expiry.CancelAfter(Remaining(tuple!.ExpiresAt));
        ct = expiry.Token;
        bool Expired() => subscription && Now >= tuple!.ExpiresAt;
        Task<(ManagedRecord, string?, int?, Result)> CutAsync(ManagedRecord at) => ExpireAsync(agent.Name, at);

        var template = LoadTemplate()!;
        var epochDir = Path.Combine(ConfigDir, "managed", agent.Name, epoch.ToString("N"));
        var passwords = StockBrokerProvisioner.Roles.ToDictionary(r => r, _ => Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
        string bundle;
        try
        {
            // Reuse: the live epoch's control credentials and record change only after its cut (below).
            if (!reuse)
            {
                WriteBrokerPasswords(agent.Name, passwords);
                WriteRecord(AuthorityPath(agent.Name), Authority(template, agent, "pending", epoch, policy.Sha256, "revoked", tuple?.ExpiresAt));
            }
            bundle = await BuildBundleAsync(agent, template, generated, epochDir, passwords["workload"], epoch, policy,
                reuse ? candidate.SandboxId : "pending", botId, serviceSecrets.Select(s => s.Value), candidate);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or JsonException)
        { return Hold(applying, 500, "managed_files_failed"); }
        Dictionary<string, string>? secret = null;
        if (!subscription)
        {
            var key = credentials.Get(agent.ProviderApiKeyEnvRef!);
            if (string.IsNullOrEmpty(key)) return Hold(applying, 409, "provider_key_reference_unresolved");
            secret = new Dictionary<string, string> { ["OPENAI_API_KEY"] = key };
        }

        // Every outside provider exists with its current value before create/attach; values only in the CLI env.
        // #502: a subscription provider is never created, updated, imported or rotated here: the existing one is only
        // verified in its persisted workspace, immediately before attachment.
        async Task<string?> EnsureProvidersAsync()
        {
            if (subscription) return await RevalidateAsync(agent.Name, candidate, ct);
            if (await EnsureProviderAsync(candidate.Provider, "openai", secret!, ct) is { } fault) return fault;
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
            try
            {
                WriteBrokerPasswords(agent.Name, passwords);
                WriteRecord(AuthorityPath(agent.Name), Authority(template, agent, candidate.SandboxId, epoch, policy.Sha256, "revoked", tuple?.ExpiresAt));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return await HoldStoppedAsync(applying, candidate, "managed_files_failed", [], ct); }
            // #502: retained subscription history of an API reuse is verified before it can be retired at commit.
            if (RetirementTuples(agent, record) is { Count: > 0 } held && await RetirementFaultAsync(agent.Name, held, ct) is { } unverifiedHistory)
                return await HoldStoppedAsync(applying, candidate, "subscription_retirement_unverified", [unverifiedHistory], ct);
        }
        else
        {
            if (agent.AppliedRuntimeKind == "docker")
            {
                if (await StopDockerAsync(agent) is { } dockerFault) return Hold(applying, 409, dockerFault);
                try { StageWorkspace(agent, Path.Combine(bundle, "workspace")); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return Hold(applying, 500, "workspace_stage_failed"); }
            }
            else if (retired is not null)
            {
                // Staged (no daemon) only for the transfer, then verified stopped again before the new sandbox exists.
                var carried = await StageAsync(retired, ct) ?? await DownloadWorkspaceAsync(agent, retired, Path.Combine(bundle, "workspace"), ct);
                using (var cleanup = new CancellationTokenSource(FanoutTimeout))
                    if (await StopVerifiedAsync(retired, cleanup.Token) is { } stopped) carried = carried is null ? "stop:" + stopped : carried + ",stop:" + stopped;
                // Nothing new exists yet: hold the revoked record itself (no UUID-less candidate), so revoke clears it.
                if (carried is not null) return Hold(record, 502, "image_change_workspace_unresolved", carried);
            }
            // #502: a mode change away from subscription first verifies every old tuple cut, detached and quiescent.
            if (RetirementTuples(agent, record) is { Count: > 0 } old && await RetirementFaultAsync(agent.Name, old, ct) is { } unverified)
                return Hold(record, 502, "subscription_retirement_unverified", unverified);
            if (Expired()) return await CutAsync(applying);
            if (await EnsureProvidersAsync() is { } providerFault)
                return providerFault == StockSubscriptionBinding.Expired ? await CutAsync(applying) : Hold(applying, 502, providerFault);
            // Pinned contract: an explicit canonical launcher, staged; no --upload with a trailing command.
            var create = await cli.RunAsync(Scoped(candidate, [
                "sandbox", "create", "--name", candidate.SandboxName, "--from", candidate.Image,
                "--policy", Path.Combine(epochDir, "policy.yaml"), .. candidate.AllProviders().SelectMany(p => new[] { "--provider", p }),
                "--no-auto-providers",
                "--restart-policy", "never", "--label", "fleet.agent=" + agent.Name, "--label", "fleet.epoch=" + epoch.ToString("N"),
                "--env", "FLEET_STOCK_STAGED=1", "--detach", "--", Entrypoint]), null, TimeSpan.FromMinutes(5), ct);
            // Whatever create reported, record any sandbox that now exists so cleanup keeps its identity.
            using (var observe = new CancellationTokenSource(FanoutTimeout))
                if (await GetAsync(candidate, observe.Token) is { } made)
                {
                    candidate = candidate with { SandboxId = made.Id };
                    applying = applying with { Candidate = candidate };
                    await checkpoint(applying);
                }
            if (Expired()) return await CutAsync(applying);
            if (create.ExitCode != 0 || create.TimedOut || string.IsNullOrEmpty(candidate.SandboxId))
                return string.IsNullOrEmpty(candidate.SandboxId) ? Hold(applying, 502, "sandbox_create_unknown")
                    : await HoldStoppedAsync(applying, candidate, "sandbox_create_unknown", [], ct);
        }

        // Staging: the launcher waits without a daemon, so transfers race no native task or file writer.
        if (await StageAsync(candidate, ct) is { } stage) return Expired() ? await CutAsync(applying) : await HoldStoppedAsync(applying, candidate, stage, [], ct);
        // Reuse keeps the workspace but refreshes its MCP routes, which follow the current grants.
        foreach (var (local, remote) in reuse ? new[] { ("app", WorkloadHome), ("trust", WorkloadHome), (Path.Combine("workspace", ".mcp.json"), "/workspace/.mcp.json") }
                     : [("app", WorkloadHome), ("trust", WorkloadHome), ("workspace", "/")])
            if (await UploadAsync(candidate, Path.Combine(bundle, local), remote, ct) is { } upload)
                return Expired() ? await CutAsync(applying) : await HoldStoppedAsync(applying, candidate, upload, [], ct);
        if (reuse)
        {
            if (record.Applied!.PolicyVersionId != policy.Id)
            {
                var set = await cli.RunAsync(Scoped(candidate, ["policy", "set", candidate.SandboxName, "--policy", Path.Combine(epochDir, "policy.yaml"), "--wait"]), null, LegTimeout, ct);
                if (set.ExitCode != 0 || set.TimedOut) return Expired() ? await CutAsync(applying) : await HoldStoppedAsync(applying, candidate, "policy_update_unknown", [], ct);
            }
            if (Expired()) return await CutAsync(applying);
            if (await EnsureProvidersAsync() is { } providerFault)
                return providerFault == StockSubscriptionBinding.Expired ? await CutAsync(applying) : await HoldStoppedAsync(applying, candidate, providerFault, [], ct);
            // The cut detached every old provider; attach exactly the current set (a dropped bot/journal stays detached).
            foreach (var provider in candidate.AllProviders())
            {
                var attach = await cli.RunAsync(Scoped(candidate, ["sandbox", "provider", "attach", candidate.SandboxName, provider]), null, LegTimeout, ct);
                if (attach.ExitCode != 0 || attach.TimedOut) return Expired() ? await CutAsync(applying) : await HoldStoppedAsync(applying, candidate, "provider_attach_unknown", [], ct);
            }
        }

        // Broker users exist only now; then the outside and guest enrollments carry the exact UUID/epoch.
        if (Expired()) return await CutAsync(applying);
        var created = await broker.CreateEpochAsync(epoch, agent.ShortName, ct, passwords);
        if (!created.Ok) return Expired() ? await CutAsync(applying) : await HoldStoppedAsync(applying, candidate, created.Code!, [.. created.OwnedPartials], ct);
        try
        {
            var active = Authority(template, agent, candidate.SandboxId, epoch, policy.Sha256, "active", tuple?.ExpiresAt);
            WriteRecord(AuthorityPath(agent.Name), active);
            WriteRecord(Path.Combine(bundle, "trust", "enrollment.json"), active);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return await HoldStoppedAsync(applying, candidate, "managed_files_failed", [], ct); }
        if (await UploadAsync(candidate, Path.Combine(bundle, "trust", "enrollment.json"), WorkloadEnrollmentFile, ct) is { } enrollment)
            return Expired() ? await CutAsync(applying) : await HoldStoppedAsync(applying, candidate, enrollment, [], ct);
        // #502: source, provider and refresh window revalidated immediately before activation; expiry cuts instead.
        if (subscription && await RevalidateAsync(agent.Name, candidate, ct) is { } drift)
            return drift == StockSubscriptionBinding.Expired || Expired() ? await CutAsync(applying) : await HoldStoppedAsync(applying, candidate, drift, [], ct);
        if (await ActivateAsync(candidate, ct) is { } activate) return Expired() ? await CutAsync(applying) : await HoldStoppedAsync(applying, candidate, activate, [], ct);
        if (Expired()) return await CutAsync(applying);
        // An image-change re-apply keeps the replaced, revoked and stopped sandbox tracked instead of dropping it.
        var superseded = retired is not null && retired.SandboxId != candidate.SandboxId ? [.. history, retired] : history;
        var applied = record with { State = "applied", Applied = candidate, Candidate = null, Errors = [], Superseded = superseded };
        // #502: a verified handoff away from subscription archives and atomically retires every old subscription tuple
        // in this same final CAS; an archive failure keeps them countable and holds the record.
        if (!subscription && RetirementTuples(agent, applied) is { Count: > 0 } retiring)
        {
            var (refs, archiveFault) = ArchiveRetirements(agent.Name, retiring, "openshell:" + mode);
            if (archiveFault is not null)
                return (applied with { State = "needs_attention", Errors = ["subscription_archive_failed", archiveFault] }, "openshell", policy.Id,
                    Result.Refused(502, "subscription_archive_failed", [archiveFault]));
            applied = Retire(applied, retiring, refs!);
        }
        return (applied, "openshell", policy.Id, Result.Done("applied_openshell"));
    }

    private TimeSpan Remaining(DateTimeOffset expiresAt)
    {
        var left = expiresAt - Now;
        return left <= TimeSpan.Zero ? TimeSpan.Zero : left > TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) : left;
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
        // Cleanup uses its own bounded deadline, never the caller's token.
        using var cleanup = new CancellationTokenSource(FanoutTimeout);
        if (await StopVerifiedAsync(binding, cleanup.Token) is { } stop) all.Add("stop:" + stop);
        return Hold(record, 502, code, [.. all]);
    }

    /// <summary>Stop if needed, then confirm no workload/native-supervisor container remains.</summary>
    private async Task<string?> StopVerifiedAsync(ManagedBinding binding, CancellationToken ct)
    {
        if (ScopeFault(binding) is { } scope) return scope;
        var sandbox = await GetAsync(binding, ct);
        if (sandbox is null) return "sandbox_unobservable";
        if (sandbox.Value.Id != binding.SandboxId) return "sandbox_identity_changed";
        // Error/Completed may still leave a native supervisor running: only Stopped skips the verified Stop.
        if (sandbox.Value.Phase != "Stopped" && await LifecycleAsync("stop", binding, ct) is { } stop) return stop;
        return await QuiescentAsync(binding, ct);
    }

    /// <summary>
    /// A request cancellation during staging/activation is a named fault, never an exception, so every
    /// caller still reaches its bounded <see cref="HoldStoppedAsync"/> Stop (review N2).
    /// </summary>
    private static async Task<string?> UncancelledAsync(Func<Task<string?>> leg, string code, CancellationToken ct)
    {
        try { return await leg(); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return code; }
    }

    private Task<string?> StageAsync(ManagedBinding binding, CancellationToken ct) =>
        UncancelledAsync(() => StageOnceAsync(binding, ct), "staging_cancelled", ct);

    private Task<string?> ActivateAsync(ManagedBinding binding, CancellationToken ct) =>
        UncancelledAsync(() => ActivateOnceAsync(binding, ct), "activation_cancelled", ct);

    /// <summary>Start into the staged launcher (no daemon) and wait until it reports staged.</summary>
    private async Task<string?> StageOnceAsync(ManagedBinding binding, CancellationToken ct)
    {
        if (ScopeFault(binding) is { } scope) return scope;
        var sandbox = await GetAsync(binding, ct);
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

    /// <summary>
    /// Review F4: the daemon's own <c>GET /health</c> on its loopback port answers 200 (the ready marker alone is
    /// written before the daemon serves).
    /// </summary>
    internal const string DaemonHealthScript = "exec 3<>/dev/tcp/127.0.0.1/8080 || exit 2; "
        + "printf 'GET /health HTTP/1.0\\r\\n\\r\\n' >&3; IFS= read -r status <&3; case \"$status\" in *' 200 '*) exit 0;; esac; exit 1";

    /// <summary>Writes this boot's activation; the launcher consumes it, prepares and starts the daemon.</summary>
    private async Task<string?> ActivateOnceAsync(ManagedBinding binding, CancellationToken ct)
    {
        if (await ExecAsync(binding, "printf 1 > \"$HOME/.fleet-activate\"", ct) != 0) return "activation_unknown";
        var until = DateTimeOffset.UtcNow + StopSettle;
        while (DateTimeOffset.UtcNow < until)
        {
            // Applied only when the daemon is up and healthy AND the exact UUID is observed Ready (never Error/Completed).
            if (await ExecAsync(binding, DaemonReadyScript, ct) == 0 && await ExecAsync(binding, DaemonHealthScript, ct, "bash") == 0)
            {
                var after = await GetAsync(binding, ct);
                if (after is null) return ScopeFault(binding) ?? "sandbox_unobservable";
                if (after.Value.Id != binding.SandboxId) return "sandbox_identity_changed";
                if (after.Value.Phase == "Ready") return null;
            }
            await Task.Delay(Poll, ct);
        }
        return "activation_unconfirmed";
    }

    private async Task<int> ExecAsync(ManagedBinding binding, string script, CancellationToken ct, string shell = "sh")
    {
        if (await VerifyIdentityAsync(binding, ct) is not null) return -1;
        var run = await cli.RunAsync(Scoped(binding, ["sandbox", "exec", "-n", binding.SandboxName, "--", shell, "-c", script]), null, LegTimeout, ct);
        return run.TimedOut ? -1 : run.ExitCode;
    }

    private async Task<string?> UploadAsync(ManagedBinding binding, string local, string remote, CancellationToken ct)
    {
        if (await VerifyIdentityAsync(binding, ct) is { } identity) return identity.Code;
        var run = await cli.RunAsync(Scoped(binding, ["sandbox", "upload", binding.SandboxName, local, remote]), null, TransferTimeout, ct);
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
        if (await GenerationFaultAsync(binding, ct) is { } generation)
        {
            // #489 planned restart: rebind the recorded generation only after the same-UUID/Stopped/
            // quiescent checks across every owned record and the gateway trust preflight; else hold.
            if (generation != "gateway_generation_changed" || !await PlannedRestartAsync(agent.Name, record, binding, ct)
                || await DescribePreflightFaultAsync(agent, ct) is not null || await host.GatewayGenerationAsync(ct) is not { } current)
                return await HoldForGenerationAsync(agent, record, generation, ct);
            binding = binding with { GatewayGeneration = current };
            record = record with { Applied = binding };
            await checkpoint(record);
        }
        if (binding.IsSubscription)
        {
            // #502: Start rechecks scope, expiry and the outside source on the actual target. Expiry is revoke-and-stop;
            // a missing or changed source is drift (existing revoke/Stop hold, never re-import); a provider that cannot
            // be verified right now refuses before any positive step.
            if (ScopeFault(binding) is { } scope) return Hold(record, 409, scope);
            if (binding.Subscription!.ExpiresAt <= Now) return await ExpireAsync(agent.Name, record);
            var (source, sourceFault) = ReadSource(agent.Name);
            if (sourceFault == StockSubscriptionBinding.Expired) return await ExpireAsync(agent.Name, record);
            var drift = sourceFault ?? (source!.SourceSha256 != binding.Subscription.SourceSha256 || !SameIdentity(binding, source)
                ? StockSubscriptionBinding.Changed : null);
            if (drift is not null) return Hold(record, 409, drift, [.. await RevokeFanoutAsync(agent.Name, record, ct)]);
            if (await RevalidateAsync(agent.Name, binding, ct) is { } unverified) return (record, null, null, Result.Refused(409, unverified));
        }
        // A repeat Start on an applied, running binding never re-stages or stops it: success when
        // Ready, otherwise a mutation-free refusal.
        if (record.State == "applied" && await GetAsync(binding, ct) is { } live && live.Id == binding.SandboxId
            && live.Phase is not ("Stopped" or "Completed" or "Error"))
            return (record, null, null, live.Phase == "Ready" ? Result.Done("started") : Result.Refused(409, "sandbox_not_stopped"));
        // Every boot is staged; the daemon starts only with this boot's activation.
        if (await StageAsync(binding, ct) is { } staged) return await HoldStoppedAsync(record, binding, staged, [], ct);
        // #502: the subscription tuple is revalidated immediately before activation; expiry cuts instead.
        if (binding.IsSubscription && await RevalidateAsync(agent.Name, binding, ct) is { } changed)
            return changed == StockSubscriptionBinding.Expired ? await ExpireAsync(agent.Name, record) : await HoldStoppedAsync(record, binding, changed, [], ct);
        if (await ActivateAsync(binding, ct) is { } fault)
            return await HoldStoppedAsync(record, binding, fault, [], ct);
        return (record with { State = "applied", Errors = [] }, null, null, Result.Done("started"));
    }

    private async Task<(ManagedRecord, string?, int?, Result)> StopCoreAsync(Agent agent, ManagedRecord record,
        Func<ManagedRecord, Task> checkpoint, CancellationToken ct)
    {
        if (agent.AppliedRuntimeKind != "openshell" || record.Applied is not { } binding)
            return (record, null, null, Result.Refused(409, "managed_binding_missing"));
        if (await GenerationFaultAsync(binding, ct) is { } generation)
        {
            // Stop on a durably stopped, quiescent binding after a planned restart is already done.
            if (generation == "gateway_generation_changed" && record.State == "stopped" && await StoppedQuiescentAsync(binding, ct) is null)
                return (record, null, null, Result.Done("stopped"));
            return await HoldForGenerationAsync(agent, record, generation, ct);
        }
        var errors = new List<string>();
        if (await LifecycleAsync("stop", binding, ct) is { } fault) errors.Add(fault);
        else if (await QuiescentAsync(binding, ct) is { } quiet) errors.Add(quiet);
        if (errors.Count > 0) return Hold(record, 502, "stop_unresolved", [.. errors]);
        // A successful Stop never clears an earlier unresolved hold or its errors, nor a revoke.
        return record.State is "needs_attention" or "revoked" || record.Candidate is not null
            ? (record, null, null, Result.Done("stopped_held"))
            : (record with { State = "stopped", Errors = [] }, null, null, Result.Done("stopped"));
    }

    private async Task<(ManagedRecord, string?, int?, Result)> RevokeCoreAsync(Agent agent, ManagedRecord record,
        Func<ManagedRecord, Task> checkpoint, CancellationToken ct)
    {
        var docker = agent.AppliedRuntimeKind == "docker";
        if (record.Applied is null && record.Candidate is null)
        {
            // #502: a Docker handoff that atomically retired its subscription bindings keeps no Applied binding; revoke
            // stays the supported retry of its authority retirement (inert history is never revoked or rehydrated).
            if (docker && record.State == "needs_attention" && record.RetiredSubscriptions.Count > 0
                && record.Errors.All(e => e is RetirePending or "authority_retire_failed" or "handoff_record_unfinalized" || e.StartsWith("interrupted_", StringComparison.Ordinal)))
                return RetireAuthority(agent.Name) ? (record with { State = "revoked", Errors = [] }, null, null, Result.Done("revoked"))
                    : Hold(record, 502, "docker_handoff_unresolved", "authority_retire_failed");
            return (record, null, null, Result.Refused(409, "managed_binding_missing"));
        }
        // Review R2: a handed-off Docker agent keeps (or regains) a retired authority, never a held revoked one;
        // this is also the supported retry of an earlier authority_retire_failed.
        var errors = await RevokeFanoutAsync(agent.Name, record, ct, keepRetired: docker);
        if (errors.Count > 0) return Hold(record, 502, "revoke_unresolved", [.. errors]);
        if (docker && !RetireAuthority(agent.Name)) return Hold(record, 502, "docker_handoff_unresolved", "authority_retire_failed");
        return (WithCustody(record) with { State = "revoked", Candidate = null, Errors = [] }, null, null, Result.Done("revoked"));
    }

    /// <summary>
    /// #502: a cleanly cut subscription candidate is never dropped; it moves to Superseded so its persisted
    /// workspace/UUID tuple stays countable custody until an atomic, archived retirement.
    /// </summary>
    private static ManagedRecord WithCustody(ManagedRecord record) =>
        record.Candidate is { IsSubscription: true } candidate && candidate != record.Applied && !record.Superseded.Contains(candidate)
            ? record with { Superseded = [.. record.Superseded, candidate] } : record;

    /// <summary>
    /// #502 revoke-and-stop at subscription expiry (any stage): admission cut, scoped detach, broker revoke, bounded Stop
    /// and quiescence over every applied/candidate target. Clean → revoked with <c>subscription_binding_expired</c>;
    /// partial → needs_attention with every cleanup error. No renewal or retry.
    /// </summary>
    private async Task<(ManagedRecord, string?, int?, Result)> ExpireAsync(string agentName, ManagedRecord record)
    {
        var errors = await RevokeFanoutAsync(agentName, record, CancellationToken.None);
        if (errors.Count > 0) return Hold(record, 502, StockSubscriptionBinding.Expired, [.. errors]);
        logger.LogWarning("Managed subscription for {Agent} expired; revoked and stopped", agentName);
        return (WithCustody(record) with { State = "revoked", Candidate = null, Errors = [StockSubscriptionBinding.Expired] }, null, null,
            Result.Refused(409, StockSubscriptionBinding.Expired));
    }

    /// <summary>
    /// #502 running/pending expiry enforcement on the existing 10-second state-monitor pass: an applied or candidate
    /// subscription tuple past its expiry gets the bounded revoke-and-stop. Records already held (needs_attention) or
    /// revoked are skipped, so a failed cleanup is reported once and never re-invoked every pass; a live in-flight apply
    /// cancels at its own expiry deadline instead.
    /// </summary>
    public async Task CheckSubscriptionExpiryAsync(string name, CancellationToken ct)
    {
        if (!Enabled) return;
        Agent? agent;
        try { agent = await store.LoadAsync(name, ct); }
        catch (Exception ex) when (ex is DbException or InvalidOperationException) { return; }
        if (agent is null) return;
        ManagedRecord record;
        try { record = ManagedRecord.Parse(agent.AppliedRuntimeBinding); }
        catch (JsonException) { return; }
        if (record.State is "needs_attention" or "revoked") return;
        var now = Now;
        if (!new[] { record.Applied, record.Candidate }.Any(b => b?.Subscription is { } s && s.ExpiresAt <= now)) return;
        if (record.Operation is { } op && OperationLive(agent.Name, op)) return;
        await RunAsync(name, "expiry", async (_, current, _, _) =>
            current.State is "needs_attention" or "revoked" ? (current, null, null, Result.Refused(409, current.State))
            : await ExpireAsync(name, current), CancellationToken.None);
    }

    // ── #502 subscription retirement: verified cut, archive, then one atomic CAS ──

    /// <summary>
    /// Old subscription tuples a handoff away from subscription retires. None while the desired selection is still
    /// subscription: retirement never happens solely from expiry or before the desired mode changed.
    /// </summary>
    private static List<ManagedBinding> RetirementTuples(Agent agent, ManagedRecord record) =>
        agent.NativeAuthMode == StockSubscriptionBinding.Mode ? [] : [.. record.SubscriptionTuples().DistinctBy(b => b.Epoch)];

    /// <summary>
    /// Retirement eligibility for every old tuple, in its persisted scope: valid scope, exact UUID (or the sandbox proven
    /// absent), none of its providers still attached, and no workload/supervisor container running. First fault or null.
    /// </summary>
    private async Task<string?> RetirementFaultAsync(string agentName, IReadOnlyList<ManagedBinding> tuples, CancellationToken ct)
    {
        using var bounded = new CancellationTokenSource(FanoutTimeout);
        var token = bounded.Token;
        foreach (var tuple in tuples)
        {
            var label = tuple.Epoch.ToString("N") + ":";
            if (ScopeFault(tuple) is { } scope) return label + scope;
            if (string.IsNullOrEmpty(tuple.SandboxId))
            {
                if (!await SandboxDeletedAsync(tuple, token)) return label + "sandbox_not_proven_absent";
                continue;
            }
            if (await VerifyIdentityAsync(tuple, token) is { } identity)
            {
                if (identity.Code != "sandbox_unobservable" || !await SandboxDeletedAsync(tuple, token)) return label + identity.Code;
            }
            else if (await AttachedAsync(tuple, token) is not { } attached) return label + "provider_attachment_unobservable";
            else if (tuple.AllProviders().Any(attached.Contains)) return label + "provider_still_attached";
            if (await QuiescentAsync(tuple, token) is { } quiet) return label + quiet;
        }
        logger.LogInformation("Managed subscription retirement for {Agent}: {Count} tuple(s) verified cut and quiescent", agentName, tuples.Count);
        return null;
    }

    private static readonly JsonSerializerOptions ArchiveOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Writes each complete old binding, its identities, cleanup evidence and the successful handoff receipt to an
    /// immutable <c>managed/&lt;agent&gt;/retired/&lt;epoch&gt;.json</c> (temp write, fsync, atomic rename, 0700/0600, hash
    /// readback). An identical earlier archive of the same epoch (a lost CAS) is reused; different content conflicts.
    /// </summary>
    private (List<RetiredSubscription>? Refs, string? Fault) ArchiveRetirements(string agentName, IReadOnlyList<ManagedBinding> tuples, string target)
    {
        var refs = new List<RetiredSubscription>();
        var directory = Path.Combine(ConfigDir, "managed", agentName, "retired");
        try
        {
            RefuseLinkedAncestors(directory);
            PrivateDirectory(directory);
            if (!OperatingSystem.IsWindows() && File.GetUnixFileMode(directory) != (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute))
                return (null, "archive_directory_not_private");
            foreach (var tuple in tuples)
            {
                var path = Path.Combine(directory, tuple.Epoch.ToString("N") + ".json");
                var binding = JsonSerializer.SerializeToNode(tuple, ArchiveOptions)!;
                var now = Now;
                byte[] bytes;
                if (AuthorityEntry(path) is { } entry)
                {
                    if (!entry) return (null, "archive_entry_unsafe:" + tuple.Epoch.ToString("N"));
                    bytes = File.ReadAllBytes(path);
                    var existing = JsonNode.Parse(bytes)?["binding"];
                    if (existing is null || !JsonNode.DeepEquals(existing, binding)) return (null, "archive_conflict:" + tuple.Epoch.ToString("N"));
                    now = JsonNode.Parse(bytes)!["retiredAt"]!.GetValue<DateTimeOffset>();
                }
                else
                {
                    bytes = Encoding.UTF8.GetBytes(new JsonObject
                    {
                        ["version"] = 1,
                        ["agent"] = agentName,
                        ["retiredAt"] = now.ToString("O"),
                        ["binding"] = binding,
                        ["cleanupEvidence"] = new JsonArray("admission_cut", "scoped_uuid_verified", "providers_detached_verified", "quiescent_verified"),
                        ["handoff"] = new JsonObject { ["target"] = target, ["succeededAt"] = now.ToString("O") },
                    }.ToJsonString());
                    var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
                    if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                    try
                    {
                        using (var stream = new FileStream(temp, options)) { stream.Write(bytes); stream.Flush(flushToDisk: true); }
                        File.Move(temp, path, overwrite: false);
                    }
                    finally { if (File.Exists(temp)) File.Delete(temp); }
                }
                var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
                if (AuthorityEntry(path) != true || Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))) != hash
                    || !OperatingSystem.IsWindows() && File.GetUnixFileMode(path) != (UnixFileMode.UserRead | UnixFileMode.UserWrite))
                    return (null, "archive_readback_failed:" + tuple.Epoch.ToString("N"));
                refs.Add(new(tuple.Epoch, tuple.SandboxWorkspace!, tuple.SandboxId, tuple.Subscription!.ProviderWorkspace, tuple.Subscription.ProviderId,
                    tuple.Subscription.OwnerGeneration, path, hash, now));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException)
        { return (null, "archive_write_failed"); }
        return (refs, null);
    }

    /// <summary>Removes the archived tuples from Applied/Candidate/Superseded and appends their inert references.</summary>
    private static ManagedRecord Retire(ManagedRecord record, IReadOnlyList<ManagedBinding> tuples, IReadOnlyList<RetiredSubscription> refs)
    {
        var epochs = tuples.Select(t => t.Epoch).ToHashSet();
        return record with
        {
            Applied = record.Applied is { IsSubscription: true } a && epochs.Contains(a.Epoch) ? null : record.Applied,
            Candidate = record.Candidate is { IsSubscription: true } c && epochs.Contains(c.Epoch) ? null : record.Candidate,
            Superseded = [.. record.Superseded.Where(h => !(h.IsSubscription && epochs.Contains(h.Epoch)))],
            RetiredSubscriptions = [.. record.RetiredSubscriptions, .. refs],
        };
    }

    /// <summary>Changed/unobservable generation: hold, then one bounded revoke/Stop fanout; never Start.</summary>
    private async Task<(ManagedRecord, string?, int?, Result)> HoldForGenerationAsync(Agent agent, ManagedRecord record, string generation, CancellationToken ct)
    {
        var errors = generation == "gateway_generation_changed" ? await RevokeFanoutAsync(agent.Name, record, ct) : [];
        return Hold(record, 409, generation, [.. errors]);
    }

    /// <summary>
    /// #489 Gateway restart row: a generation change is planned only when this record is durably
    /// stopped/revoked with no candidate or errors, its sandbox keeps the same UUID, Stopped and
    /// quiescent, and every other owned managed record is likewise idle, stopped and quiescent.
    /// </summary>
    private async Task<bool> PlannedRestartAsync(string agentName, ManagedRecord record, ManagedBinding binding, CancellationToken ct)
    {
        if (record.State is not ("stopped" or "revoked") || record.Candidate is not null || record.Errors.Count > 0) return false;
        if (await StoppedQuiescentAsync(binding, ct) is not null) return false;
        IReadOnlyList<Agent> agents;
        try { agents = await store.ListAsync(ct); }
        catch (Exception ex) when (ex is DbException or InvalidOperationException) { return false; }
        if (await host.GatewayGenerationAsync(ct) is not { } generation) return false;
        foreach (var other in agents.Where(a => !string.Equals(a.Name, agentName, StringComparison.OrdinalIgnoreCase)))
        {
            if (string.IsNullOrEmpty(other.AppliedRuntimeBinding)) continue;
            ManagedRecord owned;
            try { owned = ManagedRecord.Parse(other.AppliedRuntimeBinding); }
            catch (JsonException) { return false; }
            if (owned.Applied is null && owned.Candidate is null) continue;
            if (owned.Operation is not null || owned.Candidate is not null) return false;
            // A peer already rebound to the current generation (same UUID, no hold) is fine running or stopped.
            if (owned.Applied!.GatewayGeneration == generation && owned.State is ("applied" or "stopped") && owned.Errors.Count == 0)
            {
                if (await VerifyIdentityAsync(owned.Applied, ct) is not null) return false;
                continue;
            }
            // A clean revoke that was retired to Docker, or whose sandbox is verifiably deleted, holds nothing
            // (reviews F6r/F9) once no container runs under its UUID; unresolved state still holds.
            if (owned.State == "revoked" && owned.Errors.Count == 0 && await RetiredOrDeletedAsync(other, owned.Applied!, ct)) continue;
            if (owned.State is not ("stopped" or "revoked")) return false;
            if (await StoppedQuiescentAsync(owned.Applied!, ct) is not null) return false;
        }
        return true;
    }

    private async Task<bool> RetiredOrDeletedAsync(Agent other, ManagedBinding binding, CancellationToken ct)
    {
        var retired = false;
        if (other.AppliedRuntimeKind == "docker")
        {
            try
            {
                var path = AuthorityPath(other.Name);
                retired = AuthorityEntry(path) == true
                    && JsonNode.Parse(StockEnrollmentAuthority.ReadBounded(path))?["State"]?.GetValue<string>() == StockAuthoritySource.RetiredState;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
                or InvalidOperationException or StockIdentityDenied) { retired = false; }
        }
        if (!retired && !await SandboxDeletedAsync(binding, ct)) return false;
        return await QuiescentAsync(binding, ct) is null;
    }

    /// <summary>Upper bound on <c>sandbox list</c> pages read before absence is refused as unproven.</summary>
    internal const int MaxListPages = 64;

    /// <summary>
    /// Deleted only when the gateway lists successfully without the name across every page; a failed get alone is
    /// unobservable. Pinned 0.1.5 <c>sandbox list -o json</c> prints <c>{"sandboxes":[…],"next_page_token":"…"}</c>
    /// (review F9); an unparsable page, a repeated token or an unexhausted page bound is never absence.
    /// </summary>
    private async Task<bool> SandboxDeletedAsync(ManagedBinding binding, CancellationToken ct)
    {
        // #502: absence is proven only inside the binding's persisted scope, never the ambient default.
        if (ScopeFault(binding) is not null || await GetAsync(binding, ct) is not null) return false;
        var name = binding.SandboxName;
        var token = "";
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var page = 0; page < MaxListPages; page++)
        {
            var list = await cli.RunAsync(Scoped(binding, token.Length == 0 ? ["sandbox", "list", "-o", "json"]
                : ["sandbox", "list", "-o", "json", "--page-token", token]), null, LegTimeout, ct);
            if (list.Missing || list.TimedOut || list.ExitCode != 0) return false;
            try
            {
                using var document = JsonDocument.Parse(list.Stdout);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("sandboxes", out var sandboxes) || sandboxes.ValueKind != JsonValueKind.Array
                    || !root.TryGetProperty("next_page_token", out var next) || next.ValueKind != JsonValueKind.String) return false;
                if (!sandboxes.EnumerateArray().All(e => e.ValueKind == JsonValueKind.Object && e.TryGetProperty("name", out var n)
                    && n.ValueKind == JsonValueKind.String && n.GetString() != name)) return false;
                token = next.GetString()!;
            }
            catch (JsonException) { return false; }
            if (token.Length == 0) return true;
            if (!seen.Add(token)) return false;
        }
        return false;
    }

    private async Task<string?> StoppedQuiescentAsync(ManagedBinding binding, CancellationToken ct)
    {
        if (ScopeFault(binding) is { } scope) return scope;
        var sandbox = await GetAsync(binding, ct);
        if (sandbox is null) return "sandbox_unobservable";
        if (sandbox.Value.Id != binding.SandboxId) return "sandbox_identity_changed";
        return sandbox.Value.Phase == "Stopped" ? await QuiescentAsync(binding, ct) : "sandbox_not_stopped";
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
    internal async Task<List<string>> RevokeFanoutAsync(string agentName, ManagedRecord record, CancellationToken ct, bool stop = true,
        bool keepRetired = false)
    {
        var errors = new List<string>();
        var targets = new[] { record.Applied, record.Candidate }.OfType<ManagedBinding>().DistinctBy(b => b.Epoch).ToList();
        // Cleanup legs run to their own bounded deadlines; the caller's token is never linked, so a
        // client disconnect cannot cancel them or the Stop.
        using var overall = new CancellationTokenSource(FanoutTimeout);
        var legs = new List<Task<string?>>
        {
            Leg("admission", _ =>
            {
                var path = AuthorityPath(agentName);
                if (!File.Exists(path)) return Task.FromResult<string?>(null);
                var current = JsonNode.Parse(StockEnrollmentAuthority.ReadBounded(path))!.AsObject();
                if (keepRetired && current["State"]?.GetValue<string>() == StockAuthoritySource.RetiredState) return Task.FromResult<string?>(null);
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
            // #502: a subscription target without a valid persisted scope gets no resource command at all.
            if (ScopeFault(target) is { } scope)
            {
                errors.Add("scope:" + target.SandboxName + ":" + scope);
                continue;
            }
            // Review A1: a target recorded before its UUID (a failed pre-create apply) is clean only once the paged
            // absence proof shows its exact owned name absent; nothing is detached, stopped or deleted for it.
            if (string.IsNullOrEmpty(target.SandboxId))
                legs.Add(Leg("unverified_target:" + target.SandboxName, async token =>
                    await SandboxDeletedAsync(target, token) ? null : "sandbox_not_proven_absent"));
        }
        var scoped = targets.Where(t => ScopeFault(t) is null && !string.IsNullOrEmpty(t.SandboxId)).ToList();
        foreach (var target in scoped.DistinctBy(t => (t.SandboxWorkspace, t.SandboxId)))
        {
            // Applied and candidate may share one sandbox; detach the union of their providers. #502: grouped by persisted
            // sandbox workspace/UUID, including retained superseded subscription tuples of the same sandbox.
            var group = scoped.Where(t => t.SandboxId == target.SandboxId && t.SandboxWorkspace == target.SandboxWorkspace)
                .Concat(target.IsSubscription ? record.Superseded.Where(h => h.IsSubscription && h.SandboxId == target.SandboxId
                    && h.SandboxWorkspace == target.SandboxWorkspace) : []).ToList();
            var providers = group.SelectMany(t => t.AllProviders()).Distinct(StringComparer.Ordinal).ToList();
            legs.Add(Leg("detach:" + target.SandboxName, async token =>
            {
                if (target.IsSubscription) return await VerifiedDetachAsync(target, providers, token) is { } detach ? await DeletedOrAsync(detach, target, token) : null;
                if (await VerifyIdentityAsync(target, token) is { } identity) return await DeletedOrAsync(identity.Code, target, token);
                // Every attached provider is detached; one failure does not skip the others.
                var unresolved = false;
                foreach (var provider in providers)
                {
                    var run = await cli.RunAsync(Scoped(target, ["sandbox", "provider", "detach", target.SandboxName, provider]), null, LegTimeout, token);
                    unresolved |= run.ExitCode != 0 || run.TimedOut;
                }
                return unresolved ? "provider_detach_unknown" : null;
            }));
            if (stop) legs.Add(Leg("stop:" + target.SandboxName, async token =>
                await LifecycleAsync("stop", target, token) is { } fault ? await DeletedOrAsync(fault, target, token) : await QuiescentAsync(target, token)));
        }
        // #502: retained superseded subscription sandboxes not already covered: verified detach (only providers still
        // attached), then verified stop/quiescence, each in its own persisted workspace; a missing scope never runs.
        var covered = scoped.Select(t => (t.SandboxWorkspace, t.SandboxId)).ToHashSet();
        foreach (var history in record.Superseded.Where(h => h.IsSubscription && !string.IsNullOrEmpty(h.SandboxId))
                     .DistinctBy(h => (h.SandboxWorkspace, h.SandboxId)).Where(h => !covered.Contains((h.SandboxWorkspace, h.SandboxId))))
        {
            if (ScopeFault(history) is { } scope) { errors.Add("scope:" + history.SandboxName + ":" + scope); continue; }
            var providers = record.Superseded.Where(h => h.IsSubscription && h.SandboxId == history.SandboxId && h.SandboxWorkspace == history.SandboxWorkspace)
                .SelectMany(h => h.AllProviders()).Distinct(StringComparer.Ordinal).ToList();
            legs.Add(Leg("detach:" + history.SandboxName, async token =>
                await VerifiedDetachAsync(history, providers, token) is { } detach ? await DeletedOrAsync(detach, history, token) : null));
            if (stop) legs.Add(Leg("stop:" + history.SandboxName, async token =>
                await StopVerifiedAsync(history, token) is { } fault ? await DeletedOrAsync(fault, history, token) : null));
        }
        foreach (var leg in await Task.WhenAll(legs))
            if (leg is not null) errors.Add(leg);
        return errors;

        // Review F6r: a sandbox verifiably deleted (absent from every list page) has nothing left to detach or
        // stop; it is clean only once no container runs under its UUID. Any other fault stays unresolved.
        async Task<string?> DeletedOrAsync(string? fault, ManagedBinding target, CancellationToken token) =>
            fault == "sandbox_unobservable" && await SandboxDeletedAsync(target, token) ? await QuiescentAsync(target, token) : fault;

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

    /// <summary>
    /// #502 verified detach inside the persisted scope: exact UUID before and after, detach only the union providers the
    /// scoped attachment list still shows (idempotent for an already detached tuple), then prove none remain attached.
    /// </summary>
    private async Task<string?> VerifiedDetachAsync(ManagedBinding target, IReadOnlyList<string> providers, CancellationToken ct)
    {
        if (await VerifyIdentityAsync(target, ct) is { } identity) return identity.Code;
        if (await AttachedAsync(target, ct) is not { } attached) return "provider_attachment_unobservable";
        var unresolved = false;
        foreach (var provider in providers.Where(attached.Contains))
        {
            var run = await cli.RunAsync(Scoped(target, ["sandbox", "provider", "detach", target.SandboxName, provider]), null, LegTimeout, ct);
            unresolved |= run.ExitCode != 0 || run.TimedOut;
        }
        if (await VerifyIdentityAsync(target, ct) is { } after) return after.Code;
        if (await AttachedAsync(target, ct) is not { } remaining) return "provider_attachment_unobservable";
        return unresolved || providers.Any(remaining.Contains) ? "provider_detach_unknown" : null;
    }

    /// <summary>Every provider attached to the sandbox, across all scoped pages; null when unobservable or ambiguous.</summary>
    private async Task<HashSet<string>?> AttachedAsync(ManagedBinding target, CancellationToken ct)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var token = "";
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var page = 0; page < MaxListPages; page++)
        {
            var list = await cli.RunAsync(Scoped(target, token.Length == 0 ? ["sandbox", "provider", "list", target.SandboxName, "-o", "json"]
                : ["sandbox", "provider", "list", target.SandboxName, "-o", "json", "--page-token", token]), null, LegTimeout, ct);
            if (list.Missing || list.TimedOut || list.ExitCode != 0 || StockSubscriptionBinding.ParseAttachedPage(list.Stdout) is not { } parsed) return null;
            names.UnionWith(parsed.Names);
            token = parsed.NextToken;
            if (token.Length == 0) return names;
            if (!seen.Add(token)) return null;
        }
        return null;
    }

    /// <summary>Name-based stock control with exact-UUID checks before and after; a changed target is unknown.</summary>
    private async Task<string?> LifecycleAsync(string verb, ManagedBinding binding, CancellationToken ct)
    {
        if (await VerifyIdentityAsync(binding, ct) is { } before) return before.Code;
        var run = await cli.RunAsync(Scoped(binding, ["sandbox", verb, binding.SandboxName]), null, LegTimeout, ct);
        if (run.Missing) return "stock_cli_missing";
        if (run.TimedOut || run.ExitCode != 0) return verb + "_unknown";
        return await VerifyIdentityAsync(binding, ct) is { } after ? after.Code : null;
    }

    private async Task<Result?> VerifyIdentityAsync(ManagedBinding binding, CancellationToken ct)
    {
        if (ScopeFault(binding) is { } scope) return Result.Refused(409, scope);
        var sandbox = await GetAsync(binding, ct);
        if (sandbox is null) return Result.Refused(502, "sandbox_unobservable");
        return sandbox.Value.Id == binding.SandboxId ? null : Result.Refused(409, "sandbox_identity_changed");
    }

    private async Task<(string Id, string Phase)?> GetAsync(ManagedBinding binding, CancellationToken ct)
    {
        // #502: a subscription binding without a valid persisted scope is never looked up unscoped.
        if (ScopeFault(binding) is not null) return null;
        var name = binding.SandboxName;
        var run = await cli.RunAsync(Scoped(binding, ["sandbox", "get", name, "-o", "json"]), null, LegTimeout, ct);
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

    /// <summary>Bounded, best-effort label read of the binding's containers; never fails the operation.</summary>
    internal async Task<ManagedContainers> ObserveContainersAsync(ManagedBinding binding)
    {
        IReadOnlyList<StockSandboxContainer>? containers;
        try
        {
            using var bounded = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            containers = await host.SandboxContainersAsync(binding.SandboxId, bounded.Token);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { containers = null; }
        return ManagedContainers.From(containers, binding.Containers, DateTimeOffset.UtcNow);
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> _containersReadAt = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Longest a container observation is served before the status refresh re-reads Docker labels.</summary>
    internal TimeSpan ContainerRefreshWindow { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// #492 review F11: the periodic status refresh re-observes the applied binding's containers (bounded,
    /// at most once per window per agent) so a workload that exits or is removed after a lifecycle
    /// operation is not still shown as its last recorded status. Display-only: the record is unchanged.
    /// </summary>
    public async Task RefreshContainersAsync(string name, AgentRegistry registry, CancellationToken ct)
    {
        if (!Enabled) return;
        var now = DateTimeOffset.UtcNow;
        if (_containersReadAt.TryGetValue(name, out var last) && now - last < ContainerRefreshWindow && now >= last) return;
        _containersReadAt[name] = now;
        Agent? agent;
        try { agent = await store.LoadAsync(name, ct); }
        catch (Exception ex) when (ex is DbException or InvalidOperationException) { return; }
        if (agent is not { AppliedRuntimeKind: "openshell" }) return;
        ManagedRecord record;
        try { record = ManagedRecord.Parse(agent.AppliedRuntimeBinding); }
        catch (JsonException) { return; }
        // An in-flight operation records its own observation when it finishes.
        if (record is not { Operation: null, Applied: { } binding }) return;
        registry.ObserveManagedContainers(name, binding.SandboxId, await ObserveContainersAsync(binding));
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
        var download = await cli.RunAsync(Scoped(binding, ["sandbox", "download", binding.SandboxName, "/workspace", staging]), null, TransferTimeout, ct);
        if (download.ExitCode != 0 || download.TimedOut) return "workspace_download_unknown";
        if (await VerifyIdentityAsync(binding, ct) is { } after) return after.Code;
        // Mirror the working tree (deletions included); excluded account/control state is never touched.
        try { CopyTree(staging, target, PrivateWorkspaceEntries, prune: true); return null; }
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
            // Owned by this process's effective UID, and no linked ancestor can redirect trust/state writes.
            try { RefuseLinkedAncestors(ConfigDir); }
            catch (IOException) { return "managed_config_dir_not_private"; }
            // Review A7: setup/upgrade create the dir as the host operator while the orchestrator container runs as root,
            // so the operator uid they record (StockOpenShell:ConfigDirOwnerUid) is the other authorized owner.
            if (OwnerUid(ConfigDir) is not { } owner || (owner != GetEffectiveUid() && owner != AuthorizedConfigDirOwner))
                return "managed_config_dir_not_owned";
        }
        return null;
    }

    /// <summary>The documented host-operator owner of ConfigDir (setup/upgrade record <c>id -u</c>); null when unset.</summary>
    private uint? AuthorizedConfigDirOwner =>
        uint.TryParse(config["StockOpenShell:ConfigDirOwnerUid"], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var uid) ? uid : null;

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUid();

    /// <summary>The runtime's own lstat (PAL FileStatus: Flags, Mode, Uid, ...); null when unobservable.</summary>
    [System.Runtime.InteropServices.DllImport("libSystem.Native", EntryPoint = "SystemNative_LStat")]
    private static extern int LStat([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPUTF8Str)] string path, byte[] status);

    internal static uint? OwnerUid(string path)
    {
        var status = new byte[512];
        try { return LStat(path, status) == 0 ? BitConverter.ToUInt32(status, 8) : null; }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return null; }
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

    internal static JsonObject Authority(JsonObject template, Agent agent, string sandboxId, Guid epoch, string policySha, string state,
        DateTimeOffset? capAt = null)
    {
        // #502: a subscription enrollment expires no later than its outside binding, so admission self-expires even
        // when cleanup cannot run.
        var expires = DateTimeOffset.UtcNow.AddDays(365);
        if (capAt is { } cap && cap < expires) expires = cap;
        var record = new JsonObject
        {
            ["State"] = state,
            ["Agent"] = agent.Name,
            ["SandboxId"] = sandboxId,
            ["Epoch"] = epoch.ToString(),
            ["ExpiresAt"] = expires.ToString("O"),
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

    /// <summary>
    /// Review W4: the control credential sits beside the record in the shared authority directory; the observer
    /// credential goes only to the orchestrator-private 0700 <c>managed/observer</c> sibling, never the authority
    /// directory that Telegram/Temporal/Bridge mount. A legacy observer copy in the authority directory is removed.
    /// </summary>
    private void WriteBrokerPasswords(string agent, IReadOnlyDictionary<string, string> passwords)
    {
        WriteText(Path.Combine(AuthorityDir, $"{agent}.control.password"), passwords["control"]);
        WriteText(Path.Combine(ObserverDir, $"{agent}.observer.password"), passwords["observer"]);
        var legacy = Path.Combine(AuthorityDir, $"{agent}.observer.password");
        if (File.Exists(legacy) || new FileInfo(legacy).LinkTarget is not null) File.Delete(legacy);
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
        Guid epoch, SandboxPolicyVersion policy, string sandboxId, long? botId, IEnumerable<string> outsideSecrets, ManagedBinding binding)
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
            ["EnrollmentFile"] = WorkloadEnrollmentFile,
            ["AuthorityFile"] = WorkloadEnrollmentFile,
            ["BrokerPasswordFile"] = WorkloadHome + "/trust/workload-broker-password",
            // The staged launcher reads this tree as its content root; roles/projects are read here too.
            ["AppRoot"] = WorkloadAppRoot,
        };
        var agentNode = settings["Agent"]!.AsObject();
        if (binding.Subscription is { } subscription)
        {
            // #502: only the selected nonsecret account/provider/owner-generation metadata; never the outside file,
            // token material, credentials or approval evidence, and no API-key reference or fallback.
            agentNode["NativeAuthMode"] = StockSubscriptionBinding.Mode;
            agentNode.Remove("ProviderApiKeyEnvRef");
            settings["StockOpenShell"]!["SubscriptionAccountId"] = subscription.AccountId;
            settings["StockOpenShell"]!["SubscriptionProvider"] = binding.Provider;
            settings["StockOpenShell"]!["SubscriptionOwnerGeneration"] = subscription.OwnerGeneration.ToString("D");
        }
        else
        {
            agentNode["NativeAuthMode"] = "api_key";
            agentNode["ProviderApiKeyEnvRef"] = agent.ProviderApiKeyEnvRef;
        }
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
        WriteRecord(Path.Combine(bundle, "trust", "enrollment.json"), Authority(template, agent, sandboxId, epoch, policy.Sha256, "revoked", binding.Subscription?.ExpiresAt));
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

    /// <summary>
    /// Refuses symlinks in the source tree, on either root's ancestors, or at any destination entry. With
    /// <paramref name="prune"/>, destination entries absent from the source (or of another kind) are removed
    /// first, so the target mirrors the source; top-level excluded entries are neither copied nor removed.
    /// </summary>
    private static void CopyTree(string source, string target, IReadOnlyCollection<string> exclude, bool prune = false)
    {
        RefuseLinkedAncestors(source);
        RefuseLinks(source);
        RefuseLinkedAncestors(target);
        Copy(source, target, top: true);

        void Copy(string from, string to, bool top)
        {
            if (new DirectoryInfo(to).LinkTarget is not null) throw new IOException("symlink_refused");
            if (prune && Directory.Exists(to))
                foreach (var stale in new DirectoryInfo(to).EnumerateFileSystemInfos())
                {
                    if (top && exclude.Contains(stale.Name)) continue;
                    var kept = stale is DirectoryInfo ? Directory.Exists(Path.Combine(from, stale.Name)) : File.Exists(Path.Combine(from, stale.Name));
                    if (stale.LinkTarget is not null) throw new IOException("symlink_refused");
                    if (kept) continue;
                    if (stale is DirectoryInfo staleDir) { RefuseLinks(staleDir.FullName); staleDir.Delete(recursive: true); }
                    else stale.Delete();
                }
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

    /// <summary>
    /// After the verified Docker handoff is committed, the revoked record is kept for custody but marked
    /// retired so ordinary telemetry and control for the Docker agent are no longer held. Failure keeps the hold
    /// and returns false (no record to retire is true).
    /// </summary>
    internal bool RetireAuthority(string agentName)
    {
        try
        {
            var path = AuthorityPath(agentName);
            // Review F6: lstat semantics; only verified absence or a verified retired write is success.
            if (AuthorityEntry(path) is not { } regular) return true;
            if (!regular) return false;
            var current = JsonNode.Parse(StockEnrollmentAuthority.ReadBounded(path))!.AsObject();
            if (current["State"]?.GetValue<string>() is not ("revoked" or StockAuthoritySource.RetiredState)) return false;
            current["State"] = StockAuthoritySource.RetiredState;
            WriteRecord(path, current);
            return AuthorityEntry(path) == true
                && JsonNode.Parse(StockEnrollmentAuthority.ReadBounded(path))?["State"]?.GetValue<string>() == StockAuthoritySource.RetiredState;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
            or InvalidOperationException or StockIdentityDenied)
        { logger.LogWarning("Retiring the managed record for {Agent} failed; ordinary traffic stays held", agentName); }
        return false;
    }

    /// <summary>
    /// No-follow entry check (lstat semantics): <c>null</c> when nothing at all exists at the path (a dangling link
    /// is not absence), <c>true</c> for a plain regular file, <c>false</c> for a link, directory or other entry.
    /// </summary>
    internal static bool? AuthorityEntry(string path)
    {
        var info = new FileInfo(path);
        if (info.LinkTarget is not null) return false;
        var attributes = info.Attributes;
        if ((int)attributes == -1) return Directory.Exists(path) ? false : null;
        const FileAttributes unsafeKinds = FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device;
        if ((attributes & unsafeKinds) != 0 || !info.Exists) return false;
        return IsRegularFile(path);
    }

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int LibcOpen(string path, int flags);

    /// <summary>
    /// Regular-file proof without following or blocking: Linux opens O_RDONLY|O_NONBLOCK|O_NOFOLLOW|O_CLOEXEC, so a
    /// FIFO never blocks and a link fails, and only a seekable entry (never a FIFO or socket) is regular.
    /// </summary>
    private static bool IsRegularFile(string path)
    {
        if (!OperatingSystem.IsLinux()) return true;
        // Review W6: O_NOFOLLOW differs by architecture; an unknown one is never trusted as regular.
        if (NoFollowOpenFlags(System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture) is not { } flags) return false;
        try
        {
            var fd = LibcOpen(path, flags);
            if (fd < 0) return false;
            using var handle = new Microsoft.Win32.SafeHandles.SafeFileHandle(fd, ownsHandle: true);
            using var stream = new FileStream(handle, FileAccess.Read, 1);
            return stream.CanSeek;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or DllNotFoundException) { return false; }
    }

    /// <summary>
    /// Linux <c>O_RDONLY|O_NONBLOCK|O_NOFOLLOW|O_CLOEXEC</c> for the process architecture. O_NONBLOCK (0x800) and
    /// O_CLOEXEC (0x80000) are shared; O_NOFOLLOW is 0x20000 on x86/x64 and 0x8000 on arm/arm64. Null when unknown.
    /// </summary>
    internal static int? NoFollowOpenFlags(System.Runtime.InteropServices.Architecture architecture) => architecture switch
    {
        System.Runtime.InteropServices.Architecture.X64 or System.Runtime.InteropServices.Architecture.X86 => 0x800 | 0x20000 | 0x80000,
        System.Runtime.InteropServices.Architecture.Arm64 or System.Runtime.InteropServices.Architecture.Arm => 0x800 | 0x8000 | 0x80000,
        _ => null,
    };

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
