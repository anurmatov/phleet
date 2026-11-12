using System.Reflection;
using Fleet.Orchestrator.Configuration;
using Fleet.Orchestrator.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Temporalio.Client;
using Temporalio.Exceptions;

namespace Fleet.Orchestrator.Tests;

public class TemporalClientRegistrySignalTests
{
    [Fact]
    public async Task Restart_HumanInputRequest_RefusesBeforeTerminateOrStart()
    {
        var client = Substitute.For<ITemporalClient>();
        var handle = Substitute.For<WorkflowHandle>(client, "request", "exact-run", null!, null!);
        client.GetWorkflowHandle("request", "exact-run", Arg.Any<string?>()).Returns(handle);
        handle.FetchHistoryAsync(Arg.Any<WorkflowHistoryEventFetchOptions?>()).Returns(new Temporalio.Common.WorkflowHistory("request",
            [new Temporalio.Api.History.V1.HistoryEvent
            {
                EventType = Temporalio.Api.Enums.V1.EventType.WorkflowExecutionStarted,
                WorkflowExecutionStartedEventAttributes = new()
                {
                    WorkflowType = new() { Name = "UweHumanInputWorkflow" },
                    TaskQueue = new() { Name = "fleet" }
                }
            }]));
        var registry = new TemporalClientRegistry(Options.Create(new TemporalOptions { Address = "example:7233" }), NullLogger<TemporalClientRegistry>.Instance);
        typeof(TemporalClientRegistry).GetField("_clients", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(registry, new Dictionary<string, ITemporalClient> { ["fleet"] = client });
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => registry.RestartWorkflowAsync("fleet", "request", "exact-run", true));
        Assert.Contains("cannot be restarted", error.Message);
        await handle.DidNotReceiveWithAnyArgs().TerminateAsync(default!, default!);
        await client.DidNotReceiveWithAnyArgs().StartWorkflowAsync(default!, default!, default!);
    }

    [Theory]
    [InlineData("sent")]
    [InlineData("not_found")]
    [InlineData("failed")]
    public async Task Signal_ExactRunAndErrorMapping_ArePreserved(string expected)
    {
        var client = Substitute.For<ITemporalClient>();
        var handle = Substitute.For<WorkflowHandle>(client, "driver", "exact-run", null!, null!);
        client.GetWorkflowHandle("driver", "exact-run", Arg.Any<string?>()).Returns(handle);
        handle.SignalAsync(Arg.Any<string>(), Arg.Any<IReadOnlyCollection<object?>>(), Arg.Any<WorkflowSignalOptions?>())
            .Returns(expected == "sent" ? Task.CompletedTask : Task.FromException(expected == "not_found"
                ? new RpcException(RpcException.StatusCode.NotFound, "missing", []) : new InvalidOperationException("failed")));
        var registry = new TemporalClientRegistry(Options.Create(new TemporalOptions { Address = "example:7233" }), NullLogger<TemporalClientRegistry>.Instance);
        typeof(TemporalClientRegistry).GetField("_clients", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(registry, new Dictionary<string, ITemporalClient> { ["fleet"] = client });
        var result = await registry.SignalWorkflowAsync("fleet", "driver", "exact-run", "blocker-resolved", new { decision = "wake" }, default);
        Assert.Equal(expected, result);
        client.Received(1).GetWorkflowHandle("driver", "exact-run", Arg.Any<string?>());
        client.DidNotReceive().GetWorkflowHandle("driver", Arg.Is<string?>(r => r == null), Arg.Any<string?>());
    }
}
