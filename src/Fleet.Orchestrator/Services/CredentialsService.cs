using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Fleet.Orchestrator.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using YamlDotNet.RepresentationModel;

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
///   - Propagation preview computation (compose-grep + agent_env_refs)
///   - Atomic .env write (mutex + tmp rename + bak + cache invalidation)
///   - Propagation execution (infra stop/start + agent reprovision)
///   - Audit trail write (fire-and-forget)
/// </summary>
public sealed class CredentialsService
{
    // Serialised write access — concurrent PUT calls queue here
    private static readonly SemaphoreSlim WriteMutex = new(1, 1);

    private readonly string _envFilePath;
    private readonly string _composeFilePath;
    private readonly string _orchestratorContainerName;
    private readonly ICredentialsReader _credentialsReader;
    private readonly DockerService _docker;
    private readonly ContainerProvisioningService _provisioning;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<CredentialsService> _logger;

    public CredentialsService(
        Microsoft.Extensions.Configuration.IConfiguration config,
        ICredentialsReader credentialsReader,
        DockerService docker,
        ContainerProvisioningService provisioning,
        IServiceScopeFactory scopeFactory,
        ILogger<CredentialsService> logger)
    {
        _envFilePath = config["Provisioning:EnvFilePath"] ?? "/app/deploy/.env";
        var baseDir = config["Provisioning:BaseDir"] ?? "/app/deploy";
        _composeFilePath = Path.Combine(baseDir, "docker-compose.yml");
        _orchestratorContainerName = config["Provisioning:OrchestratorContainerName"] ?? "fleet-orchestrator";
        _credentialsReader = credentialsReader;
        _docker = docker;
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
        var (infra, infraWarnings) = ComputeInfraPropagation(key);
        var (agents, agentWarnings) = await ComputeAgentPropagationAsync(key, ct);
        var warnings = new List<string>(infraWarnings.Concat(agentWarnings));
        var selfRecreate = infra.Contains(_orchestratorContainerName, StringComparer.OrdinalIgnoreCase);
        return new PropagationPreview(key, infra, agents, selfRecreate, warnings);
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

            // 3. Write to .env.tmp then rename (atomic)
            var dir = Path.GetDirectoryName(_envFilePath) ?? "/";
            var tmpPath = Path.Combine(dir, Path.GetFileName(_envFilePath) + ".tmp");
            await File.WriteAllLinesAsync(tmpPath, updated, ct);
            File.Move(tmpPath, _envFilePath, overwrite: true);

            // 4. Copy to .env.bak (best-effort)
            try { File.Copy(_envFilePath, _envFilePath + ".bak", overwrite: true); }
            catch (Exception ex) { _logger.LogWarning(ex, "Could not write .env.bak"); }

            // 5. Invalidate ICredentialsReader cache synchronously
            _credentialsReader.InvalidateCache();

            // 6. Write audit row fire-and-forget
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

            // 7. Run propagation
            return await RunPropagationAsync(key, ct);
        }
        finally
        {
            WriteMutex.Release();
        }
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
            List<string> infra;
            List<string> infraWarnings;
            try { (infra, infraWarnings) = ComputeInfraPropagation(key); }
            catch (ComposeUnavailableException ex)
            {
                return (true, false, ex.Message);
            }

            _ = infraWarnings; // informational
            if (!infra.Contains(target, StringComparer.OrdinalIgnoreCase))
                return (false, false, null);

            try
            {
                await _docker.StopContainerAsync(target);
                await _docker.StartContainerAsync(target);
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

    // ── Infra propagation (compose parsing) ───────────────────────────────────

    private (List<string> containers, List<string> warnings) ComputeInfraPropagation(string changedKey)
    {
        var containers = new List<string>();
        var warnings = new List<string>();

        if (!File.Exists(_composeFilePath))
        {
            warnings.Add($"compose_file_absent: {_composeFilePath}");
            return (containers, warnings);
        }

        try
        {
            var yaml = new YamlStream();
            using var reader = new StreamReader(_composeFilePath);
            yaml.Load(reader);

            if (yaml.Documents.Count == 0) return (containers, warnings);
            var root = (YamlMappingNode)yaml.Documents[0].RootNode;

            if (!root.Children.TryGetValue(new YamlScalarNode("services"), out var servicesNode)
                || servicesNode is not YamlMappingNode services)
                return (containers, warnings);

            var keyRegex = new Regex(@"\$\{" + Regex.Escape(changedKey) + @"\}", RegexOptions.Compiled);

            foreach (var (serviceKeyNode, serviceValueNode) in services.Children)
            {
                var serviceKey = ((YamlScalarNode)serviceKeyNode).Value ?? "";
                if (serviceValueNode is not YamlMappingNode service) continue;

                var containerName = serviceKey; // default: service key
                if (service.Children.TryGetValue(new YamlScalarNode("container_name"), out var cnNode)
                    && cnNode is YamlScalarNode cnScalar && !string.IsNullOrEmpty(cnScalar.Value))
                    containerName = cnScalar.Value;

                if (ServiceConsumesKey(service, changedKey, keyRegex))
                    containers.Add(containerName);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not parse docker-compose.yml at {Path}", _composeFilePath);
            warnings.Add($"compose_parse_error: {ex.Message}");
        }

        return (containers, warnings);
    }

    private static bool ServiceConsumesKey(YamlMappingNode service, string changedKey, Regex keyRegex)
    {
        // Check env_file: — if present and references .env (any path), this service consumes all keys
        if (service.Children.TryGetValue(new YamlScalarNode("env_file"), out var envFileNode))
        {
            if (EnvFileNodeContainsDotEnv(envFileNode))
                return true;
        }

        // Check environment:
        if (!service.Children.TryGetValue(new YamlScalarNode("environment"), out var envNode))
        {
            // Also scan all values for ${CHANGED_KEY} interpolation
            return ServiceValuesContainInterpolation(service, keyRegex);
        }

        if (envNode is YamlMappingNode envMapping)
        {
            // Mapping form: KEY: value
            foreach (var (k, _) in envMapping.Children)
            {
                if (k is YamlScalarNode ks && string.Equals(ks.Value, changedKey, StringComparison.Ordinal))
                    return true;
            }
        }
        else if (envNode is YamlSequenceNode envSeq)
        {
            // Sequence form: - KEY=value or - KEY
            foreach (var item in envSeq.Children)
            {
                if (item is not YamlScalarNode scalar || string.IsNullOrEmpty(scalar.Value)) continue;
                var eqIdx = scalar.Value.IndexOf('=');
                var k = eqIdx >= 0 ? scalar.Value[..eqIdx] : scalar.Value;
                if (string.Equals(k, changedKey, StringComparison.Ordinal))
                    return true;
            }
        }

        // Also scan all YAML values in the service for ${CHANGED_KEY} interpolation
        return ServiceValuesContainInterpolation(service, keyRegex);
    }

    private static bool EnvFileNodeContainsDotEnv(YamlNode node)
    {
        // env_file can be a scalar, sequence of scalars, or sequence of mappings {path: ..., required: ...}
        if (node is YamlScalarNode s)
            return PathIsDotEnv(s.Value);

        if (node is YamlSequenceNode seq)
        {
            foreach (var item in seq.Children)
            {
                if (item is YamlScalarNode sc && PathIsDotEnv(sc.Value))
                    return true;
                if (item is YamlMappingNode m
                    && m.Children.TryGetValue(new YamlScalarNode("path"), out var pn)
                    && pn is YamlScalarNode ps && PathIsDotEnv(ps.Value))
                    return true;
            }
        }

        return false;
    }

    private static bool PathIsDotEnv(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        var name = Path.GetFileName(path);
        return string.Equals(name, ".env", StringComparison.Ordinal)
            || string.Equals(name, ".env", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ServiceValuesContainInterpolation(YamlMappingNode service, Regex keyRegex)
    {
        foreach (var (_, v) in service.Children)
        {
            if (v is YamlScalarNode sv && sv.Value is not null && keyRegex.IsMatch(sv.Value))
                return true;
        }
        return false;
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
        var (infra, infraWarnings) = ComputeInfraPropagation(key);
        var warnings = new List<string>(infraWarnings);

        List<string> agents;
        List<string> agentWarnings;
        try { (agents, agentWarnings) = await ComputeAgentPropagationAsync(key, ct); }
        catch (DbUnavailableException ex)
        {
            agentWarnings = [$"db_unavailable: {ex.Message}"];
            agents = [];
        }
        warnings.AddRange(agentWarnings);

        var selfRecreate = infra.Contains(_orchestratorContainerName, StringComparer.OrdinalIgnoreCase);
        var infraToRestart = selfRecreate
            ? infra.Where(c => !c.Equals(_orchestratorContainerName, StringComparison.OrdinalIgnoreCase)).ToList()
            : infra;

        var infraRestarted = new List<string>();
        var infraFailed = new List<string>();
        foreach (var container in infraToRestart)
        {
            try
            {
                await _docker.StopContainerAsync(container);
                await _docker.StartContainerAsync(container);
                infraRestarted.Add(container);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to restart infra container {Name}", container);
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

        // Find last index of the key to handle duplicates (update first, remove rest)
        var lastIdx = -1;
        for (var i = 0; i < lines.Length; i++)
        {
            var t = lines[i].Trim();
            if (t.StartsWith('#') || !t.Contains('=')) continue;
            var k = t[..t.IndexOf('=')].Trim();
            if (string.Equals(k, key, StringComparison.Ordinal)) lastIdx = i;
        }

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
                    }
                    // Subsequent duplicate occurrences: drop them
                    continue;
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
