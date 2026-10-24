using Fleet.Agent;
using Fleet.Agent.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Fleet.Agent.Tests;

public sealed class OpenShellStartupTests
{
    [Fact]
    public void BootstrapSelection_RegistersNoCoreExecutorTransportOrIntake()
    {
        using var fixture = new OwnedConfiguration();
        var services = new ServiceCollection();
        Assert.True(services.TryAddOpenShellBootstrapServices(fixture.Config));
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(AgentHarness) || d.ServiceType == typeof(TaskManager)
            || d.ServiceType == typeof(ClaudeExecutor) || d.ServiceType == typeof(CodexExecutor) || d.ServiceType == typeof(GeminiExecutor));
        Assert.Throws<InvalidOperationException>(() => services.AddAgentCoreServices(fixture.Config));
        Assert.Throws<InvalidOperationException>(() => services.AddAgentCliServices());
        Assert.Throws<InvalidOperationException>(() => services.AddAgentDaemonServices(fixture.Config));
    }

    [Theory]
    [InlineData("OpenShell:BootstrapIngress", "http://ingress.example.invalid/identity/ready")]
    [InlineData("OpenShell:BootstrapIngress", "https://ingress.example.invalid/mcp")]
    [InlineData("OpenShell:BootstrapIngress", "https://ingress.example.invalid/identity/ready?agent=agent1")]
    [InlineData("OpenShell:LaunchId", "")]
    [InlineData("OpenShell:IdentityEpoch", "invalid")]
    [InlineData("OpenShell:PlanHash", "invalid")]
    [InlineData("OpenShell:Deadline", "2000-01-01T00:00:00Z")]
    [InlineData("OpenShell:BootstrapCapabilityFile", "/missing-owned-fixture")]
    public void BootstrapInvalidPlan_FailsBeforeAnyRegistration(string key, string value)
    {
        using var fixture = new OwnedConfiguration(); fixture.Values[key] = value;
        var services = new ServiceCollection();
        Assert.ThrowsAny<Exception>(() => services.TryAddOpenShellBootstrapServices(fixture.Config));
        Assert.Empty(services);
    }

    [Fact]
    public void MissingSelection_DoesNotRegisterBootstrapOrChangeLegacyMode()
    {
        var services = new ServiceCollection();
        Assert.False(services.TryAddOpenShellBootstrapServices(new ConfigurationBuilder().Build()));
        Assert.Empty(services);
    }

    internal sealed class OwnedConfiguration : IDisposable
    {
        private readonly string _file = Path.Combine(Path.GetTempPath(), "owned-bootstrap-" + Guid.NewGuid().ToString("N"));
        public readonly Dictionary<string, string?> Values;
        public IConfiguration Config => new ConfigurationBuilder().AddInMemoryCollection(Values).Build();
        public OwnedConfiguration()
        {
            File.WriteAllText(_file, new string('x', 48));
            Values = new() { ["OpenShell:Bootstrap"] = "true", ["OpenShell:BootstrapIngress"] = "https://ingress.example.invalid/identity/ready",
                ["OpenShell:LaunchId"] = Guid.NewGuid().ToString("D"), ["OpenShell:IdentityEpoch"] = Guid.NewGuid().ToString("D"),
                ["OpenShell:PlanHash"] = new string('a', 64), ["OpenShell:Deadline"] = DateTime.UtcNow.AddSeconds(50).ToString("O"),
                ["OpenShell:BootstrapCapabilityFile"] = _file };
        }
        public void Dispose() => File.Delete(_file);
    }
}
