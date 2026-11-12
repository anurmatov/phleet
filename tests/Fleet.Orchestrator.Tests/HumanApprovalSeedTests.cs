using System.Reflection;
using System.Text.Json;
using Fleet.Orchestrator.Data;
using Microsoft.EntityFrameworkCore;

namespace Fleet.Orchestrator.Tests;

public class HumanApprovalSeedTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Seed_NewRequestOnlyInsertsWhenAbsent_AndNeverOverwritesCustomRows(bool existing)
    {
        var options = new DbContextOptionsBuilder<OrchestratorDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new OrchestratorDbContext(options);
        const string name = "UweHumanApprovalRequestWorkflow";
        db.WorkflowDefinitions.Add(new WorkflowDefinition { Namespace = "fleet", TaskQueue = "fleet", Name = "CustomWorkflow", Definition = "{}", Version = 7 });
        if (existing) db.WorkflowDefinitions.Add(new WorkflowDefinition { Namespace = "fleet", TaskQueue = "fleet", Name = name, Definition = "{\"custom\":true}", Version = 9, IsActive = false });
        await db.SaveChangesAsync();
        var path = new DirectoryInfo(AppContext.BaseDirectory);
        while (path != null && !File.Exists(Path.Combine(path.FullName, "seed.example.json"))) path = path.Parent;
        var seed = JsonSerializer.Deserialize<SeedFile>(File.ReadAllText(Path.Combine(path!.FullName, "seed.example.json")),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        seed.WorkflowDefinitions = seed.WorkflowDefinitions.Where(w => w.Name == name).ToList();
        var method = typeof(DbSeeder).GetMethod("SeedWorkflowDefinitionsAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
        await (Task)method.Invoke(null, [db, seed, null])!;
        await (Task)method.Invoke(null, [db, seed, null])!;
        Assert.Equal(2, await db.WorkflowDefinitions.CountAsync());
        Assert.Equal(7, (await db.WorkflowDefinitions.SingleAsync(w => w.Name == "CustomWorkflow")).Version);
        var row = await db.WorkflowDefinitions.SingleAsync(w => w.Name == name);
        Assert.Equal(existing ? 9 : 1, row.Version);
        Assert.Equal(!existing, row.IsActive);
        if (existing) Assert.Equal("{\"custom\":true}", row.Definition);
        else Assert.Contains("approval_visit", row.Definition);
    }
}
