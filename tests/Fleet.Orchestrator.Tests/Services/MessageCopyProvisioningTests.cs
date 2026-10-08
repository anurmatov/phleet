using System.Text.Json;
using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Services;
using Microsoft.EntityFrameworkCore;
namespace Fleet.Orchestrator.Tests.Services;

public sealed class MessageCopyProvisioningTests
{
    private const string Grant = "mcp__fleet-telegram-copy__copy_message";
    private static Agent Agent(string provider, bool bot = true, bool grant = true) => new()
    {
        Name = "agent1", DisplayName = "Agent1", Role = "test", ContainerName = "fleet-agent1", WorkDir = "/workspace",
        Provider = provider, Model = "model", Networks = [new() { NetworkName = "fleet-net" }],
        EnvRefs = bot ? [new() { EnvKeyName = "TELEGRAM_AGENT1_BOT_TOKEN" }] : [],
        Tools = grant ? [new() { ToolName = Grant }] : [],
    };

    [Theory]
    [InlineData("claude")]
    [InlineData("codex")]
    [InlineData("gemini")]
    public async Task ExplicitGrantAndBot_EmitCanonicalHeaderlessLoopbackServer(string provider)
    {
        await using var harness = ProvisioningHarness.Create();
        await harness.SeedAsync(db => { var agent = Agent(provider); agent.Tools[0].ToolName = " " + Grant.ToUpperInvariant() + " "; db.Agents.Add(agent); });
        Assert.True((await harness.Service.ProvisionAsync("agent1")).Success);
        var dir = harness.GeneratedDir("fleet-agent1");
        using var settings = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "appsettings.json")));
        Assert.True(settings.RootElement.GetProperty("Telegram").GetProperty("MessageCopyEnabled").GetBoolean());
        Assert.Contains(settings.RootElement.GetProperty("Agent").GetProperty("AllowedTools").EnumerateArray(), t => t.GetString() == Grant);
        Assert.DoesNotContain("\"Journal\"", File.ReadAllText(Path.Combine(dir, "appsettings.json")));
        using var mcp = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, ".mcp.json")));
        var server = mcp.RootElement.GetProperty("mcpServers").GetProperty("fleet-telegram-copy");
        Assert.Equal("http://127.0.0.1:8092/telegram-copy/v1/mcp", server.GetProperty("url").GetString());
        Assert.False(server.TryGetProperty("headers", out _));
        Assert.Contains(Grant, File.ReadAllText(Path.Combine(dir, "settings.json")));
    }

    [Theory]
    [InlineData("claude")]
    [InlineData("codex")]
    [InlineData("gemini")]
    public async Task DisabledGrant_KeepsGeneratedFilesByteIdentical(string provider)
    {
        await using var harness = ProvisioningHarness.Create();
        await harness.SeedAsync(db => db.Agents.Add(Agent(provider, grant: false)));
        Assert.True((await harness.Service.ProvisionAsync("agent1")).Success);
        var dir = harness.GeneratedDir("fleet-agent1"); var files = new[] { "appsettings.json", ".mcp.json", "settings.json" };
        var before = files.ToDictionary(n => n, n => File.ReadAllBytes(Path.Combine(dir, n)));
        await harness.MutateAsync(async db => { (await db.Agents.SingleAsync()).Tools.Add(new() { ToolName = Grant, IsEnabled = false }); await db.SaveChangesAsync(); });
        Assert.True((await harness.Service.ProvisionAsync("agent1")).Success);
        foreach (var file in files) Assert.Equal(before[file], File.ReadAllBytes(Path.Combine(dir, file)));
    }

    [Fact]
    public async Task GrantWithoutBot_WarnsAndOmitsRuntimeCapability()
    {
        await using var harness = ProvisioningHarness.Create();
        await harness.SeedAsync(db => db.Agents.Add(Agent("codex", bot: false)));
        Assert.Contains("message_copy_unavailable:no_telegram_bot", (await harness.Service.PreviewAsync("agent1")).Diffs);
        Assert.True((await harness.Service.ProvisionAsync("agent1")).Success);
        Assert.Contains(harness.Logs.Entries, e => e.Message.Contains("message_copy_unavailable:no_telegram_bot"));
        var dir = harness.GeneratedDir("fleet-agent1");
        Assert.DoesNotContain("MessageCopyEnabled", File.ReadAllText(Path.Combine(dir, "appsettings.json")));
        Assert.DoesNotContain("fleet-telegram-copy", File.ReadAllText(Path.Combine(dir, ".mcp.json")));
    }

    [Fact]
    public async Task ReservedEndpoint_RefusesBeforeDeprovision()
    {
        await using var harness = ProvisioningHarness.Create();
        await harness.SeedAsync(db => { var agent = Agent("codex"); agent.McpEndpoints.Add(new() { McpName = "fleet-telegram-copy", Url = "http://untrusted.test", TransportType = "http" }); db.Agents.Add(agent); });
        var result = await harness.Service.ReprovisionAsync("agent1");
        Assert.False(result.Success); Assert.Equal("message_copy_endpoint_reserved", result.Message); Assert.Equal(0, harness.DockerRequestCount);
    }
}
