using Fleet.Temporal.Configuration;
using Fleet.Temporal.Mcp;
using Fleet.Temporal.Models;
using Fleet.Temporal.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Temporalio.Client;

namespace Fleet.Temporal.Tests;

public class TemporalRelayReservedSignalTests
{
    public static IEnumerable<object[]> Reserved() => CeoGateSignals.All.Select(s => new object[] { " " + s.ToUpperInvariant() + " " });
    [Theory]
    [MemberData(nameof(Reserved))]
    public async Task Relay_ReservedSignal_NeverAcquiresClient(string signal)
    {
        var factory = Substitute.For<ITemporalClientFactory>();
        var listener = new TemporalRelayListener(Options.Create(new RabbitMqOptions()), new TaskCompletionRegistry(NullLogger<TaskCompletionRegistry>.Instance), factory, NullLogger<TemporalRelayListener>.Instance);
        await listener.HandleWorkflowSignalAsync(new RelayMessage(0, "agent1", "{}", DateTimeOffset.UtcNow, WorkflowId: "test", SignalName: signal));
        await factory.DidNotReceiveWithAnyArgs().GetClientAsync(default!);
    }
    [Fact]
    public async Task Relay_OrdinarySignal_StillForwards()
    {
        var factory = Substitute.For<ITemporalClientFactory>();
        var client = Substitute.For<ITemporalClient>();
        factory.GetClientAsync("fleet").Returns(client);
        var handle = Substitute.For<WorkflowHandle>(client, "test", null!, null!, null!);
        client.GetWorkflowHandle("test", Arg.Any<string?>(), Arg.Any<string?>()).Returns(handle);
        var listener = new TemporalRelayListener(Options.Create(new RabbitMqOptions()), new TaskCompletionRegistry(NullLogger<TaskCompletionRegistry>.Instance), factory, NullLogger<TemporalRelayListener>.Instance);
        await listener.HandleWorkflowSignalAsync(new RelayMessage(0, "agent1", "{}", DateTimeOffset.UtcNow, WorkflowId: "test", SignalName: "ordinary"));
        await handle.Received(1).SignalAsync("ordinary", Arg.Is<IReadOnlyCollection<object?>>(a => a.Single()!.Equals("{}")), Arg.Any<WorkflowSignalOptions?>());
    }
}
