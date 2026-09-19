using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Fleet.Orchestrator.Tests.Services;

/// <summary>
/// Which agents count as using a style (#317).
/// </summary>
/// <remarks>
/// <para>
/// This one answer drives two things that must never disagree: the operator-facing list, which
/// says whose voice an edit is about to change, and the delete guard, which refuses to orphan an
/// agent. A style shown as unused that the guard then refuses to delete is a bug report; the
/// reverse — deleting a style an agent still names — leaves that agent writing an
/// <c>outputStyle</c> into <c>settings.json</c> that resolves to nothing, while
/// <c>system/init</c> keeps reporting the configured name.
/// </para>
/// <para>
/// SQLite rather than the InMemory provider, because these tests care about rows written and read
/// back through a relational store.
/// </para>
/// </remarks>
public class OutputStyleUsageTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<OrchestratorDbContext> _options;

    public OutputStyleUsageTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var ctx = new OrchestratorDbContext(_options);
        ctx.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private OrchestratorDbContext NewContext() => new(_options);

    private async Task SeedAgentsAsync(params (string Name, string? Style)[] agents)
    {
        await using var ctx = NewContext();
        foreach (var (name, style) in agents)
        {
            ctx.Agents.Add(new Agent
            {
                Name          = name,
                DisplayName   = name,
                Role          = "developer",
                Model         = "model-x",
                ContainerName = $"ctr-{name}",
                OutputStyle   = style,
            });
        }
        await ctx.SaveChangesAsync();
    }

    [Fact]
    public async Task GroupsAgentsByStyle_Ordered()
    {
        await SeedAgentsAsync(
            ("zulu", "fleet-messaging"),
            ("alpha", "fleet-messaging"),
            ("mike", "output-style-probe"));

        await using var ctx = NewContext();
        var usage = await OutputStyleUsage.ByStyleAsync(ctx);

        Assert.Equal(["alpha", "zulu"], usage["fleet-messaging"]);
        Assert.Equal(["mike"], usage["output-style-probe"]);
    }

    [Fact]
    public async Task AgentsWithNoStyle_AreNotCounted()
    {
        // NULL is the rollout switch — an agent on no style must not hold any style hostage.
        // Empty string is the same thing arriving from a form that cleared the select.
        await SeedAgentsAsync(("alpha", null), ("bravo", ""), ("charlie", "fleet-messaging"));

        await using var ctx = NewContext();
        var usage = await OutputStyleUsage.ByStyleAsync(ctx);

        Assert.Equal(["charlie"], usage["fleet-messaging"]);
        Assert.Single(usage);
    }

    [Fact]
    public async Task UnreferencedStyle_IsEmptyRatherThanMissing()
    {
        await SeedAgentsAsync(("alpha", "fleet-messaging"));

        await using var ctx = NewContext();
        var usage = await OutputStyleUsage.ByStyleAsync(ctx);

        Assert.Empty(OutputStyleUsage.For(usage, "output-style-probe"));
        Assert.Empty(await OutputStyleUsage.AgentsUsingAsync(ctx, "output-style-probe"));
    }

    /// <summary>
    /// Provisioning resolves the style with <c>s.Name == agent.OutputStyle</c>, which MySQL answers
    /// case-insensitively, and the style name is a primary key so two rows cannot differ by case
    /// alone. So an agent that spelled the name differently really is on that style, and both the
    /// list and the guard have to say so — on any provider, not just the one under test.
    /// </summary>
    [Fact]
    public async Task AgentSpellingTheNameDifferently_StillCounts()
    {
        await SeedAgentsAsync(("alpha", "Fleet-Messaging"), ("bravo", "fleet-messaging"));

        await using var ctx = NewContext();
        var usage = await OutputStyleUsage.ByStyleAsync(ctx);

        Assert.Equal(["alpha", "bravo"], OutputStyleUsage.For(usage, "fleet-messaging"));
        Assert.Equal(["alpha", "bravo"], await OutputStyleUsage.AgentsUsingAsync(ctx, "fleet-messaging"));
    }

    [Fact]
    public async Task TheListAndTheDeleteGuardAgree()
    {
        await SeedAgentsAsync(("alpha", "fleet-messaging"), ("bravo", null));

        await using var ctx = NewContext();
        var usage = await OutputStyleUsage.ByStyleAsync(ctx);

        foreach (var style in new[] { "fleet-messaging", "output-style-probe" })
        {
            Assert.Equal(
                OutputStyleUsage.For(usage, style),
                await OutputStyleUsage.AgentsUsingAsync(ctx, style));
        }
    }
}
