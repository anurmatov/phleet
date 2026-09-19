using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Services;
using Fleet.Orchestrator.Tools;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Fleet.Orchestrator.Tests.Tools;

/// <summary>
/// That <c>update_agent_config</c> is actually wired to the grant hook (#311).
/// </summary>
/// <remarks>
/// The provenance rules themselves live in <c>AgentProjectAccessSyncTests</c>. What is asserted
/// here is the wiring — that changing an agent's projects through the tool reaches the hook at all,
/// and that a call which does not mention projects leaves the table alone. A missing call here is
/// invisible at the tool's own return value: it reports the project change either way, and the
/// failure only shows up later as a 403 on every memory read.
/// </remarks>
public class UpdateAgentConfigProjectAccessTests
{
    private sealed class CountingNotifier : IAclChangeNotifier
    {
        public int Calls { get; private set; }

        public Task PublishAclChangedAsync(CancellationToken ct = default)
        {
            Calls++;
            return Task.CompletedTask;
        }
    }

    private static OrchestratorDbContext CreateDb(string name) =>
        new(new DbContextOptionsBuilder<OrchestratorDbContext>().UseInMemoryDatabase(name).Options);

    // A fresh context per scope over the same in-memory store: the tool disposes the scope it
    // creates, so handing it one shared context would dispose the one the assertions read.
    private static IServiceScopeFactory BuildScopeFactory(string dbName)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => CreateDb(dbName));
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    private static void SeedAgent(OrchestratorDbContext db, string name, params string[] projects)
    {
        var agent = new Agent
        {
            Name = name,
            DisplayName = name,
            Role = "developer",
            Model = "claude-sonnet-5",
            ContainerName = $"fleet-{name}",
        };
        db.Agents.Add(agent);
        db.SaveChanges();

        foreach (var p in projects)
            db.AgentProjects.Add(new AgentProject { AgentId = agent.Id, ProjectName = p });
        db.SaveChanges();
    }

    [Fact]
    public async Task Adding_A_Project_Grants_Access_And_Broadcasts()
    {
        var name = nameof(Adding_A_Project_Grants_Access_And_Broadcasts);
        var db = CreateDb(name);
        SeedAgent(db, "adev");
        var notifier = new CountingNotifier();
        var tool = new UpdateAgentConfigTool(BuildScopeFactory(name), notifier);

        await tool.UpdateAgentConfigAsync("adev", projects: "fleet,mml");

        var rows = db.AgentProjectAccess.Where(x => x.AgentName == "adev").OrderBy(x => x.Project).ToList();
        Assert.Equal(["fleet", "mml"], rows.Select(r => r.Project));
        Assert.All(rows, r => Assert.Equal(AgentProjectAccessSource.Assignment, r.Source));
        Assert.Equal(1, notifier.Calls);
    }

    [Fact]
    public async Task Dropping_A_Project_Revokes_Access_And_Broadcasts()
    {
        var name = nameof(Dropping_A_Project_Revokes_Access_And_Broadcasts);
        var db = CreateDb(name);
        SeedAgent(db, "adev", "fleet", "mml");
        db.AgentProjectAccess.AddRange(
            new AgentProjectAccess { AgentName = "adev", Project = "fleet", Source = AgentProjectAccessSource.Assignment },
            new AgentProjectAccess { AgentName = "adev", Project = "mml", Source = AgentProjectAccessSource.Assignment });
        db.SaveChanges();

        var notifier = new CountingNotifier();
        var tool = new UpdateAgentConfigTool(BuildScopeFactory(name), notifier);

        await tool.UpdateAgentConfigAsync("adev", projects: "fleet");

        var row = Assert.Single(db.AgentProjectAccess.Where(x => x.AgentName == "adev").ToList());
        Assert.Equal("fleet", row.Project);
        Assert.Equal(1, notifier.Calls);
    }

    /// <summary>
    /// A call that does not mention projects must not touch the table. Every scalar agent setting
    /// goes through this tool, so a hook that ran unconditionally would rewrite the ACL on edits
    /// that have nothing to do with it.
    /// </summary>
    [Fact]
    public async Task A_Config_Change_That_Does_Not_Touch_Projects_Leaves_Access_Alone()
    {
        var name = nameof(A_Config_Change_That_Does_Not_Touch_Projects_Leaves_Access_Alone);
        var db = CreateDb(name);
        SeedAgent(db, "adev", "fleet");
        db.AgentProjectAccess.Add(new AgentProjectAccess
        {
            AgentName = "adev",
            Project = "fuddyduddy",
            Source = AgentProjectAccessSource.Manual,
        });
        db.SaveChanges();

        var notifier = new CountingNotifier();
        var tool = new UpdateAgentConfigTool(BuildScopeFactory(name), notifier);

        await tool.UpdateAgentConfigAsync("adev", model: "claude-opus-5");

        var row = Assert.Single(db.AgentProjectAccess.Where(x => x.AgentName == "adev").ToList());
        Assert.Equal("fuddyduddy", row.Project);
        Assert.Equal(AgentProjectAccessSource.Manual, row.Source);
        Assert.Equal(0, notifier.Calls);
    }
}
