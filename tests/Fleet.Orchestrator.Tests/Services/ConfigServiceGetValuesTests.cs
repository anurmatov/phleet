using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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

    private ConfigService BuildService(IServiceScopeFactory scopeFactory)
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
            NullLogger<ConfigService>.Instance);
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
}
