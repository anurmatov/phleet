using System.Text.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Fleet.Shared.Security;
using Fleet.Agent.Configuration;
using Fleet.Agent.Services;
using Fleet.Shared;
using Microsoft.Extensions.Configuration;

namespace Fleet.Agent;

/// <summary>
/// Startup refusal for the stock whole-agent path. An operator record is intent, not proof that
/// receiving services authenticate a workload. In particular it cannot authorize legacy ?agent
/// attribution. No lifecycle actions, trust provisioning or enrollment writes occur here.
/// </summary>
public static class StockOpenShellEnrollment
{
    public const string StockCommit = "bdb956f2cd65a635f1294fccf1d138d263bf650f";
    public const string ConfigurationKey = "StockOpenShell:EnrollmentFile";
    private static readonly HashSet<string> Fields = new(StringComparer.Ordinal)
    {
        "version", "state", "agent", "sandboxId", "stockCommit", "policySha256", "expiresAt"
    };

    public static StockBrokerIdentity? Validate(IConfiguration configuration)
    {
        var binding = Validate(configuration, Environment.GetEnvironmentVariable("HOME") ?? "",
            () => OperatingSystem.IsLinux() ? GetEffectiveUid() : uint.MaxValue);
        if (binding is not null && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CODEX_HOME")))
            Deny("stock_enrollment_native_config_override");
        return binding;
    }

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUid();

    internal static StockBrokerIdentity? Validate(IConfiguration configuration, string home, Func<uint> effectiveUid, string rootFile = HarnessDefaults.CodexRootTomlContainerPath)
    {
        var enabled = configuration["StockOpenShell:Enabled"];
        var isEnabled = false;
        if (enabled is not (null or "") && !bool.TryParse(enabled, out isEnabled)) Deny("stock_enrollment_enabled_invalid");
        var path = configuration[ConfigurationKey];
        if (!isEnabled)
        {
            if (path is not (null or "")) Deny("stock_enrollment_disabled_with_file");
            if (!string.IsNullOrEmpty(configuration["StockOpenShell:AuthorityFile"])
                || !string.IsNullOrEmpty(configuration["StockOpenShell:BrokerPasswordFile"]))
                Deny("stock_enrollment_disabled_with_authority");
            return null; // Opt-in only; no new behavior for existing installs.
        }
        if (path is null or "") Deny("stock_enrollment_file_missing");
        if (!Path.IsPathFullyQualified(path)) Deny("stock_enrollment_path_invalid");
        var authorityFile = configuration["StockOpenShell:AuthorityFile"];
        if (!string.IsNullOrEmpty(authorityFile))
        {
            // One operator-owned public enrollment record, shared read-only with the outside receivers.
            // EnrollmentFile is retained as a compatibility alias, never an independent identity.
            if (path != authorityFile) Deny("stock_enrollment_authority_mismatch");
            return ValidateProfile(configuration, home, effectiveUid, rootFile, authorityFile);
        }
        string json;
        try
        {
            using var stream = File.OpenRead(path);
            if (stream.Length > 16384) Deny("stock_enrollment_invalid");
            using var reader = new StreamReader(stream);
            json = reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Do not include a path, content, credential or inner exception in the startup log.
            throw new InvalidOperationException("stock_enrollment_unreadable");
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in root.EnumerateObject())
                if (!Fields.Contains(field.Name) || !seen.Add(field.Name)) Deny("stock_enrollment_invalid");
            if (!seen.SetEquals(Fields) || root.GetProperty("version").GetInt32() != 1) Deny("stock_enrollment_invalid");
            var state = root.GetProperty("state").GetString();
            if (state == "revoked") Deny("stock_enrollment_revoked");
            if (state != "active") Deny("stock_enrollment_state_unknown");
            if (string.IsNullOrWhiteSpace(configuration["Agent:Name"])
                || root.GetProperty("agent").GetString() != configuration["Agent:Name"])
                Deny("stock_enrollment_agent_mismatch");
            if (root.GetProperty("stockCommit").GetString() != StockCommit) Deny("stock_enrollment_stock_commit");
            if (string.IsNullOrWhiteSpace(root.GetProperty("sandboxId").GetString())) Deny("stock_enrollment_sandbox_missing");
            var policy = root.GetProperty("policySha256").GetString();
            if (policy is not { Length: 64 } || policy.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
                Deny("stock_enrollment_policy_missing");
            if (!root.GetProperty("expiresAt").TryGetDateTimeOffset(out var expires)) Deny("stock_enrollment_invalid");
            if (expires <= DateTimeOffset.UtcNow) Deny("stock_enrollment_expired");
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or FormatException
            || ex is InvalidOperationException && !ex.Message.StartsWith("stock_enrollment_", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("stock_enrollment_invalid");
        }

        // The old minimal intent record cannot authorize any receiving route.
        throw new InvalidOperationException("stock_enrollment_required_route_unverified:memory");
    }

    private static StockBrokerIdentity ValidateProfile(IConfiguration configuration, string home, Func<uint> effectiveUid,
        string rootFile, string authorityFile)
    {
        try
        {
            var authority = StockEnrollmentAuthority.Load(authorityFile!);
            if (authority.Agent != configuration["Agent:Name"] || authority.Agent != configuration["Agent:ShortName"])
                Deny("stock_enrollment_authority_mismatch");
            if (authority.StockCommit != StockCommit) Deny("stock_enrollment_stock_commit");
            if (authority.PolicySha256 is not { Length: 64 } || authority.PolicySha256.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
                Deny("stock_enrollment_policy_missing");
            if (File.Exists(authority.AssertionPrivateKeyPath)) Deny("stock_enrollment_outside_key_visible");
            if (configuration["Agent:Provider"] != "codex" || string.IsNullOrWhiteSpace(configuration["Agent:Role"])
                || configuration.GetValue<bool>("Agent:HostedProvider")
                || !string.IsNullOrEmpty(configuration["Agent:HarnessExtra"]) || !string.IsNullOrEmpty(configuration["Agent:CodexOssBaseUrl"])
                || string.IsNullOrWhiteSpace(configuration["Agent:Model"])
                || HostedModelProviders.TryResolve("codex", configuration["Agent:Model"] ?? "", out _, out _)
                || CodexLocalModelProviders.Split(configuration["Agent:Model"] ?? "").Provider is not null)
                Deny("stock_enrollment_native_profile_unsupported");
            if (!string.IsNullOrEmpty(configuration[$"{TelegramOptions.Section}:{nameof(TelegramOptions.BotToken)}"])
                || !string.IsNullOrEmpty(configuration["Journal:IngestToken"])
                || configuration.GetValue<bool>("Journal:FilesEnabled") || configuration.GetValue<bool>("Journal:SendEnabled")
                || !string.IsNullOrEmpty(configuration["Conversations:SouthBaseUrl"]) || !string.IsNullOrEmpty(configuration["ClientChannel:OwnerPrincipalToken"]))
                Deny("stock_enrollment_extra_route_unsupported");
            if (effectiveUid() is 0 or uint.MaxValue || !Path.IsPathFullyQualified(home) || home == "/root")
                Deny("stock_enrollment_nonroot_required");
            var work = configuration["Agent:WorkDir"];
            if (string.IsNullOrEmpty(work) || !Path.IsPathFullyQualified(work)) Deny("stock_enrollment_workspace_invalid");
            using var mcp = JsonDocument.Parse(StockEnrollmentAuthority.ReadBounded(Path.Combine(work!, ".mcp.json")));
            StockRequestProof.Unique(mcp.RootElement);
            var servers = mcp.RootElement.GetProperty("mcpServers"); StockRequestProof.Unique(servers);
            var memory = servers.GetProperty("memory"); StockRequestProof.Unique(memory);
            if (mcp.RootElement.EnumerateObject().Count() != 1 || servers.EnumerateObject().Count() != 1
                || memory.EnumerateObject().Count() != 1 || memory.GetProperty("url").GetString() != $"https://{authority.MemoryHost}/mcp")
                Deny("stock_enrollment_mcp_route_invalid");
            if (!configuration.GetSection("Agent:AllowedTools").GetChildren().Any(c => c.Value is
                "mcp__memory__memory_get" or "mcp__memory__memory_search" or "mcp__memory__memory_list"))
                Deny("stock_enrollment_memory_tools_missing");
            if (!File.Exists(Path.Combine(home, ".codex-host", "auth.json")) || !File.Exists(rootFile))
                Deny("stock_enrollment_native_inputs_missing");
            var options = configuration.GetSection(AgentOptions.Section).Get<AgentOptions>()!;
            var expectedRoot = HarnessDefaults.Build(AgentHarness.InputFor(options, _ => null)!).CodexRootToml;
            if (StockEnrollmentAuthority.ReadText(rootFile) != expectedRoot) Deny("stock_enrollment_native_config_mismatch");
            // Validates exact broker host, epoch vhost, principal and credential BEFORE registrations.
            var broker = StockBrokerIdentity.Optional(authorityFile, configuration["StockOpenShell:BrokerPasswordFile"], "workload")!;
            broker.RequireBinding(authority);
            broker.Configure(new RabbitMQ.Client.ConnectionFactory { HostName = configuration["RabbitMq:Host"] ?? "" });
            return broker;
        }
        catch (Exception ex) when (ex is StockIdentityDenied or IOException or UnauthorizedAccessException or JsonException
            or KeyNotFoundException or ArgumentException or FormatException || ex is InvalidOperationException
            && !ex.Message.StartsWith("stock_enrollment_", StringComparison.Ordinal))
        { throw new InvalidOperationException("stock_enrollment_profile_invalid"); }
    }

    internal static void RequirePrepared(IConfiguration configuration, StockBrokerIdentity binding, string home)
    {
        try
        {
            var authority = StockEnrollmentAuthority.Load(configuration["StockOpenShell:AuthorityFile"]!);
            binding.RequireBinding(authority);
            var options = configuration.GetSection(AgentOptions.Section).Get<AgentOptions>()!;
            var root = HarnessDefaults.Build(AgentHarness.InputFor(options, _ => null)!).CodexRootToml;
            var tools = configuration.GetSection("Agent:AllowedTools").GetChildren().Select(c => c.Value)
                .Where(t => t is "mcp__memory__memory_get" or "mcp__memory__memory_search" or "mcp__memory__memory_list")
                .Select(t => t!["mcp__memory__".Length..]).ToArray();
            var expected = root + "\n[mcp_servers.memory]\nurl = " + JsonSerializer.Serialize($"https://{authority.MemoryHost}/mcp")
                + "\nenabled_tools = " + JsonSerializer.Serialize(tools) + "\n";
            var directory = Path.Combine(home, ".codex");
            if (StockEnrollmentAuthority.ReadText(Path.Combine(directory, "config.toml")) != expected
                || !CryptographicOperations.FixedTimeEquals(
                    SHA256.HashData(StockEnrollmentAuthority.ReadBounded(Path.Combine(directory, "auth.json"))),
                    SHA256.HashData(StockEnrollmentAuthority.ReadBounded(Path.Combine(home, ".codex-host", "auth.json")))))
                throw new InvalidOperationException("stock_enrollment_native_not_prepared");
        }
        catch (Exception ex) when (ex is StockIdentityDenied or IOException or UnauthorizedAccessException or ArgumentException or FormatException
            || ex is InvalidOperationException && !ex.Message.StartsWith("stock_enrollment_", StringComparison.Ordinal))
        { throw new InvalidOperationException("stock_enrollment_native_not_prepared"); }
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Deny(string reason) => throw new InvalidOperationException(reason);
}
