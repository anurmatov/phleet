using Fleet.Orchestrator.Application;
using Fleet.Orchestrator.Configuration;
using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Services;
using Fleet.Orchestrator.Tests.Support;
using Fleet.Orchestrator.Tools;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Fleet.Orchestrator.Tests.Application;

/// <summary>
/// No two agents end up on one host port, whatever writes it and however the writes interleave.
/// Each agent's HTTP API is published on its port, so a second agent on the same one has a
/// container that cannot start.
/// </summary>
/// <remarks>
/// <para>
/// The writers are the production ones, built by the container over SQLite. The seam is a
/// SaveChanges interceptor that holds ONE chosen write at its save: after it has read the ports and
/// chosen one, before anything is stored. The second write is started while the first is held
/// there, and only then is the first released. Nothing sleeps.
/// </para>
/// <para>
/// The second write is started on the test's own thread. With SQLite every database call completes
/// synchronously, so a writer runs on the calling thread until its first real wait: without the
/// gate that is never (it stores its port while the first is still held), with the gate it is the
/// gate. Each test asserts the first write is held before it starts the second.
/// </para>
/// </remarks>
public sealed class HostPortAllocationTests : IAsyncLifetime
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private SqliteConnection _connection = null!;
    private WebApplication _app = null!;
    private readonly HoldOneSave _hold = new();

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        await _connection.OpenAsync();

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Provisioning:EnvFilePath"] = Path.Combine(Path.GetTempPath(), $"ports-{Guid.NewGuid():N}.env"),
            ["Provisioning:BaseDir"] = Path.Combine(Path.GetTempPath(), $"ports-{Guid.NewGuid():N}"),
        });
        IConfiguration config = builder.Configuration;
        var services = builder.Services;
        services.AddDbContext<OrchestratorDbContext>(o => o.UseSqlite(_connection).AddInterceptors(_hold));
        services.AddSingleton<ITelegramSetup>(new Writers.TelegramSetup());
        services.AddSingleton<IConfigWriter>(new Writers.RecordingConfigWriter());
        services.AddSingleton<IAclChangeNotifier>(new SilentAclNotifier());
        services.AddSingleton(Writers.InertPublisher());
        services.AddSingleton(new DockerService(
            NullLogger<DockerService>.Instance, new HttpClient(new NoDockerApi()) { BaseAddress = new Uri("http://localhost") }));
        services.AddSingleton(new JournalTokenService(config));
        services.AddSingleton<ContainerProvisioningService>();
        services.AddSingleton(new AgentRegistry(null!, NullLogger<AgentRegistry>.Instance));
        services.AddSingleton(new TemporalClientRegistry(
            Options.Create(new TemporalOptions()), NullLogger<TemporalClientRegistry>.Instance));
        services.AddAgentWrites();
        _app = builder.Build();

        await using var scope = _app.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>().Database.EnsureCreatedAsync();

        // Two agents with no stored port: they answer on 8081 and 8082, so the next free port is 8083.
        await SeedAsync("cto", "co-cto");
        await SeedAsync("other", "developer");
    }

    public async Task DisposeAsync()
    {
        _hold.Release();
        await _app.DisposeAsync();
        await _connection.DisposeAsync();
    }

    // ── Two creates ──────────────────────────────────────────────────────────────────────────────

    public static TheoryData<WriteCaller, WriteCaller> Callers() => new()
    {
        { WriteCaller.Rest, WriteCaller.Rest },
        { WriteCaller.Mcp, WriteCaller.Mcp },
        { WriteCaller.Rest, WriteCaller.Mcp },
        { WriteCaller.Mcp, WriteCaller.Rest },
    };

    [Theory]
    [MemberData(nameof(Callers))]
    public async Task TwoCreates_TheSecondStartedWhileTheFirstHasChosenItsPortAndStoredNothing_GetDifferentPorts(
        WriteCaller firstCaller, WriteCaller secondCaller)
    {
        var creator = _app.Services.GetRequiredService<AgentCreator>();

        var first = HeldAtItsSave("first", () => creator.CreateAsync(New("first"), firstCaller));
        var second = creator.CreateAsync(New("second"), secondCaller);
        _hold.Release();
        var outcomes = await Task.WhenAll(first, second).WaitAsync(Patience);

        // Both succeed: the second waits for the first to commit, it is not turned away.
        Assert.All(outcomes, outcome => Assert.Null(outcome.Fault));
        var ports = await PortsAsync();
        Assert.Equal(8083, ports["first"].Stored);
        Assert.Equal(8084, ports["second"].Stored);
        // What each caller was told is what is stored.
        Assert.Equal(ports["first"].Stored, outcomes[0].Value!.HostPort);
        Assert.Equal(ports["second"].Stored, outcomes[1].Value!.HostPort);
        AssertNoTwoAgentsOnOnePort(ports);
    }

    [Fact]
    public async Task TwoCreates_ThroughTheCreateTool_GetDifferentPorts()
    {
        var first = HeldAtItsSave("first", () => Tool<CreateAgentTool>().CreateAgentAsync("first", "developer", "sonnet"));
        var second = Tool<CreateAgentTool>().CreateAgentAsync("second", "developer", "sonnet");
        _hold.Release();
        var answers = await Task.WhenAll(first, second).WaitAsync(Patience);

        Assert.All(answers, answer => Assert.Contains("created in DB", answer));
        Assert.Contains("host_port: 8083", answers[0]);
        Assert.Contains("host_port: 8084", answers[1]);
        AssertNoTwoAgentsOnOnePort(await PortsAsync());
    }

    [Fact]
    public async Task TwoCreates_OfOneName_TheSecondIsRefused_AndOneAgentIsStored()
    {
        var creator = _app.Services.GetRequiredService<AgentCreator>();

        var first = HeldAtItsSave("twin", () => creator.CreateAsync(New("twin"), WriteCaller.Rest));
        var second = creator.CreateAsync(New("twin"), WriteCaller.Mcp);
        _hold.Release();
        var outcomes = await Task.WhenAll(first, second).WaitAsync(Patience);

        Assert.Null(outcomes[0].Fault);
        Assert.Equal("agent_exists", outcomes[1].Fault?.Code);
        var ports = await PortsAsync();
        Assert.Equal(["cto", "other", "twin"], ports.Keys.Order(StringComparer.Ordinal));
        AssertNoTwoAgentsOnOnePort(ports);
    }

    [Fact]
    public async Task ACreate_CancelledWhileItWaitsForAnother_StoresNothing_AndHoldsNothing()
    {
        var creator = _app.Services.GetRequiredService<AgentCreator>();
        using var cancel = new CancellationTokenSource();

        var first = HeldAtItsSave("first", () => creator.CreateAsync(New("first"), WriteCaller.Rest));
        var waiting = creator.CreateAsync(New("gave-up"), WriteCaller.Rest, cancel.Token);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(Patience));
        _hold.Release();
        Assert.Null((await first.WaitAsync(Patience)).Fault);

        // The cancelled wait did not keep the gate: the next create goes through.
        var next = await creator.CreateAsync(New("next"), WriteCaller.Mcp).WaitAsync(Patience);

        Assert.Null(next.Fault);
        var ports = await PortsAsync();
        Assert.Equal(["cto", "first", "next", "other"], ports.Keys.Order(StringComparer.Ordinal));
        AssertNoTwoAgentsOnOnePort(ports);
    }

    // ── A create and a host_port update: the other writer of a port ──────────────────────────────

    [Fact]
    public async Task AnUpdate_AskingForThePortACreateHasChosen_WaitsForIt_AndIsRefused()
    {
        var creator = _app.Services.GetRequiredService<AgentCreator>();
        var writer = _app.Services.GetRequiredService<AgentConfigWriter>();

        // The create has chosen 8083 and stored nothing; the update asks for 8083.
        var create = HeldAtItsSave("first", () => creator.CreateAsync(New("first"), WriteCaller.Rest));
        var update = writer.UpdateAsync("other", new AgentConfigPatch { HostPort = 8083 }, WriteCaller.Mcp);
        _hold.Release();
        var created = await create.WaitAsync(Patience);
        var updated = await update.WaitAsync(Patience);

        Assert.Null(created.Fault);
        Assert.Equal("host_port_in_use", updated.Fault?.Code);
        Assert.Equal("Host port 8083 is already used by agent 'first'. Nothing was changed.", updated.Fault?.Message);
        var ports = await PortsAsync();
        Assert.Equal(8083, ports["first"].Stored);
        Assert.Null(ports["other"].Stored);
        AssertNoTwoAgentsOnOnePort(ports);
    }

    [Fact]
    public async Task ACreate_StartedWhileAnUpdateIsTakingTheNextPort_WaitsForIt_AndTakesAnother()
    {
        var creator = _app.Services.GetRequiredService<AgentCreator>();
        var writer = _app.Services.GetRequiredService<AgentConfigWriter>();

        // The update has checked that 8083 is free and stored nothing; a create would choose 8083.
        var update = HeldAtItsSave("other", () => writer.UpdateAsync("other", new AgentConfigPatch { HostPort = 8083 }, WriteCaller.Mcp));
        var create = creator.CreateAsync(New("first"), WriteCaller.Mcp);
        _hold.Release();
        var updated = await update.WaitAsync(Patience);
        var created = await create.WaitAsync(Patience);

        Assert.Null(updated.Fault);
        Assert.Null(created.Fault);
        var ports = await PortsAsync();
        Assert.Equal(8083, ports["other"].Stored);
        Assert.Equal(8084, ports["first"].Stored);
        AssertNoTwoAgentsOnOnePort(ports);
    }

    [Fact]
    public async Task AnUpdate_ThatSendsNoHostPort_DoesNotWaitForACreate()
    {
        var creator = _app.Services.GetRequiredService<AgentCreator>();
        var writer = _app.Services.GetRequiredService<AgentConfigWriter>();

        var create = HeldAtItsSave("first", () => creator.CreateAsync(New("first"), WriteCaller.Rest));
        // Completes while the create still holds the gate.
        var updated = await writer.UpdateAsync("other", new AgentConfigPatch { MaxTurns = 70 }, WriteCaller.Mcp).WaitAsync(Patience);
        Assert.False(create.IsCompleted);
        _hold.Release();

        Assert.Null(updated.Fault);
        Assert.Null((await create.WaitAsync(Patience)).Fault);
        Assert.Equal(70, await DbAsync(db => db.Agents.Where(a => a.Name == "other").Select(a => a.MaxTurns).SingleAsync()));
    }

    // ── A host_port update on its own ────────────────────────────────────────────────────────────

    [Theory]
    // 8081 is where 'cto' answers with no stored port (8080 + its id).
    [InlineData("other", 8081, "cto")]
    // 9000 is the stored port of 'far'.
    [InlineData("other", 9000, "far")]
    // Clearing 'far' would put it on 8080 + its id = 8083, which 'near' has stored.
    [InlineData("far", 0, "near")]
    public async Task AnUpdate_ToAPortAnotherAgentAnswersOn_IsRefused_AndNothingChanges(string agent, int hostPort, string holder)
    {
        await SeedAsync("far", "developer", storedPort: 9000);
        await SeedAsync("near", "developer", storedPort: 8083);
        var before = await PortsAsync();

        var outcome = await _app.Services.GetRequiredService<AgentConfigWriter>()
            .UpdateAsync(agent, new AgentConfigPatch { HostPort = hostPort, MaxTurns = 70 }, WriteCaller.Mcp);

        Assert.Equal("host_port_in_use", outcome.Fault?.Code);
        Assert.Contains($"is already used by agent '{holder}'", outcome.Fault?.Message);
        Assert.Equal(before, await PortsAsync());
        // Nothing else in the request was saved either.
        Assert.Equal(50, await DbAsync(db => db.Agents.Where(a => a.Name == agent).Select(a => a.MaxTurns).SingleAsync()));
    }

    [Fact]
    public async Task AnUpdate_ToAFreePort_ItsOwnPort_OrClearingToAFreeOne_IsStored()
    {
        await SeedAsync("far", "developer", storedPort: 9000);
        var tool = Tool<UpdateAgentConfigTool>();

        Assert.StartsWith("Agent 'other' updated:", await tool.UpdateAgentConfigAsync("other", host_port: 9100));
        // The port it already answers on, now stored: not a collision with itself.
        Assert.StartsWith("Agent 'cto' updated:", await tool.UpdateAgentConfigAsync("cto", host_port: 8081));
        // Back to 8080 + id = 8083, which nobody holds.
        Assert.StartsWith("Agent 'far' updated:", await tool.UpdateAgentConfigAsync("far", host_port: 0));
        Assert.Equal("Host port 9100 is already used by agent 'other'. Nothing was changed.",
            await tool.UpdateAgentConfigAsync("far", host_port: 9100));

        var ports = await PortsAsync();
        Assert.Equal(((int?)8081, (int?)9100, (int?)null), (ports["cto"].Stored, ports["other"].Stored, ports["far"].Stored));
        AssertNoTwoAgentsOnOnePort(ports);
    }

    // ── Plumbing ─────────────────────────────────────────────────────────────────────────────────

    private static NewAgent New(string name) => new() { Name = name, Role = "developer", Model = "sonnet", Provision = false };

    /// <summary>
    /// Starts a write that touches <paramref name="agentName"/> and returns once it is held at its
    /// save: it has read the ports and chosen one, and has stored nothing.
    /// </summary>
    private Task<T> HeldAtItsSave<T>(string agentName, Func<Task<T>> write)
    {
        _hold.Arm(agentName);
        var running = write();
        Assert.True(_hold.IsHolding, "the first write did not reach its save on this thread: the test's ordering no longer holds");
        Assert.False(running.IsCompleted);
        return running;
    }

    private T Tool<T>() where T : class => ActivatorUtilities.CreateInstance<T>(_app.Services);

    private async Task<T> DbAsync<T>(Func<OrchestratorDbContext, Task<T>> read)
    {
        await using var scope = _app.Services.CreateAsyncScope();
        return await read(scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>());
    }

    private Task SeedAsync(string name, string role, int? storedPort = null) => DbAsync(async db =>
    {
        db.Agents.Add(new Agent
        {
            Name = name, DisplayName = name, Role = role, Model = "sonnet", Provider = "claude",
            MemoryLimitMb = 1024, ContainerName = $"fleet-{name}", ShortName = name, HostPort = storedPort,
        });
        return await db.SaveChangesAsync();
    });

    /// <summary>
    /// Every agent's stored port and the port it answers on, the latter by the function
    /// provisioning publishes the container with.
    /// </summary>
    private Task<Dictionary<string, (int? Stored, int Effective)>> PortsAsync() => DbAsync(async db =>
        (await db.Agents.AsNoTracking().ToListAsync())
            .ToDictionary(a => a.Name, a => (a.HostPort, ContainerProvisioningService.GetEffectiveHostPort(a))));

    private static void AssertNoTwoAgentsOnOnePort(Dictionary<string, (int? Stored, int Effective)> ports)
    {
        var shared = ports.GroupBy(p => p.Value.Effective).Where(g => g.Count() > 1)
            .Select(g => $"{g.Key}: {string.Join(", ", g.Select(p => p.Key).Order(StringComparer.Ordinal))}")
            .ToList();
        Assert.True(shared.Count == 0, $"agents share a host port — {string.Join("; ", shared)}");
    }

    /// <summary>
    /// Holds the next save that adds or changes the armed agent, until released. It holds before
    /// the save opens its transaction, so the shared connection stays free for the other write.
    /// </summary>
    private sealed class HoldOneSave : SaveChangesInterceptor
    {
        private string? _agentName;
        private TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool IsHolding { get; private set; }

        public void Arm(string agentName)
        {
            _release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            IsHolding = false;
            _agentName = agentName;
        }

        public void Release() => _release.TrySetResult();

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var armed = _agentName;
            if (armed is not null && eventData.Context!.ChangeTracker.Entries<Agent>()
                    .Any(e => e.State is EntityState.Added or EntityState.Modified && e.Entity.Name == armed))
            {
                _agentName = null; // one save only: a second write of the same name goes straight through
                IsHolding = true;
                await _release.Task;
            }
            return result;
        }
    }

    private sealed class SilentAclNotifier : IAclChangeNotifier
    {
        public Task PublishAclChangedAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class NoDockerApi : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new InvalidOperationException($"unexpected Docker call {request.Method} {request.RequestUri}");
    }
}
