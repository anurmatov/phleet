using YamlDotNet.Serialization;

namespace Fleet.Orchestrator.Tests.Services;

public class OpenShellComposeTests
{
    [Fact]
    public void NonemptyHostAllowlist_ReachesOrchestrator_NotBridge()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "CLAUDE.md"))) root = root.Parent;
        Assert.NotNull(root);
        var text = File.ReadAllText(Path.Combine(root.FullName, "docker-compose.example.yml"));
        var id = new string('a', 64);
        text = text.Replace("${FLEET_DOCKER_INFRASTRUCTURE_CONTAINER_IDS:-}", id, StringComparison.Ordinal);
        var compose = new DeserializerBuilder().Build().Deserialize<Dictionary<object, object>>(text);
        var services = (Dictionary<object, object>)compose["services"];
        List<string> Environment(string service) => ((List<object>)((Dictionary<object, object>)services[service])["environment"])
            .Select(x => (string)x).ToList();
        Assert.Contains("Docker__InfrastructureContainerIds=" + id, Environment("fleet-orchestrator"));
        Assert.DoesNotContain(Environment("fleet-bridge"),
            e => e.StartsWith("Docker__InfrastructureContainerIds=", StringComparison.Ordinal));
    }
}
