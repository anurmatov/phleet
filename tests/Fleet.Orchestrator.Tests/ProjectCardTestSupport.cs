using System.Collections.Concurrent;
using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Tools;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Fleet.Orchestrator.Tests;

/// <summary>
/// A private SQLite store (foreign keys on) plus the scope factory the MCP tools take. SQLite rather
/// than InMemory so unique indexes and the delete-then-insert of a projects replace-all are real.
/// </summary>
internal sealed class TestDb : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<OrchestratorDbContext> _options;

    public CapturingLoggerProvider Logs { get; } = new();
    public IServiceScopeFactory ScopeFactory { get; }

    public TestDb()
    {
        _connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        _connection.Open();
        _options = new DbContextOptionsBuilder<OrchestratorDbContext>().UseSqlite(_connection).Options;
        using (var db = NewDb())
            db.Database.EnsureCreated();

        // A fresh context per scope: the tools dispose the scope they create.
        var services = new ServiceCollection();
        services.AddScoped(_ => NewDb());
        services.AddSingleton(Logs.CreateLogger<ProjectContextTools>());
        ScopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    public OrchestratorDbContext NewDb() => new(_options);

    public void Dispose() => _connection.Dispose();
}

/// <summary>Seeding and log capture shared by the #347 card, route and mode tests. Placeholder names only.</summary>
internal static class ProjectCardTestSupport
{
    /// <summary>A context whose versions are <paramref name="fullVersions"/> in order; the last is current.</summary>
    public static ProjectContext SeedContext(OrchestratorDbContext db, string name, params string[] fullVersions)
    {
        var ctx = new ProjectContext { Name = name, CurrentVersion = fullVersions.Length };
        db.ProjectContexts.Add(ctx);
        db.SaveChanges();

        for (var i = 0; i < fullVersions.Length; i++)
        {
            db.ProjectContextVersions.Add(new ProjectContextVersion
            {
                ProjectContextId = ctx.Id,
                VersionNumber    = i + 1,
                Content          = fullVersions[i],
                CreatedBy        = "test",
            });
        }
        db.SaveChanges();
        return ctx;
    }

    /// <summary>
    /// Inserts a card version directly — bypassing the write gate, which is how content the gate
    /// would refuse (an invalid marker, a missing keep) gets into history for rollback tests.
    /// </summary>
    public static void SeedCard(OrchestratorDbContext db, int contextId, int version, string content, int basedOnFullVersion, bool current = true)
    {
        db.ProjectContextCardVersions.Add(new ProjectContextCardVersion
        {
            ProjectContextId   = contextId,
            VersionNumber      = version,
            Content            = content,
            BasedOnFullVersion = basedOnFullVersion,
            CreatedBy          = "test",
        });
        if (current)
            db.ProjectContexts.Single(p => p.Id == contextId).CurrentCardVersion = version;
        db.SaveChanges();
    }

    public static Agent SeedAgent(OrchestratorDbContext db, string name, params (string Project, string Mode)[] projects)
    {
        var agent = new Agent
        {
            Name          = name,
            DisplayName   = name,
            Role          = "developer",
            Model         = "claude-sonnet-5",
            ContainerName = $"fleet-{name}",
        };
        db.Agents.Add(agent);
        db.SaveChanges();

        foreach (var (project, mode) in projects)
            db.AgentProjects.Add(new AgentProject { AgentId = agent.Id, ProjectName = project, ContextMode = mode });
        db.SaveChanges();
        return agent;
    }
}

/// <summary>Captures every log entry, for asserting the rollback Warnings.</summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    public ConcurrentQueue<(string Category, LogLevel Level, string Message)> Entries { get; } = new();

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(this, categoryName);

    public ILogger<T> CreateLogger<T>() => new Typed<T>(CreateLogger(typeof(T).FullName!));

    public IEnumerable<string> Warnings => Entries.Where(e => e.Level == LogLevel.Warning).Select(e => e.Message);

    public void Dispose() { }

    private sealed class CapturingLogger(CapturingLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            owner.Entries.Enqueue((category, logLevel, formatter(state, exception)));
    }

    private sealed class Typed<T>(ILogger inner) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => inner.BeginScope(state);
        public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            inner.Log(logLevel, eventId, state, exception, formatter);
    }
}
