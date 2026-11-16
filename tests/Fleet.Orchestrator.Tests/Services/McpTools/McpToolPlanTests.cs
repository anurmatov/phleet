using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Services.McpTools;
using Fleet.Shared;

namespace Fleet.Orchestrator.Tests.Services.McpTools;

/// <summary>
/// Which saved MCP endpoints are asked for their tools, at what address and with which headers.
/// No request is made here.
/// </summary>
public sealed class McpToolPlanTests
{
    private const string MemoryUrl = "http://fleet-memory:3100";
    private const string Secret = "value-from-env-file";

    private static Agent NewAgent(string provider = "claude")
    {
        return new Agent
        {
            Name = "agent1", DisplayName = "agent1", Role = "developer", Model = "sonnet", Provider = provider,
            ContainerName = "fleet-agent1", MemoryLimitMb = 1024,
        };
    }

    private static AgentMcpEndpoint Endpoint(string name, string url, string transport = "http", string? headers = null) =>
        new() { McpName = name, Url = url, TransportType = transport, HeaderEnvRefsJson = headers };

    private static string Mapping(string header, string envRef) =>
        $$"""[{"headerName":"{{header}}","envRefName":"{{envRef}}"}]""";

    private static IReadOnlyList<McpPlanEntry> Build(
        Agent agent, Dictionary<string, string>? env = null, List<SecretBinding>? bindings = null, string? journalToken = "cj1.read.token") =>
        McpToolPlan.Build(agent, env ?? [], bindings ?? [], MemoryUrl, () => journalToken);

    private static McpPlanEntry Only(IReadOnlyList<McpPlanEntry> plan, string name) => Assert.Single(plan, e => e.Name == name);

    [Fact]
    public void AnEndpointWithoutHeaders_IsAskedAtItsSavedAddress()
    {
        var agent = NewAgent();
        agent.McpEndpoints.Add(Endpoint("example", "https://mcp.example.test/mcp"));

        var entry = Only(Build(agent), "example");

        Assert.Null(entry.NotAsked);
        Assert.Equal(new Uri("https://mcp.example.test/mcp"), entry.Target!.Url);
        Assert.False(entry.Target.Sse);
        Assert.Empty(entry.Target.Headers);
    }

    [Fact]
    public void TheFleetsOwnServers_AreAskedAtTheAddressTheAgentIsGiven()
    {
        var agent = NewAgent();
        agent.McpEndpoints.Add(Endpoint("fleet-telegram", "http://fleet-telegram:3800/mcp"));
        agent.McpEndpoints.Add(Endpoint("fleet-temporal", "http://fleet-temporal-bridge:3001/mcp?agent=stale"));

        var plan = Build(agent);

        Assert.Equal("?agent=agent1&formatting_mode=0", Only(plan, "fleet-telegram").Target!.Url.Query);
        Assert.Equal("?agent=agent1", Only(plan, "fleet-temporal").Target!.Url.Query);
    }

    [Fact]
    public void EveryAgentHasFleetMemory_WhetherOrNotItHasARowForIt()
    {
        var plan = Build(NewAgent());

        var memory = Assert.Single(plan);
        Assert.Equal("fleet-memory", memory.Name);
        Assert.Equal("?agent=agent1", memory.Target!.Url.Query);

        var withRow = NewAgent();
        withRow.McpEndpoints.Add(Endpoint("fleet-memory", "http://fleet-memory:3100/mcp"));
        Assert.Single(Build(withRow));
    }

    [Fact]
    public void AnSseEndpoint_IsAskedOverSse_AndAnyOtherTransportIsNotAsked()
    {
        var agent = NewAgent();
        agent.McpEndpoints.Add(Endpoint("legacy", "http://legacy.example.test/sse", "sse"));
        agent.McpEndpoints.Add(Endpoint("local", "http://local.example.test", "stdio"));

        var plan = Build(agent);

        Assert.True(Only(plan, "legacy").Target!.Sse);
        var local = Only(plan, "local");
        Assert.Null(local.Target);
        Assert.Equal(McpServerTools.UnsupportedTransport, local.NotAsked!.Status);
    }

    [Fact]
    public void AnAddressThatIsNotHttp_IsNotAsked()
    {
        var agent = NewAgent();
        agent.McpEndpoints.Add(Endpoint("file", "file:///etc/passwd"));
        agent.McpEndpoints.Add(Endpoint("blank", "not a url"));

        var plan = Build(agent);

        Assert.Equal(McpServerTools.Misconfigured, Only(plan, "file").NotAsked!.Status);
        Assert.Equal(McpServerTools.Misconfigured, Only(plan, "blank").NotAsked!.Status);
    }

    [Fact]
    public void AHeaderMappedToAnUnboundEnvRef_CarriesItsEnvFileValue()
    {
        var agent = NewAgent();
        agent.EnvRefs.Add(new AgentEnvRef { EnvKeyName = "EXAMPLE_API_KEY" });
        agent.McpEndpoints.Add(Endpoint("example", "https://mcp.example.test/mcp", headers: Mapping("X-Example-Key", "EXAMPLE_API_KEY")));

        var entry = Only(Build(agent, new() { ["EXAMPLE_API_KEY"] = Secret }), "example");

        Assert.Equal(Secret, Assert.Single(entry.Target!.Headers, h => h.Key == "X-Example-Key").Value);
    }

    private static SecretBinding Binding(string hosts = "mcp.example.test", string header = "X-Example-Key", string kind = "static") =>
        new() { EnvKeyName = "EXAMPLE_API_KEY", Hosts = hosts, HeaderName = header, Kind = kind };

    private static Agent AgentWithBoundHeader(string url = "https://mcp.example.test/mcp", string header = "X-Example-Key")
    {
        var agent = NewAgent();
        agent.EnvRefs.Add(new AgentEnvRef { EnvKeyName = "EXAMPLE_API_KEY" });
        agent.McpEndpoints.Add(Endpoint("example", url, headers: Mapping(header, "EXAMPLE_API_KEY")));
        return agent;
    }

    [Fact]
    public void AHeaderMappedToABoundSecret_CarriesItsValue_ToTheBindingsOwnHostAndHeader()
    {
        var entry = Only(
            Build(AgentWithBoundHeader(), new() { ["EXAMPLE_API_KEY"] = Secret }, [Binding()]), "example");

        Assert.Null(entry.NotAsked);
        Assert.Equal("mcp.example.test", entry.Target!.Url.Host);
        Assert.Equal(Secret, Assert.Single(entry.Target.Headers, h => h.Key == "X-Example-Key").Value);
    }

    [Theory]
    // Another host than the binding names.
    [InlineData("https://other.example.test/mcp", "X-Example-Key", "mcp.example.test", "X-Example-Key")]
    // A host that only ends like the binding's.
    [InlineData("https://evil-mcp.example.test/mcp", "X-Example-Key", "mcp.example.test", "X-Example-Key")]
    // Not https.
    [InlineData("http://mcp.example.test/mcp", "X-Example-Key", "mcp.example.test", "X-Example-Key")]
    // Another port.
    [InlineData("https://mcp.example.test:8443/mcp", "X-Example-Key", "mcp.example.test", "X-Example-Key")]
    // Another header than the binding injects.
    [InlineData("https://mcp.example.test/mcp", "X-Other", "mcp.example.test", "X-Example-Key")]
    public void ABoundSecret_IsNotSent_WhereItsBindingWouldNotSendIt(string url, string mappedHeader, string hosts, string bindingHeader)
    {
        var entry = Only(
            Build(AgentWithBoundHeader(url, mappedHeader), new() { ["EXAMPLE_API_KEY"] = Secret }, [Binding(hosts, bindingHeader)]),
            "example");

        Assert.Null(entry.Target);
        Assert.Equal(McpServerTools.Misconfigured, entry.NotAsked!.Status);
        Assert.Contains("EXAMPLE_API_KEY", entry.NotAsked.Reason);
        Assert.DoesNotContain(Secret, entry.NotAsked.Reason);
    }

    [Fact]
    public void AGitHubAppSecret_IsNeverSent_ItsKeyIsNotAHeaderValue()
    {
        var binding = Binding(hosts: "api.github.com", header: "Authorization", kind: SecretBindingRules.KindGitHubApp);
        binding.AppIdEnvKey = "EXAMPLE_APP_ID";
        var agent = AgentWithBoundHeader("https://api.github.com/mcp", "Authorization");

        var entry = Only(Build(agent, new() { ["EXAMPLE_API_KEY"] = Secret }, [binding]), "example");

        Assert.Null(entry.Target);
        Assert.Equal(McpServerTools.NeedsAgentSecret, entry.NotAsked!.Status);
        Assert.DoesNotContain(Secret, entry.NotAsked.Reason);
    }

    [Fact]
    public void AnSshKey_IsNeverSent()
    {
        var binding = Binding(hosts: "deploy@mcp.example.test", kind: SecretBindingRules.KindSshKey);
        binding.SshKnownHosts = "mcp.example.test ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIExampleExampleExampleExampleExampleExampleEx";

        var entry = Only(Build(AgentWithBoundHeader(), new() { ["EXAMPLE_API_KEY"] = Secret }, [binding]), "example");

        Assert.Null(entry.Target);
        Assert.NotEqual(McpServerTools.Ok, entry.NotAsked!.Status);
        Assert.DoesNotContain(Secret, entry.NotAsked.Reason);
    }

    [Fact]
    public void ABoundSecretWithNoUsableValue_IsNotAsked()
    {
        var entry = Only(Build(AgentWithBoundHeader(), new() { ["EXAMPLE_API_KEY"] = "<secret>" }, [Binding()]), "example");

        Assert.Null(entry.Target);
        Assert.Equal(McpServerTools.Misconfigured, entry.NotAsked!.Status);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("<secret>")]
    [InlineData("two\nlines")]
    public void AHeaderWhoseEnvRefHasNoUsableValue_IsNotAsked(string? value)
    {
        var agent = NewAgent();
        agent.EnvRefs.Add(new AgentEnvRef { EnvKeyName = "EXAMPLE_API_KEY" });
        agent.McpEndpoints.Add(Endpoint("example", "https://mcp.example.test/mcp", headers: Mapping("X-Example-Key", "EXAMPLE_API_KEY")));
        var env = new Dictionary<string, string>();
        if (value is not null) env["EXAMPLE_API_KEY"] = value;

        var entry = Only(Build(agent, env), "example");

        Assert.Null(entry.Target);
        Assert.Equal(McpServerTools.Misconfigured, entry.NotAsked!.Status);
    }

    [Fact]
    public void AMappingToAnEnvRefTheAgentDoesNotHold_IsNotAsked_EvenWhenTheEnvFileHasTheKey()
    {
        var agent = NewAgent();
        agent.McpEndpoints.Add(Endpoint("example", "https://mcp.example.test/mcp", headers: Mapping("X-Example-Key", "SOMEONE_ELSES_KEY")));

        var entry = Only(Build(agent, new() { ["SOMEONE_ELSES_KEY"] = Secret }), "example");

        Assert.Null(entry.Target);
        Assert.Equal(McpServerTools.Misconfigured, entry.NotAsked!.Status);
    }

    [Fact]
    public void StoredMappingsThatAreNotValid_AreNotAsked()
    {
        var agent = NewAgent();
        agent.McpEndpoints.Add(Endpoint("example", "https://mcp.example.test/mcp", headers: "{not json"));

        Assert.Equal(McpServerTools.Misconfigured, Only(Build(agent), "example").NotAsked!.Status);
    }

    [Fact]
    public void MappedHeadersOnAProviderThatSendsNone_AreNotAsked()
    {
        var agent = NewAgent("gemini");
        agent.EnvRefs.Add(new AgentEnvRef { EnvKeyName = "EXAMPLE_API_KEY" });
        agent.McpEndpoints.Add(Endpoint("example", "https://mcp.example.test/mcp", headers: Mapping("X-Example-Key", "EXAMPLE_API_KEY")));

        Assert.Equal(
            McpServerTools.Misconfigured,
            Only(Build(agent, new() { ["EXAMPLE_API_KEY"] = Secret }), "example").NotAsked!.Status);
    }

    [Fact]
    public void TheJournal_IsAskedWithTheAgentsReadToken_AndNotAtAllWithoutAKey()
    {
        var agent = NewAgent();
        agent.McpEndpoints.Add(Endpoint("fleet-comms-journal", "http://fleet-comms:8080/journal/v1/mcp?x=1"));

        var asked = Only(Build(agent), "fleet-comms-journal");
        Assert.Equal("Bearer cj1.read.token", asked.Target!.Headers["Authorization"]);
        Assert.Equal("", asked.Target.Url.Query);

        var noKey = Only(Build(agent, journalToken: null), "fleet-comms-journal");
        Assert.Null(noKey.Target);
        Assert.Equal(McpServerTools.Misconfigured, noKey.NotAsked!.Status);
    }

    [Fact]
    public void TheAgentsOwnFileServerName_IsNotAsked()
    {
        var agent = NewAgent();
        agent.McpEndpoints.Add(Endpoint("fleet-journal-files", "http://127.0.0.1:8091/journal-files/v1/mcp"));

        Assert.Equal(McpServerTools.Misconfigured, Only(Build(agent), "fleet-journal-files").NotAsked!.Status);
    }
}
