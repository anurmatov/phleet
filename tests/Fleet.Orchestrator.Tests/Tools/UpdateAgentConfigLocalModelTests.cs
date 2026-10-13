using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Services;
using Fleet.Orchestrator.Tools;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Fleet.Orchestrator.Tests.Tools;

/// <summary>
/// #382 through <c>update_agent_config</c> and <c>get_agent_config</c>: <c>local_base_url</c> and
/// its alias <c>anthropic_base_url</c> (A1), codex local mode, the gemini refusal, and a provider
/// switch while local, one case per direction — the MCP mirror of
/// <c>AgentConfigEndpointsLocalModelTests</c>. #386: pi's rules (A5), switches into pi (A6) and the
/// <c>get_agent_config</c> lines (A25).
/// </summary>
public class UpdateAgentConfigLocalModelTests
{
    private const string Url = "http://inference-host:11434";

    private readonly DbContextOptions<OrchestratorDbContext> _options =
        new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseInMemoryDatabase($"local-model-{Guid.NewGuid():N}")
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

    private UpdateAgentConfigTool Tool() => new(Scopes(), new NoOpAclChangeNotifier());

    private void Seed(string provider, string model, string? url = null, int? window = null, string? envRef = null,
        string? effort = null)
    {
        using var db = new OrchestratorDbContext(_options);
        db.Agents.Add(new Agent
        {
            Name = "agent1", DisplayName = "agent1", Role = "test", Model = model, Provider = provider,
            MemoryLimitMb = 1024, ContainerName = "agent1", LocalBaseUrl = url, ContextWindow = window,
            Effort = effort,
            EnvRefs = envRef is null ? [] : [new AgentEnvRef { EnvKeyName = envRef }],
        });
        db.SaveChanges();
    }

    private Agent Stored()
    {
        using var db = new OrchestratorDbContext(_options);
        return db.Agents.AsNoTracking().Single(a => a.Name == "agent1");
    }

    // ── codex local set / clear ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Codex_SetThenClear()
    {
        Seed("codex", "ollama/qwen3.8:27b");

        var set = await Tool().UpdateAgentConfigAsync("agent1", local_base_url: "HTTP://Inference-Host:11434/");

        Assert.Contains($"local_base_url: (none) → {Url}", set);
        Assert.Equal(Url, Stored().LocalBaseUrl);

        var clear = await Tool().UpdateAgentConfigAsync("agent1", local_base_url: "");

        Assert.Contains($"local_base_url: {Url} → (none)", clear);
        Assert.Null(Stored().LocalBaseUrl);
    }

    [Fact]
    public async Task Gemini_WithAUrl_IsRefusedByL1()
    {
        Seed("gemini", "gemini-2.5-pro");

        var result = await Tool().UpdateAgentConfigAsync("agent1", local_base_url: Url);

        Assert.Contains("Local model runs on claude, codex or pi", result);
        Assert.Null(Stored().LocalBaseUrl);
    }

    // ── A1 (acceptance 9, MCP) ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A1a_OnlyTheAlias_IsStored()
    {
        Seed("claude", "qwen3.8:27b");

        await Tool().UpdateAgentConfigAsync("agent1", anthropic_base_url: Url);

        Assert.Equal(Url, Stored().LocalBaseUrl);
    }

    [Fact]
    public async Task A1b_BothEmpty_Clears()
    {
        Seed("claude", "qwen3.8:27b", Url);

        await Tool().UpdateAgentConfigAsync("agent1", local_base_url: "", anthropic_base_url: "", model: "claude-sonnet-5");

        Assert.Null(Stored().LocalBaseUrl);
    }

    [Theory]
    [InlineData("", Url)]
    [InlineData(Url, "http://other-host:11434")]
    public async Task A1ce_Conflict_NamesBothAndSavesNothing(string local, string alias)
    {
        Seed("claude", "qwen3.8:27b", "http://seeded-host:11434");

        var result = await Tool().UpdateAgentConfigAsync("agent1",
            local_base_url: local, anthropic_base_url: alias, memory_limit_mb: 2048);

        Assert.Contains("local_base_url", result);
        Assert.Contains("anthropic_base_url", result);
        var stored = Stored();
        Assert.Equal(("http://seeded-host:11434", 1024), (stored.LocalBaseUrl, stored.MemoryLimitMb));
    }

    [Fact]
    public async Task A1d_TwoSpellingsOfOneOrigin_AreStoredCanonical()
    {
        Seed("codex", "ollama/qwen3.8:27b");

        await Tool().UpdateAgentConfigAsync("agent1", local_base_url: "http://Inference-Host:11434/", anthropic_base_url: Url);

        Assert.Equal(Url, Stored().LocalBaseUrl);
    }

    [Fact]
    public async Task A1f_AFaultInTheAlias_NamesTheAlias()
    {
        Seed("codex", "ollama/qwen3.8:27b");

        var result = await Tool().UpdateAgentConfigAsync("agent1", local_base_url: Url, anthropic_base_url: Url + "/v1");

        Assert.StartsWith("anthropic_base_url:", result);
        Assert.Null(Stored().LocalBaseUrl);
    }

    // ── provider switch while local (acceptance 11, MCP) ─────────────────────────────────────────

    [Fact]
    public async Task CodexToClaude_WithTheBareTag_Accepted_AlonePrefixedModelRejectedByV6()
    {
        Seed("codex", "ollama/qwen3.8:27b", Url, 131_072);

        var rejected = await Tool().UpdateAgentConfigAsync("agent1", provider: "claude");
        Assert.Contains("selects the codex path", rejected);
        Assert.Equal("codex", Stored().Provider);

        var accepted = await Tool().UpdateAgentConfigAsync("agent1", provider: "claude", model: "qwen3.8:27b");
        Assert.DoesNotContain("Invalid", accepted);
        var stored = Stored();
        Assert.Equal(("claude", "qwen3.8:27b", Url, (int?)131_072),
            (stored.Provider, stored.Model, stored.LocalBaseUrl, stored.ContextWindow));
    }

    [Fact]
    public async Task ClaudeToCodex_WithTheComposedModel_Accepted_AloneBareTagRejectedByC1()
    {
        Seed("claude", "qwen3.8:27b", Url, 131_072);

        var rejected = await Tool().UpdateAgentConfigAsync("agent1", provider: "codex");
        Assert.Contains("ollama/<tag>", rejected);
        Assert.Equal("claude", Stored().Provider);

        var accepted = await Tool().UpdateAgentConfigAsync("agent1", provider: "codex", model: "ollama/qwen3.8:27b");
        Assert.DoesNotContain("Invalid", accepted);
        var stored = Stored();
        Assert.Equal(("codex", "ollama/qwen3.8:27b", Url, (int?)131_072),
            (stored.Provider, stored.Model, stored.LocalBaseUrl, stored.ContextWindow));
    }

    // ── get_agent_config ─────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("codex", "ollama/qwen3.8:27b", Url, null, "- Local model: on — server " + Url, "- Context window: 131072 tokens\n")]
    [InlineData("codex", "ollama/qwen3.8:27b", null, "CODEX_OSS_BASE_URL", "- Local model: on — server from CODEX_OSS_BASE_URL Env Ref (legacy)", "(ignored: legacy Env Ref; set the local server URL)")]
    [InlineData("codex", "gpt-5", null, null, "- Local model: off", "(ignored: local model off)")]
    [InlineData("claude", "qwen3.8:27b", Url, null, "- Local model: on — server " + Url, "- Context window: 131072 tokens\n")]
    [InlineData("pi", PiTag, PiUrl, null, "- Local model: on — server " + PiUrl + " (Pi)", "- Context window: 131072 tokens\n")]
    public async Task GetAgentConfig_ReportsTheLocalModel(
        string provider, string model, string? url, string? envRef, string localLine, string windowText)
    {
        Seed(provider, model, url, 131_072, envRef);

        var result = (await new GetAgentConfigTool(Scopes()).GetAgentConfigAsync("agent1")).ReplaceLineEndings("\n");

        Assert.Contains(localLine + "\n", result);
        Assert.Contains(windowText, result);
    }

    // ── #386 provider pi ─────────────────────────────────────────────────────────────────────────

    private const string PiUrl = "http://inference-host:8000";
    private const string PiTag = "qwen3.8-flash-next";

    private (string Provider, string Model, string? Url, int? Window, string? Effort) Row()
    {
        var stored = Stored();
        return (stored.Provider, stored.Model, stored.LocalBaseUrl, stored.ContextWindow, stored.Effort);
    }

    [Theory]
    [InlineData("pi", "- Provider: pi\n")]
    [InlineData("codex", "- Provider: codex\n")]
    [InlineData("", "- Provider: (empty: claude)\n")]
    public async Task A25_GetAgentConfig_PrintsTheProvider(string provider, string line)
    {
        Seed(provider, PiTag, PiUrl, 131_072);

        var result = (await new GetAgentConfigTool(Scopes()).GetAgentConfigAsync("agent1")).ReplaceLineEndings("\n");

        Assert.Contains(line, result);
    }

    [Fact]
    public async Task Pi_SetUpByASwitchFromClaudeCloud_IsStoredCanonical()
    {
        Seed("claude", "claude-opus-5-5");

        var result = await Tool().UpdateAgentConfigAsync("agent1",
            provider: "pi", model: PiTag, local_base_url: "HTTP://Inference-Host:8000/", context_window: 131_072, effort: "off");

        Assert.DoesNotContain("Invalid", result);
        Assert.Equal(("pi", PiTag, (string?)PiUrl, (int?)131_072, (string?)"off"), Row());
    }

    // ── A5 over MCP: each rule's text, and nothing saved ─────────────────────────────────────────

    public static TheoryData<string, string> A5Cases => new()
    {
        { "PI1 no url", Fleet.Shared.PiLocalModel.Pi1Text },
        { "PI1 url cleared", Fleet.Shared.PiLocalModel.Pi1Text },
        { "PI2 charset", Fleet.Shared.PiLocalModel.Pi2Text },
        { "PI2 ollama/", Fleet.Shared.PiLocalModel.Pi2Text },
        { "PI2 lmstudio/", Fleet.Shared.PiLocalModel.Pi2Text },
        { "PI2 zai/", Fleet.Shared.PiLocalModel.Pi2Text },
        { "PI3 unset", Fleet.Shared.PiLocalModel.Pi3Text },
        { "PI3 65535", Fleet.Shared.PiLocalModel.Pi3Text },
        { "PI4 xhigh", Fleet.Shared.PiLocalModel.Pi4Text },
        { "L1 gemini url", Fleet.Shared.LocalModel.L1Text },
    };

    [Theory]
    [MemberData(nameof(A5Cases))]
    public async Task A5_PiRules_ReturnTheRuleText_AndLeaveTheRowUnchanged(string @case, string expected)
    {
        if (@case == "PI1 no url") Seed("claude", "claude-opus-5-5");
        else if (@case == "L1 gemini url") Seed("gemini", "gemini-2.5-pro");
        else Seed("pi", PiTag, PiUrl, 131_072, effort: "on");
        var before = Row();

        var tool = Tool();
        var result = @case switch
        {
            "PI1 no url" => await tool.UpdateAgentConfigAsync("agent1", provider: "pi", model: PiTag, context_window: 131_072),
            "PI1 url cleared" => await tool.UpdateAgentConfigAsync("agent1", local_base_url: ""),
            "PI2 charset" => await tool.UpdateAgentConfigAsync("agent1", model: "qwen 3.8"),
            "PI2 ollama/" => await tool.UpdateAgentConfigAsync("agent1", model: "ollama/" + PiTag),
            "PI2 lmstudio/" => await tool.UpdateAgentConfigAsync("agent1", model: "lmstudio/" + PiTag),
            "PI2 zai/" => await tool.UpdateAgentConfigAsync("agent1", model: "zai/glm-5.3"),
            "PI3 unset" => await tool.UpdateAgentConfigAsync("agent1", context_window: 0),
            "PI3 65535" => await tool.UpdateAgentConfigAsync("agent1", context_window: 65_535),
            "PI4 xhigh" => await tool.UpdateAgentConfigAsync("agent1", effort: "xhigh"),
            "L1 gemini url" => await tool.UpdateAgentConfigAsync("agent1", local_base_url: PiUrl),
            _ => throw new ArgumentOutOfRangeException(nameof(@case)),
        };

        Assert.Contains(expected, result);
        Assert.Equal(before, Row());
    }

    // ── A6 over MCP: a switch sends the matching values; nothing is rewritten ────────────────────

    [Fact]
    public async Task A6_CodexLocalToPi_WithTagAndEffort_KeepsUrlAndWindow()
    {
        Seed("codex", "ollama/qwen3.8:27b", Url, 131_072, effort: "none");

        var result = await Tool().UpdateAgentConfigAsync("agent1", provider: "pi", model: "qwen3.8:27b", effort: "off");

        Assert.DoesNotContain("Invalid", result);
        Assert.Equal(("pi", "qwen3.8:27b", (string?)Url, (int?)131_072, (string?)"off"), Row());
    }

    [Fact]
    public async Task A6_CodexLocalToPi_ProviderAlone_IsRefusedByPI2()
    {
        Seed("codex", "ollama/qwen3.8:27b", Url, 131_072, effort: "none");
        var before = Row();

        var result = await Tool().UpdateAgentConfigAsync("agent1", provider: "pi");

        Assert.Contains(Fleet.Shared.PiLocalModel.Pi2Text, result);
        Assert.Equal(before, Row());
    }

    [Fact]
    public async Task A6_ClaudeLocalWithXhighToPi_ProviderAlone_IsRefusedByPI4()
    {
        Seed("claude", "qwen3.8:27b", Url, 131_072, effort: "xhigh");
        var before = Row();

        var result = await Tool().UpdateAgentConfigAsync("agent1", provider: "pi");

        Assert.Contains(Fleet.Shared.PiLocalModel.Pi4Text, result);
        Assert.Equal(before, Row());
    }
}
