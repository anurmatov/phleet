using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fleet.Orchestrator.Tests.Services;

/// <summary>
/// Unit tests for ConfigService.GetValuesAsync agent-derived template expansion.
///
/// Uses EF Core InMemory provider to avoid MySQL dependency.
/// A temp .env file seeds the env map so the lookup path is fully exercised.
/// </summary>
public class ConfigServiceGetValuesTests : IDisposable
{
    private readonly string _envFile;
    private readonly List<string> _tempFiles = [];

    public ConfigServiceGetValuesTests()
    {
        _envFile = Path.GetTempFileName();
        _tempFiles.Add(_envFile);
    }

    public void Dispose()
    {
        foreach (var f in _tempFiles)
            try { File.Delete(f); } catch { /* best effort */ }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static OrchestratorDbContext CreateDb(string name)
    {
        var options = new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseInMemoryDatabase(name)
            .Options;
        return new OrchestratorDbContext(options);
    }

    private static IServiceScopeFactory BuildScopeFactory(OrchestratorDbContext db)
    {
        var services = new ServiceCollection();
        services.AddSingleton(db);
        services.AddScoped<OrchestratorDbContext>(_ => db);
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    private ConfigService BuildService(IServiceScopeFactory scopeFactory,
        ILogger<ConfigService>? logger = null)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Provisioning:EnvFilePath"] = _envFile,
            })
            .Build();

        var rabbitOptions = Microsoft.Extensions.Options.Options.Create(
            new Fleet.Orchestrator.Configuration.RabbitMqOptions { Host = "", Exchange = "" });

        return new ConfigService(config, rabbitOptions, scopeFactory,
            logger ?? NullLogger<ConfigService>.Instance);
    }

    // ── TemplateToRegex static tests ──────────────────────────────────────────

    [Fact]
    public void TemplateToRegex_SingleUnderscore_MatchesExpectedKey()
    {
        var rx = ConfigService.TemplateToRegex("TELEGRAM_{SHORTNAME}_BOT_TOKEN");
        Assert.True(rx.IsMatch("TELEGRAM_MYAGENT_BOT_TOKEN"));
        Assert.True(rx.IsMatch("TELEGRAM_CTO_BOT_TOKEN")); // single segment
    }

    [Fact]
    public void TemplateToRegex_MultiUnderscore_DoesNotMatchMultiSegment()
    {
        // [^_]+ means the shortname segment must not contain underscore.
        // TELEGRAM_CTO_LEAD_BOT_TOKEN has "CTO_LEAD" which contains underscore — no match.
        var rx = ConfigService.TemplateToRegex("TELEGRAM_{SHORTNAME}_BOT_TOKEN");
        Assert.False(rx.IsMatch("TELEGRAM_CTO_LEAD_BOT_TOKEN"));
    }

    // ── GetValuesAsync — env_ref walk ─────────────────────────────────────────

    [Fact]
    public async Task GetValuesAsync_AgentWithMatchingEnvRef_KeyedByAgentName()
    {
        await using var db = CreateDb("test_matching_envref");
        var agent = new Agent
        {
            Name = "myagent", ShortName = "myagent", DisplayName = "My Agent",
            Role = "dev", Model = "claude", ContainerName = "fleet-myagent",
        };
        agent.EnvRefs.Add(new AgentEnvRef { EnvKeyName = "TELEGRAM_MYAGENT_BOT_TOKEN" });
        db.Agents.Add(agent);
        await db.SaveChangesAsync();

        await File.WriteAllTextAsync(_envFile, "TELEGRAM_MYAGENT_BOT_TOKEN=bot-token-123\n");

        var svc = BuildService(BuildScopeFactory(db));
        var result = await svc.GetValuesAsync(["TELEGRAM_{SHORTNAME}_BOT_TOKEN"]);

        Assert.True(result.AgentDerived.ContainsKey("TELEGRAM_{SHORTNAME}_BOT_TOKEN"));
        var inner = result.AgentDerived["TELEGRAM_{SHORTNAME}_BOT_TOKEN"];
        Assert.True(inner.ContainsKey("myagent"), "inner dict must be keyed by agent.Name");
        Assert.Equal("bot-token-123", inner["myagent"]);
    }

    /// <summary>
    /// A template is not denylisted, but what it resolves to can be: an Env Ref the denylist covers
    /// is never read through one, so a credential withheld by name is not served by pattern.
    /// </summary>
    [Theory]
    [InlineData("FLEET_{SHORTNAME}_API_KEY", "FLEET_QDRANT_API_KEY")]
    [InlineData("FLEET_{SHORTNAME}_BEARER_TOKEN", "FLEET_TEMPORAL_BEARER_TOKEN")]
    public async Task GetValuesAsync_ADenylistedEnvRef_IsNotServedThroughATemplate_WhileOthersAre(string template, string envKey)
    {
        await using var db = CreateDb($"test_denylisted_{envKey}");
        var agent = new Agent
        {
            Name = "myagent", ShortName = "myagent", DisplayName = "My Agent",
            Role = "dev", Model = "claude", ContainerName = "fleet-myagent",
        };
        agent.EnvRefs.Add(new AgentEnvRef { EnvKeyName = envKey });
        agent.EnvRefs.Add(new AgentEnvRef { EnvKeyName = "TELEGRAM_MYAGENT_BOT_TOKEN" });
        db.Agents.Add(agent);
        await db.SaveChangesAsync();

        await File.WriteAllTextAsync(_envFile, $"{envKey}=synthetic-secret\nTELEGRAM_MYAGENT_BOT_TOKEN=bot-token-123\n");

        var result = await BuildService(BuildScopeFactory(db)).GetValuesAsync([template, "TELEGRAM_{SHORTNAME}_BOT_TOKEN"]);

        Assert.Empty(result.AgentDerived[template]);
        Assert.Equal("bot-token-123", result.AgentDerived["TELEGRAM_{SHORTNAME}_BOT_TOKEN"]["myagent"]);
    }

    /// <summary>
    /// A denylisted Env Ref is left out before one is chosen: it cannot win the alphabetical pick
    /// over an Env Ref that may be served, and so hide it.
    /// </summary>
    [Fact]
    public async Task GetValuesAsync_ADenylistedEnvRef_DoesNotTakeAnAllowedOnesPlace()
    {
        await using var db = CreateDb("test_denylisted_pick");
        var agent = new Agent
        {
            Name = "myagent", ShortName = "myagent", DisplayName = "My Agent",
            Role = "dev", Model = "claude", ContainerName = "fleet-myagent",
        };
        agent.EnvRefs.Add(new AgentEnvRef { EnvKeyName = "FLEET_QDRANT_API_KEY" });
        agent.EnvRefs.Add(new AgentEnvRef { EnvKeyName = "FLEET_XYZ_API_KEY" });
        db.Agents.Add(agent);
        await db.SaveChangesAsync();

        await File.WriteAllTextAsync(_envFile, "FLEET_QDRANT_API_KEY=synthetic-secret\nFLEET_XYZ_API_KEY=allowed-value\n");

        var result = await BuildService(BuildScopeFactory(db)).GetValuesAsync(["FLEET_{SHORTNAME}_API_KEY"]);

        Assert.Equal("allowed-value", result.AgentDerived["FLEET_{SHORTNAME}_API_KEY"]["myagent"]);
    }

    [Fact]
    public async Task GetValuesAsync_AgentWithoutEnvRef_AbsentFromInnerDict()
    {
        await using var db = CreateDb("test_no_envref");
        var agent = new Agent
        {
            Name = "notoken", ShortName = "notoken", DisplayName = "No Token",
            Role = "dev", Model = "claude", ContainerName = "fleet-notoken",
        };
        // No EnvRefs added
        db.Agents.Add(agent);
        await db.SaveChangesAsync();

        await File.WriteAllTextAsync(_envFile, "# no tokens\n");

        var svc = BuildService(BuildScopeFactory(db));
        var result = await svc.GetValuesAsync(["TELEGRAM_{SHORTNAME}_BOT_TOKEN"]);

        var inner = result.AgentDerived["TELEGRAM_{SHORTNAME}_BOT_TOKEN"];
        Assert.False(inner.ContainsKey("notoken"), "agent without env_ref must be absent from inner dict");
    }

    [Fact]
    public async Task GetValuesAsync_AgentEnvRefWithMissingEnvVar_AbsentFromInnerDict()
    {
        await using var db = CreateDb("test_missing_env_var");
        var agent = new Agent
        {
            Name = "orphan", ShortName = "orphan", DisplayName = "Orphan",
            Role = "dev", Model = "claude", ContainerName = "fleet-orphan",
        };
        agent.EnvRefs.Add(new AgentEnvRef { EnvKeyName = "TELEGRAM_ORPHAN_BOT_TOKEN" });
        db.Agents.Add(agent);
        await db.SaveChangesAsync();

        // env var not present in .env file
        await File.WriteAllTextAsync(_envFile, "# no telegram tokens defined\n");

        var svc = BuildService(BuildScopeFactory(db));
        var result = await svc.GetValuesAsync(["TELEGRAM_{SHORTNAME}_BOT_TOKEN"]);

        var inner = result.AgentDerived["TELEGRAM_{SHORTNAME}_BOT_TOKEN"];
        Assert.False(inner.ContainsKey("orphan"),
            "agent whose env_ref key is absent from .env must be absent from inner dict");
    }

    [Fact]
    public async Task GetValuesAsync_MultipleAgents_EachKeyedByOwnName()
    {
        await using var db = CreateDb("test_multi_agents");
        foreach (var (name, envKey, tokenValue) in new[]
        {
            ("agent-alpha", "TELEGRAM_ALPHA_BOT_TOKEN", "token-alpha"),
            ("agent-beta",  "TELEGRAM_BETA_BOT_TOKEN",  "token-beta"),
        })
        {
            var a = new Agent
            {
                Name = name, ShortName = name, DisplayName = name,
                Role = "dev", Model = "claude", ContainerName = $"fleet-{name}",
            };
            a.EnvRefs.Add(new AgentEnvRef { EnvKeyName = envKey });
            db.Agents.Add(a);
            await File.AppendAllTextAsync(_envFile, $"{envKey}={tokenValue}\n");
        }
        await db.SaveChangesAsync();

        var svc = BuildService(BuildScopeFactory(db));
        var result = await svc.GetValuesAsync(["TELEGRAM_{SHORTNAME}_BOT_TOKEN"]);

        var inner = result.AgentDerived["TELEGRAM_{SHORTNAME}_BOT_TOKEN"];
        Assert.Equal("token-alpha", inner["agent-alpha"]);
        Assert.Equal("token-beta",  inner["agent-beta"]);
    }

    [Fact]
    public async Task GetValuesAsync_AgentWithTwoMatchingEnvRefs_AlphabeticFirstWins_AndLogsWarning()
    {
        await using var db = CreateDb("test_two_matching_envrefs");
        var agent = new Agent
        {
            Name = "multi", ShortName = "multi", DisplayName = "Multi",
            Role = "dev", Model = "claude", ContainerName = "fleet-multi",
        };
        // Two env_refs both matching the template; alphabetically AAA < BBB → AAA wins
        agent.EnvRefs.Add(new AgentEnvRef { EnvKeyName = "TELEGRAM_AAA_BOT_TOKEN" });
        agent.EnvRefs.Add(new AgentEnvRef { EnvKeyName = "TELEGRAM_BBB_BOT_TOKEN" });
        db.Agents.Add(agent);
        await db.SaveChangesAsync();

        await File.WriteAllTextAsync(_envFile,
            "TELEGRAM_AAA_BOT_TOKEN=val_aaa\nTELEGRAM_BBB_BOT_TOKEN=val_bbb\n");

        var logger = new CapturingLogger<ConfigService>();
        var svc = BuildService(BuildScopeFactory(db), logger);
        var result = await svc.GetValuesAsync(["TELEGRAM_{SHORTNAME}_BOT_TOKEN"]);

        var inner = result.AgentDerived["TELEGRAM_{SHORTNAME}_BOT_TOKEN"];
        Assert.True(inner.ContainsKey("multi"), "agent must appear in inner dict");
        Assert.Equal("val_aaa", inner["multi"]);
        Assert.True(logger.HasWarning("Remove duplicates"),
            "Expected a Warning log about duplicate env_refs");
    }
}

/// <summary>
/// Captures log records for assertion in tests.
/// </summary>
file sealed class CapturingLogger<T> : ILogger<T>
{
    private readonly List<(LogLevel Level, string Message)> _entries = [];

    public bool HasWarning(string fragment) =>
        _entries.Any(e => e.Level == LogLevel.Warning && e.Message.Contains(fragment));

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter)
        => _entries.Add((logLevel, formatter(state, exception)));

    public bool IsEnabled(LogLevel logLevel) => true;
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
}
