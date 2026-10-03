using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Services;
namespace Fleet.Orchestrator.Tests.Services;

public sealed class JournalFilesProvisioningTests
{
    private const string Grant = "mcp__fleet-journal-files__fetch_attachment";
    private static readonly Dictionary<string, string?> Config = new() { ["Journal:TokenKey"] = Convert.ToBase64String(Enumerable.Repeat((byte)42, 32).ToArray()) };
    private static Agent Agent(string provider, bool capture = true, bool enabledGrant = true) => new()
    {
        Name = "agent1", DisplayName = "Agent1", Role = "test", ContainerName = "fleet-agent1", WorkDir = "/workspace",
        Provider = provider, Model = "model", JournalEnabled = capture,
        EnvRefs = [new() { EnvKeyName = "TELEGRAM_AGENT1_BOT_TOKEN" }],
        Networks = [new() { NetworkName = "fleet-net" }],
        Tools = [new() { ToolName = Grant, IsEnabled = enabledGrant }],
    };
    [Theory]
    [InlineData("claude")]
    [InlineData("codex")]
    [InlineData("gemini")]
    [InlineData("codex", true)]
    public async Task ExplicitGrantAndCaptureEmitHeaderlessServerAndRuntimeReadToken(string provider, bool uppercaseGrant = false)
    {
        await using var harness = ProvisioningHarness.Create(Config);
        await harness.SeedAsync(db => { var agent = Agent(provider); if (uppercaseGrant) agent.Tools[0].ToolName = Grant.ToUpperInvariant(); db.Agents.Add(agent); });
        var result = await harness.Service.ProvisionAsync("agent1"); Assert.True(result.Success, result.Message);
        var dir = harness.GeneratedDir("fleet-agent1");
        using var config = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(dir, "appsettings.json")));
        Assert.True(config.RootElement.GetProperty("Journal").GetProperty("FilesEnabled").GetBoolean());
        Assert.Contains(config.RootElement.GetProperty("Agent").GetProperty("AllowedTools").EnumerateArray(), t => t.GetString() == Grant);
        Assert.StartsWith("cj1.read.agent1.", config.RootElement.GetProperty("Journal").GetProperty("ReadToken").GetString());
        if (provider != "claude") Assert.False(config.RootElement.GetProperty("Agent").TryGetProperty("McpHeaderSupport", out _));
        using var mcp = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(dir, ".mcp.json")));
        var server = mcp.RootElement.GetProperty("mcpServers").GetProperty("fleet-journal-files");
        Assert.Equal("http://127.0.0.1:8091/journal-files/v1/mcp", server.GetProperty("url").GetString());
        Assert.False(server.TryGetProperty("headers", out _));
    }
    [Theory]
    [InlineData("claude")]
    [InlineData("codex")]
    [InlineData("gemini")]
    public async Task DisabledGrantEmitsNeitherListenerNorReadToken(string provider)
    {
        await using var harness = ProvisioningHarness.Create(Config);
        await harness.SeedAsync(db => db.Agents.Add(Agent(provider, enabledGrant: false)));
        Assert.True((await harness.Service.ProvisionAsync("agent1")).Success);
        var dir = harness.GeneratedDir("fleet-agent1");
        Assert.DoesNotContain("FilesEnabled", await File.ReadAllTextAsync(Path.Combine(dir, "appsettings.json")));
        Assert.DoesNotContain("ReadToken", await File.ReadAllTextAsync(Path.Combine(dir, "appsettings.json")));
        Assert.DoesNotContain("fleet-journal-files", await File.ReadAllTextAsync(Path.Combine(dir, ".mcp.json")));
    }
    [Theory]
    [InlineData("claude", true)]
    [InlineData("codex", true)]
    [InlineData("gemini", true)]
    [InlineData("claude", false)]
    [InlineData("codex", false)]
    [InlineData("gemini", false)]
    public async Task DisabledGrantKeepsEveryGeneratedFileByteIdentical(string provider, bool bot)
    {
        await using var harness = ProvisioningHarness.Create(Config);
        await harness.SeedAsync(db => { var agent = Agent(provider); agent.Tools.Clear(); if (!bot) agent.EnvRefs.Clear(); db.Agents.Add(agent); });
        Assert.True((await harness.Service.ProvisionAsync("agent1")).Success);
        var dir = harness.GeneratedDir("fleet-agent1");
        var names = new[] { "appsettings.json", ".mcp.json", "settings.json" };
        var before = names.ToDictionary(name => name, name => File.ReadAllBytes(Path.Combine(dir, name)));
        await harness.MutateAsync(async db => { var agent = await db.Agents.SingleAsync(a => a.Name == "agent1");
            agent!.Tools.Add(new() { ToolName = Grant, IsEnabled = false }); await db.SaveChangesAsync(); });
        Assert.True((await harness.Service.ProvisionAsync("agent1")).Success);
        foreach (var name in names) Assert.Equal(before[name], await File.ReadAllBytesAsync(Path.Combine(dir, name)));
    }
    [Fact]
    public async Task GrantWithoutCaptureWarnsInPreviewAndLogAndEmitsNothing()
    {
        await using var harness = ProvisioningHarness.Create(Config);
        await harness.SeedAsync(db => db.Agents.Add(Agent("codex", capture: false)));
        var preview = await harness.Service.PreviewAsync("agent1");
        Assert.Contains("journal_files_unavailable:journal_capture_off", preview.Diffs);
        Assert.True((await harness.Service.ProvisionAsync("agent1")).Success);
        Assert.Contains(harness.Logs.Entries, e => e.Message.Contains("journal_files_unavailable:journal_capture_off"));
        var dir = harness.GeneratedDir("fleet-agent1");
        Assert.DoesNotContain("fleet-journal-files", await File.ReadAllTextAsync(Path.Combine(dir, ".mcp.json")));
        Assert.DoesNotContain("\"Journal\"", await File.ReadAllTextAsync(Path.Combine(dir, "appsettings.json")));
    }
    [Theory]
    [InlineData("claude", false)]
    [InlineData("claude", true)]
    [InlineData("codex", false)]
    [InlineData("codex", true)]
    [InlineData("gemini", false)]
    [InlineData("gemini", true)]
    public async Task SendGrantAndSwitch_EmitOnlyEffectiveKeys(string provider, bool cross)
    {
        await using var harness = ProvisioningHarness.Create(Config);
        await harness.SeedAsync(db =>
        {
            var agent = Agent(provider);
            agent.Tools = [new() { ToolName = " MCP__FLEET-JOURNAL-FILES__SEND_ATTACHMENT " }];
            agent.JournalCrossChatEnabled = cross;
            db.Agents.Add(agent);
        });
        Assert.True((await harness.Service.ProvisionAsync("agent1")).Success);
        var dir = harness.GeneratedDir("fleet-agent1");
        using var config = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(dir, "appsettings.json")));
        var journal = config.RootElement.GetProperty("Journal");
        Assert.True(journal.GetProperty("SendEnabled").GetBoolean());
        Assert.False(journal.TryGetProperty("FilesEnabled", out _));
        Assert.StartsWith("cj1.read.agent1.", journal.GetProperty("ReadToken").GetString());
        Assert.Equal(cross, journal.TryGetProperty("CrossChatEnabled", out _));
        Assert.Equal(cross, journal.TryGetProperty("CrossChatToken", out _));
        if (cross) Assert.StartsWith("cj1.read-cross-chat.agent1.", journal.GetProperty("CrossChatToken").GetString());
        Assert.Contains(config.RootElement.GetProperty("Agent").GetProperty("AllowedTools").EnumerateArray(), t => t.GetString() == JournalGrants.SendGrant);
        using var mcp = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(dir, ".mcp.json")));
        Assert.True(mcp.RootElement.GetProperty("mcpServers").TryGetProperty("fleet-journal-files", out _));
    }

    [Theory]
    [InlineData("none", true)]
    [InlineData("send", false)]
    [InlineData("fetch", true)]
    public async Task SwitchWithoutEffectiveSendGrant_EmitsNoCrossKeys(string grant, bool capture)
    {
        await using var harness = ProvisioningHarness.Create(Config);
        await harness.SeedAsync(db =>
        {
            var agent = Agent("codex", capture);
            agent.JournalCrossChatEnabled = false;
            agent.Tools.Clear();
            if (grant != "none") agent.Tools.Add(new() { ToolName = grant == "send" ? JournalGrants.SendGrant : Grant });
            db.Agents.Add(agent);
        });
        Assert.True((await harness.Service.ProvisionAsync("agent1")).Success);
        var dir = harness.GeneratedDir("fleet-agent1"); var names = new[] { "appsettings.json", ".mcp.json", "settings.json" };
        var before = names.ToDictionary(n => n, n => File.ReadAllBytes(Path.Combine(dir, n)));
        await harness.MutateAsync(async db => { (await db.Agents.SingleAsync()).JournalCrossChatEnabled = true; await db.SaveChangesAsync(); });
        var preview = await harness.Service.PreviewAsync("agent1");
        Assert.Contains("journal_cross_chat_unavailable:" + (capture ? "send_grant_missing" : "journal_capture_off"), preview.Diffs);
        if (!capture) Assert.Contains("journal_send_unavailable:journal_capture_off", preview.Diffs);
        Assert.True((await harness.Service.ProvisionAsync("agent1")).Success);
        foreach (var name in names) Assert.Equal(before[name], File.ReadAllBytes(Path.Combine(dir, name)));
        var generated = await File.ReadAllTextAsync(Path.Combine(dir, "appsettings.json"));
        Assert.DoesNotContain("SendEnabled", generated);
        Assert.DoesNotContain("CrossChatEnabled", generated);
        Assert.DoesNotContain("CrossChatToken", generated);
    }

    [Fact]
    public async Task ReservedEndpointRefusesBeforeDeprovisionEvenWithCaptureOff()
    {
        await using var harness = ProvisioningHarness.Create();
        await harness.SeedAsync(db => { var agent = Agent("codex", capture: false);
            agent.McpEndpoints.Add(new() { McpName = "fleet-journal-files", TransportType = "http", Url = "http://untrusted.test" }); db.Agents.Add(agent); });
        var result = await harness.Service.ReprovisionAsync("agent1");
        Assert.False(result.Success); Assert.Equal("journal_files_endpoint_reserved", result.Message); Assert.Equal(0, harness.DockerRequestCount);
    }
}
