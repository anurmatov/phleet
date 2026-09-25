using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Services;
using Fleet.Orchestrator.Tests.Services;
using Fleet.Orchestrator.Tools;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Fleet.Orchestrator.Tests.Tools;

/// <summary>
/// The #346 <c>Size:</c> lines on <c>create_instruction</c>, <c>update_instruction</c> and
/// <c>rollback_instruction</c>: today's success output is an exact prefix, the <c>Size:</c> line is
/// always appended, <c>Size warning:</c> only for crossed / stillOver, and errors get neither.
/// </summary>
public sealed class InstructionToolsSizeTests : IDisposable
{
    private readonly SizeToolsDb _db = new();
    private readonly SizeLogCapture _logs = new();
    private PromptSizePolicy Policy => new(10_000, 10_000, _logs);

    public void Dispose() => _db.Dispose();

    private Task<string> CreateAsync(string name, string content) =>
        new CreateInstructionTool(_db.ScopeFactory, Policy).CreateInstructionAsync(name, content, "agent-a");

    private Task<string> UpdateAsync(string name, string content) =>
        new UpdateInstructionTool(_db.ScopeFactory, Policy).UpdateInstructionAsync(name, content, "edit", "agent-a");

    private Task<string> RollbackAsync(string name, int version) =>
        new RollbackInstructionTool(_db.ScopeFactory, Policy).RollbackInstructionAsync(name, version);

    [Fact]
    public async Task Create_appends_the_size_line_to_the_unchanged_output()
    {
        var result = await CreateAsync("row-a", "abc");

        Assert.Equal(
            "Instruction 'row-a' created at v1." +
            "\nSize: 3 UTF-8 bytes; instruction soft limit 10,000 (PromptSizeWarnings:InstructionBytes).",
            result);
    }

    [Fact]
    public async Task Update_over_the_limit_saves_and_warns()
    {
        await CreateAsync("row-a", "abc");

        var result = await UpdateAsync("row-a", new string('a', 10_001));

        Assert.StartsWith("Instruction 'row-a' updated to v2.\nSize: 3 → 10,001 UTF-8 bytes; instruction soft limit 10,000 (PromptSizeWarnings:InstructionBytes).\nSize warning: Instruction 'row-a' is 10,001 UTF-8 bytes, 1 over", result);
        Assert.Equal(3, result.Split('\n').Length);

        using var db = _db.NewDb();
        Assert.Equal(2, db.Instructions.Single(i => i.Name == "row-a").CurrentVersion);

        var line = Assert.Single(_logs.Entries);
        Assert.Equal("PromptSize crossed: surface=mcp kind=instruction name=row-a bytes=10,001 previousBytes=3 limitBytes=10,000", line.Message);
    }

    [Fact]
    public async Task Rollback_appends_size_against_the_current_content()
    {
        await CreateAsync("row-a", new string('a', 10_100));
        await UpdateAsync("row-a", new string('a', 10_050));

        var result = await RollbackAsync("row-a", 1);

        Assert.StartsWith("Instruction 'row-a' rolled back to v1 content — saved as v3.\nSize: 10,050 → 10,100 UTF-8 bytes;", result);
        Assert.Contains("\nSize warning: Instruction 'row-a' is still over the 10,000-byte soft limit (PromptSizeWarnings:InstructionBytes): 10,050 → 10,100 UTF-8 bytes. Saved anyway.", result);
    }

    [Fact]
    public async Task Errors_have_no_size_line()
    {
        Assert.Equal("Instruction 'no-such-row' not found.", await UpdateAsync("no-such-row", "abc"));
        Assert.Equal("Instruction 'no-such-row' not found.", await RollbackAsync("no-such-row", 1));

        await CreateAsync("row-a", "abc");
        Assert.Equal("Instruction 'row-a' already exists. Use update_instruction to add a new version.", await CreateAsync("row-a", "abc"));
        Assert.Equal("Version 7 does not exist for instruction 'row-a'.", await RollbackAsync("row-a", 7));
    }

    [Fact]
    public async Task Disabled_limit_still_reports_the_size()
    {
        var result = await new CreateInstructionTool(_db.ScopeFactory, new PromptSizePolicy(0, 10_000, _logs))
            .CreateInstructionAsync("row-a", new string('a', 20_000), "agent-a");

        Assert.Equal(
            "Instruction 'row-a' created at v1." +
            "\nSize: 20,000 UTF-8 bytes; instruction soft limit disabled (PromptSizeWarnings:InstructionBytes=0).",
            result);
        Assert.Empty(_logs.Entries);
    }
}

/// <summary>A private SQLite store plus the scope factory the orchestrator MCP tools take.</summary>
internal sealed class SizeToolsDb : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<OrchestratorDbContext> _options;

    public IServiceScopeFactory ScopeFactory { get; }

    public SizeToolsDb()
    {
        _connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        _connection.Open();
        _options = new DbContextOptionsBuilder<OrchestratorDbContext>().UseSqlite(_connection).Options;
        using (var db = NewDb())
            db.Database.EnsureCreated();

        var services = new ServiceCollection();
        services.AddScoped(_ => NewDb());
        services.AddLogging(b => b.ClearProviders());
        ScopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    public OrchestratorDbContext NewDb() => new(_options);

    public void Dispose() => _connection.Dispose();
}
