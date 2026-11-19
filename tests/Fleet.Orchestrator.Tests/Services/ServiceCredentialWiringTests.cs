using System.Text.RegularExpressions;

namespace Fleet.Orchestrator.Tests.Services;

/// <summary>
/// Where the example deployment hands over the optional credentials for Qdrant and Temporal: each
/// to the service that presents it and to no other, empty unless <c>.env</c> sets it, and never
/// from a value exported in the shell that runs setup or upgrade.
/// </summary>
public sealed partial class ServiceCredentialWiringTests
{
    private static readonly string Root = RepositoryRoot();
    private static readonly string Compose = Read("docker-compose.example.yml");

    private static string Read(string path) => File.ReadAllText(Path.Combine(Root, path));

    [GeneratedRegex(@"^  ([a-z0-9-]+):\r?\n", RegexOptions.Multiline)]
    private static partial Regex ServiceStart();

    /// <summary>Each service's block of the file, by name.</summary>
    private static Dictionary<string, string> Services()
    {
        var servicesStart = Compose.IndexOf("\nservices:", StringComparison.Ordinal);
        var servicesEnd = Regex.Match(Compose[(servicesStart + 1)..], @"^(volumes|networks):\r?\n", RegexOptions.Multiline);
        var section = Compose.Substring(servicesStart, servicesEnd.Index + 1);
        var starts = ServiceStart().Matches(section);
        var blocks = new Dictionary<string, string>();
        for (var i = 0; i < starts.Count; i++)
        {
            var end = i + 1 < starts.Count ? starts[i + 1].Index : section.Length;
            blocks[starts[i].Groups[1].Value] = section[starts[i].Index..end];
        }
        return blocks;
    }

    private static List<string> ServicesThatAreGiven(string variable) =>
        [.. Services().Where(s => s.Value.Contains("${" + variable, StringComparison.Ordinal)).Select(s => s.Key).Order()];

    [Fact]
    public void TheQdrantApiKey_GoesToFleetMemoryAlone_EmptyUnlessSet()
    {
        Assert.Equal(["fleet-memory"], ServicesThatAreGiven("FLEET_QDRANT_API_KEY"));
        Assert.Contains("      - Qdrant__ApiKey=${FLEET_QDRANT_API_KEY:-}\n", Services()["fleet-memory"]);
        // Its address: only what .env gives, and beside it the default a keyless start uses (a key
        // never uses it: IndexConnectionTests starts fleet-memory with these lines).
        Assert.Equal(["fleet-memory"], ServicesThatAreGiven("FLEET_QDRANT_URL"));
        Assert.Contains("      - Qdrant__Url=${FLEET_QDRANT_URL:-}\n", Services()["fleet-memory"]);
        Assert.Contains("      - Qdrant__DefaultUrl=http://qdrant:6334\n", Services()["fleet-memory"]);
    }

    [Fact]
    public void TheTemporalToken_GoesToTheOrchestratorAndTheBridgeAlone_EmptyUnlessSet()
    {
        Assert.Equal(["fleet-orchestrator", "fleet-temporal-bridge"], ServicesThatAreGiven("FLEET_TEMPORAL_BEARER_TOKEN"));
        Assert.Contains("      - Temporal__BearerToken=${FLEET_TEMPORAL_BEARER_TOKEN:-}\n", Services()["fleet-orchestrator"]);
        Assert.Contains("      - TemporalBridge__BearerToken=${FLEET_TEMPORAL_BEARER_TOKEN:-}\n", Services()["fleet-temporal-bridge"]);
    }

    /// <summary>
    /// Compose prefers the shell's value to <c>.env</c>'s. Both scripts drop the shell's before
    /// their first Docker call, so <c>.env</c> is what a service is started with.
    /// </summary>
    [Theory]
    [InlineData("setup.sh")]
    [InlineData("upgrade.sh")]
    public void SetupAndUpgrade_DropAnExportedValue_BeforeTheirFirstDockerCall(string script)
    {
        var lines = Read(script).Split('\n');
        var unset = Array.FindIndex(lines, l => l == "unset FLEET_QDRANT_URL FLEET_QDRANT_API_KEY FLEET_TEMPORAL_BEARER_TOKEN");
        var firstDocker = Array.FindIndex(lines, l => Regex.IsMatch(l, @"^[^#]*\bdocker\b"));

        Assert.True(unset >= 0, $"{script} does not unset the three variables");
        Assert.True(firstDocker > unset, $"{script} calls docker (line {firstDocker + 1}) before it unsets them (line {unset + 1})");
    }

    /// <summary>The example names each variable, commented out, and gives no value for a credential.</summary>
    [Fact]
    public void TheEnvExample_NamesThem_AndSetsNoCredential()
    {
        var example = Read(".env.example");

        Assert.Contains("\n#FLEET_QDRANT_URL=https://qdrant.example.internal:6334\n", example);
        Assert.Contains("\n#FLEET_QDRANT_API_KEY=\n", example);
        Assert.Contains("\n#FLEET_TEMPORAL_BEARER_TOKEN=\n", example);
        Assert.DoesNotMatch(@"(?m)^\s*FLEET_(QDRANT_API_KEY|QDRANT_URL|TEMPORAL_BEARER_TOKEN)=", example);
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Fleet.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("could not locate the repository root");
    }
}
