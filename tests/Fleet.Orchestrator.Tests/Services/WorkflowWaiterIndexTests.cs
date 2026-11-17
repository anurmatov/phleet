using System.Text.Json;
using Fleet.Orchestrator.Models;
using Fleet.Orchestrator.Services;
using Google.Protobuf;
using Temporalio.Api.Common.V1;
using Temporalio.Api.Enums.V1;
using Temporalio.Api.History.V1;
using Temporalio.Api.WorkflowService.V1;
using Temporalio.Client;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Fleet.Orchestrator.Tests.Services;

/// <summary>
/// Reading which run waits for a run out of its start input, and the shape a run's links have on
/// the wire, through the SDK service request path with a controlled RPC response.
/// No live Temporal server is used.
/// </summary>
public sealed class WorkflowWaiterIndexTests
{
    private static Payload Json(string json, string encoding = "json/plain")
    {
        var payload = new Payload { Data = ByteString.CopyFromUtf8(json) };
        payload.Metadata["encoding"] = ByteString.CopyFromUtf8(encoding);
        return payload;
    }

    private static GetWorkflowExecutionHistoryResponse Started(string? waiter = null) => new()
    {
        History = new History
        {
            Events =
            {
                new HistoryEvent
                {
                    EventType = EventType.WorkflowExecutionStarted,
                    WorkflowExecutionStartedEventAttributes = new WorkflowExecutionStartedEventAttributes
                    {
                        Input = new Payloads { Payloads_ = { Json(waiter is null ? "{}" : $$"""{"WaiterWorkflowId":"{{waiter}}","Other":"private synthetic task"}""") } },
                    },
                },
            },
        },
    };

    private sealed class HistoryRpc : WorkflowService
    {
        public List<(GetWorkflowExecutionHistoryRequest Request, RpcOptions Options)> Calls { get; } = [];
        public Func<GetWorkflowExecutionHistoryRequest, RpcOptions, Task<GetWorkflowExecutionHistoryResponse>> Reply { get; set; } =
            (_, _) => Task.FromResult(Started("driver-1"));

        protected override async Task<T> InvokeRpcAsync<T>(
            string rpc, IMessage req, MessageParser<T> resp, RpcOptions? options = null)
        {
            Assert.Equal("GetWorkflowExecutionHistory", rpc);
            var request = Assert.IsType<GetWorkflowExecutionHistoryRequest>(req);
            Assert.NotNull(options);
            Calls.Add((request.Clone(), options));
            return (T)(object)await Reply(request, options);
        }

        public ITemporalClient Client(string ns = "test")
        {
            var client = Substitute.For<ITemporalClient>();
            client.WorkflowService.Returns(this);
            client.Options.Returns(new TemporalClientOptions { Namespace = ns });
            return client;
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("driver-1")]
    public async Task Read_IsPinnedToRunAndFirstPage_AndCachedIncludingNoWaiter(string? waiter)
    {
        var rpc = new HistoryRpc { Reply = (_, _) => Task.FromResult(Started(waiter)) };
        var client = rpc.Client();
        var index = new WorkflowWaiterIndex(NullLogger<WorkflowWaiterIndex>.Instance);

        Assert.Equal(waiter, await index.WaiterOfAsync(client, "test", "child-1", "run-1", default));
        Assert.Equal(waiter, await index.WaiterOfAsync(client, "test", "child-1", "run-1", default));

        var call = Assert.Single(rpc.Calls);
        Assert.Equal("test", call.Request.Namespace);
        Assert.Equal("child-1", call.Request.Execution.WorkflowId);
        Assert.Equal("run-1", call.Request.Execution.RunId);
        Assert.Equal(1, call.Request.MaximumPageSize);
        Assert.Empty(call.Request.NextPageToken);
        Assert.False(call.Request.WaitNewEvent);
        Assert.Equal(WorkflowWaiterIndex.ReadBound, call.Options.Timeout);
    }

    [Fact]
    public async Task Cache_DistinguishesNamespacesAndRuns_AndDoesNotReadAnUnpinnedRun()
    {
        var rpc = new HistoryRpc();
        var index = new WorkflowWaiterIndex(NullLogger<WorkflowWaiterIndex>.Instance);
        var client = rpc.Client();
        Assert.Null(await index.WaiterOfAsync(client, "test", "child", "", default));
        await index.WaiterOfAsync(client, "test", "child", "run-1", default);
        await index.WaiterOfAsync(client, "test", "child", "run-2", default);
        await index.WaiterOfAsync(rpc.Client("other"), "other", "child", "run-1", default);
        Assert.Equal(3, rpc.Calls.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrMissingStartEvent_BacksOffOtherReads_WithoutFailingTheList(bool missingStart)
    {
        var rpc = new HistoryRpc
        {
            Reply = (_, _) => missingStart
                ? Task.FromResult(new GetWorkflowExecutionHistoryResponse())
                : Task.FromException<GetWorkflowExecutionHistoryResponse>(new InvalidOperationException("synthetic private task")),
        };
        var client = rpc.Client();
        var index = new WorkflowWaiterIndex(NullLogger<WorkflowWaiterIndex>.Instance);
        Assert.Null(await index.WaiterOfAsync(client, "test", "child", "run-1", default));
        Assert.Null(await index.WaiterOfAsync(client, "test", "other", "run-2", default));
        Assert.Single(rpc.Calls);
    }

    [Fact]
    public async Task ConcurrentCacheMisses_StartOnlyOneRpc_AndDoNotQueueTheOtherListCalls()
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<GetWorkflowExecutionHistoryResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var rpc = new HistoryRpc { Reply = (_, _) => { reached.TrySetResult(); return release.Task; } };
        var index = new WorkflowWaiterIndex(NullLogger<WorkflowWaiterIndex>.Instance);
        var client = rpc.Client();
        var first = index.WaiterOfAsync(client, "test", "child", "run-1", default);
        await reached.Task;
        try
        {
            var overlapping = Enumerable.Range(0, 20)
                .Select(_ => index.WaiterOfAsync(client, "test", "child", "run-1", default)).ToArray();
            Assert.All(overlapping, call => Assert.True(call.IsCompleted, "a list call queued behind a history read"));
            Assert.Single(rpc.Calls);
        }
        finally
        {
            release.TrySetResult(Started("driver-1"));
            await first;
        }
        Assert.Equal("driver-1", await index.WaiterOfAsync(client, "test", "child", "run-1", default));
        Assert.Single(rpc.Calls);
    }

    [Fact]
    public async Task CallerCancellation_IsPropagatedAndDoesNotPoisonTheCache()
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rpc = new HistoryRpc
        {
            Reply = async (_, options) =>
            {
                reached.TrySetResult();
                await Task.Delay(Timeout.Infinite, options.CancellationToken!.Value);
                return Started();
            },
        };
        var client = rpc.Client();
        var index = new WorkflowWaiterIndex(NullLogger<WorkflowWaiterIndex>.Instance);
        using var caller = new CancellationTokenSource();
        var first = index.WaiterOfAsync(client, "test", "child", "run-1", caller.Token);
        await reached.Task;
        caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        rpc.Reply = (_, _) => Task.FromResult(Started("driver-1"));
        Assert.Equal("driver-1", await index.WaiterOfAsync(client, "test", "child", "run-1", default));
        Assert.Equal(2, rpc.Calls.Count);
    }

    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => Now;
        public override long GetTimestamp() => Now.UtcTicks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    }

    [Fact]
    public async Task FailureBackoff_ExpiresAfterOneMinute_AndNeverLogsInputOrExceptionText()
    {
        var time = new ManualTime();
        var logs = new EpicGrants.CapturingLogger<WorkflowWaiterIndex>();
        var index = new WorkflowWaiterIndex(logs) { Time = time };
        var rpc = new HistoryRpc
        {
            Reply = (_, _) => Task.FromException<GetWorkflowExecutionHistoryResponse>(new InvalidOperationException("synthetic private task")),
        };
        var client = rpc.Client();
        Assert.Null(await index.WaiterOfAsync(client, "test", "child", "run-1", default));
        time.Now += WorkflowWaiterIndex.Backoff - TimeSpan.FromTicks(1);
        Assert.Null(await index.WaiterOfAsync(client, "test", "child", "run-1", default));
        Assert.Single(rpc.Calls);
        time.Now += TimeSpan.FromTicks(1);
        rpc.Reply = (_, _) => Task.FromResult(Started("driver-1"));
        Assert.Equal("driver-1", await index.WaiterOfAsync(client, "test", "child", "run-1", default));
        Assert.Equal(2, rpc.Calls.Count);
        Assert.DoesNotContain(logs.Lines, line => line.Contains("synthetic private task"));
    }

    [Fact]
    public async Task SlowSuccessfulReads_ShareOneSnapshotBudget_AndKeepAllListedRuns()
    {
        var rpc = new HistoryRpc
        {
            Reply = async (_, options) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(1), options.CancellationToken!.Value);
                return Started("driver-1");
            },
        };
        var client = rpc.Client();
        var index = new WorkflowWaiterIndex(NullLogger<WorkflowWaiterIndex>.Instance);
        var runs = Enumerable.Range(0, 10).Select(i => new WorkflowSummary(
            $"child-{i}", $"run-{i}", "ExampleWorkflow", "test", "test", "Running", DateTimeOffset.UnixEpoch)).ToList();
        var identities = runs.Select(r => r.RunId).ToArray();
        var watch = System.Diagnostics.Stopwatch.StartNew();

        await index.EnrichAsync(new Dictionary<string, ITemporalClient> { ["test"] = client }, runs, default);

        Assert.InRange(watch.Elapsed, WorkflowWaiterIndex.ReadBound, WorkflowWaiterIndex.ReadBound + TimeSpan.FromSeconds(2));
        Assert.Equal(identities, runs.Select(r => r.RunId));
        Assert.InRange(runs.Count(r => r.WaiterWorkflowId == "driver-1"), 1, 3);
        Assert.InRange(rpc.Calls.Count, 1, 4);
        var callCount = rpc.Calls.Count;
        await index.EnrichAsync(new Dictionary<string, ITemporalClient> { ["test"] = client }, runs, default);
        Assert.Equal(callCount, rpc.Calls.Count);
    }

    [Fact]
    public void AnInputThatNamesItsWaiter_GivesThatWorkflowId()
    {
        var input = Json("""{"Repo":"example-org/example-repo","IssueNumber":7,"WaiterWorkflowId":" driver-example-1 "}""");

        Assert.Equal("driver-example-1", WorkflowWaiterIndex.WaiterIn(input));
    }

    [Theory]
    // No such member.
    [InlineData("""{"Repo":"example-org/example-repo"}""")]
    // Blank, or not text.
    [InlineData("""{"WaiterWorkflowId":""}""")]
    [InlineData("""{"WaiterWorkflowId":"   "}""")]
    [InlineData("""{"WaiterWorkflowId":null}""")]
    [InlineData("""{"WaiterWorkflowId":42}""")]
    [InlineData("""{"WaiterWorkflowId":{"id":"driver-example-1"}}""")]
    // Not an object at the top.
    [InlineData("""["WaiterWorkflowId","driver-example-1"]""")]
    [InlineData("\"WaiterWorkflowId: driver-example-1\"")]
    // Not JSON at all, or cut short.
    [InlineData("WaiterWorkflowId=driver-example-1")]
    [InlineData("""{"WaiterWorkflowId":"driver-exa""")]
    [InlineData("")]
    public void AnInputThatNamesNone_GivesNone(string json)
    {
        Assert.Null(WorkflowWaiterIndex.WaiterIn(Json(json)));
    }

    [Fact]
    public void AnInputInAnotherEncoding_IsNotRead()
    {
        var json = """{"WaiterWorkflowId":"driver-example-1"}""";

        Assert.Null(WorkflowWaiterIndex.WaiterIn(Json(json, "binary/plain")));
        Assert.Null(WorkflowWaiterIndex.WaiterIn(Json(json, "binary/encrypted")));
        Assert.Null(WorkflowWaiterIndex.WaiterIn(new Payload { Data = ByteString.CopyFromUtf8(json) }));
    }

    [Fact]
    public void AnIdTooLongToBeOne_IsNotKept()
    {
        var input = Json($$"""{"WaiterWorkflowId":"{{new string('x', WorkflowWaiterIndex.MaxIdLength + 1)}}"}""");

        Assert.Null(WorkflowWaiterIndex.WaiterIn(input));
    }

    [Fact]
    public void ARunsLinks_AreOnTheWireByTheseNames_AndNullWhenItStandsAlone()
    {
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var linked = new WorkflowSummary(
            "child-1", "run-2", "ConsensusReviewWorkflow", "fleet", "fleet", "Running", DateTimeOffset.UnixEpoch,
            ParentWorkflowId: "parent-1", ParentRunId: "run-1", WaiterWorkflowId: "driver-example-1");
        var alone = new WorkflowSummary("solo-1", "run-3", "UwePrImplementationWorkflow", "fleet", "fleet", "Running", DateTimeOffset.UnixEpoch);

        var linkedJson = JsonDocument.Parse(JsonSerializer.Serialize(linked, web)).RootElement;
        Assert.Equal("parent-1", linkedJson.GetProperty("parentWorkflowId").GetString());
        Assert.Equal("run-1", linkedJson.GetProperty("parentRunId").GetString());
        Assert.Equal("driver-example-1", linkedJson.GetProperty("waiterWorkflowId").GetString());

        var aloneJson = JsonDocument.Parse(JsonSerializer.Serialize(alone, web)).RootElement;
        Assert.Equal(JsonValueKind.Null, aloneJson.GetProperty("parentWorkflowId").ValueKind);
        Assert.Equal(JsonValueKind.Null, aloneJson.GetProperty("waiterWorkflowId").ValueKind);
        // The fields a client has always read are where they were.
        Assert.Equal("solo-1", aloneJson.GetProperty("workflowId").GetString());
    }
}
