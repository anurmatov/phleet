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
///   - Atomic .env write (mutex + bak-before-write + tmp rename)
///   - Propagation execution (infra recreate + agent reprovision)
///   - Audit trail write (fire-and-forget, after mutex release)
/// </summary>
public sealed class CredentialsService
{
    // Serialised write access — concurrent PUT calls queue here
    private static readonly SemaphoreSlim WriteMutex = new(1, 1);

    private readonly string _envFilePath;
    private readonly string _composeFilePath;
    private readonly string _orchestratorContainerName;
    private readonly ICredentialsReader _credentialsReader;
    private readonly SetupService _setup;
    private readonly ContainerProvisioningService _provisioning;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<CredentialsService> _logger;

    public CredentialsService(
        Microsoft.Extensions.Configuration.IConfiguration config,
        ICredentialsReader credentialsReader,
        SetupService setup,
        ContainerProvisioningService provisioning,
        IServiceScopeFactory scopeFactory,
        ILogger<CredentialsService> logger)
    {
        _envFilePath = config["Provisioning:EnvFilePath"] ?? "/app/deploy/.env";
        var baseDir = config["Provisioning:BaseDir"] ?? "/app/deploy";
        _composeFilePath = Path.Combine(baseDir, "docker-compose.yml");
        _orchestratorContainerName = config["Provisioning:OrchestratorContainerName"] ?? "fleet-orchestrator";
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
        var (infra, infraWarnings) = ComputeInfraPropagation(key, throwOnUnavailable: false);
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
            List<string> infra;
            List<string> infraWarnings;
            try { (infra, infraWarnings) = ComputeInfraPropagation(key, throwOnUnavailable: true); }
            catch (ComposeUnavailableException ex)
            {
                return (true, false, ex.Message);
            }

            _ = infraWarnings; // informational
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

    // ── Infra propagation (compose parsing) ───────────────────────────────────

    private (List<string> containers, List<string> warnings) ComputeInfraPropagation(
        string changedKey, bool throwOnUnavailable)
    {
        var containers = new List<string>();
        var warnings = new List<string>();

        if (!File.Exists(_composeFilePath))
        {
            var msg = $"compose_file_absent: {_composeFilePath}";
            if (throwOnUnavailable)
                throw new ComposeUnavailableException(msg);
            warnings.Add(msg);
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

            var composeDir = Path.GetDirectoryName(_composeFilePath) ?? "/";
            var keyRegex = new Regex(@"\$\{" + Regex.Escape(changedKey) + @"\}", RegexOptions.Compiled);

            foreach (var (serviceKeyNode, serviceValueNode) in services.Children)
            {
                var serviceKey = ((YamlScalarNode)serviceKeyNode).Value ?? "";
                if (serviceValueNode is not YamlMappingNode service) continue;

                var containerName = serviceKey; // default: service key
                if (service.Children.TryGetValue(new YamlScalarNode("container_name"), out var cnNode)
                    && cnNode is YamlScalarNode cnScalar && !string.IsNullOrEmpty(cnScalar.Value))
                    containerName = cnScalar.Value;

                if (ServiceConsumesKey(service, changedKey, keyRegex, composeDir, _envFilePath))
                    containers.Add(containerName);
            }
        }
        catch (ComposeUnavailableException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var msg = $"compose_parse_error: {ex.Message}";
            if (throwOnUnavailable)
                throw new ComposeUnavailableException(msg, ex);
            _logger.LogWarning(ex, "Could not parse docker-compose.yml at {Path}", _composeFilePath);
            warnings.Add(msg);
        }

        return (containers, warnings);
    }

    private static bool ServiceConsumesKey(
        YamlMappingNode service, string changedKey, Regex keyRegex,
        string composeDir, string envFilePath)
    {
        // Check env_file: — if present and references our .env, this service consumes all keys
        if (service.Children.TryGetValue(new YamlScalarNode("env_file"), out var envFileNode))
        {
            if (EnvFileNodeContainsDotEnv(envFileNode, composeDir, envFilePath))
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

    private static bool EnvFileNodeContainsDotEnv(YamlNode node, string composeDir, string envFilePath)
    {
        // env_file can be a scalar, sequence of scalars, or sequence of mappings {path: ..., required: ...}
        if (node is YamlScalarNode s)
            return PathMatchesEnvFile(s.Value, composeDir, envFilePath);

        if (node is YamlSequenceNode seq)
        {
            foreach (var item in seq.Children)
            {
                if (item is YamlScalarNode sc && PathMatchesEnvFile(sc.Value, composeDir, envFilePath))
                    return true;
                if (item is YamlMappingNode m
                    && m.Children.TryGetValue(new YamlScalarNode("path"), out var pn)
                    && pn is YamlScalarNode ps && PathMatchesEnvFile(ps.Value, composeDir, envFilePath))
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Resolves the env_file path relative to the compose directory and compares
    /// against the canonical .env file path. Falls back to filename-only comparison
    /// for relative paths that can't be fully resolved.
    /// </summary>
    private static bool PathMatchesEnvFile(string? rawPath, string composeDir, string envFilePath)
    {
        if (string.IsNullOrEmpty(rawPath)) return false;

        // Resolve relative paths against the compose file directory
        var resolved = Path.IsPathRooted(rawPath)
            ? rawPath
            : Path.GetFullPath(Path.Combine(composeDir, rawPath));

        if (string.Equals(resolved, Path.GetFullPath(envFilePath), StringComparison.OrdinalIgnoreCase))
            return true;

        // Fallback: filename-only match (covers the common case of env_file: .env)
        var name = Path.GetFileName(rawPath);
        return string.Equals(name, ".env", StringComparison.OrdinalIgnoreCase);
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
        var (infra, infraWarnings) = ComputeInfraPropagation(key, throwOnUnavailable: false);
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
