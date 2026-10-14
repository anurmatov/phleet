using System.Text.Json;
using Fleet.Orchestrator.Data;
using Fleet.Shared;
using Microsoft.Extensions.Logging;

namespace Fleet.Orchestrator.Tests.Services;

/// <summary>
/// #460 provisioning: a codex agent gets <c>.generated/codex-root.toml</c> from the same plan the
/// effective-settings view serves, written before the container is created; <c>Agent.HarnessExtra</c>
/// appears in the generated appsettings only when set (every other agent keeps its bytes, see
/// <see cref="ProviderGoldenTests"/>); a stored value the allowlist refuses is logged once and the
/// mode defaults are written.
/// </summary>
public sealed class HarnessProvisioningTests
{
    private static async Task<ProvisioningHarness> ProvisionAsync(string provider, string model, string? extra, string? url = null)
    {
        var h = ProvisioningHarness.Create();
        await h.SeedAsync(db => db.Agents.Add(new Agent
        {
            Name = "agent1", DisplayName = "agent1", Role = "developer", Model = model, Provider = provider,
            ContainerName = "fleet-agent1", MemoryLimitMb = 1024, LocalBaseUrl = url,
            ContextWindow = url is null ? null : 131_072, HarnessExtra = extra,
        }));
        var result = await h.Service.ProvisionAsync("agent1");
        Assert.True(result.Success, result.Message);
        return h;
    }

    [Fact]
    public async Task Codex_WritesCodexRootToml_FromThePlan_BeforeTheContainerIsCreated()
    {
        await using var h = await ProvisionAsync("codex", "gpt-5.5", "history.persistence = \"save-all\"");

        var file = Path.Combine(h.GeneratedDir("fleet-agent1"), HarnessDefaults.CodexRootTomlFileName);
        Assert.Equal(
            "check_for_update_on_startup = false\n\n[analytics]\nenabled = false\n\n[history]\npersistence = \"save-all\"\n",
            await File.ReadAllTextAsync(file));
        // Written in the config step that precedes the create call, and mounted read-only at the
        // pinned path, so the bind never mounts a missing source as an empty directory.
        Assert.Contains(h.DockerRequests, r => r.Contains("/containers/create", StringComparison.Ordinal));
        Assert.Contains(h.CreateBodies, b => b.Contains("codex-root.toml:/app/codex-root.toml:ro", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("claude", "claude-opus-5-5")]
    [InlineData("gemini", "gemini-2.5-pro")]
    public async Task OtherProviders_GetNoCodexRootToml(string provider, string model)
    {
        await using var h = await ProvisionAsync(provider, model, null);

        Assert.False(File.Exists(Path.Combine(h.GeneratedDir("fleet-agent1"), HarnessDefaults.CodexRootTomlFileName)));
    }

    [Fact]
    public async Task HarnessExtra_IsWrittenToAppsettings_OnlyWhenSet()
    {
        const string extra = "--disallowedTools WebSearch\nMAX_MCP_OUTPUT_TOKENS=12000\n";
        await using var with = await ProvisionAsync("claude", "claude-opus-5-5", extra);
        await using var without = await ProvisionAsync("claude", "claude-opus-5-5", null);

        var withJson = await File.ReadAllTextAsync(Path.Combine(with.GeneratedDir("fleet-agent1"), "appsettings.json"));
        var withoutJson = await File.ReadAllTextAsync(Path.Combine(without.GeneratedDir("fleet-agent1"), "appsettings.json"));

        Assert.Equal(extra, JsonDocument.Parse(withJson).RootElement.GetProperty("Agent").GetProperty("HarnessExtra").GetString());
        Assert.False(JsonDocument.Parse(withoutJson).RootElement.GetProperty("Agent").TryGetProperty("HarnessExtra", out _));
    }

    [Fact]
    public async Task AStoredValueTheAllowlistRefuses_LogsOnce_AndWritesTheModeDefaults()
    {
        await using var h = await ProvisionAsync("codex", "ollama/qwen3.8:27b", "web_search = \"live\"", "http://inference-host:11434");

        var warnings = h.Logs.Entries.Where(e => e.Message.StartsWith("harnessExtra ignored", StringComparison.Ordinal)).ToList();
        Assert.Single(warnings);
        Assert.Equal(LogLevel.Warning, warnings[0].Level);
        Assert.Contains("cannot enable web search in local mode", warnings[0].Message);
        Assert.Contains("persistence = \"none\"",
            await File.ReadAllTextAsync(Path.Combine(h.GeneratedDir("fleet-agent1"), HarnessDefaults.CodexRootTomlFileName)));
    }
}
