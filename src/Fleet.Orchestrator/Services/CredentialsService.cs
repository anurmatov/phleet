using System.Text.Json;
using System.Text.Json.Serialization;
using Fleet.Orchestrator.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Fleet.Orchestrator.Services;

// ── Registry types ─────────────────────────────────────────────────────────────

public sealed record CredentialEntry(
    string Key,
    string Description,
    string Category,
    bool Editable,
    bool BootstrapOnly,
    bool Sensitive,
    bool ConfirmRecreate,
    List<string> Consumers);

public sealed class CredentialRegistry
{
    private readonly Dictionary<string, CredentialEntry> _byKey;

    public CredentialRegistry(List<CredentialEntry> entries)
    {
        Entries = entries;
        _byKey = entries.ToDictionary(e => e.Key, StringComparer.Ordinal);
    }

    public List<CredentialEntry> Entries { get; }

    public bool TryGet(string key, out CredentialEntry? entry) =>
        _byKey.TryGetValue(key, out entry);
}

// ── Propagation results ────────────────────────────────────────────────────────

public sealed record PropagationPreview(
    string Key,
    List<string> Infra,
    List<string> Agents,
    bool SelfRecreate,
    List<string> Warnings);

public sealed record PropagationResult(
    List<string> InfraRestarted,
    List<string> InfraFailed,
    List<string> AgentsReprovisioned,
    List<(string Name, string Error)> AgentsFailed,
    List<string> Warnings,
    bool SelfRecreate);

// ── Credentials service ────────────────────────────────────────────────────────

/// <summary>
/// Encapsulates all credential registry and save logic:
///   - Registry loading + startup validation
///   - Propagation preview computation (registry-driven + agent_env_refs)
///   - Atomic .env write (mutex + bak-before-write + tmp rename)
///   - Propagation execution (infra recreate + agent reprovision)
///   - Audit trail write (fire-and-forget, after mutex release)
/// </summary>
public sealed class CredentialsService
{
    // Serialised write access — concurrent PUT calls queue here
    private static readonly SemaphoreSlim WriteMutex = new(1, 1);

    private readonly string _envFilePath;
    private readonly string _orchestratorContainerName;
    private readonly CredentialRegistry _registry;
    private readonly ICredentialsReader _credentialsReader;
    private readonly SetupService _setup;
    private readonly ContainerProvisioningService _provisioning;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<CredentialsService> _logger;

    public CredentialsService(
        Microsoft.Extensions.Configuration.IConfiguration config,
        CredentialRegistry registry,
        ICredentialsReader credentialsReader,
        SetupService setup,
        ContainerProvisioningService provisioning,
        IServiceScopeFactory scopeFactory,
        ILogger<CredentialsService> logger)
    {
        _envFilePath = config["Provisioning:EnvFilePath"] ?? "/app/deploy/.env";
        _orchestratorContainerName = config["Provisioning:OrchestratorContainerName"] ?? "fleet-orchestrator";
        _registry = registry;
        _credentialsReader = credentialsReader;
        _setup = setup;
        _provisioning = provisioning;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    // ── Registry ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Loads and validates credentials-registry.json at the given path.
    /// Throws if the file is absent, unparseable, or contains duplicate keys.
    /// </summary>
    public static CredentialRegistry LoadRegistry(string jsonPath)
    {
        if (!File.Exists(jsonPath))
            throw new InvalidOperationException(
                $"credentials-registry.json not found at '{jsonPath}'. " +
                "This file is required for the orchestrator to start.");

        string json;
        try { json = File.ReadAllText(jsonPath); }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Could not read credentials-registry.json at '{jsonPath}': {ex.Message}", ex);
        }

        RegistryFile? parsed;
        try { parsed = JsonSerializer.Deserialize<RegistryFile>(json, JsonOpts); }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"credentials-registry.json is not valid JSON: {ex.Message}", ex);
        }

        if (parsed?.Entries is null)
            throw new InvalidOperationException(
                "credentials-registry.json is missing the 'entries' array.");

        // Duplicate key check
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in parsed.Entries)
        {
            if (!seen.Add(e.Key))
                throw new InvalidOperationException(
                    $"credentials-registry.json contains duplicate key '{e.Key}'. " +
                    "Each key must appear exactly once.");
        }

        return new CredentialRegistry(parsed.Entries);
    }

    private sealed class RegistryFile
    {
        [JsonPropertyName("entries")]
        public List<CredentialEntry>? Entries { get; set; }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    // ── .env read ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads a single key value directly from .env (bypasses ICredentialsReader cache).
    /// Returns null when key is absent or file is unreadable.
    /// </summary>
    public string? ReadEnvValue(string key)
    {
        try
        {
            return EnvFileCredentialsReader.LoadEnvFile(_envFilePath)
                .TryGetValue(key, out var v) ? v : null;
        }
        catch { return null; }
    }

    // ── Propagation preview ───────────────────────────────────────────────────

    public async Task<PropagationPreview> ComputePropagationAsync(string key, CancellationToken ct = default)
    {
        var (infra, selfRecreate) = GetInfraScope(_registry, key, _orchestratorContainerName);
        var (agents, agentWarnings) = await ComputeAgentPropagationAsync(key, ct);
        return new PropagationPreview(key, infra, agents, selfRecreate, agentWarnings);
    }

    // ── Atomic write + propagation ────────────────────────────────────────────

    public async Task<PropagationResult> SaveAndPropagateAsync(
        string key, string newValue, CancellationToken ct = default)
    {
        if (!await WriteMutex.WaitAsync(TimeSpan.FromSeconds(30), ct))
            throw new TimeoutException("Another credential save is in progress. Try again shortly.");

        try
        {
            // 1. Read current .env
            var lines = File.Exists(_envFilePath)
                ? await File.ReadAllLinesAsync(_envFilePath, ct)
                : Array.Empty<string>();

            // 2. Upsert the key into the lines (first-= split, duplicate-key handling)
            var updated = UpsertEnvLine(lines, key, newValue);

            // 3. Backup current .env BEFORE writing new content
            if (File.Exists(_envFilePath))
            {
                try { File.Copy(_envFilePath, _envFilePath + ".bak", overwrite: true); }
                catch (Exception ex) { _logger.LogWarning(ex, "Could not write .env.bak"); }
            }

            // 4. Write to .env.tmp then rename atomically
            var dir = Path.GetDirectoryName(_envFilePath) ?? "/";
            var tmpPath = Path.Combine(dir, Path.GetFileName(_envFilePath) + ".tmp");
            await File.WriteAllLinesAsync(tmpPath, updated, ct);
            try { File.Move(tmpPath, _envFilePath, overwrite: true); }
            catch
            {
                try { File.Delete(tmpPath); } catch { /* best effort */ }
                throw;
            }

            // 5. Invalidate ICredentialsReader cache synchronously
            _credentialsReader.InvalidateCache();
        }
        finally
        {
            WriteMutex.Release();
        }

        // 6. Write audit row fire-and-forget (mutex already released)
        _ = Task.Run(async () =>
        {
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var db = scope.ServiceProvider.GetService<OrchestratorDbContext>();
                if (db is not null)
                {
                    db.CredentialsAudit.Add(new CredentialsAudit { KeyName = key });
                    await db.SaveChangesAsync(CancellationToken.None);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not write credentials audit row for key={Key}", key);
            }
        }, CancellationToken.None);

        // 7. Run propagation (mutex already released)
        return await RunPropagationAsync(key, ct);
    }

    // ── Retry propagation for a single target ─────────────────────────────────

    /// <summary>
    /// Returns true if the target is in the re-computed propagation set and the operation
    /// was attempted. Returns false if the target is not in scope (caller should 404).
    /// Throws on infra/agent execution failure (caller wraps in 207).
    /// </summary>
    public async Task<(bool inScope, bool success, string? error)> RetryPropagationAsync(
        string key, string target, string type, CancellationToken ct = default)
    {
        if (type.Equals("infra", StringComparison.OrdinalIgnoreCase))
        {
            var (infra, _) = GetInfraScope(_registry, key, _orchestratorContainerName);
            if (!infra.Contains(target, StringComparer.OrdinalIgnoreCase))
                return (false, false, null);

            try
            {
                await _setup.InfraContainerRecreateAsync(target, ct);
                return (true, true, null);
            }
            catch (Exception ex) { return (true, false, ex.Message); }
        }
        else // agent
        {
            List<string> agents;
            try { (agents, _) = await ComputeAgentPropagationAsync(key, ct); }
            catch (DbUnavailableException ex)
            {
                return (true, false, ex.Message);
            }

            if (!agents.Contains(target, StringComparer.OrdinalIgnoreCase))
                return (false, false, null);

            try
            {
                var result = await _provisioning.ReprovisionAsync(target, ct: ct);
                return result.Success
                    ? (true, true, null)
                    : (true, false, result.Message);
            }
            catch (Exception ex) { return (true, false, ex.Message); }
        }
    }

    // ── Infra propagation (registry-driven) ──────────────────────────────────

    /// <summary>
    /// Returns the infra containers that should be restarted when <paramref name="changedKey"/>
    /// changes, and whether the orchestrator itself is among them.
    /// Uses <see cref="CredentialRegistry"/> as the authoritative scope source —
    /// replaces the old docker-compose.yml grepping that incorrectly included any service
    /// with <c>env_file: .env</c> regardless of which key was changed.
    /// </summary>
    public static (List<string> containers, bool selfRecreate) GetInfraScope(
        CredentialRegistry registry, string changedKey, string orchestratorContainerName)
    {
        if (!registry.TryGet(changedKey, out var entry) || entry is null)
            return ([], false);

        var containers = new List<string>(entry.Consumers);
        var selfRecreate = containers.Contains(orchestratorContainerName, StringComparer.OrdinalIgnoreCase);
        return (containers, selfRecreate);
    }

    // ── Agent propagation (DB) ────────────────────────────────────────────────

    private async Task<(List<string> agents, List<string> warnings)> ComputeAgentPropagationAsync(
        string changedKey, CancellationToken ct)
    {
        var warnings = new List<string>();
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetService<OrchestratorDbContext>();
            if (db is null) return ([], warnings);

            var agents = await db.Agents
                .Where(a => a.EnvRefs.Any(r => r.EnvKeyName == changedKey))
                .Select(a => a.Name)
                .ToListAsync(ct);

            return (agents, warnings);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "agent_env_refs query failed for key={Key}", changedKey);
            warnings.Add($"db_unavailable: {ex.Message}");
            throw new DbUnavailableException(ex.Message, ex);
        }
    }

    // ── Propagation execution ─────────────────────────────────────────────────

    private async Task<PropagationResult> RunPropagationAsync(string key, CancellationToken ct)
    {
        var (infra, selfRecreate) = GetInfraScope(_registry, key, _orchestratorContainerName);
        var warnings = new List<string>();

        List<string> agents;
        List<string> agentWarnings;
        try { (agents, agentWarnings) = await ComputeAgentPropagationAsync(key, ct); }
        catch (DbUnavailableException ex)
        {
            agentWarnings = [$"db_unavailable: {ex.Message}"];
            agents = [];
        }
        warnings.AddRange(agentWarnings);

        var infraToRestart = selfRecreate
            ? infra.Where(c => !c.Equals(_orchestratorContainerName, StringComparison.OrdinalIgnoreCase)).ToList()
            : infra;

        var infraRestarted = new List<string>();
        var infraFailed = new List<string>();
        foreach (var container in infraToRestart)
        {
            try
            {
                await _setup.InfraContainerRecreateAsync(container, ct);
                infraRestarted.Add(container);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to recreate infra container {Name}", container);
                infraFailed.Add(container);
            }
        }

        var agentsReprovisioned = new List<string>();
        var agentsFailed = new List<(string, string)>();
        foreach (var agentName in agents)
        {
            try
            {
                var result = await _provisioning.ReprovisionAsync(agentName, ct: ct);
                if (result.Success)
                    agentsReprovisioned.Add(agentName);
                else
                    agentsFailed.Add((agentName, result.Message));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to reprovision agent {Name}", agentName);
                agentsFailed.Add((agentName, ex.Message));
            }
        }

        return new PropagationResult(
            infraRestarted, infraFailed,
            agentsReprovisioned, agentsFailed,
            warnings, selfRecreate);
    }

    // ── .env upsert ────────────────────────────────────────────────────────────

    public static string[] UpsertEnvLine(string[] lines, string key, string value)
    {
        var result = new List<string>(lines.Length + 1);
        var written = false;
        var firstSeen = false;

        for (var i = 0; i < lines.Length; i++)
        {
            var t = lines[i].Trim();
            if (!t.StartsWith('#') && t.Contains('='))
            {
                var k = t[..t.IndexOf('=')].Trim();
                if (string.Equals(k, key, StringComparison.Ordinal))
                {
                    if (!firstSeen)
                    {
                        // First occurrence: replace with new value
                        result.Add($"{key}={value}");
                        written = true;
                        firstSeen = true;
                        continue;
                    }
                    // Subsequent duplicate occurrences: preserve as-is
                }
            }
            result.Add(lines[i]);
        }

        if (!written)
            result.Add($"{key}={value}");

        return result.ToArray();
    }
}

// ── Sentinel exceptions for propagation error reporting ───────────────────────

public sealed class ComposeUnavailableException(string message, Exception? inner = null)
    : Exception(message, inner);

public sealed class DbUnavailableException(string message, Exception? inner = null)
    : Exception(message, inner);
