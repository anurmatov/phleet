using Microsoft.AspNetCore.Http;
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
    /// <summary>
    /// The one HTTP exception in managed mode (#492): the adapter's stock-authenticated
    /// <c>sandbox exec</c> reaches POST /cancel or /cancel/{taskId} from the workload's own loopback.
    /// Only the actual connection address counts; forwarded headers are never read. Everything else,
    /// and the existing manual profile, keeps the health-only surface.
    /// </summary>
    public static bool AllowsManagedLoopbackCancel(bool managed, string method, string? path, System.Net.IPAddress? remote)
    {
        if (!managed || !HttpMethods.IsPost(method) || remote is null) return false;
        if (path is null || !System.Text.RegularExpressions.Regex.IsMatch(path, @"\A/cancel(/[^/]{1,256})?\z")) return false;
        if (remote.IsIPv4MappedToIPv6) remote = remote.MapToIPv4();
        return System.Net.IPAddress.IsLoopback(remote);
    }

    public const string StockCommit = "bdb956f2cd65a635f1294fccf1d138d263bf650f";
    public const string ConfigurationKey = "StockOpenShell:EnrollmentFile";
    private static readonly HashSet<string> Fields = new(StringComparer.Ordinal)
    {
        "version", "state", "agent", "sandboxId", "stockCommit", "policySha256", "expiresAt"
    };

    public static StockBrokerIdentity? Validate(IConfiguration configuration, string rootFile = HarnessDefaults.CodexRootTomlContainerPath)
    {
        var binding = Validate(configuration, Environment.GetEnvironmentVariable("HOME") ?? "",
            () => OperatingSystem.IsLinux() ? GetEffectiveUid() : uint.MaxValue, rootFile);
        if (binding is not null && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CODEX_HOME")))
            Deny("stock_enrollment_native_config_override");
        return binding;
    }

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUid();

    internal static StockBrokerIdentity? Validate(IConfiguration configuration, string home, Func<uint> effectiveUid, string rootFile = HarnessDefaults.CodexRootTomlContainerPath, Func<string, string?>? environment = null)
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
            if (!string.IsNullOrEmpty(configuration["Agent:NativeAuthMode"])
                || !string.IsNullOrEmpty(configuration["Agent:ProviderApiKeyEnvRef"]))
                Deny("stock_enrollment_disabled_with_native_auth");
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
            return ValidateProfile(configuration, home, effectiveUid, rootFile, authorityFile, environment ?? Environment.GetEnvironmentVariable);
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
        string rootFile, string authorityFile, Func<string, string?> environment)
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
            if (string.IsNullOrEmpty(work) || !Path.IsPathFullyQualified(work) || work.Contains('\r') || work.Contains('\n')) Deny("stock_enrollment_workspace_invalid");
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
            var placeholder = ApiPlaceholder(configuration, home, environment);
            var api = placeholder is not null;
            if ((!api && !File.Exists(Path.Combine(home, ".codex-host", "auth.json"))) || !File.Exists(rootFile))
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

    internal static void RequirePrepared(IConfiguration configuration, StockBrokerIdentity binding, string home, Func<string, string?>? environment = null)
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
            var placeholder = ApiPlaceholder(configuration, home, environment ?? Environment.GetEnvironmentVariable);
            var api = placeholder is not null;
            var expected = (api ? ApiLogin : "") + root + "\n[mcp_servers.memory]\nurl = " + JsonSerializer.Serialize($"https://{authority.MemoryHost}/mcp")
                + "\nenabled_tools = " + JsonSerializer.Serialize(tools) + "\n";
            var directory = Path.Combine(home, ".codex");
            if (api)
            {
                RequirePrivateDirectory(directory);
                RequirePrivateFile(Path.Combine(directory, "auth.json"));
                RequirePrivateFile(Path.Combine(directory, "config.toml"));
            }
            var actualAuth = StockEnrollmentAuthority.ReadBounded(Path.Combine(directory, "auth.json"));
            if (api) RequireApiAuth(actualAuth, placeholder!);
            if (StockEnrollmentAuthority.ReadText(Path.Combine(directory, "config.toml")) != expected
                || !api && !CryptographicOperations.FixedTimeEquals(SHA256.HashData(actualAuth),
                    SHA256.HashData(StockEnrollmentAuthority.ReadBounded(Path.Combine(home, ".codex-host", "auth.json")))))
                throw new InvalidOperationException("stock_enrollment_native_not_prepared");
        }
        catch (Exception ex) when (ex is StockIdentityDenied or IOException or UnauthorizedAccessException or JsonException or ArgumentException or KeyNotFoundException or FormatException
            || ex is InvalidOperationException && !ex.Message.StartsWith("stock_enrollment_", StringComparison.Ordinal))
        { throw new InvalidOperationException("stock_enrollment_native_not_prepared"); }
    }

    private const string ApiLogin = "forced_login_method = \"api\"\n";

    // Syntax is a refusal check, NOT proof of stock issuance or secret isolation. Outside
    // provider attachment/readiness and exact runtime binding remain the orchestrator's job.
    private static string? ApiPlaceholder(IConfiguration configuration, string home, Func<string, string?> environment)
    {
        var mode = configuration["Agent:NativeAuthMode"];
        var reference = configuration["Agent:ProviderApiKeyEnvRef"];
        if (string.IsNullOrEmpty(mode) && string.IsNullOrEmpty(reference)) return null;
        if (mode != "api_key" || reference is null || !System.Text.RegularExpressions.Regex.IsMatch(reference, @"\A[A-Za-z_][A-Za-z0-9_]{0,127}\z"))
            Deny("stock_enrollment_native_auth_invalid");
        // Never read the host credential reference in the workload. Stock supplies the standard
        // child environment variable after outside attachment, containing only an issued handle.
        var value = environment("OPENAI_API_KEY");
        if (value is null || value.Length > 1024 || !System.Text.RegularExpressions.Regex.IsMatch(value,
            @"\Aopenshell:resolve:env:(?:v[0-9]+_|s[A-Za-z0-9_]+_)?OPENAI_API_KEY\z"))
            Deny("stock_enrollment_native_auth_invalid");
        if (Path.Exists(Path.Combine(home, ".codex-host"))) Deny("stock_enrollment_native_cache_forbidden");
        return value;
    }

    private static void RequireApiAuth(byte[] bytes, string placeholder)
    {
        using var json = JsonDocument.Parse(bytes);
        StockRequestProof.Unique(json.RootElement);
        if (json.RootElement.EnumerateObject().Count() != 1
            || json.RootElement.GetProperty("OPENAI_API_KEY").GetString() != placeholder)
            Deny("stock_enrollment_native_not_prepared");
    }

    internal static void PrepareNative(IConfiguration configuration, string home, StockBrokerIdentity binding,
        string rootFile = HarnessDefaults.CodexRootTomlContainerPath, Func<string, string?>? environment = null)
    {
        if (!OperatingSystem.IsLinux()) Deny("stock_enrollment_nonroot_required");
        environment ??= Environment.GetEnvironmentVariable;
        try
        {
            // Recheck admission before any writes, including direct invocation of the preparation mode.
            var authority = StockEnrollmentAuthority.Load(configuration["StockOpenShell:AuthorityFile"]!);
            binding.RequireBinding(authority);
            var placeholder = ApiPlaceholder(configuration, home, environment);
            var api = placeholder is not null;
            var options = configuration.GetSection(AgentOptions.Section).Get<AgentOptions>()!;
            var root = HarnessDefaults.Build(AgentHarness.InputFor(options, _ => null)!).CodexRootToml;
            if (StockEnrollmentAuthority.ReadText(rootFile) != root) Deny("stock_enrollment_native_config_mismatch");
            var tools = configuration.GetSection("Agent:AllowedTools").GetChildren().Select(c => c.Value)
                .Where(t => t is "mcp__memory__memory_get" or "mcp__memory__memory_search" or "mcp__memory__memory_list")
                .Select(t => t!["mcp__memory__".Length..]).ToArray();
            var config = (api ? ApiLogin : "") + root + "\n[mcp_servers.memory]\nurl = "
                + JsonSerializer.Serialize($"https://{authority.MemoryHost}/mcp")
                + "\nenabled_tools = " + JsonSerializer.Serialize(tools) + "\n";
            var directory = Path.Combine(home, ".codex");
            RequireDirectoryAncestors(directory);
            if (api)
            {
                if (Path.Exists(directory)) RequirePrivateDirectory(directory);
                else Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                foreach (var leaf in new[] { "auth.json", "config.toml" })
                    if (Path.Exists(Path.Combine(directory, leaf))) RequirePrivateFile(Path.Combine(directory, leaf));
                var authPath = Path.Combine(directory, "auth.json");
                if (File.Exists(authPath)) RequireApiAuth(StockEnrollmentAuthority.ReadBounded(authPath), placeholder!);
            }
            else Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var auth = api ? JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string?> { ["OPENAI_API_KEY"] = placeholder })
                : StockEnrollmentAuthority.ReadBounded(Path.Combine(home, ".codex-host", "auth.json"));
            PrivateWrite(Path.Combine(directory, "auth.json"), auth);
            PrivateWrite(Path.Combine(directory, "config.toml"), System.Text.Encoding.UTF8.GetBytes(config));
            RequirePrepared(configuration, binding, home, _ => placeholder);
        }
        catch (Exception ex) when (ex is StockIdentityDenied or IOException or UnauthorizedAccessException or JsonException
            or ArgumentException or KeyNotFoundException or FormatException || ex is InvalidOperationException
            && !ex.Message.StartsWith("stock_enrollment_", StringComparison.Ordinal))
        { throw new InvalidOperationException("stock_enrollment_native_not_prepared"); }
    }

    private static void RequireDirectoryAncestors(string path)
    {
        // Check the not-yet-created leaf AND every existing ancestor before mkdir or writes.
        for (var current = new DirectoryInfo(path); current is not null; current = current.Parent)
            if (current.LinkTarget is not null) Deny("stock_enrollment_native_path_invalid");
    }

    private static void RequirePrivateDirectory(string path)
    {
        if (!OperatingSystem.IsLinux()) Deny("stock_enrollment_nonroot_required");
        RequireDirectoryAncestors(path);
        if (!Directory.Exists(path) || (File.GetUnixFileMode(path) & ~(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute)) != 0)
            Deny("stock_enrollment_native_path_invalid");
    }

    private static void RequirePrivateFile(string path)
    {
        if (!OperatingSystem.IsLinux()) Deny("stock_enrollment_nonroot_required");
        var file = new FileInfo(path);
        if (file.LinkTarget is not null || !file.Exists || (file.Attributes & FileAttributes.Directory) != 0
            || File.GetUnixFileMode(path) != (UnixFileMode.UserRead | UnixFileMode.UserWrite))
            Deny("stock_enrollment_native_path_invalid");
    }

    private static void PrivateWrite(string path, byte[] bytes)
    {
        if (!OperatingSystem.IsLinux()) Deny("stock_enrollment_nonroot_required");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, new FileStreamOptions { Mode = FileMode.CreateNew,
                Access = FileAccess.Write, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite }))
            { file.Write(bytes); file.Flush(true); }
            File.Move(temporary, path, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Deny(string reason) => throw new InvalidOperationException(reason);
}
