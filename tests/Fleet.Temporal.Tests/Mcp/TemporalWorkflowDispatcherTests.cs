using System.Text.Json;
using Fleet.Temporal.Mcp;
using NSubstitute;
using Temporalio.Client;

namespace Fleet.Temporal.Tests.Mcp;

/// <summary>
/// Verifies <see cref="TemporalWorkflowDispatcher"/> starts NotifyCtoWorkflow with the
/// correct workflow type, task queue, and input payload.
///
/// The task-queue regression guard is the most operationally-sensitive check: a wrong
/// task queue starts the workflow successfully but no worker picks it up, leaving the
/// history at length=2 forever with no error.
/// </summary>
public sealed class TemporalWorkflowDispatcherTests
{
    private sealed class FakeClientFactory(ITemporalClient client) : ITemporalClientFactory
    {
        public string? LastNamespace { get; private set; }

        public Task<ITemporalClient> GetClientAsync(string @namespace)
        {
            LastNamespace = @namespace;
            return Task.FromResult(client);
        }
    }

    [Fact]
    public async Task FireAndForgetAsync_StartsNotifyCtoWorkflow_WithCorrectTypeQueueAndInput()
    {
        var client = Substitute.For<ITemporalClient>();

        string? capturedType = null;
        IReadOnlyCollection<object?>? capturedArgs = null;
        WorkflowOptions? capturedOptions = null;

        var fakeHandle = new WorkflowHandle(client, "notify-cto-test", null!, null!, null!);

        client.StartWorkflowAsync(
                Arg.Do<string>(t => capturedType = t),
                Arg.Do<IReadOnlyCollection<object?>>(a => capturedArgs = a),
                Arg.Do<WorkflowOptions>(o => capturedOptions = o))
            .Returns(Task.FromResult(fakeHandle));

        var factory = new FakeClientFactory(client);
        var dispatcher = new TemporalWorkflowDispatcher(factory);

        var id = await dispatcher.FireAndForgetAsync("acto", "tool X keeps returning 500");

        // Namespace used to acquire the client
        Assert.Equal("fleet", factory.LastNamespace);

        // Workflow type must be the compiled NotifyCtoWorkflow — not FireAndForgetTaskWorkflow.
        // Wrong type → workflow starts but no worker handles it (historyLength=2 hang).
        Assert.Equal(TemporalWorkflowDispatcher.NotifyCtoWorkflowType, capturedType);
        Assert.Equal("NotifyCtoWorkflow", capturedType);

        // Task queue must be "fleet" — wrong value silently drops the workflow.
        Assert.Equal(TemporalWorkflowDispatcher.FleetNamespace, capturedOptions!.TaskQueue);
        Assert.Equal("fleet", capturedOptions.TaskQueue);

        // Input payload must carry TargetAgent and TaskDescription.
        var arg = Assert.Single(capturedArgs!);
        var json = JsonSerializer.Serialize(arg);
        var doc = JsonDocument.Parse(json);
        Assert.Equal("acto", doc.RootElement.GetProperty("TargetAgent").GetString());
        Assert.Equal("tool X keeps returning 500", doc.RootElement.GetProperty("TaskDescription").GetString());

        // Returned ID is the handle's workflow ID
        Assert.Equal("notify-cto-test", id);
    }
}
