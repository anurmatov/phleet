using System.Text.RegularExpressions;

namespace Fleet.Orchestrator.Tests.Identity;

/// <summary>
/// The example compose file keeps every service that takes no credential off the network agents
/// are on. An agent's identity on the MCP servers means nothing if it can reach Temporal or Qdrant
/// directly, and a later edit that puts one back would fail nothing else.
/// </summary>
public sealed partial class ComposeNetworkTests
{
    private const string AgentNetwork = "fleet-net";

    private static readonly string Compose = File.ReadAllText(Path.Combine(RepositoryRoot(), "docker-compose.example.yml"));

    /// <summary>The networks a service lists, read from its block of the file.</summary>
    private static List<string> NetworksOf(string service)
    {
        var start = Regex.Match(Compose, $@"^  {Regex.Escape(service)}:\r?\n", RegexOptions.Multiline);
        Assert.True(start.Success, $"no service '{service}' in the example compose file");
        var rest = Compose[(start.Index + start.Length)..];
        var end = NextTopLevel().Match(rest);
        var block = end.Success ? rest[..end.Index] : rest;

        var networks = Regex.Match(block, @"^    networks:\r?\n((?:      - .+\r?\n)+)", RegexOptions.Multiline);
        Assert.True(networks.Success, $"service '{service}' lists no networks");
        return [.. networks.Groups[1].Value.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim().TrimStart('-').Trim())];
    }

    [GeneratedRegex(@"^(  [a-z0-9-]+:|[a-z]+:)\r?\n", RegexOptions.Multiline)]
    private static partial Regex NextTopLevel();

    [Theory]
    [InlineData("temporal-server")]
    [InlineData("temporal-ui")]
    [InlineData("temporal-postgresql")]
    [InlineData("qdrant")]
    public void AServiceThatTakesNoCredential_IsNotOnTheAgentsNetwork(string service)
    {
        Assert.DoesNotContain(AgentNetwork, NetworksOf(service));
    }

    [Fact]
    public void Temporal_IsReachedByTheOrchestratorAndTheBridge_WhichAgentsReachInItsPlace()
    {
        Assert.Equal(["fleet-temporal-net"], NetworksOf("temporal-server"));
        Assert.Equal(["fleet-temporal-net"], NetworksOf("temporal-ui"));
        Assert.Equal(["fleet-temporal-net"], NetworksOf("temporal-postgresql"));
        foreach (var service in new[] { "fleet-orchestrator", "fleet-temporal-bridge" })
        {
            Assert.Contains("fleet-temporal-net", NetworksOf(service));
            Assert.Contains(AgentNetwork, NetworksOf(service));
        }
    }

    [Fact]
    public void Qdrant_IsReachedByFleetMemoryOnly()
    {
        Assert.Equal(["fleet-memory-net"], NetworksOf("qdrant"));
        Assert.Contains("fleet-memory-net", NetworksOf("fleet-memory"));
        Assert.Contains(AgentNetwork, NetworksOf("fleet-memory"));
    }

    [Theory]
    [InlineData("fleet-telegram")]
    [InlineData("fleet-playwright")]
    [InlineData("rabbitmq")]
    [InlineData("fleet-dashboard")]
    public void NoOtherService_JoinsTheNetworksAgentsAreKeptOutOf(string service)
    {
        var networks = NetworksOf(service);
        Assert.DoesNotContain("fleet-temporal-net", networks);
        Assert.DoesNotContain("fleet-memory-net", networks);
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Fleet.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root not found");
    }
}
