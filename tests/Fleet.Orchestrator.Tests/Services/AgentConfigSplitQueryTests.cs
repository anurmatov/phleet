using System.Data.Common;
using Fleet.Orchestrator.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Fleet.Orchestrator.Tests.Services;

/// <summary>
/// Regression guard for phleet#195: PUT and DELETE /api/agents/{name} must use AsSplitQuery()
/// so that loading an agent with 8 parallel Include() collections does not produce a single
/// cartesian-join SQL query that causes OOM on operator-class agents.
///
/// Each test verifies that the same LINQ query used in the endpoint handler emits
/// multiple SQL statements (one per Include collection) rather than one large join.
/// A count of 1 would mean AsSplitQuery() was removed and the cartesian join is back.
/// </summary>
public class AgentConfigSplitQueryTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SqlCommandCountInterceptor _interceptor;
    private readonly DbContextOptions<OrchestratorDbContext> _options;

    public AgentConfigSplitQueryTests()
    {
        // Shared in-memory SQLite connection — kept open for the lifetime of the test
        // so the schema persists across EF context instances.
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        _interceptor = new SqlCommandCountInterceptor();

        _options = new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(_interceptor)
            // Suppress the split-query warning logged when AsSplitQuery is used without a transaction.
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.MultipleCollectionIncludeWarning))
            .Options;

        using var ctx = new OrchestratorDbContext(_options);
        ctx.Database.EnsureCreated();
        SeedAgent(ctx);
    }

    private static void SeedAgent(OrchestratorDbContext ctx)
    {
        var agent = new Agent
        {
            Name        = "test-split-agent",
            DisplayName = "Test Split Agent",
            Role        = "test",
            Model       = "claude-sonnet",
            ContainerName = "fleet-test-split-agent",
        };
        agent.Tools.Add(new AgentTool { ToolName = "memory_get", IsEnabled = true });
        agent.Projects.Add(new AgentProject { ProjectName = "fleet" });
        agent.McpEndpoints.Add(new AgentMcpEndpoint { McpName = "fleet-memory", Url = "http://fleet-memory:3100", TransportType = "http" });
        agent.EnvRefs.Add(new AgentEnvRef { EnvKeyName = "GITHUB_APP_PEM" });
        agent.TelegramUsers.Add(new AgentTelegramUser { UserId = 12345 });
        agent.TelegramGroups.Add(new AgentTelegramGroup { GroupId = -99999 });
        agent.Networks.Add(new AgentNetwork { NetworkName = "fleet-net" });
        ctx.Agents.Add(agent);
        ctx.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();

    // ── PUT /api/agents/{name}/config ────────────────────────────────────────

    [Fact]
    public async Task PutAgentConfig_QueryEmitsMultipleSqlStatements_NotSingleCartesianJoin()
    {
        using var ctx = new OrchestratorDbContext(_options);
        _interceptor.Reset();

        // Mirror the exact LINQ query from PUT /api/agents/{name}/config in Program.cs
        _ = await ctx.Agents
            .Include(a => a.Tools)
            .Include(a => a.Projects)
            .Include(a => a.McpEndpoints)
            .Include(a => a.Networks)
            .Include(a => a.EnvRefs)
            .Include(a => a.TelegramUsers)
            .Include(a => a.TelegramGroups)
            .Include(a => a.Instructions)
            .AsSplitQuery()
            .FirstOrDefaultAsync(a => a.Name == "test-split-agent");

        // AsSplitQuery produces 9 statements: 1 for the agent + 1 per Include collection.
        // If someone removes AsSplitQuery, EF Core collapses everything into 1 cartesian join.
        Assert.True(_interceptor.CommandCount > 1,
            $"Expected multiple SQL statements (split query), but only {_interceptor.CommandCount} statement(s) were issued. " +
            "This means AsSplitQuery() was removed from the PUT /api/agents/{{name}}/config handler, " +
            "which causes OOM on operator-class agents (phleet#195).");
    }

    // ── DELETE /api/agents/{name} ────────────────────────────────────────────

    [Fact]
    public async Task DeleteAgent_QueryEmitsMultipleSqlStatements_NotSingleCartesianJoin()
    {
        using var ctx = new OrchestratorDbContext(_options);
        _interceptor.Reset();

        // Mirror the exact LINQ query from DELETE /api/agents/{name} in Program.cs
        _ = await ctx.Agents
            .Include(a => a.Tools)
            .Include(a => a.Projects)
            .Include(a => a.McpEndpoints)
            .Include(a => a.Networks)
            .Include(a => a.EnvRefs)
            .Include(a => a.TelegramUsers)
            .Include(a => a.TelegramGroups)
            .Include(a => a.Instructions)
            .AsSplitQuery()
            .FirstOrDefaultAsync(a => a.Name == "test-split-agent");

        Assert.True(_interceptor.CommandCount > 1,
            $"Expected multiple SQL statements (split query), but only {_interceptor.CommandCount} statement(s) were issued. " +
            "This means AsSplitQuery() was removed from the DELETE /api/agents/{{name}} handler (phleet#195).");
    }

    // ── GET /api/agents/{name}/config — already had AsSplitQuery, verify stays that way ───

    [Fact]
    public async Task GetAgentConfig_QueryEmitsMultipleSqlStatements_AlreadyFixed()
    {
        using var ctx = new OrchestratorDbContext(_options);
        _interceptor.Reset();

        // Mirror GET /api/agents/{name}/config query (AsSplitQuery was already present before #195)
        _ = await ctx.Agents
            .Include(a => a.Tools.OrderBy(t => t.ToolName))
            .Include(a => a.Projects.OrderBy(p => p.ProjectName))
            .Include(a => a.McpEndpoints.OrderBy(e => e.McpName))
            .Include(a => a.Networks.OrderBy(n => n.NetworkName))
            .Include(a => a.EnvRefs.OrderBy(r => r.EnvKeyName))
            .Include(a => a.TelegramUsers.OrderBy(u => u.UserId))
            .Include(a => a.TelegramGroups.OrderBy(g => g.GroupId))
            .Include(a => a.Instructions.OrderBy(i => i.LoadOrder))
            .AsSplitQuery()
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Name == "test-split-agent");

        Assert.True(_interceptor.CommandCount > 1,
            $"Expected multiple SQL statements (split query), but only {_interceptor.CommandCount} statement(s) were issued. " +
            "AsSplitQuery() was removed from GET /api/agents/{{name}}/config (phleet#195).");
    }
}

/// <summary>
/// Counts the number of SQL commands issued by EF Core during a test window.
/// Call Reset() before the query under test, then read CommandCount after.
/// </summary>
internal sealed class SqlCommandCountInterceptor : DbCommandInterceptor
{
    private int _count;

    public int CommandCount => _count;

    public void Reset() => Interlocked.Exchange(ref _count, 0);

    public override DbDataReader ReaderExecuted(
        DbCommand command,
        CommandExecutedEventData eventData,
        DbDataReader result)
    {
        Interlocked.Increment(ref _count);
        return base.ReaderExecuted(command, eventData, result);
    }

    public override ValueTask<DbDataReader> ReaderExecutedAsync(
        DbCommand command,
        CommandExecutedEventData eventData,
        DbDataReader result,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _count);
        return base.ReaderExecutedAsync(command, eventData, result, cancellationToken);
    }
}
