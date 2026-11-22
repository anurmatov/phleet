using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fleet.Conversations.Journal;
using Fleet.Journal.Client;
using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Services;
using Microsoft.Extensions.Configuration;

namespace Fleet.Orchestrator.Tests.Services;

public sealed class JournalProvisioningTests
{
    private static readonly string Key = Base64Url(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());

    [Fact]
    public void Token_matches_the_comms_wire_contract()
    {
        var service = Tokens(("Journal:TokenKey", Key));

        var token = service.Mint(JournalTokenService.PurposeIngest, "agent_1");
        var mac = Base64Url(HMACSHA256.HashData(
            Enumerable.Range(1, 32).Select(i => (byte)i).ToArray(),
            Encoding.UTF8.GetBytes("cj1|ingest|agent_1")));

        Assert.Equal($"cj1.ingest.agent_1.{mac}", token);
        Assert.True(JournalTokens.TryVerify(
            token, JournalTokens.PurposeIngest, JournalTokens.ParseKeys(Key), out var subject));
        Assert.Equal("agent_1", subject);
    }

    [Fact]
    public void Excluded_chat_ids_are_parsed_deduplicated_and_sorted()
    {
        var service = Tokens(("Journal:TokenKey", Key), ("Journal:ExcludedChatIds", "-20, 10,-20,,bad"));

        Assert.Equal([-20L, 10L], service.ExcludedChatIds());
    }

    [Fact]
    public void Journal_endpoint_gets_bearer_header_and_queryless_url()
    {
        var agent = Agent("claude");
        agent.McpEndpoints.Add(new AgentMcpEndpoint
        {
            McpName = ContainerProvisioningService.JournalMcpServerName,
            TransportType = "http",
            Url = "http://fleet-comms:8082/mcp?agent=old",
        });

        using var doc = JsonDocument.Parse(ContainerProvisioningService.GenerateMcpJson(
            agent, "http://fleet-memory:3100", "cj1.read.agent-1.signature"));
        var server = doc.RootElement.GetProperty("mcpServers")
            .GetProperty(ContainerProvisioningService.JournalMcpServerName);

        Assert.Equal("http://fleet-comms:8082/mcp", server.GetProperty("url").GetString());
        Assert.Equal("Bearer cj1.read.agent-1.signature",
            server.GetProperty("headers").GetProperty("Authorization").GetString());
    }

    [Theory]
    [InlineData("claude", true)]
    [InlineData("codex", true)]
    [InlineData("gemini", true)]
    [InlineData("", true)]              // an empty provider is claude
    [InlineData("other-harness", false)] // not in the table: no transcript, no headers
    public void Header_support_is_compiled_per_provider(string provider, bool expected) =>
        Assert.Equal(expected, ContainerProvisioningService.SupportsMcpHeaders(provider));

    [Fact]
    public async Task Flag_and_bot_emit_ingest_token_and_exclusions()
    {
        await using var harness = ProvisioningHarness.Create(new Dictionary<string, string?>
        {
            ["Journal:TokenKey"] = Key,
            ["Journal:ExcludedChatIds"] = "-100,200",
        });
        await harness.SeedAsync(db =>
        {
            var agent = Agent("claude");
            agent.JournalEnabled = true;
            agent.EnvRefs.Add(new AgentEnvRef { EnvKeyName = "TELEGRAM_AGENT_BOT_TOKEN" });
            agent.Networks.Add(new AgentNetwork { NetworkName = "fleet-net" });
            db.Agents.Add(agent);
        });

        var result = await harness.Service.ProvisionAsync("agent-1");

        Assert.True(result.Success, result.Message);
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Combine(harness.GeneratedDir("fleet-agent-1"), "appsettings.json")));
        var journal = doc.RootElement.GetProperty("Journal");
        Assert.StartsWith("cj1.ingest.agent-1.", journal.GetProperty("IngestToken").GetString());
        Assert.Equal(JsonValueKind.String, journal.GetProperty("ExcludedChatIds").ValueKind);
        Assert.Equal("-100,200", journal.GetProperty("ExcludedChatIds").GetString());

        var options = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(doc.RootElement.GetRawText())))
            .Build()
            .GetSection("Journal")
            .Get<JournalOptions>();
        Assert.NotNull(options);
        Assert.Equal([-100L, 200L], JournalOptions.ParseExcludedChatIds(options.ExcludedChatIds).Order());
    }

    [Fact]
    public async Task Flag_without_bot_emits_nothing_and_logs_info()
    {
        await using var harness = ProvisioningHarness.Create(new Dictionary<string, string?>
        {
            ["Journal:TokenKey"] = Key,
        });
        await harness.SeedAsync(db =>
        {
            var agent = Agent("claude");
            agent.JournalEnabled = true;
            agent.Networks.Add(new AgentNetwork { NetworkName = "fleet-net" });
            db.Agents.Add(agent);
        });

        var result = await harness.Service.ProvisionAsync("agent-1");

        Assert.True(result.Success, result.Message);
        var json = await File.ReadAllTextAsync(
            Path.Combine(harness.GeneratedDir("fleet-agent-1"), "appsettings.json"));
        Assert.False(JsonDocument.Parse(json).RootElement.TryGetProperty("Journal", out _));
        Assert.Contains(harness.Logs.Entries,
            e => e.Level == Microsoft.Extensions.Logging.LogLevel.Information
                 && e.Message.Contains("no Telegram bot token ref", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Blank_key_refuses_reprovision_before_any_docker_request()
    {
        await using var harness = ProvisioningHarness.Create();
        await harness.SeedAsync(db =>
        {
            var agent = Agent("claude");
            agent.JournalEnabled = true;
            db.Agents.Add(agent);
        });

        var result = await harness.Service.ReprovisionAsync("agent-1");

        Assert.False(result.Success);
        Assert.Equal("journal_key_missing", result.Message);
        Assert.Equal(0, harness.DockerRequestCount);
    }

    /// <summary>
    /// The journal token is a header, so a provider whose CLI sends none cannot have the endpoint.
    /// Every known provider sends headers now; the rule stands for one outside the table, which a
    /// provision refuses even earlier, by name (PV1), before Docker is asked anything.
    /// </summary>
    [Fact]
    public async Task A_provider_that_sends_no_headers_cannot_have_the_journal_endpoint_and_is_refused_before_docker()
    {
        var settings = new Dictionary<string, string?> { ["Journal:TokenKey"] = Key };
        var agent = Agent("other-harness");
        agent.McpEndpoints.Add(new AgentMcpEndpoint
        {
            McpName = ContainerProvisioningService.JournalMcpServerName,
            TransportType = "http",
            Url = "http://fleet-comms:8082/mcp",
        });

        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var policy = new ProvisioningPolicy(config, new JournalTokenService(config));
        Assert.Equal("journal_headers_unsupported", policy.DescribeJournalProvisioningFault(agent));
        Assert.Equal("journal_headers_unsupported", Assert.Throws<InvalidOperationException>(
            () => ContainerProvisioningService.GenerateMcpJson(agent, "http://fleet-memory:3100", "cj1.read.agent-1.signature")).Message);

        await using var harness = ProvisioningHarness.Create(settings);
        await harness.SeedAsync(db => db.Agents.Add(agent));

        var result = await harness.Service.ReprovisionAsync("agent-1");

        Assert.False(result.Success);
        Assert.Equal(0, harness.DockerRequestCount);
    }

    /// <summary>A gemini agent's CLI sends headers, so it reads the journal with its own token like the others.</summary>
    [Theory]
    [InlineData("claude")]
    [InlineData("codex")]
    [InlineData("gemini")]
    public async Task Every_known_provider_gets_the_journal_endpoint_with_its_read_token(string provider)
    {
        await using var harness = ProvisioningHarness.Create(new Dictionary<string, string?>
        {
            ["Journal:TokenKey"] = Key,
        });
        File.AppendAllText(harness.EnvFile, $"{ProvisioningFaults.GeminiApiKeyEnvRef}=synthetic-gemini-api-key\n");
        await harness.SeedAsync(db =>
        {
            var agent = Agent(provider);
            agent.McpEndpoints.Add(new AgentMcpEndpoint
            {
                McpName = ContainerProvisioningService.JournalMcpServerName,
                TransportType = "http",
                Url = "http://fleet-comms:8082/mcp",
            });
            if (provider == "gemini")
                agent.EnvRefs.Add(new AgentEnvRef { EnvKeyName = ProvisioningFaults.GeminiApiKeyEnvRef });
            db.Agents.Add(agent);
        });

        var result = await harness.Service.ProvisionAsync("agent-1");

        Assert.True(result.Success, result.Message);
        var generated = harness.GeneratedDir("fleet-agent-1");
        using var mcp = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(generated, ".mcp.json")));
        Assert.StartsWith("Bearer cj1.read.agent-1.", mcp.RootElement.GetProperty("mcpServers")
            .GetProperty(ContainerProvisioningService.JournalMcpServerName)
            .GetProperty("headers").GetProperty("Authorization").GetString());
        using var appsettings = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(generated, "appsettings.json")));
        Assert.True(appsettings.RootElement.GetProperty("Agent").GetProperty("McpHeaderSupport").GetBoolean());
    }

    private static JournalTokenService Tokens(params (string Key, string Value)[] values) =>
        new(new ConfigurationBuilder().AddInMemoryCollection(
            values.ToDictionary(v => v.Key, v => (string?)v.Value)).Build());

    private static Agent Agent(string provider) => new()
    {
        Name = "agent-1",
        DisplayName = "Agent One",
        Role = "test",
        Model = "test-model",
        ContainerName = "fleet-agent-1",
        Provider = provider,
    };

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
