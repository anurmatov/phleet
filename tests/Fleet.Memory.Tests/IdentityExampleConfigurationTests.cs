using YamlDotNet.Serialization;

namespace Fleet.Memory.Tests;

public sealed class IdentityExampleConfigurationTests
{
    [Fact]
    public void ExampleComposeParses_AndKeepsVerifiedListenerInternalAndDefaultOff()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "docker-compose.example.yml"))) root = root.Parent;
        Assert.NotNull(root);
        var compose = new DeserializerBuilder().Build().Deserialize<Dictionary<object, object>>(
            File.ReadAllText(Path.Combine(root.FullName, "docker-compose.example.yml")));
        var services = Assert.IsType<Dictionary<object, object>>(compose["services"]);
        var memory = Assert.IsType<Dictionary<object, object>>(services["fleet-memory"]);
        var environment = Assert.IsType<List<object>>(memory["environment"]).Cast<string>().ToArray();
        Assert.Equal(12, environment.Count(value => value.StartsWith("FleetIdentity__", StringComparison.Ordinal)));
        Assert.Contains("FleetIdentity__Enabled=${FLEET_MEMORY_IDENTITY_ENABLED:-false}", environment);
        Assert.Equal(new[] { "3100:3100" }, Assert.IsType<List<object>>(memory["ports"]).Cast<string>());
        Assert.Contains(Assert.IsType<List<object>>(memory["volumes"]), value => value.ToString()!.EndsWith("/identity/memory:/run/fleet-identity:ro", StringComparison.Ordinal));
    }
}
