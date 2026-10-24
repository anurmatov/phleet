using System.Data.Common;
using System.Net;
using Fleet.Orchestrator.Configuration;
using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Services;
using Fleet.Orchestrator.Tests.WorkflowOutcomes;
using Fleet.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Fleet.Orchestrator.Tests.Migrations.WorkflowOutcomes;

public sealed partial class WorkflowOutcomeMigrationTests
{
    [Theory]
    [Trait("WorkflowOutcomeSuite", "migration")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OwnershipRestore_FirstTransferCommitsAfterObserverSnapshot_CurrentOwnerSurvives(bool outcome)
    {
        await using var host = await ApiHost.StartAsync(ApiDatabase.ForMySql(_connectionString));
        var run = await host.SeedRunAsync(r => r.Coordinator = "old-coordinator");
        var pause = new BeforeOwnershipRead();
        var services = new ServiceCollection();
        services.AddDbContext<OrchestratorDbContext>(o =>
        {
            host.Database.Configure(o);
            o.AddInterceptors(pause);
        });
        await using var provider = services.BuildServiceProvider();
        var store = new WorkflowOutcomeStore(provider.GetRequiredService<IServiceScopeFactory>(), new FakeProtector(),
            Options.Create(new WorkflowOutcomeOptions()), host.Clock, NullLogger<WorkflowOutcomeStore>.Instance);
        var lease = (await store.AcquireLeaseAsync("observer", default))!;
        Assert.NotNull(lease);
        var facts = new WorkflowOutcomeRunFacts
        {
            Namespace = run.Namespace, WorkflowId = run.WorkflowId, RunId = run.RunId, WorkflowType = run.WorkflowType,
            Terminal = new WorkflowOutcomeTerminal(12, "WorkflowExecutionFailed", "Failed", null, "{}", new string('a', 64),
                null, null, null, null, null, true),
        };
        var ownership = new WorkflowOutcomeOwnershipResult("project-a", "rule", null, null,
            "policy-coordinator", "rule", [], new string('b', 64));
        var verdict = new WorkflowOutcomeVerdict(WorkflowOutcomeClassifications.ConfirmedFailure,
            WorkflowOutcomeReasonCodes.WorkflowFailed, WorkflowOutcomeEvidenceStates.Complete, null, [], [], null, null);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        pause.Armed = true;
        var restore = outcome
            ? (Task)store.StoreOutcomeAsync(lease, facts, verdict, ownership, 604800, deadline.Token)
            : store.StoreOwnershipAsync(lease, facts, ownership, deadline.Token);
        try
        {
            // FencedAsync has already read its checkpoint, establishing the REPEATABLE READ snapshot.
            // Pause BEFORE the run read so the first operator transfer can commit independently.
            await pause.Reached.Task.WaitAsync(deadline.Token);
            var transfer = await host.AsAdmin(HttpMethod.Post, $"{Root}/{run.Id}/transfer",
                new WorkflowOutcomeTransferRequest(1, 1, "old-coordinator", ApiHost.Coordinator,
                    "owner change", "https://example.com/approvals/9")).WaitAsync(deadline.Token);
            Assert.Equal(HttpStatusCode.OK, transfer.StatusCode);
            await using var committed = NewContext(_connectionString);
            Assert.Equal(ApiHost.Coordinator, (await committed.WorkflowOutcomeRuns.AsNoTracking().SingleAsync()).Coordinator);
            Assert.Single(await committed.WorkflowOutcomeDecisions.AsNoTracking().ToListAsync());
        }
        finally
        {
            pause.Resume.TrySetResult();
            await restore.WaitAsync(deadline.Token);
        }
        await using var check = NewContext(_connectionString);
        var saved = await check.WorkflowOutcomeRuns.AsNoTracking().SingleAsync();
        Assert.Equal(ApiHost.Coordinator, saved.Coordinator);
        Assert.Equal(ownership.PolicyHash, saved.OwnershipPolicyHash);
        Assert.Equal(1, saved.Version);
        var audit = Assert.Single(await check.WorkflowOutcomeDecisions.AsNoTracking().ToListAsync());
        Assert.Equal((WorkflowOutcomeInboxService.TransferOperation, WorkflowOutcomeInboxService.TransferredStatus, ApiHost.Coordinator),
            (audit.Operation, audit.Status, audit.Owner));
    }

    private sealed class BeforeOwnershipRead : DbCommandInterceptor
    {
        private bool _checkpointRead;
        public bool Armed { get; set; }
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (Armed && command.CommandText.Contains("workflow_outcome_checkpoints", StringComparison.Ordinal))
                _checkpointRead = true;
            if (Armed && command.CommandText.Contains("workflow_outcome_runs", StringComparison.Ordinal))
            {
                Armed = false;
                Assert.True(_checkpointRead);
                await using var isolation = command.Connection!.CreateCommand();
                isolation.Transaction = command.Transaction;
                isolation.CommandText = "SELECT @@transaction_isolation";
                Assert.Equal("REPEATABLE-READ", (string)(await isolation.ExecuteScalarAsync(cancellationToken))!);
                Reached.TrySetResult();
                await Resume.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }
}
