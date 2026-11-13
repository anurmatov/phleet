using Fleet.Temporal.Configuration;
using Fleet.Temporal.Mcp;
using Fleet.Temporal.Models;
using Fleet.Temporal.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Temporalio.Client;
using Temporalio.Exceptions;

namespace Fleet.Temporal.Tests.Services;

/// <summary>
/// A relayed workflow signal used to be acknowledged whether or not Temporal took it. These pin
/// the retry that replaced that, without a broker or a Temporal server.
/// </summary>
public sealed class TemporalRelayListenerTests
{
    private static readonly RelayMessage Signal =
        new(0, "agent1", "{}", DateTimeOffset.UtcNow, WorkflowId: "wf-1", SignalName: "blocker-resolved");

    private readonly ITemporalClientFactory _factory = Substitute.For<ITemporalClientFactory>();
    private readonly WorkflowHandle _handle;
    private readonly List<TimeSpan> _delays = [];

    public TemporalRelayListenerTests()
    {
        var client = Substitute.For<ITemporalClient>();
        _handle = Substitute.For<WorkflowHandle>(client, "wf-1", null!, null!, null!);
        client.GetWorkflowHandle("wf-1", Arg.Any<string?>(), Arg.Any<string?>()).Returns(_handle);
        _factory.GetClientAsync("fleet").Returns(client);
    }

    private TemporalRelayListener Listener(string host = "") => new(
        Options.Create(new RabbitMqOptions { Host = host }),
        new TaskCompletionRegistry(NullLogger<TaskCompletionRegistry>.Instance),
        _factory,
        NullLogger<TemporalRelayListener>.Instance)
    {
        DelayAsync = (delay, _) => { _delays.Add(delay); return Task.CompletedTask; },
    };

    private static RpcException Rpc(RpcException.StatusCode code) => new(code, code.ToString(), null);

    private void SignalFails(params Exception?[] outcomes)
    {
        var queue = new Queue<Exception?>(outcomes);
        _handle.SignalAsync(default!, default!, default).ReturnsForAnyArgs(_ =>
            queue.TryDequeue(out var ex) && ex is not null ? Task.FromException(ex) : Task.CompletedTask);
    }

    private int SignalCalls => _handle.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(WorkflowHandle.SignalAsync));

    [Fact]
    public async Task ASignalThatFailsWhileTemporalIsUnavailable_IsRetriedUntilItIsDelivered()
    {
        SignalFails(Rpc(RpcException.StatusCode.Unavailable), new TimeoutException(), null);

        var outcome = await Listener().HandleWorkflowSignalAsync(Signal);

        Assert.Equal(RelaySignalOutcome.Delivered, outcome);
        Assert.Equal(3, SignalCalls);
        Assert.Equal(TemporalRelayListener.DefaultSignalRetryDelays.Take(2), _delays);
    }

    /// <summary>A client that cannot connect yet is the same kind of failure: it can pass.</summary>
    [Fact]
    public async Task AClientThatCannotConnectYet_IsRetried()
    {
        var client = await _factory.GetClientAsync("fleet");
        _factory.GetClientAsync("fleet").Returns(
            _ => Task.FromException<ITemporalClient>(new InvalidOperationException("connect failed")),
            _ => Task.FromResult(client));

        Assert.Equal(RelaySignalOutcome.Delivered, await Listener().HandleWorkflowSignalAsync(Signal));
        Assert.Equal(1, SignalCalls);
        Assert.Single(_delays);
    }

    [Theory]
    [InlineData(RpcException.StatusCode.NotFound)]
    [InlineData(RpcException.StatusCode.InvalidArgument)]
    public async Task AWorkflowThatIsGoneOrARequestThatIsInvalid_IsDroppedAtOnce(RpcException.StatusCode code)
    {
        SignalFails(Rpc(code));

        Assert.Equal(RelaySignalOutcome.Dropped, await Listener().HandleWorkflowSignalAsync(Signal));
        Assert.Equal(1, SignalCalls);
        Assert.Empty(_delays);
    }

    /// <summary>
    /// Bounded: the message is held unacknowledged while it retries, and the queue is consumed one
    /// message at a time, so an endless retry would hold every delegation reply behind it.
    /// </summary>
    [Fact]
    public async Task ASignalThatNeverGoesThrough_IsGivenUpAfterTheLastDelay()
    {
        _handle.SignalAsync(default!, default!, default).ThrowsAsyncForAnyArgs(Rpc(RpcException.StatusCode.Unavailable));

        Assert.Equal(RelaySignalOutcome.GaveUp, await Listener().HandleWorkflowSignalAsync(Signal));
        Assert.Equal(TemporalRelayListener.DefaultSignalRetryDelays.Length + 1, SignalCalls);
        Assert.Equal(TemporalRelayListener.DefaultSignalRetryDelays, _delays);
    }

    /// <summary>The bridge stopping mid-retry hands the message back instead of deciding its fate.</summary>
    [Fact]
    public async Task AShutdownDuringARetry_Interrupts()
    {
        _handle.SignalAsync(default!, default!, default).ThrowsAsyncForAnyArgs(Rpc(RpcException.StatusCode.Unavailable));
        var listener = Listener();
        listener.DelayAsync = (_, ct) => Task.FromCanceled(new CancellationToken(canceled: true));

        Assert.Equal(RelaySignalOutcome.Interrupted, await listener.HandleWorkflowSignalAsync(Signal));
        Assert.Equal(1, SignalCalls);
    }
}
