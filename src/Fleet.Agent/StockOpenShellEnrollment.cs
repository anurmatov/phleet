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

    /// <summary>
    /// #492 review F7: the one <c>codex-root.toml</c> both staged preparation and daemon registration
    /// validate. A managed launch uploads it under <c>StockOpenShell:AppRoot</c>, which must also be the
    /// launcher's content root; the manual profile is unchanged (its content root for preparation, the
    /// image's <see cref="HarnessDefaults.CodexRootTomlContainerPath"/> for registration).
    /// </summary>
    public static string CodexRootFile(IConfiguration configuration, string? contentRoot = null)
    {
        if (bool.TryParse(configuration["StockOpenShell:Enabled"], out var enabled) && enabled
            && configuration["StockOpenShell:AppRoot"] is { Length: > 0 } appRoot)
        {
            if (!Path.IsPathFullyQualified(appRoot)) Deny("stock_enrollment_app_root_invalid");
            if (contentRoot is not null && NormalizeRoot(contentRoot) != NormalizeRoot(appRoot))
                Deny("stock_enrollment_app_root_mismatch");
            return Path.Combine(appRoot, HarnessDefaults.CodexRootTomlFileName);
        }
        return contentRoot is null
            ? HarnessDefaults.CodexRootTomlContainerPath
            : Path.Combine(contentRoot, HarnessDefaults.CodexRootTomlFileName);
    }

    private static string NormalizeRoot(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    /// <summary>
    /// The daemon registration gate: validates the enrollment against the same root file preparation
    /// read (<see cref="CodexRootFile"/>), then requires the prepared native profile.
    /// </summary>
    public static StockBrokerIdentity? ValidateForRegistration(IConfiguration configuration)
    {
        var binding = Validate(configuration, CodexRootFile(configuration));
        if (binding is not null) RequirePrepared(configuration, binding, Environment.GetEnvironmentVariable("HOME") ?? "");
        return binding;
    }

    internal static StockBrokerIdentity? ValidateForRegistration(IConfiguration configuration, string home, Func<uint> effectiveUid,
        Func<string, string?>? environment = null)
    {
        var binding = Validate(configuration, home, effectiveUid, CodexRootFile(configuration), environment);
        if (binding is not null) RequirePrepared(configuration, binding, home, environment);
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
            if (ManagedRouteKeys.Any(key => !string.IsNullOrEmpty(configuration[key])))
                Deny("stock_enrollment_disabled_with_managed_route");
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
            // Managed mode only (#492; #510 also subscription): the agent's own bot and journal capture may hold exactly
            // the stock placeholders from the child environment (the final start's overlay), never a literal or
            // another value. The manual profile, files/send/cross-chat and other routes stay refused.
            var slots = IsManaged(configuration) ? ManagedCredentialSlots(configuration, environment) : null;
            var subscription = configuration["Agent:NativeAuthMode"] == SubscriptionMode;
            if ((slots is null && ManagedRouteKeys.Any(key => !string.IsNullOrEmpty(configuration[key])
                    && !(subscription && SubscriptionKeys.Contains(key))))
                || !subscription && SubscriptionKeys.Any(key => !string.IsNullOrEmpty(configuration[key]))
                || !SlotHolds(configuration, slots, TelegramTokenKey) || !SlotHolds(configuration, slots, JournalTokenKey)
                || !SlotHolds(configuration, slots, JournalStockKey)
                || !SlotHolds(configuration, slots, JournalReadKey) || !SlotHolds(configuration, slots, JournalCrossChatKey)
                // Files/send/cross-chat only with managed capture and its placeholder slots (#492).
                || JournalFilesSelected(configuration) && (slots is null || !slots.ContainsKey(JournalReadKey))
                || configuration.GetValue<bool>("Journal:CrossChatEnabled") && (slots is null || !slots.ContainsKey(JournalCrossChatKey))
                || !string.IsNullOrEmpty(configuration["Conversations:SouthBaseUrl"]) || !string.IsNullOrEmpty(configuration["ClientChannel:OwnerPrincipalToken"]))
                Deny("stock_enrollment_extra_route_unsupported");
            if (effectiveUid() is 0 or uint.MaxValue || !Path.IsPathFullyQualified(home) || home == "/root")
                Deny("stock_enrollment_nonroot_required");
            var work = configuration["Agent:WorkDir"];
            if (string.IsNullOrEmpty(work) || !Path.IsPathFullyQualified(work) || work.Contains('\r') || work.Contains('\n')) Deny("stock_enrollment_workspace_invalid");
            using var mcp = JsonDocument.Parse(StockEnrollmentAuthority.ReadBounded(Path.Combine(work!, ".mcp.json")));
            StockRequestProof.Unique(mcp.RootElement);
            var servers = mcp.RootElement.GetProperty("mcpServers"); StockRequestProof.Unique(servers);
            // Exactly memory plus each first-party service route in the outside record, each at its exact host.
            var memoryName = MemoryMcpName(servers);
            var routes = McpRoutes(authority, configuration, memoryName);
            if (mcp.RootElement.EnumerateObject().Count() != 1 || servers.EnumerateObject().Count() != routes.Count)
                Deny("stock_enrollment_mcp_route_invalid");
            foreach (var (name, url, _) in routes)
            {
                var server = servers.GetProperty(name); StockRequestProof.Unique(server);
                if (server.EnumerateObject().Count() != 1 || server.GetProperty("url").GetString() != url)
                    Deny("stock_enrollment_mcp_route_invalid");
            }
            // #505: only grants under the workload's own memory name count, never the other accepted name's.
            if (!configuration.GetSection("Agent:AllowedTools").GetChildren().Any(c =>
                    StockServices.MemoryTools.Any(tool => c.Value == StockServices.ToolGrant(memoryName, tool))))
                Deny("stock_enrollment_memory_tools_missing");
            var auth = NativeAuthFor(configuration, home, environment);
            if ((auth.Manual && !File.Exists(Path.Combine(home, ".codex-host", "auth.json"))) || !File.Exists(rootFile))
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
            var auth = NativeAuthFor(configuration, home, environment ?? Environment.GetEnvironmentVariable);
            var expected = NativeConfig(configuration, authority, environment ?? Environment.GetEnvironmentVariable, root!, auth);
            var directory = Path.Combine(home, ".codex");
            if (!auth.Manual)
            {
                RequirePrivateDirectory(directory);
                RequirePrivateFile(Path.Combine(directory, "auth.json"));
                RequirePrivateFile(Path.Combine(directory, "config.toml"));
            }
            var actualAuth = StockEnrollmentAuthority.ReadBounded(Path.Combine(directory, "auth.json"));
            if (auth.ApiPlaceholder is { } placeholder) RequireApiAuth(actualAuth, placeholder);
            if (auth.Subscription is { } subscription) RequireSubscriptionAuth(actualAuth, subscription);
            if (StockEnrollmentAuthority.ReadText(Path.Combine(directory, "config.toml")) != expected
                || auth.Manual && !CryptographicOperations.FixedTimeEquals(SHA256.HashData(actualAuth),
                    SHA256.HashData(StockEnrollmentAuthority.ReadBounded(Path.Combine(home, ".codex-host", "auth.json")))))
                throw new InvalidOperationException("stock_enrollment_native_not_prepared");
        }
        catch (Exception ex) when (ex is StockIdentityDenied or IOException or UnauthorizedAccessException or JsonException or ArgumentException or KeyNotFoundException or FormatException
            || ex is InvalidOperationException && !ex.Message.StartsWith("stock_enrollment_", StringComparison.Ordinal))
        { throw new InvalidOperationException("stock_enrollment_native_not_prepared"); }
    }

    private const string ApiLogin = "forced_login_method = \"api\"\n";

    /// <summary>The exact #498 stock HTTP descriptor table (no base_url/env_key/bearer/auth/aws, wire_api default).</summary>
    internal const string SubscriptionDescriptor =
        "\n[model_providers.openai-http]\nname = \"OpenAI\"\nrequires_openai_auth = true\nsupports_websockets = false\n"
        + "supports_standalone_web_search = true\nhttp_headers = { version = \"" + CodexVersion + "\" }\n";

    /// <summary>The one #498 suppression block; analytics stays the generated defaults' own <c>[analytics]</c> table.</summary>
    internal const string SubscriptionFeatures = "\n[features]\nplugins = false\napps = false\n";

    private static readonly string[] SubscriptionRootKeys = ["forced_login_method", "forced_chatgpt_workspace_id", "model_provider"];

    /// <summary>
    /// The one deterministic native config builder shared by preparation and registration checks. Subscription (#502):
    /// forced ChatGPT/account/provider root keys first, then the generated defaults unchanged (their own
    /// <c>[analytics] enabled=false</c> and <c>[history]</c>), then the exact descriptor, one <c>[features]</c> block and the
    /// native MCP tables (memory, then #510 each enrolled first-party route). Duplicate or shadowing keys/tables in the
    /// defaults refuse.
    /// </summary>
    internal static string NativeConfig(IConfiguration configuration, StockEnrollmentAuthority authority, Func<string, string?> environment,
        string root, NativeAuth auth)
    {
        var mcp = NativeMcp(configuration, authority, environment);
        if (auth.ApiPlaceholder is not null) return ApiLogin + root + mcp;
        if (auth.Subscription is not { } subscription) return root + mcp;
        var lines = root.Split('\n');
        if (lines.Any(l => SubscriptionRootKeys.Any(k => l.StartsWith(k + " ", StringComparison.Ordinal) || l.StartsWith(k + "=", StringComparison.Ordinal))
                || l.StartsWith("[features", StringComparison.Ordinal) || l.StartsWith("[model_providers", StringComparison.Ordinal)
                || l.StartsWith("[mcp_servers", StringComparison.Ordinal))
            || !lines.Contains("[analytics]") || !lines.Contains("enabled = false"))
            Deny("stock_enrollment_native_config_mismatch");
        return "forced_login_method = \"chatgpt\"\nforced_chatgpt_workspace_id = " + JsonSerializer.Serialize(subscription.AccountId)
            + "\nmodel_provider = \"openai-http\"\n" + root + SubscriptionDescriptor + SubscriptionFeatures + mcp;
    }

    /// <summary>
    /// The stock example's cache shape: <c>chatgptAuthTokens</c>, null API key, the issued handle as access token, an empty
    /// refresh token, a nonsecret parsing-only ID-token stub and the selected account, with a bounded local timestamp.
    /// </summary>
    internal static byte[] SubscriptionCache(SubscriptionInputs inputs, DateTimeOffset now)
    {
        var seconds = now.ToUnixTimeSeconds();
        static string B64(object value) => Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(value)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var idToken = string.Join('.', B64(new { alg = "none", typ = "JWT" }),
            B64(new { iss = "https://auth.openai.com", aud = "codex", sub = "openshell-app-server", email = "app-server@openshell.local", iat = seconds, exp = seconds + 3600 }),
            "placeholder");
        return JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?>
        {
            ["auth_mode"] = "chatgptAuthTokens",
            ["OPENAI_API_KEY"] = null,
            ["tokens"] = new Dictionary<string, string>
            {
                ["id_token"] = idToken, ["access_token"] = inputs.Handle, ["refresh_token"] = "", ["account_id"] = inputs.AccountId,
            },
            ["last_refresh"] = now.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture),
        });
    }

    /// <summary>Exact cache semantics: the current handle, empty refresh token, selected account, the parsing-only stub.</summary>
    private static void RequireSubscriptionAuth(byte[] bytes, SubscriptionInputs inputs)
    {
        using var json = JsonDocument.Parse(bytes);
        var root = json.RootElement;
        StockRequestProof.Unique(root);
        if (root.EnumerateObject().Count() != 4 || root.GetProperty("auth_mode").GetString() != "chatgptAuthTokens"
            || root.GetProperty("OPENAI_API_KEY").ValueKind != JsonValueKind.Null
            || !DateTimeOffset.TryParse(root.GetProperty("last_refresh").GetString(), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal, out _))
            Deny("stock_enrollment_native_not_prepared");
        var tokens = root.GetProperty("tokens");
        StockRequestProof.Unique(tokens);
        if (tokens.EnumerateObject().Count() != 4 || tokens.GetProperty("access_token").GetString() != inputs.Handle
            || tokens.GetProperty("refresh_token").GetString() != "" || tokens.GetProperty("account_id").GetString() != inputs.AccountId
            || tokens.GetProperty("id_token").GetString() is not { } stub || !IsParsingStub(stub))
            Deny("stock_enrollment_native_not_prepared");
    }

    /// <summary>Only the nonsecret <c>alg=none</c> stub with the fixed example identity and a placeholder signature.</summary>
    private static bool IsParsingStub(string token)
    {
        var parts = token.Split('.');
        if (parts.Length != 3 || parts[2] != "placeholder") return false;
        static byte[] Decode(string part) => Convert.FromBase64String(part.Replace('-', '+').Replace('_', '/').PadRight(part.Length + (4 - part.Length % 4) % 4, '='));
        using var header = JsonDocument.Parse(Decode(parts[0]));
        using var payload = JsonDocument.Parse(Decode(parts[1]));
        var p = payload.RootElement;
        return header.RootElement.EnumerateObject().Count() == 2 && header.RootElement.GetProperty("alg").GetString() == "none"
            && header.RootElement.GetProperty("typ").GetString() == "JWT"
            && p.EnumerateObject().Count() == 6 && p.GetProperty("iss").GetString() == "https://auth.openai.com"
            && p.GetProperty("aud").GetString() == "codex" && p.GetProperty("sub").GetString() == "openshell-app-server"
            && p.GetProperty("email").GetString() == "app-server@openshell.local"
            && p.GetProperty("iat").TryGetInt64(out var iat) && p.GetProperty("exp").TryGetInt64(out var exp) && exp == iat + 3600;
    }

    /// <summary>
    /// #505: the workload's memory endpoint name, exactly one of <c>memory</c> and the ordinary <c>fleet-memory</c> among the
    /// bounded, duplicate-checked <c>.mcp.json</c> servers. None, both or another spelling refuses; nothing picks a default.
    /// Its URL and shape are checked with the other routes.
    /// </summary>
    private static string MemoryMcpName(JsonElement servers)
    {
        if (servers.EnumerateObject().Select(p => p.Name).Where(StockServices.IsMemoryMcpName).ToList() is not [var name])
            throw new InvalidOperationException("stock_enrollment_mcp_route_invalid");
        return name;
    }

    /// <summary>The memory endpoint name from the workload's <c>.mcp.json</c>, re-read for preparation and registration.</summary>
    private static string MemoryMcpName(IConfiguration configuration)
    {
        var work = configuration["Agent:WorkDir"];
        if (string.IsNullOrEmpty(work) || !Path.IsPathFullyQualified(work)) Deny("stock_enrollment_workspace_invalid");
        using var mcp = JsonDocument.Parse(StockEnrollmentAuthority.ReadBounded(Path.Combine(work, ".mcp.json")));
        StockRequestProof.Unique(mcp.RootElement);
        var servers = mcp.RootElement.GetProperty("mcpServers"); StockRequestProof.Unique(servers);
        return MemoryMcpName(servers);
    }

    /// <summary>
    /// Memory under the workload's own endpoint name, then each first-party service route of the outside record, then
    /// the comms-journal read route (#492), as (MCP name, exact URL, callable tools).
    /// </summary>
    private static List<(string Name, string Url, IReadOnlyList<string> Tools)> McpRoutes(StockEnrollmentAuthority authority, IConfiguration configuration,
        string memoryName)
    {
        var routes = new List<(string, string, IReadOnlyList<string>)> { (memoryName, $"https://{authority.MemoryHost}/mcp", StockServices.MemoryTools) };
        foreach (var (service, route) in (authority.Services ?? new Dictionary<string, StockServiceRoute>()).OrderBy(p => p.Key, StringComparer.Ordinal))
            routes.Add((StockServices.McpNames[service], $"https://{route.Host}/mcp", route.Tools));
        if (authority.JournalRead is { } journal)
            routes.Add((StockJournalReadRoute.McpName, $"https://{journal.Host}{journal.Path}", journal.Tools));
        // The agent-local files/send listener: loopback inside the agent process, no egress, its own grant checks.
        if (JournalFilesSelected(configuration))
            routes.Add((StockCredentials.JournalFilesMcpName, StockCredentials.JournalFilesUrl, ["fetch_attachment", "send_attachment"]));
        return routes;
    }

    /// <summary>
    /// The exact native MCP tables: one per route, enabled tools narrowed to the configured AllowedTools
    /// that the route permits (memory's read-only set, or the record's grants), never widened.
    /// </summary>
    private static string NativeMcp(IConfiguration configuration, StockEnrollmentAuthority authority, Func<string, string?> environment)
    {
        var allowed = configuration.GetSection("Agent:AllowedTools").GetChildren().Select(c => c.Value).OfType<string>().ToArray();
        var text = new System.Text.StringBuilder();
        var memoryName = MemoryMcpName(configuration);
        foreach (var (name, url, tools) in McpRoutes(authority, configuration, memoryName))
        {
            var prefix = "mcp__" + name + "__";
            var enabled = allowed.Where(t => t.StartsWith(prefix, StringComparison.Ordinal)).Select(t => t[prefix.Length..])
                .Where(t => tools.Contains(t, StringComparer.Ordinal)).ToArray();
            // #502: the subscription path registers exactly memory_get on the native memory route (its get-only signer
            // rule). #510: every other enrolled route keeps the same enabled-grant ∩ record-tool intersection as API-key mode.
            if (configuration["Agent:NativeAuthMode"] == SubscriptionMode && name == memoryName)
            {
                if (!enabled.Contains("memory_get")) Deny("stock_enrollment_mcp_route_invalid");
                enabled = ["memory_get"];
            }
            text.Append("\n[mcp_servers.").Append(name).Append("]\nurl = ").Append(JsonSerializer.Serialize(url))
                .Append("\nenabled_tools = ").Append(JsonSerializer.Serialize(enabled)).Append('\n');
            // #492: the comms-journal read MCP authenticates with the derived read token, which stays
            // outside; the header carries exactly the placeholder stock issued in the child environment.
            if (name == StockJournalReadRoute.McpName)
            {
                var placeholder = environment(StockCredentials.JournalReadToken);
                if (!StockCredentials.IsPlaceholder(placeholder, StockCredentials.JournalReadToken))
                    Deny("stock_enrollment_journal_read_placeholder_invalid");
                text.Append("http_headers = { Authorization = ").Append(JsonSerializer.Serialize("Bearer " + placeholder)).Append(" }\n");
            }
        }
        return text.ToString();
    }

    internal const string SubscriptionMode = "subscription";
    internal const string SubscriptionHandleKey = "CODEX_AUTH_ACCESS_TOKEN";
    private const string SubscriptionAccountKey = "StockOpenShell:SubscriptionAccountId";
    private const string SubscriptionProviderKey = "StockOpenShell:SubscriptionProvider";
    private const string SubscriptionOwnerKey = "StockOpenShell:SubscriptionOwnerGeneration";
    private static readonly string[] SubscriptionKeys = [SubscriptionAccountKey, SubscriptionProviderKey, SubscriptionOwnerKey];
    /// <summary>Pinned Codex version for the stock <c>openai-http</c> descriptor's public version header (#498).</summary>
    internal const string CodexVersion = "0.164.0";

    /// <summary>#502 selected subscription inputs: the stock-issued access handle and the outside-selected account.</summary>
    internal sealed record SubscriptionInputs(string Handle, string AccountId);

    /// <summary>The selected native auth branch: manual host cache, API-key placeholder or subscription placeholder.</summary>
    internal sealed record NativeAuth(string? ApiPlaceholder, SubscriptionInputs? Subscription)
    {
        public bool Manual => ApiPlaceholder is null && Subscription is null;
    }

    internal static NativeAuth NativeAuthFor(IConfiguration configuration, string home, Func<string, string?> environment) =>
        configuration["Agent:NativeAuthMode"] == SubscriptionMode
            ? new(null, SubscriptionInputsFor(configuration, home, environment))
            : new(ApiPlaceholder(configuration, home, environment), null);

    /// <summary>
    /// #502: subscription mode reads only the stock-issued <c>CODEX_AUTH_ACCESS_TOKEN</c> handle from the child environment
    /// (exact placeholder syntax; outside provider attachment is the issuance proof, syntax alone is not) and the
    /// outside-selected nonsecret account/provider/owner-generation metadata. No API reference or key, no host cache.
    /// </summary>
    private static SubscriptionInputs SubscriptionInputsFor(IConfiguration configuration, string home, Func<string, string?> environment)
    {
        if (!string.IsNullOrEmpty(configuration["Agent:ProviderApiKeyEnvRef"])
            || !string.IsNullOrEmpty(environment("OPENAI_API_KEY")) || !string.IsNullOrEmpty(environment("CODEX_API_KEY")))
            Deny("stock_enrollment_native_auth_invalid");
        var handle = environment(SubscriptionHandleKey);
        if (!StockCredentials.IsPlaceholder(handle, SubscriptionHandleKey)) Deny("stock_enrollment_native_auth_invalid");
        var account = configuration[SubscriptionAccountKey];
        if (account is null || !System.Text.RegularExpressions.Regex.IsMatch(account, @"\A[A-Za-z0-9_-]{1,256}\z")
            || configuration[SubscriptionProviderKey] != configuration["Agent:Name"] + "-codex-sub"
            || !Guid.TryParseExact(configuration[SubscriptionOwnerKey], "D", out var owner) || owner == Guid.Empty)
            Deny("stock_enrollment_subscription_metadata_invalid");
        if (Path.Exists(Path.Combine(home, ".codex-host"))) Deny("stock_enrollment_native_cache_forbidden");
        return new(handle!, account);
    }

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

    private const string TelegramTokenKey = $"{TelegramOptions.Section}:{nameof(TelegramOptions.BotToken)}";
    private const string JournalTokenKey = "Journal:IngestToken";
    private const string JournalStockKey = "Journal:StockPlaceholders";
    private const string JournalReadKey = "Journal:ReadToken";
    private const string JournalCrossChatKey = "Journal:CrossChatToken";

    private static bool JournalFilesSelected(IConfiguration configuration) =>
        configuration.GetValue<bool>("Journal:FilesEnabled") || configuration.GetValue<bool>("Journal:SendEnabled");

    /// <summary>Managed-only route selectors (nonsecret, written by the orchestrator) and the overlay's flag.</summary>
    private static readonly string[] ManagedRouteKeys = ["StockOpenShell:TelegramBotId", "StockOpenShell:JournalCapture", JournalStockKey,
        SubscriptionAccountKey, SubscriptionProviderKey, SubscriptionOwnerKey];

    private static bool SlotHolds(IConfiguration configuration, IReadOnlyDictionary<string, string?>? slots, string key) =>
        string.IsNullOrEmpty(configuration[key]) || slots is not null && slots.TryGetValue(key, out var expected) && configuration[key] == expected;

    /// <summary>
    /// Managed mode (#492): the ordinary Telegram/journal credential slots, filled only from the
    /// placeholders stock issued in the child environment for the attached outside providers. The
    /// orchestrator supplies only the nonsecret bot id and the capture switch; a missing, literal or
    /// foreign value refuses. Empty when neither route is selected.
    /// </summary>
    internal static Dictionary<string, string?> ManagedCredentialSlots(IConfiguration configuration, Func<string, string?> environment)
    {
        var slots = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (configuration["StockOpenShell:TelegramBotId"] is { Length: > 0 } botId)
        {
            var id = botId.Length <= 16 && long.TryParse(botId, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
            slots[TelegramTokenKey] = StockTelegramHttpHandler.SdkToken(id, environment(StockCredentials.TelegramBotToken))
                ?? throw new InvalidOperationException("stock_enrollment_telegram_placeholder_invalid");
        }
        if (configuration["StockOpenShell:JournalCapture"] is { Length: > 0 } capture)
        {
            if (!bool.TryParse(capture, out var on) || !on) Deny("stock_enrollment_journal_placeholder_invalid");
            var placeholder = environment(StockCredentials.JournalIngestToken);
            if (!StockCredentials.IsPlaceholder(placeholder, StockCredentials.JournalIngestToken)) Deny("stock_enrollment_journal_placeholder_invalid");
            slots[JournalTokenKey] = placeholder;
            slots[JournalStockKey] = "true";
            // Files/send and cross-chat use the same derived read / cross-chat tokens as ordinary mode,
            // kept outside; the agent's existing grant checks (requester-only, cross-chat rules) are unchanged.
            if (JournalFilesSelected(configuration))
            {
                var read = environment(StockCredentials.JournalReadToken);
                if (!StockCredentials.IsPlaceholder(read, StockCredentials.JournalReadToken)) Deny("stock_enrollment_journal_placeholder_invalid");
                slots[JournalReadKey] = read;
                if (configuration.GetValue<bool>("Journal:CrossChatEnabled"))
                {
                    var cross = environment(StockCredentials.JournalCrossChatToken);
                    if (!configuration.GetValue<bool>("Journal:SendEnabled") || !StockCredentials.IsPlaceholder(cross, StockCredentials.JournalCrossChatToken))
                        Deny("stock_enrollment_journal_placeholder_invalid");
                    slots[JournalCrossChatKey] = cross;
                }
            }
        }
        return slots;
    }

    /// <summary>
    /// The final managed daemon start's configuration overlay (Program.cs), so every ordinary reader
    /// sees the placeholder-only slots. Empty outside managed mode; the startup gate still validates it.
    /// </summary>
    public static IReadOnlyDictionary<string, string?> ManagedCredentialOverlay(IConfiguration configuration, Func<string, string?>? environment = null) =>
        IsManaged(configuration) ? ManagedCredentialSlots(configuration, environment ?? Environment.GetEnvironmentVariable)
            : new Dictionary<string, string?>();

    /// <summary>
    /// Shared managed-mode classification (#492, #502): stock enabled with an explicit managed auth selection, API key or
    /// subscription. The manual profile (no selection) is not managed.
    /// </summary>
    public static bool IsManaged(IConfiguration configuration) =>
        bool.TryParse(configuration["StockOpenShell:Enabled"], out var enabled) && enabled
        && configuration["Agent:NativeAuthMode"] is "api_key" or SubscriptionMode;

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
            var native = NativeAuthFor(configuration, home, environment);
            var options = configuration.GetSection(AgentOptions.Section).Get<AgentOptions>()!;
            var root = HarnessDefaults.Build(AgentHarness.InputFor(options, _ => null)!).CodexRootToml;
            if (StockEnrollmentAuthority.ReadText(rootFile) != root) Deny("stock_enrollment_native_config_mismatch");
            var config = NativeConfig(configuration, authority, environment, root!, native);
            var directory = Path.Combine(home, ".codex");
            RequireDirectoryAncestors(directory);
            if (!native.Manual)
            {
                if (Path.Exists(directory)) RequirePrivateDirectory(directory);
                else Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                foreach (var leaf in new[] { "auth.json", "config.toml" })
                    if (Path.Exists(Path.Combine(directory, leaf))) RequirePrivateFile(Path.Combine(directory, leaf));
                // Never overwrite a foreign existing cache: only this branch's own earlier placeholder cache is replaced.
                var authPath = Path.Combine(directory, "auth.json");
                if (File.Exists(authPath) && native.ApiPlaceholder is { } existingApi) RequireApiAuth(StockEnrollmentAuthority.ReadBounded(authPath), existingApi);
                if (File.Exists(authPath) && native.Subscription is { } existingSubscription)
                    RequireSubscriptionAuth(StockEnrollmentAuthority.ReadBounded(authPath), existingSubscription);
            }
            else Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var auth = native.ApiPlaceholder is { } placeholder
                ? JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string?> { ["OPENAI_API_KEY"] = placeholder })
                : native.Subscription is { } subscription ? SubscriptionCache(subscription, DateTimeOffset.UtcNow)
                : StockEnrollmentAuthority.ReadBounded(Path.Combine(home, ".codex-host", "auth.json"));
            PrivateWrite(Path.Combine(directory, "auth.json"), auth);
            PrivateWrite(Path.Combine(directory, "config.toml"), System.Text.Encoding.UTF8.GetBytes(config));
            RequirePrepared(configuration, binding, home, environment);
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
