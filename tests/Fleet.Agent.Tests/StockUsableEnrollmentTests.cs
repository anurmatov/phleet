using System.Text.Json;
using Fleet.Agent;
using Fleet.Agent.Configuration;
using Fleet.Agent.Services;
using Fleet.Shared;
using Fleet.Shared.Security;
using Microsoft.Extensions.Configuration;

namespace Fleet.Agent.Tests;

public sealed class StockUsableEnrollmentTests
{
    [Fact]
    public void BoundSupportedProfile_ReturnsTheValidatedConnectionBindingWithoutConnecting()
    {
        using var fixture = new Fixture();
        var binding = StockOpenShellEnrollment.Validate(fixture.Configuration(), fixture.Home, () => 10001, fixture.RootFile);
        Assert.NotNull(binding);
        Assert.Equal("agent1", binding.Agent);
        Assert.Equal("stock-" + fixture.Epoch.ToString("N"), binding.VirtualHost);
    }

    [Fact]
    public void DirectStartup_RequiresPreparedNativeAuthAndExactMemoryConfiguration()
    {
        using var fixture = new Fixture();
        var configuration = fixture.Configuration();
        var binding = StockOpenShellEnrollment.Validate(configuration, fixture.Home, () => 10001, fixture.RootFile)!;
        Assert.Throws<InvalidOperationException>(() => StockOpenShellEnrollment.RequirePrepared(configuration, binding, fixture.Home));
        var directory = Path.Combine(fixture.Home, ".codex");
        Directory.CreateDirectory(directory);
        File.Copy(Path.Combine(fixture.Home, ".codex-host", "auth.json"), Path.Combine(directory, "auth.json"));
        File.WriteAllText(Path.Combine(directory, "config.toml"), File.ReadAllText(fixture.RootFile)
            + "\n[mcp_servers.memory]\nurl = \"https://memory.example.test/mcp\"\nenabled_tools = [\"memory_get\"]\n");
        StockOpenShellEnrollment.RequirePrepared(configuration, binding, fixture.Home);
        File.WriteAllText(Path.Combine(directory, "auth.json"), "foreign-synthetic-credential");
        Assert.Throws<InvalidOperationException>(() => StockOpenShellEnrollment.RequirePrepared(configuration, binding, fixture.Home));
        File.Copy(Path.Combine(fixture.Home, ".codex-host", "auth.json"), Path.Combine(directory, "auth.json"), true);
        File.AppendAllText(Path.Combine(directory, "config.toml"), "\n[mcp_servers.foreign]\nurl = \"https://foreign.example.test/mcp\"\n");
        Assert.Throws<InvalidOperationException>(() => StockOpenShellEnrollment.RequirePrepared(configuration, binding, fixture.Home));
    }

    [Theory]
    [InlineData("root_config", "stock_enrollment_native_config_mismatch")]
    [InlineData("root", "stock_enrollment_nonroot_required")]
    [InlineData("foreign_agent", "stock_enrollment_authority_mismatch")]
    [InlineData("outside_key_visible", "stock_enrollment_outside_key_visible")]
    [InlineData("foreign_policy", "stock_enrollment_policy_missing")]
    [InlineData("mcp_query", "stock_enrollment_mcp_route_invalid")]
    [InlineData("extra_mcp", "stock_enrollment_mcp_route_invalid")]
    [InlineData("unsupported_native", "stock_enrollment_native_profile_unsupported")]
    [InlineData("unverified_route", "stock_enrollment_extra_route_unsupported")]
    [InlineData("wrong_broker", "stock_enrollment_profile_invalid")]
    [InlineData("missing_credential", "stock_enrollment_native_inputs_missing")]
    public void UnsupportedOrWrongBinding_RefusesBeforeRegistration(string fault, string reason)
    {
        using var fixture = new Fixture();
        if (fault == "root_config") File.WriteAllText(fixture.RootFile, "unverified = true");
        if (fault == "foreign_agent") fixture.Record = fixture.Record with { Agent = "agent2" };
        if (fault == "outside_key_visible") File.WriteAllText(fixture.Record.AssertionPrivateKeyPath, "synthetic-non-key");
        if (fault == "foreign_policy") fixture.Record = fixture.Record with { PolicySha256 = "invalid" };
        if (fault == "mcp_query") fixture.Mcp("https://memory.example.test/mcp?agent=foreign");
        if (fault == "extra_mcp") fixture.Mcp("https://memory.example.test/mcp", true);
        if (fault == "unsupported_native") fixture.Settings["Agent:Provider"] = "claude";
        if (fault == "unverified_route") fixture.Settings["Journal:IngestToken"] = "synthetic";
        if (fault == "wrong_broker") fixture.Settings["RabbitMq:Host"] = "foreign.example.test";
        if (fault == "missing_credential") File.Delete(Path.Combine(fixture.Home, ".codex-host", "auth.json"));
        var error = Assert.Throws<InvalidOperationException>(() => StockOpenShellEnrollment.Validate(fixture.Configuration(), fixture.Home,
            () => fault == "root" ? 0u : 10001u, fixture.RootFile));
        Assert.Equal(reason, error.Message);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "stock-profile-" + Guid.NewGuid().ToString("N"));
        public Guid Epoch { get; } = Guid.NewGuid();
        public string Home => Path.Combine(_directory, "home");
        public string RootFile => Path.Combine(_directory, "codex-root.toml");
        public StockEnrollmentAuthority Record { get; set; }
        public Dictionary<string, string?> Settings { get; }
        public Fixture()
        {
            Directory.CreateDirectory(Path.Combine(Home, ".codex-host"));
            File.WriteAllText(Path.Combine(Home, ".codex-host", "auth.json"), "{\"synthetic\":true}");
            File.WriteAllText(RootFile, HarnessDefaults.Build(AgentHarness.InputFor(new AgentOptions { Name = "agent1", Role = "synthetic", Provider = "codex", WorkDir = _directory }, _ => null)!).CodexRootToml);
            var authorityPath = Path.Combine(_directory, "authority.json");
            var password = Path.Combine(_directory, "password");
            File.WriteAllText(password, "synthetic-password");
            Record = new StockEnrollmentAuthority
            {
                State = "active", Agent = "agent1", SandboxId = "synthetic", Epoch = Epoch, ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
                GatewayIssuer = "issuer", GatewayAudience = "audience", GatewayJwksPath = Path.Combine(_directory, "jwks"),
                AssertionPrivateKeyPath = Path.Combine(_directory, "private"), AssertionPublicKeyPath = Path.Combine(_directory, "public"),
                MemoryHost = "memory.example.test", BrokerHost = "broker.example.test", PolicySha256 = new string('b', 64), StockCommit = StockOpenShellEnrollment.StockCommit
            };
            Settings = new() { ["StockOpenShell:Enabled"] = "true", ["StockOpenShell:EnrollmentFile"] = authorityPath,
                ["StockOpenShell:AuthorityFile"] = authorityPath, ["StockOpenShell:BrokerPasswordFile"] = password,
                ["Agent:Name"] = "agent1", ["Agent:Role"] = "synthetic", ["Agent:ShortName"] = "agent1", ["Agent:Provider"] = "codex", ["Agent:Model"] = "sol", ["Agent:WorkDir"] = _directory,
                ["Agent:AllowedTools:0"] = "mcp__memory__memory_get", ["RabbitMq:Host"] = "broker.example.test" };
            Mcp("https://memory.example.test/mcp");
        }
        public void Mcp(string url, bool extra = false)
        {
            var servers = new Dictionary<string, object> { ["memory"] = new { url } };
            if (extra) servers["foreign"] = new { url = "https://foreign.example.test/mcp" };
            File.WriteAllText(Path.Combine(_directory, ".mcp.json"), JsonSerializer.Serialize(new { mcpServers = servers }));
        }
        public IConfiguration Configuration()
        {
            File.WriteAllText(Settings["StockOpenShell:EnrollmentFile"]!, JsonSerializer.Serialize(Record));
            return new ConfigurationBuilder().AddInMemoryCollection(Settings).Build();
        }
        public void Dispose() => Directory.Delete(_directory, true);
    }
}
