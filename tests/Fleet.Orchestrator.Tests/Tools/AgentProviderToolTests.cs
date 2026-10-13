using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Services;
using Fleet.Orchestrator.Tools;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Fleet.Orchestrator.Tests.Tools;

/// <summary>
/// #386 PV1 through <c>create_agent</c> and <c>update_agent_config</c> (A30): an unknown provider is
/// refused with the PV1 text and nothing is written; an empty provider stays claude, and the four
/// known names are accepted. The REST twins are in <c>AgentCreateEndpointsTests</c> and
/// <c>AgentConfigEndpointsLocalModelTests</c>.
/// </summary>
public class AgentProviderToolTests
{
    private readonly DbContextOptions<OrchestratorDbContext> _options =
        new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseInMemoryDatabase($"agent-provider-{Guid.NewGuid():N}")
            .Options;

    private sealed class NoOpAclChangeNotifier : IAclChangeNotifier
    {
        public Task PublishAclChangedAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private IServiceScopeFactory Scopes()
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => new OrchestratorDbContext(_options));
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    private CreateAgentTool Create() => new(Scopes(), new ConfigurationBuilder().Build());

    private UpdateAgentConfigTool Update() => new(Scopes(), new NoOpAclChangeNotifier());

    private static string Pv1(string provider) => $"Unknown provider '{provider}'; use claude, codex, gemini or pi.";

    private List<Agent> Agents()
    {
        using var db = new OrchestratorDbContext(_options);
        return db.Agents.AsNoTracking().ToList();
    }

    private void Seed(string provider)
    {
        using var db = new OrchestratorDbContext(_options);
        db.Agents.Add(new Agent
        {
            Name = "agent1", DisplayName = "agent1", Role = "test", Model = "claude-opus-5-5",
            Provider = provider, MemoryLimitMb = 1024, ContainerName = "agent1",
        });
        db.SaveChanges();
    }

    // ── create_agent ─────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("foo")]
    [InlineData("Claude")]
    [InlineData("pi ")]
    public async Task Create_AnUnknownProvider_IsRefused_AndNoRowIsWritten(string provider)
    {
        var result = await Create().CreateAgentAsync("agent1", "developer", "model-x", provider: provider);

        Assert.Equal(Pv1(provider), result);
        Assert.Empty(Agents());
        await using var db = new OrchestratorDbContext(_options);
        Assert.Empty(db.AgentNetworks);
        Assert.Empty(db.AgentEnvRefs);
    }

    [Theory]
    [InlineData(null, "claude")]
    [InlineData("", "")]
    [InlineData("claude", "claude")]
    [InlineData("codex", "codex")]
    [InlineData("gemini", "gemini")]
    public async Task Create_AKnownOrEmptyProvider_IsAccepted(string? provider, string stored)
    {
        var result = await Create().CreateAgentAsync("agent1", "developer", "model-x", provider: provider);

        Assert.StartsWith("Agent 'agent1' created in DB", result);
        var agent = Assert.Single(Agents());
        Assert.Equal(stored, agent.Provider);
        Assert.Null(agent.LocalBaseUrl);
        Assert.Null(agent.ContextWindow);
    }

    // ── #386 H1: create_agent checks the local server URL and context like update_agent_config ──

    [Fact]
    public async Task Create_ACompletePiAgent_IsStoredWithTheCanonicalUrlAndTheContext()
    {
        var result = await Create().CreateAgentAsync("agent1", "developer", "qwen3.8-flash-next", provider: "pi",
            local_base_url: "HTTP://Inference-Host:8000/", context_window: 131072);

        Assert.StartsWith("Agent 'agent1' created in DB", result);
        var agent = Assert.Single(Agents());
        Assert.Equal("pi", agent.Provider);
        Assert.Equal("http://inference-host:8000", agent.LocalBaseUrl);
        Assert.Equal(131072, agent.ContextWindow);
    }

    [Theory]
    [InlineData("qwen3.8-flash-next", null, 131072, "Pi runs only on a local server; set the local server URL.")]
    [InlineData("qwen3.8-flash-next", "http://inference-host:8000", null, "Pi needs the server's context size, at least 65536; set the context window to the server's --ctx.")]
    [InlineData("qwen3.8-flash-next", "http://inference-host:8000", 65535, "Pi needs the server's context size, at least 65536; set the context window to the server's --ctx.")]
    [InlineData("lmstudio/qwen3.8-flash-next", "http://inference-host:8000", 131072, "Pi takes the bare model tag the server serves (no ollama/ or lmstudio/ prefix).")]
    public async Task Create_AnIncompletePiAgent_IsRefusedWithTheRuleText_AndNoRowIsWritten(
        string model, string? url, int? context, string expected)
    {
        var result = await Create().CreateAgentAsync("agent1", "developer", model, provider: "pi",
            local_base_url: url, context_window: context);

        Assert.Equal(expected, result);
        Assert.Empty(Agents());
        await using var db = new OrchestratorDbContext(_options);
        Assert.Empty(db.AgentNetworks);
        Assert.Empty(db.AgentEnvRefs);
    }

    [Fact]
    public async Task Create_GeminiWithAUrl_IsRefusedByL1()
    {
        var result = await Create().CreateAgentAsync("agent1", "developer", "gemini-2.5-pro", provider: "gemini",
            local_base_url: "http://inference-host:8000");

        Assert.Equal("Local model runs on claude, codex or pi; clear the local server URL first.", result);
        Assert.Empty(Agents());
    }

    // ── update_agent_config ──────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("foo")]
    [InlineData("CODEX")]
    public async Task Update_AnUnknownProvider_IsRefused_AndTheRowIsUnchanged(string provider)
    {
        Seed("claude");

        var result = await Update().UpdateAgentConfigAsync("agent1", provider: provider, memory_limit_mb: 2048);

        Assert.Equal(Pv1(provider), result);
        var stored = Assert.Single(Agents());
        Assert.Equal(("claude", 1024), (stored.Provider, stored.MemoryLimitMb));
    }

    [Fact]
    public async Task Update_ARowAlreadyHoldingAnUnknownProvider_IsRefusedUntilTheProviderIsFixed()
    {
        Seed("foo");

        var refused = await Update().UpdateAgentConfigAsync("agent1", memory_limit_mb: 2048);
        Assert.Equal(Pv1("foo"), refused);
        Assert.Equal(1024, Assert.Single(Agents()).MemoryLimitMb);

        var fixedRow = await Update().UpdateAgentConfigAsync("agent1", provider: "claude", memory_limit_mb: 2048);
        Assert.Contains("provider: foo → claude", fixedRow);
        Assert.Equal(("claude", 2048), (Agents()[0].Provider, Agents()[0].MemoryLimitMb));
    }

    [Fact]
    public async Task Update_AnEmptyProvider_StaysClaude()
    {
        Seed("claude");

        var result = await Update().UpdateAgentConfigAsync("agent1", provider: "");

        Assert.Contains("provider: claude → ", result);
        Assert.Equal("", Assert.Single(Agents()).Provider);
    }
}
