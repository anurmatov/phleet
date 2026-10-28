using System.Text.Json;
using Fleet.Agent;
using Fleet.Agent.Configuration;
using Fleet.Agent.Services;
using Fleet.Shared;
using Fleet.Shared.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Fleet.Agent.Tests;

public sealed class ManagedStockAgentTests
{
    private const string Placeholder = "openshell:resolve:env:v7_OPENAI_API_KEY";

    [Fact]
    public void Api_profile_prepares_placeholder_only_without_manual_auth_cache()
    {
        using var f = new Fixture();
        var configuration = f.Config();
        var binding = StockOpenShellEnrollment.Validate(configuration, f.Home, () => 10001, f.Root, _ => Placeholder)!;
        StockOpenShellEnrollment.PrepareNative(configuration, f.Home, binding, f.Root, _ => Placeholder);
        StockOpenShellEnrollment.RequirePrepared(configuration, binding, f.Home, _ => Placeholder);
        var auth = File.ReadAllText(Path.Combine(f.Home, ".codex", "auth.json"));
        using var json = JsonDocument.Parse(auth);
        Assert.Single(json.RootElement.EnumerateObject());
        Assert.Equal(Placeholder, json.RootElement.GetProperty("OPENAI_API_KEY").GetString());
        Assert.StartsWith("forced_login_method = \"api\"\n", File.ReadAllText(Path.Combine(f.Home, ".codex", "config.toml")));
        Assert.False(Directory.Exists(Path.Combine(f.Home, ".codex-host")));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(f.Home, ".codex", "auth.json")));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(f.Home, ".codex", "config.toml")));
        }
    }

    [Theory]
    [InlineData("openshell:resolve:env:OPENAI_API_KEY")]
    [InlineData("openshell:resolve:env:v7_OPENAI_API_KEY")]
    [InlineData("openshell:resolve:env:sabc123_OPENAI_API_KEY")]
    public void Source_supported_handle_forms_use_only_the_standard_child_environment(string placeholder)
    {
        using var f = new Fixture();
        string? EnvironmentValue(string name)
        {
            Assert.Equal("OPENAI_API_KEY", name);
            return placeholder;
        }
        var c = f.Config();
        var b = StockOpenShellEnrollment.Validate(c, f.Home, () => 10001, f.Root, EnvironmentValue)!;
        StockOpenShellEnrollment.PrepareNative(c, f.Home, b, f.Root, EnvironmentValue);
        StockOpenShellEnrollment.RequirePrepared(c, b, f.Home, EnvironmentValue);
    }

    [Fact]
    public void Public_mode_existing_auth_is_not_overwritten()
    {
        using var f = new Fixture();
        var c = f.Config();
        var b = StockOpenShellEnrollment.Validate(c, f.Home, () => 10001, f.Root, _ => Placeholder)!;
        StockOpenShellEnrollment.PrepareNative(c, f.Home, b, f.Root, _ => Placeholder);
        var path = Path.Combine(f.Home, ".codex", "auth.json");
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);
        else throw new PlatformNotSupportedException("Stock startup requires Linux");
        var bytes = File.ReadAllBytes(path);
        Assert.Throws<InvalidOperationException>(() => StockOpenShellEnrollment.PrepareNative(c, f.Home, b, f.Root, _ => Placeholder));
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sk-synthetic-literal")]
    [InlineData("openshell:resolve:env:OTHER_KEY")]
    [InlineData("openshell:resolve:env:OPENAI_API_KEY\n")]
    [InlineData(" openshell:resolve:env:OPENAI_API_KEY")]
    public void Missing_literal_foreign_or_padded_placeholder_refuses_before_writes(string? value)
    {
        using var f = new Fixture();
        Assert.Throws<InvalidOperationException>(() => StockOpenShellEnrollment.Validate(f.Config(), f.Home, () => 10001, f.Root, _ => value));
        Assert.False(Directory.Exists(Path.Combine(f.Home, ".codex")));
    }

    [Theory]
    [InlineData("oauth", "KEY_REF")]
    [InlineData("api_key", null)]
    [InlineData("api_key", "literal key")]
    [InlineData(null, "KEY_REF")]
    public void Unsupported_mode_or_reference_refuses(string? mode, string? reference)
    {
        using var f = new Fixture();
        f.Settings["Agent:NativeAuthMode"] = mode;
        f.Settings["Agent:ProviderApiKeyEnvRef"] = reference;
        Assert.Throws<InvalidOperationException>(() => StockOpenShellEnrollment.Validate(f.Config(), f.Home, () => 10001, f.Root, _ => Placeholder));
    }

    [Theory]
    [InlineData("manual")]
    [InlineData("oauth")]
    [InlineData("symlink")]
    public void Existing_account_cache_or_unsafe_auth_path_refuses(string fault)
    {
        using var f = new Fixture();
        var binding = StockOpenShellEnrollment.Validate(f.Config(), f.Home, () => 10001, f.Root, _ => Placeholder)!;
        var directory = Path.Combine(f.Home, ".codex");
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        if (fault == "manual") Directory.CreateDirectory(Path.Combine(f.Home, ".codex-host"));
        if (fault == "oauth") File.WriteAllText(Path.Combine(directory, "auth.json"), "{\"tokens\":{\"synthetic\":true}}");
        if (fault == "symlink") File.CreateSymbolicLink(Path.Combine(directory, "auth.json"), f.Root);
        var before = File.ReadAllText(f.Root);
        Assert.Throws<InvalidOperationException>(() => StockOpenShellEnrollment.PrepareNative(f.Config(), f.Home, binding, f.Root, _ => Placeholder));
        Assert.Equal(before, File.ReadAllText(f.Root));
    }

    [Fact]
    public void Direct_startup_rejects_wrong_auth_extra_fields_and_unforced_login()
    {
        using var f = new Fixture();
        var c = f.Config();
        var b = StockOpenShellEnrollment.Validate(c, f.Home, () => 10001, f.Root, _ => Placeholder)!;
        StockOpenShellEnrollment.PrepareNative(c, f.Home, b, f.Root, _ => Placeholder);
        var auth = Path.Combine(f.Home, ".codex", "auth.json");
        var config = Path.Combine(f.Home, ".codex", "config.toml");
        foreach (var bad in new[] { "{}", "{\"OPENAI_API_KEY\":\"literal\"}", JsonSerializer.Serialize(new { OPENAI_API_KEY = Placeholder, tokens = new { } }) })
        {
            File.WriteAllText(auth, bad);
            Assert.Throws<InvalidOperationException>(() => StockOpenShellEnrollment.RequirePrepared(c, b, f.Home, _ => Placeholder));
        }
        File.WriteAllText(auth, JsonSerializer.Serialize(new { OPENAI_API_KEY = Placeholder }));
        File.WriteAllText(config, File.ReadAllText(config).Replace("forced_login_method = \"api\"\n", ""));
        Assert.Throws<InvalidOperationException>(() => StockOpenShellEnrollment.RequirePrepared(c, b, f.Home, _ => Placeholder));
    }

    [Fact]
    public void Manual_profile_keeps_original_auth_bytes_and_does_not_read_api_environment()
    {
        using var f = new Fixture();
        f.Settings.Remove("Agent:NativeAuthMode");
        f.Settings.Remove("Agent:ProviderApiKeyEnvRef");
        Directory.CreateDirectory(Path.Combine(f.Home, ".codex-host"));
        const string manual = "{\"synthetic_manual\":true}\n";
        File.WriteAllText(Path.Combine(f.Home, ".codex-host", "auth.json"), manual);
        var c = f.Config();
        string? NoEnvironment(string _) => throw new Exception("manual profile must not discover API credentials");
        var b = StockOpenShellEnrollment.Validate(c, f.Home, () => 10001, f.Root, NoEnvironment)!;
        StockOpenShellEnrollment.PrepareNative(c, f.Home, b, f.Root, NoEnvironment);
        StockOpenShellEnrollment.RequirePrepared(c, b, f.Home, NoEnvironment);
        Assert.Equal(manual, File.ReadAllText(Path.Combine(f.Home, ".codex", "auth.json")));
        if (OperatingSystem.IsLinux()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            File.GetUnixFileMode(Path.Combine(f.Home, ".codex")));
        Assert.DoesNotContain("forced_login_method", File.ReadAllText(Path.Combine(f.Home, ".codex", "config.toml")));
    }

    [Theory]
    [InlineData("key_ref")]
    [InlineData("mixedCaseRef")]
    public void Saved_reference_names_are_accepted_without_resolving_them(string reference)
    {
        using var f = new Fixture();
        f.Settings["Agent:ProviderApiKeyEnvRef"] = reference;
        var c = f.Config();
        string? ChildEnvironment(string key)
        {
            Assert.Equal("OPENAI_API_KEY", key);
            return Placeholder;
        }
        var b = StockOpenShellEnrollment.Validate(c, f.Home, () => 10001, f.Root, ChildEnvironment)!;
        StockOpenShellEnrollment.PrepareNative(c, f.Home, b, f.Root, ChildEnvironment);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Fresh_profile_under_symlinked_home_refuses_before_directory_or_file_writes(bool manual)
    {
        using var f = new Fixture();
        if (manual)
        {
            f.Settings.Remove("Agent:NativeAuthMode");
            f.Settings.Remove("Agent:ProviderApiKeyEnvRef");
            Directory.CreateDirectory(Path.Combine(f.Home, ".codex-host"));
            File.WriteAllText(Path.Combine(f.Home, ".codex-host", "auth.json"), "{\"synthetic\":true}");
        }
        var actual = f.Home + ".actual";
        Directory.Move(f.Home, actual);
        Directory.CreateSymbolicLink(f.Home, actual);
        var c = f.Config();
        var b = StockOpenShellEnrollment.Validate(c, f.Home, () => 10001, f.Root, _ => Placeholder)!;
        Assert.Throws<InvalidOperationException>(() => StockOpenShellEnrollment.PrepareNative(c, f.Home, b, f.Root, _ => Placeholder));
        Assert.False(Directory.Exists(Path.Combine(actual, ".codex")));
    }

    [Fact]
    public void Revoked_admission_after_validation_cannot_prepare_any_file()
    {
        using var f = new Fixture();
        var c = f.Config();
        var b = StockOpenShellEnrollment.Validate(c, f.Home, () => 10001, f.Root, _ => Placeholder)!;
        var path = f.Settings["StockOpenShell:AuthorityFile"]!;
        var record = JsonSerializer.Deserialize<StockEnrollmentAuthority>(File.ReadAllText(path))!;
        File.WriteAllText(path, JsonSerializer.Serialize(record with { State = "revoked" }));
        Assert.Throws<InvalidOperationException>(() => StockOpenShellEnrollment.PrepareNative(c, f.Home, b, f.Root, _ => Placeholder));
        Assert.False(Directory.Exists(Path.Combine(f.Home, ".codex")));
    }

    [Theory]
    [InlineData("Agent:NativeAuthMode", "api_key")]
    [InlineData("Agent:ProviderApiKeyEnvRef", "EXTERNAL_KEY_REF")]
    public void Disabled_stock_cannot_fall_back_with_managed_auth_fields(string key, string value)
    {
        var c = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [key] = value }).Build();
        Assert.Equal("stock_enrollment_disabled_with_native_auth", Assert.Throws<InvalidOperationException>(
            () => StockOpenShellEnrollment.Validate(c, "/synthetic", () => 10001)).Message);
    }

    [Fact]
    public void Malformed_prepared_auth_is_a_content_free_refusal()
    {
        using var f = new Fixture();
        var c = f.Config();
        var b = StockOpenShellEnrollment.Validate(c, f.Home, () => 10001, f.Root, _ => Placeholder)!;
        StockOpenShellEnrollment.PrepareNative(c, f.Home, b, f.Root, _ => Placeholder);
        File.WriteAllText(Path.Combine(f.Home, ".codex", "auth.json"), "synthetic-private-invalid-json");
        Assert.Equal("stock_enrollment_native_not_prepared", Assert.Throws<InvalidOperationException>(
            () => StockOpenShellEnrollment.RequirePrepared(c, b, f.Home, _ => Placeholder)).Message);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "managed-agent-" + Guid.NewGuid().ToString("N"));
        public string Home => Path.Combine(_dir, "home");
        public string Root => Path.Combine(_dir, "root.toml");
        public Dictionary<string, string?> Settings { get; } = new();
        public Fixture()
        {
            Directory.CreateDirectory(Home);
            File.WriteAllText(Root, HarnessDefaults.Build(AgentHarness.InputFor(new AgentOptions { Name = "agent1", Role = "synthetic", Provider = "codex", WorkDir = _dir }, _ => null)!).CodexRootToml);
            var authority = Path.Combine(_dir, "authority.json");
            var password = Path.Combine(_dir, "password");
            File.WriteAllText(password, "synthetic");
            File.WriteAllText(authority, JsonSerializer.Serialize(new StockEnrollmentAuthority
            {
                State = "active", Agent = "agent1", SandboxId = "synthetic", Epoch = Guid.NewGuid(), ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
                GatewayIssuer = "issuer", GatewayAudience = "audience", GatewayJwksPath = Path.Combine(_dir, "jwks"),
                AssertionPrivateKeyPath = Path.Combine(_dir, "private"), AssertionPublicKeyPath = Path.Combine(_dir, "public"),
                MemoryHost = "memory.example.test", BrokerHost = "broker.example.test", PolicySha256 = new string('b', 64), StockCommit = StockOpenShellEnrollment.StockCommit
            }));
            foreach (var (key, value) in new Dictionary<string, string?>
            {
                ["StockOpenShell:Enabled"] = "true", ["StockOpenShell:EnrollmentFile"] = authority,
                ["StockOpenShell:AuthorityFile"] = authority, ["StockOpenShell:BrokerPasswordFile"] = password,
                ["Agent:Name"] = "agent1", ["Agent:ShortName"] = "agent1", ["Agent:Role"] = "synthetic", ["Agent:Provider"] = "codex",
                ["Agent:Model"] = "sol", ["Agent:WorkDir"] = _dir, ["Agent:AllowedTools:0"] = "mcp__memory__memory_get",
                ["Agent:NativeAuthMode"] = "api_key", ["Agent:ProviderApiKeyEnvRef"] = "EXTERNAL_KEY_REF", ["RabbitMq:Host"] = "broker.example.test"
            }) Settings[key] = value;
            File.WriteAllText(Path.Combine(_dir, ".mcp.json"), "{\"mcpServers\":{\"memory\":{\"url\":\"https://memory.example.test/mcp\"}}}");
        }
        public IConfiguration Config() => new ConfigurationBuilder().AddInMemoryCollection(Settings).Build();
        public void Dispose() => Directory.Delete(_dir, true);
    }
}

/// <summary>#492: the managed adapter's loopback POST cancel is the only extra HTTP route.</summary>
public sealed class ManagedStockLoopbackCancelTests
{
    [Theory]
    [InlineData(true, "POST", "/cancel", "127.0.0.1", true)]
    [InlineData(true, "POST", "/cancel/task%201", "::1", true)]
    [InlineData(true, "POST", "/cancel", "::ffff:127.0.0.1", true)]
    [InlineData(true, "POST", "/cancel", "10.0.0.5", false)]        // non-loopback, whatever headers say
    [InlineData(true, "GET", "/cancel", "127.0.0.1", false)]
    [InlineData(true, "POST", "/cancel_bg/x", "127.0.0.1", false)]
    [InlineData(true, "POST", "/cancel/a/b", "127.0.0.1", false)]
    [InlineData(true, "POST", "/memory", "127.0.0.1", false)]
    [InlineData(false, "POST", "/cancel", "127.0.0.1", false)]      // manual profile keeps health-only
    public void Only_managed_loopback_post_cancel_is_admitted(bool managed, string method, string path, string remote, bool allowed) =>
        Assert.Equal(allowed, StockOpenShellEnrollment.AllowsManagedLoopbackCancel(managed, method, path, System.Net.IPAddress.Parse(remote)));

    [Fact]
    public void Missing_remote_address_is_refused() =>
        Assert.False(StockOpenShellEnrollment.AllowsManagedLoopbackCancel(true, "POST", "/cancel", null));
}

/// <summary>
/// #492: the shipped guard and cancel routes on a real Kestrel host over actual loopback TCP. Non-loopback
/// refusal is covered by <see cref="ManagedStockLoopbackCancelTests"/>.
/// </summary>
public sealed class ManagedStockCancelHostTests
{
    private static async Task<(Microsoft.AspNetCore.Builder.WebApplication App, HttpClient Client)> HostAsync(bool stockMode, bool managed)
    {
        var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder();
        // Explicit loopback listen overrides any ASPNETCORE_HTTP_PORTS the host environment carries.
        builder.WebHost.ConfigureKestrel(k => k.Listen(System.Net.IPAddress.Loopback, 0));
        builder.Logging.ClearProviders();
        var options = Microsoft.Extensions.Options.Options.Create(new AgentOptions { Name = "test", Role = "test", WorkDir = "/tmp", Provider = "codex" });
        builder.Services.AddSingleton(new TaskManager(options, NSubstitute.Substitute.For<IAgentExecutor>(),
            new SessionManager(), Microsoft.Extensions.Logging.Abstractions.NullLogger<TaskManager>.Instance));
        var app = builder.Build();
        app.UseControlGuard(stockMode, managed);
        app.MapGet("/health", () => Microsoft.AspNetCore.Http.Results.Ok());
        app.MapCancelEndpoints();
        await app.StartAsync();
        return (app, new HttpClient { BaseAddress = new Uri(app.Urls.First()) });
    }

    [Theory]
    [InlineData(true, "POST", "/cancel", 200)]
    [InlineData(true, "POST", "/cancel/task%201", 200)]
    [InlineData(true, "GET", "/cancel", 404)]
    [InlineData(true, "POST", "/cancel_bg/x", 404)]
    [InlineData(false, "POST", "/cancel", 404)] // manual profile keeps the health-only surface
    public async Task Managed_loopback_cancel_reaches_the_real_endpoint_only_when_admitted(bool managed, string method, string path, int status)
    {
        var (app, client) = await HostAsync(stockMode: true, managed);
        try
        {
            using var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path));
            Assert.Equal(status, (int)response.StatusCode);
            Assert.Equal(200, (int)(await client.GetAsync("/health")).StatusCode);
        }
        finally { client.Dispose(); await app.DisposeAsync(); }
    }
}
