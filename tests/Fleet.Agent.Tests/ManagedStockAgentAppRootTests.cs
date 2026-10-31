using System.Text.Json;
using Fleet.Agent.Configuration;
using Fleet.Agent.Services;
using Fleet.Shared;
using Fleet.Shared.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Fleet.Agent.Tests;

/// <summary>
/// #492 review F7: staged preparation and daemon registration must validate the same
/// <c>codex-root.toml</c>. The managed launch uploads it under <c>StockOpenShell:AppRoot</c>; registration
/// used the fixed image path <c>/app/codex-root.toml</c>, so a valid upload was refused at daemon start.
/// The manual profile keeps its existing paths.
/// </summary>
public sealed class ManagedStockAgentAppRootTests
{
    private const string Placeholder = "openshell:resolve:env:v7_OPENAI_API_KEY";

    [Fact]
    public void Registration_validates_the_app_root_file_preparation_read()
    {
        using var f = new Fixture();
        var configuration = f.Config();
        // Preparation: the staged launcher's content root is the AppRoot tree.
        var prepared = StockOpenShellEnrollment.CodexRootFile(configuration, f.AppRoot);
        Assert.Equal(Path.Combine(f.AppRoot, HarnessDefaults.CodexRootTomlFileName), prepared);
        var binding = StockOpenShellEnrollment.Validate(configuration, f.Home, () => 10001, prepared, _ => Placeholder)!;
        StockOpenShellEnrollment.PrepareNative(configuration, f.Home, binding, prepared, _ => Placeholder);

        // Daemon registration: no content root is known, yet it reads the same uploaded file.
        Assert.Equal(prepared, StockOpenShellEnrollment.CodexRootFile(configuration));
        Assert.NotNull(StockOpenShellEnrollment.ValidateForRegistration(configuration, f.Home, () => 10001, _ => Placeholder));
    }

    [Fact]
    public void Registration_reads_the_uploaded_app_root_file_not_the_image_path()
    {
        using var f = new Fixture();
        var configuration = f.Config();
        var root = Path.Combine(f.AppRoot, HarnessDefaults.CodexRootTomlFileName);
        var binding = StockOpenShellEnrollment.Validate(configuration, f.Home, () => 10001, root, _ => Placeholder)!;
        StockOpenShellEnrollment.PrepareNative(configuration, f.Home, binding, root, _ => Placeholder);

        File.WriteAllText(root, "# a different uploaded root\n");
        Assert.Equal("stock_enrollment_native_config_mismatch", Assert.Throws<InvalidOperationException>(() =>
            StockOpenShellEnrollment.ValidateForRegistration(configuration, f.Home, () => 10001, _ => Placeholder)).Message);
        File.Delete(root);
        Assert.Equal("stock_enrollment_native_inputs_missing", Assert.Throws<InvalidOperationException>(() =>
            StockOpenShellEnrollment.ValidateForRegistration(configuration, f.Home, () => 10001, _ => Placeholder)).Message);
    }

    [Fact]
    public void Daemon_registration_routes_through_the_app_root_selection()
    {
        using var f = new Fixture();
        f.Settings["StockOpenShell:AppRoot"] = "relative/app";
        var services = new ServiceCollection();
        Assert.Equal("stock_enrollment_app_root_invalid",
            Assert.Throws<InvalidOperationException>(() => services.AddAgentCoreServices(f.Config())).Message);
    }

    [Fact]
    public void Preparation_refuses_a_content_root_other_than_the_app_root()
    {
        using var f = new Fixture();
        var configuration = f.Config();
        Assert.Equal(Path.Combine(f.AppRoot, HarnessDefaults.CodexRootTomlFileName),
            StockOpenShellEnrollment.CodexRootFile(configuration, f.AppRoot + Path.DirectorySeparatorChar));
        Assert.Equal("stock_enrollment_app_root_mismatch", Assert.Throws<InvalidOperationException>(() =>
            StockOpenShellEnrollment.CodexRootFile(configuration, "/app")).Message);
    }

    [Fact]
    public void Manual_profile_default_paths_are_unchanged()
    {
        using var f = new Fixture();
        f.Settings.Remove("StockOpenShell:AppRoot");
        var manual = f.Config();
        Assert.Equal(HarnessDefaults.CodexRootTomlContainerPath, StockOpenShellEnrollment.CodexRootFile(manual));
        Assert.Equal("/app/codex-root.toml", StockOpenShellEnrollment.CodexRootFile(manual, "/app"));

        // AppRoot is a managed-only key: it never moves a disabled install's paths.
        var disabled = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["StockOpenShell:Enabled"] = "false", ["StockOpenShell:AppRoot"] = f.AppRoot,
        }).Build();
        Assert.Equal(HarnessDefaults.CodexRootTomlContainerPath, StockOpenShellEnrollment.CodexRootFile(disabled));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "managed-approot-" + Guid.NewGuid().ToString("N"));
        public string Home => Path.Combine(_dir, "home");
        public string AppRoot => Path.Combine(Home, "app");
        public Dictionary<string, string?> Settings { get; } = new();

        public Fixture()
        {
            Directory.CreateDirectory(AppRoot);
            var authority = Path.Combine(_dir, "authority.json");
            var password = Path.Combine(_dir, "password");
            File.WriteAllText(password, "synthetic");
            File.WriteAllText(authority, JsonSerializer.Serialize(new StockEnrollmentAuthority
            {
                State = "active", Agent = "agent1", SandboxId = "synthetic", Epoch = Guid.NewGuid(), ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
                GatewayIssuer = "issuer", GatewayAudience = "audience", GatewayJwksPath = Path.Combine(_dir, "jwks"),
                AssertionPrivateKeyPath = Path.Combine(_dir, "private"), AssertionPublicKeyPath = Path.Combine(_dir, "public"),
                MemoryHost = "memory.example.test", BrokerHost = "broker.example.test", PolicySha256 = new string('b', 64),
                StockCommit = StockOpenShellEnrollment.StockCommit,
            }));
            foreach (var (key, value) in new Dictionary<string, string?>
            {
                ["StockOpenShell:Enabled"] = "true", ["StockOpenShell:EnrollmentFile"] = authority,
                ["StockOpenShell:AuthorityFile"] = authority, ["StockOpenShell:BrokerPasswordFile"] = password,
                ["StockOpenShell:AppRoot"] = AppRoot,
                ["Agent:Name"] = "agent1", ["Agent:ShortName"] = "agent1", ["Agent:Role"] = "synthetic", ["Agent:Provider"] = "codex",
                ["Agent:Model"] = "sol", ["Agent:WorkDir"] = _dir, ["Agent:AllowedTools:0"] = "mcp__memory__memory_get",
                ["Agent:NativeAuthMode"] = "api_key", ["Agent:ProviderApiKeyEnvRef"] = "EXTERNAL_KEY_REF", ["RabbitMq:Host"] = "broker.example.test",
            }) Settings[key] = value;
            File.WriteAllText(Path.Combine(_dir, ".mcp.json"), "{\"mcpServers\":{\"memory\":{\"url\":\"https://memory.example.test/mcp\"}}}");
            // Only the uploaded AppRoot tree carries the root file; the image path is not written.
            var options = Config().GetSection(AgentOptions.Section).Get<AgentOptions>()!;
            File.WriteAllText(Path.Combine(AppRoot, HarnessDefaults.CodexRootTomlFileName),
                HarnessDefaults.Build(AgentHarness.InputFor(options, _ => null)!).CodexRootToml);
        }

        public IConfiguration Config() => new ConfigurationBuilder().AddInMemoryCollection(Settings).Build();
        public void Dispose() => Directory.Delete(_dir, true);
    }
}
