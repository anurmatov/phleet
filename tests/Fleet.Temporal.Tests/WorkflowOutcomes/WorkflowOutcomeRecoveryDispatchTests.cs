using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Fleet.Shared;
using Fleet.Temporal.Configuration;
using Fleet.Temporal.Mcp;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Temporalio.Api.Enums.V1;
using Temporalio.Client;
using Temporalio.Exceptions;

namespace Fleet.Temporal.Tests.WorkflowOutcomes;

/// <summary>
/// #476 AC16: the existing start/cancel/signal tools with an optional <c>recovery_operation_id</c>.
/// The orchestrator side is a small state machine over the real <see cref="WorkflowRecoveryForwarder"/>
/// (reserved → dispatching once → dispatched / dispatch_unknown / blocked), so the HTTP mapping is
/// exercised for real; Temporal is a substitute where a lost reply must be simulated, and a real
/// time-skipping server where RejectDuplicate must be proven.
/// </summary>
[Trait("WorkflowOutcomeSuite", "dispatch")]
public sealed class WorkflowOutcomeRecoveryDispatchTests
{
    private const string Coordinator = "coordinator";
    private static readonly Guid OperationId = Guid.Parse("4f6c1d2e-8a9b-4c3d-9e1f-0a1b2c3d4e5f");
    private static string ReplacementId => "recovery-" + OperationId.ToString("D");
    private const string Input = "{\"Task\":\"redo the thing\"}";

    /// <summary>The orchestrator's recovery routes for one reserved operation.</summary>
    private sealed class FakeRecoveryApi : HttpMessageHandler
    {
        public string State { get; set; } = "reserved";
        public required WorkflowRecoveryDispatchRequest Expected { get; init; }
        public bool LoseAuthorizeReply { get; set; }
        public bool AllowEveryTime { get; set; }
        public List<(string Method, string Path, string Body, string? Authorization)> Requests { get; } = [];
        public List<WorkflowRecoveryReceiptRequest> Receipts { get; } = [];
        private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add((request.Method.Method, path, body, request.Headers.Authorization?.ToString()));
            var operationPath = "/api/workflow-outcomes/recovery/" + OperationId.ToString("D");

            if (request.Method == HttpMethod.Get && path == operationPath)
                return Json(HttpStatusCode.OK, new { operationId = OperationId, state = State, replacementWorkflowId = ReplacementId });

            if (path == operationPath + "/authorize-dispatch")
            {
                var dispatch = JsonSerializer.Deserialize<WorkflowRecoveryDispatchRequest>(body, Web)!;
                if (State != "reserved" && !AllowEveryTime)
                    return Json(HttpStatusCode.Conflict, new { allowed = false, reason = "already_dispatching" });
                if (dispatch != Expected)
                    return Json(HttpStatusCode.UnprocessableEntity, new { allowed = false, reason = "dispatch_mismatch" });
                State = "dispatching";
                if (LoseAuthorizeReply) throw new HttpRequestException("reply lost");
                return Json(HttpStatusCode.OK, new { allowed = true, replacementWorkflowId = ReplacementId });
            }

            if (path == operationPath + "/receipt")
            {
                var receipt = JsonSerializer.Deserialize<WorkflowRecoveryReceiptRequest>(body, Web)!;
                Receipts.Add(receipt);
                State = receipt.Result switch
                {
                    WorkflowRecoveryReceiptResults.Unknown => "dispatch_unknown",
                    WorkflowRecoveryReceiptResults.Rejected => "blocked",
                    _ => "dispatched",
                };
                return Json(HttpStatusCode.OK, new { operationId = OperationId, state = State });
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(HttpStatusCode status, object value) => new(status)
        {
            Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json"),
        };
    }

    private sealed class ToolContext
    {
        public required TemporalWorkflowTools Tool { get; init; }
        public required ITemporalClient Client { get; init; }
        public required WorkflowHandle Handle { get; init; }
        public required FakeRecoveryApi Api { get; init; }
        public required RecordingLogger<TemporalWorkflowTools> Logger { get; init; }
        public List<(string Id, WorkflowOptions Options)> Starts { get; } = [];
        public List<string> Signals { get; } = [];
        public int Cancels { get; set; }
    }

    private static WorkflowRecoveryDispatchRequest StartRequest(string type = "ExampleWorkflow", string input = Input) => new(
        Coordinator, "temporal_start_workflow", "fleet", type, "fleet", WorkflowOutcomeHashes.Sha256Hex(input), null, null, null);

    private static ToolContext Build(
        WorkflowRecoveryDispatchRequest? expected = null, string cto = Coordinator, string? caller = Coordinator,
        Func<WorkflowOptions, Task<WorkflowHandle>>? start = null, IWorkflowRecoveryForwarder? forwarder = null)
    {
        var api = new FakeRecoveryApi { Expected = expected ?? StartRequest() };
        var http = new HttpClient(api) { BaseAddress = new Uri("https://orchestrator.example/") };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", WorkflowOutcomeTestValues.AdminToken);
        var httpFactory = Substitute.For<IHttpClientFactory>();
        httpFactory.CreateClient(WorkflowRecoveryForwarder.HttpClientName).Returns(http);
        httpFactory.CreateClient("orchestrator").Returns(new HttpClient());

        var client = Substitute.For<ITemporalClient>();
        var handle = Substitute.For<WorkflowHandle>(client, "target-1", null!, null!, null!);
        var clientFactory = Substitute.For<ITemporalClientFactory>();
        clientFactory.GetClientAsync(Arg.Any<string>()).Returns(client);
        client.GetWorkflowHandle(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>()).Returns(handle);

        var ctoConfig = Substitute.For<CtoAgentConfigService>();
        ctoConfig.GetCtoAgent().Returns(cto);
        var httpContext = new DefaultHttpContext();
        if (caller is not null) httpContext.Request.QueryString = new QueryString("?agent=" + Uri.EscapeDataString(caller));
        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns(httpContext);

        var logger = new RecordingLogger<TemporalWorkflowTools>();
        var context = new ToolContext
        {
            Tool = new TemporalWorkflowTools(
                clientFactory,
                new WorkflowTypeRegistry(httpFactory, Options.Create(new TemporalBridgeOptions()), NullLogger<WorkflowTypeRegistry>.Instance),
                ctoConfig,
                Substitute.For<IWorkflowGateStateReader>(),
                Substitute.For<IEpicGrantDecisionForwarder>(),
                accessor,
                logger,
                forwarder ?? new WorkflowRecoveryForwarder(httpFactory)),
            Client = client,
            Handle = handle,
            Api = api,
            Logger = logger,
        };

        client.StartWorkflowAsync(Arg.Any<string>(), Arg.Any<IReadOnlyCollection<object?>>(), Arg.Any<WorkflowOptions>())
            .Returns(call =>
            {
                var options = call.ArgAt<WorkflowOptions>(2);
                context.Starts.Add((options.Id!, options));
                return start is null
                    ? Task.FromResult(new WorkflowHandle(client, options.Id!, null, "run-1", "run-1"))
                    : start(options);
            });
        handle.SignalAsync(Arg.Do<string>(name => context.Signals.Add(name)), Arg.Any<IReadOnlyCollection<object?>>(), Arg.Any<WorkflowSignalOptions?>())
            .Returns(Task.CompletedTask);
        handle.CancelAsync(Arg.Any<WorkflowCancelOptions?>()).Returns(_ =>
        {
            context.Cancels++;
            return Task.CompletedTask;
        });
        handle.DescribeAsync(Arg.Any<WorkflowDescribeOptions?>()).Returns(
            Task.FromResult<WorkflowExecutionDescription>(Description("target-1", "run-42", "ExampleWorkflow", "fleet")));
        return context;
    }

    private sealed class TestDescription(Temporalio.Api.WorkflowService.V1.DescribeWorkflowExecutionResponse raw)
        : WorkflowExecutionDescription(raw, Temporalio.Converters.DataConverter.Default);

    private static TestDescription Description(string workflowId, string runId, string type, string taskQueue) => new(new()
    {
        WorkflowExecutionInfo = new Temporalio.Api.Workflow.V1.WorkflowExecutionInfo
        {
            Execution = new Temporalio.Api.Common.V1.WorkflowExecution { WorkflowId = workflowId, RunId = runId },
            Type = new Temporalio.Api.Common.V1.WorkflowType { Name = type },
            TaskQueue = taskQueue,
            Status = WorkflowExecutionStatus.Running,
        },
    });

    // ── AC16: same operation twice + lost RPC reply → at most one deterministic start ─────────

    [Fact]
    public async Task Start_SameOperationTwiceWithLostRpcReply_StartsAtMostOnceWithTheDeterministicId()
    {
        var context = Build(start: _ => throw new RpcException(RpcException.StatusCode.Unavailable, "connection reset", null));

        var first = await context.Tool.StartWorkflowAsync("ExampleWorkflow", input: Input, recovery_operation_id: OperationId.ToString("D"));
        var second = await context.Tool.StartWorkflowAsync("ExampleWorkflow", input: Input, recovery_operation_id: OperationId.ToString("D"));

        var (id, options) = Assert.Single(context.Starts);
        Assert.Equal(ReplacementId, id);
        Assert.Equal(WorkflowIdReusePolicy.RejectDuplicate, options.IdReusePolicy);
        Assert.Equal(WorkflowIdConflictPolicy.Fail, options.IdConflictPolicy);
        Assert.Equal("fleet", options.TaskQueue);

        // The ambiguous RPC is recorded unknown, never retried, and the readback shows it.
        Assert.Contains("unknown", first);
        Assert.Contains("operation state: dispatch_unknown", first);
        Assert.Contains("No retry was made", first);
        var receipt = Assert.Single(context.Api.Receipts);
        Assert.Equal(WorkflowRecoveryReceiptResults.Unknown, receipt.Result);
        Assert.Equal(ReplacementId, receipt.ResultWorkflowId);
        Assert.Equal("rpc_Unavailable", receipt.SanitizedFailure);
        Assert.Equal("dispatch_unknown", context.Api.State);

        // The second call is refused by the orchestrator and sends nothing.
        Assert.StartsWith("Error:", second);
        Assert.Contains("already_dispatching", second);
        Assert.Contains("Nothing was sent", second);
        Assert.Equal(2, context.Api.Requests.Count(r => r.Path.EndsWith("/authorize-dispatch")));
        Assert.All(context.Api.Requests, r => Assert.Equal("Bearer " + WorkflowOutcomeTestValues.AdminToken, r.Authorization));
    }

    [Fact]
    public async Task Start_Allowed_UsesTheReservationAndRecordsStarted()
    {
        var context = Build();

        var result = JsonDocument.Parse(await context.Tool.StartWorkflowAsync(
            "ExampleWorkflow", input: Input, recovery_operation_id: OperationId.ToString("D"))).RootElement;

        Assert.Equal("started", result.GetProperty("status").GetString());
        Assert.Equal(ReplacementId, result.GetProperty("workflowId").GetString());
        Assert.Equal("recorded", result.GetProperty("receipt").GetString());
        var receipt = Assert.Single(context.Api.Receipts);
        Assert.Equal(new WorkflowRecoveryReceiptRequest(WorkflowRecoveryReceiptResults.Started, ReplacementId, "run-1", null, null), receipt);
        Assert.Equal("dispatched", context.Api.State);

        var authorize = context.Api.Requests.Single(r => r.Path.EndsWith("/authorize-dispatch"));
        using var body = JsonDocument.Parse(authorize.Body);
        Assert.Equal(WorkflowOutcomeHashes.Sha256Hex(Input), body.RootElement.GetProperty("inputSha256").GetString());
        Assert.Equal(Coordinator, body.RootElement.GetProperty("coordinator").GetString());
        Assert.Equal("temporal_start_workflow", body.RootElement.GetProperty("tool").GetString());
    }

    /// <summary>A second target for the same operation — another type, another input — is refused and sends nothing.</summary>
    [Theory]
    [InlineData("OtherWorkflow", Input)]
    [InlineData("ExampleWorkflow", "{\"Task\":\"something else\"}")]
    public async Task Start_SecondTargetForTheOperation_IsRefusedAndSendsNothing(string type, string input)
    {
        var context = Build();
        var started = await context.Tool.StartWorkflowAsync("ExampleWorkflow", input: Input, recovery_operation_id: OperationId.ToString("D"));
        Assert.Contains("\"started\"", started);

        var other = await context.Tool.StartWorkflowAsync(type, input: input, recovery_operation_id: OperationId.ToString("D"));

        Assert.StartsWith("Error:", other);
        Assert.Single(context.Starts);

        // And on a fresh reservation a mismatched target is refused outright.
        var fresh = Build();
        var mismatch = await fresh.Tool.StartWorkflowAsync(type, input: input, recovery_operation_id: OperationId.ToString("D"));
        Assert.Contains("dispatch_mismatch", mismatch);
        Assert.Empty(fresh.Starts);
        Assert.Equal("reserved", fresh.Api.State);
    }

    [Fact]
    public async Task Start_AuthorizeReplyLost_SendsNothingAndInventsNoReceipt()
    {
        var context = Build();
        context.Api.LoseAuthorizeReply = true;

        var result = await context.Tool.StartWorkflowAsync("ExampleWorkflow", input: Input, recovery_operation_id: OperationId.ToString("D"));

        Assert.Contains("authorization outcome is unknown", result);
        Assert.Contains("do not resend", result);
        Assert.Empty(context.Starts);
        Assert.Empty(context.Api.Receipts);
        Assert.Equal("dispatching", context.Api.State);
    }

    [Theory]
    [InlineData(Coordinator, null, "caller identity is unresolved")]
    [InlineData(Coordinator, "agent1", "not the configured coordinator")]
    [InlineData("", Coordinator, "not configured")]
    public async Task Start_CallerIsNotTheConfiguredCoordinator_IsRefusedBeforeAuthorize(string cto, string? caller, string reason)
    {
        var context = Build(cto: cto, caller: caller);

        var result = await context.Tool.StartWorkflowAsync("ExampleWorkflow", input: Input, recovery_operation_id: OperationId.ToString("D"));

        Assert.Contains(reason, result);
        Assert.Empty(context.Api.Requests);
        Assert.Empty(context.Starts);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-uuid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task Start_InvalidOperationId_IsRefusedBeforeAnything(string operationId)
    {
        var context = Build();

        var result = await context.Tool.StartWorkflowAsync("ExampleWorkflow", input: Input, recovery_operation_id: operationId);

        Assert.Contains("must be a UUID", result);
        Assert.Empty(context.Api.Requests);
        Assert.Empty(context.Starts);
    }

    [Fact]
    public async Task Start_WorkflowIdOtherThanTheReservation_IsRefusedBeforeAuthorize()
    {
        var context = Build();

        var result = await context.Tool.StartWorkflowAsync(
            "ExampleWorkflow", workflow_id: "my-own-id", input: Input, recovery_operation_id: OperationId.ToString("D"));

        Assert.Contains("workflow_id must be omitted or exactly", result);
        Assert.Empty(context.Api.Requests);
    }

    [Fact]
    public async Task Start_DefinitiveTemporalRejection_IsRecordedRejected()
    {
        var context = Build(start: _ => throw new RpcException(RpcException.StatusCode.InvalidArgument, "bad task queue", null));

        var result = await context.Tool.StartWorkflowAsync("ExampleWorkflow", input: Input, recovery_operation_id: OperationId.ToString("D"));

        Assert.Contains("rejected by Temporal (rpc_InvalidArgument)", result);
        Assert.DoesNotContain("bad task queue", result);
        Assert.Equal(WorkflowRecoveryReceiptResults.Rejected, Assert.Single(context.Api.Receipts).Result);
    }

    /// <summary>An id that already exists may be this very dispatch after a lost reply: unknown, with the existing run.</summary>
    [Fact]
    public async Task Start_AlreadyStarted_IsUnknownNotRejected()
    {
        var context = Build(start: _ => throw new WorkflowAlreadyStartedException("exists", ReplacementId, "ExampleWorkflow", "run-existing"));

        var result = await context.Tool.StartWorkflowAsync("ExampleWorkflow", input: Input, recovery_operation_id: OperationId.ToString("D"));

        Assert.Contains("dispatch_unknown", result);
        var receipt = Assert.Single(context.Api.Receipts);
        Assert.Equal(WorkflowRecoveryReceiptResults.Unknown, receipt.Result);
        Assert.Equal("run-existing", receipt.ResultRunId);
    }

    /// <summary>
    /// Real Temporal: the reserved id is started once; even if an orchestrator wrongly authorized a
    /// second dispatch, RejectDuplicate makes the second start fail and it is reported unknown —
    /// one run exists for the deterministic id.
    /// </summary>
    [Fact]
    public async Task Start_RealTemporal_DeterministicIdCannotStartTwice()
    {
        await using var env = await Temporalio.Testing.WorkflowEnvironment.StartTimeSkippingAsync();
        using var worker = new Temporalio.Worker.TemporalWorker(env.Client,
            new Temporalio.Worker.TemporalWorkerOptions("fleet").AddWorkflow<Fleet.Temporal.Tests.Mcp.ReusePolicyTestWorkflow>());
        await worker.ExecuteAsync(async () =>
        {
            var expected = StartRequest("ReusePolicyTestWorkflow", "");
            var api = new FakeRecoveryApi { Expected = expected, AllowEveryTime = true };
            var http = new HttpClient(api) { BaseAddress = new Uri("https://orchestrator.example/") };
            var httpFactory = Substitute.For<IHttpClientFactory>();
            httpFactory.CreateClient(WorkflowRecoveryForwarder.HttpClientName).Returns(http);
            httpFactory.CreateClient("orchestrator").Returns(new HttpClient());
            var clientFactory = Substitute.For<ITemporalClientFactory>();
            clientFactory.GetClientAsync(Arg.Any<string>()).Returns(env.Client);
            var cto = Substitute.For<CtoAgentConfigService>();
            cto.GetCtoAgent().Returns(Coordinator);
            var httpContext = new DefaultHttpContext { Request = { QueryString = new QueryString("?agent=" + Coordinator) } };
            var accessor = Substitute.For<IHttpContextAccessor>();
            accessor.HttpContext.Returns(httpContext);
            var tool = new TemporalWorkflowTools(
                clientFactory,
                new WorkflowTypeRegistry(httpFactory, Options.Create(new TemporalBridgeOptions()), NullLogger<WorkflowTypeRegistry>.Instance),
                cto, Substitute.For<IWorkflowGateStateReader>(), Substitute.For<IEpicGrantDecisionForwarder>(), accessor,
                NullLogger<TemporalWorkflowTools>.Instance, new WorkflowRecoveryForwarder(httpFactory));

            var first = JsonDocument.Parse(
                await tool.StartWorkflowAsync("ReusePolicyTestWorkflow", recovery_operation_id: OperationId.ToString("D"))).RootElement;
            Assert.Equal("started", first.GetProperty("status").GetString());
            var firstRun = first.GetProperty("runId").GetString();
            await env.Client.GetWorkflowHandle(ReplacementId).TerminateAsync("test close");

            var second = await tool.StartWorkflowAsync("ReusePolicyTestWorkflow", recovery_operation_id: OperationId.ToString("D"));
            Assert.Contains("dispatch_unknown", second);

            // The latest (and only) run for the deterministic id is still the first one.
            var latest = await env.Client.GetWorkflowHandle(ReplacementId).DescribeAsync();
            Assert.Equal(firstRun, latest.RunId);
            Assert.Equal(WorkflowExecutionStatus.Terminated, latest.Status);
            Assert.Equal(
                [WorkflowRecoveryReceiptResults.Started, WorkflowRecoveryReceiptResults.Unknown],
                api.Receipts.Select(r => r.Result));
        });
    }

    // ── Cancel ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Cancel_TargetsTheExactDescribedRun_AfterAuthorization()
    {
        var context = Build(expected: new WorkflowRecoveryDispatchRequest(
            Coordinator, "temporal_cancel_workflow", "fleet", "ExampleWorkflow", "fleet", null, "target-1", "run-42", null));

        var result = JsonDocument.Parse(await context.Tool.CancelWorkflowAsync("target-1", recovery_operation_id: OperationId.ToString("D"))).RootElement;

        Assert.Equal("cancel_requested", result.GetProperty("status").GetString());
        Assert.Equal("run-42", result.GetProperty("runId").GetString());
        Assert.Equal(1, context.Cancels);
        context.Client.Received().GetWorkflowHandle("target-1", "run-42", Arg.Any<string?>());
        Assert.Equal(
            new WorkflowRecoveryReceiptRequest(WorkflowRecoveryReceiptResults.CancelRequested, "target-1", "run-42", null, null),
            Assert.Single(context.Api.Receipts));
    }

    [Fact]
    public async Task Cancel_OtherTargetThanReserved_IsRefusedAndCancelsNothing()
    {
        var context = Build(expected: new WorkflowRecoveryDispatchRequest(
            Coordinator, "temporal_cancel_workflow", "fleet", "ExampleWorkflow", "fleet", null, "target-1", "run-OTHER", null));

        var result = await context.Tool.CancelWorkflowAsync("target-1", recovery_operation_id: OperationId.ToString("D"));

        Assert.Contains("dispatch_mismatch", result);
        Assert.Equal(0, context.Cancels);
    }

    [Fact]
    public async Task Cancel_ClosedRun_IsRecordedRejected()
    {
        var context = Build(expected: new WorkflowRecoveryDispatchRequest(
            Coordinator, "temporal_cancel_workflow", "fleet", "ExampleWorkflow", "fleet", null, "target-1", "run-42", null));
        context.Handle.CancelAsync(Arg.Any<WorkflowCancelOptions?>())
            .ThrowsAsync(new RpcException(RpcException.StatusCode.NotFound, "workflow execution already completed", null));

        var result = await context.Tool.CancelWorkflowAsync("target-1", recovery_operation_id: OperationId.ToString("D"));

        Assert.Contains("rejected by Temporal (rpc_NotFound)", result);
        Assert.Equal(WorkflowRecoveryReceiptResults.Rejected, Assert.Single(context.Api.Receipts).Result);
    }

    // ── Signal: a recovery id adds no authority and sends nothing ─────────────────────────────

    public static TheoryData<string, string> SignalCases => new()
    {
        { "merge-approval", "{\"Decision\":\"approved\",\"GrantId\":\"g\",\"VisitId\":\"v\",\"ArtifactRef\":\"a\",\"Evidence\":\"https://example.com/e\"}" },
        { "design-approval", "{\"Decision\":\"changes_requested\",\"Comment\":\"fix it\"}" },
        { "doc-review", "{\"Decision\":\"approved\"}" },
        { "advisory-review", "{\"Decision\":\"approved\"}" },
        { "acceptance-decision", "{\"Decision\":\"accepted\"}" },
        { " Merge-Approval ", "{\"Decision\":\"approved\"}" },
        { "human-review", "{\"Decision\":\"approved\"}" },
        { "escalation-decision", "{\"Decision\":\"retry\"}" },
    };

    [Theory]
    [MemberData(nameof(SignalCases))]
    public async Task Signal_WithRecoveryId_SendsNothingAndAuthorizesNothing(string signal, string args)
    {
        var context = Build();

        var result = await context.Tool.SignalWorkflowAsync("target-1", signal, args, recovery_operation_id: OperationId.ToString("D"));

        Assert.StartsWith("Error:", result);
        Assert.Contains("Nothing was sent", result);
        Assert.Empty(context.Signals);
        Assert.Empty(context.Api.Requests);
        Assert.Equal("reserved", context.Api.State);
        if (CeoGateSignals.IsReserved(signal))
            Assert.Contains("adds no gate authority", result);
        else
            Assert.Contains("resume and accept_late need the separate delegate-recovery handler", result);
    }

    // ── Null recovery id: byte-identical behaviour and gates ─────────────────────────────────

    /// <summary>Fails the test if the guarded path is ever reached.</summary>
    private sealed class ForbiddenForwarder : IWorkflowRecoveryForwarder
    {
        public Task<WorkflowRecoveryAuthorization> AuthorizeDispatchAsync(Guid operationId, WorkflowRecoveryDispatchRequest request, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("recovery forwarder must not be called");
        public Task<WorkflowRecoveryReceiptOutcome> RecordReceiptAsync(Guid operationId, WorkflowRecoveryReceiptRequest request, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("recovery forwarder must not be called");
        public Task<string?> ReadStateAsync(Guid operationId, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("recovery forwarder must not be called");
    }

    public static TheoryData<string, string?> NullIdSignalCases => new()
    {
        { "merge-approval", "{\"Decision\":\"changes_requested\",\"Comment\":\"needs a fix\"}" },   // CTO feedback exception
        { "merge-approval", "{\"Decision\":\"approved\"}" },                                       // CEO-only refusal
        { "advisory-review", "{\"Decision\":\"approved\"}" },                                      // reserved, no exception
        { "human-review", "{\"Decision\":\"approved\"}" },                                         // plain signal
        { "escalation-decision", null },                                                          // no payload
    };

    [Theory]
    [MemberData(nameof(NullIdSignalCases))]
    public async Task Signal_NullRecoveryId_IsIdenticalToOmittingIt(string signal, string? args)
    {
        var omitted = Build(forwarder: new ForbiddenForwarder());
        var explicitNull = Build(forwarder: new ForbiddenForwarder());

        var a = await omitted.Tool.SignalWorkflowAsync("target-1", signal, args);
        var b = await explicitNull.Tool.SignalWorkflowAsync("target-1", signal, args, recovery_operation_id: null);

        Assert.Equal(a, b);
        Assert.Equal(omitted.Signals, explicitNull.Signals);
        Assert.Empty(explicitNull.Api.Requests);
    }

    [Fact]
    public async Task StartAndCancel_NullRecoveryId_AreIdenticalToOmittingIt()
    {
        var omitted = Build(forwarder: new ForbiddenForwarder());
        var explicitNull = Build(forwarder: new ForbiddenForwarder());

        Assert.Equal(
            await omitted.Tool.StartWorkflowAsync("ExampleWorkflow", "fixed-id", Input),
            await explicitNull.Tool.StartWorkflowAsync("ExampleWorkflow", "fixed-id", Input, recovery_operation_id: null));
        var (_, a) = Assert.Single(omitted.Starts);
        var (_, b) = Assert.Single(explicitNull.Starts);
        Assert.Equal((a.Id, a.TaskQueue, a.IdReusePolicy, a.IdConflictPolicy), (b.Id, b.TaskQueue, b.IdReusePolicy, b.IdConflictPolicy));
        Assert.Equal(WorkflowIdReusePolicy.AllowDuplicate, b.IdReusePolicy);
        Assert.Equal(WorkflowIdConflictPolicy.Unspecified, b.IdConflictPolicy);

        Assert.Equal(
            await omitted.Tool.CancelWorkflowAsync("target-1"),
            await explicitNull.Tool.CancelWorkflowAsync("target-1", recovery_operation_id: null));
        Assert.Equal(omitted.Cancels, explicitNull.Cancels);
        await explicitNull.Handle.DidNotReceiveWithAnyArgs().DescribeAsync(default);
    }

    /// <summary>The MCP server builds the tool from DI: the registered forwarder must reach it.</summary>
    [Fact]
    public async Task Tool_BuiltFromDependencyInjection_ReceivesTheRecoveryForwarder()
    {
        var forwarder = Substitute.For<IWorkflowRecoveryForwarder>();
        forwarder.AuthorizeDispatchAsync(default, default!, default)
            .ReturnsForAnyArgs(WorkflowRecoveryAuthorization.Refused("probe"));
        var cto = Substitute.For<CtoAgentConfigService>();
        cto.GetCtoAgent().Returns(Coordinator);
        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns(new DefaultHttpContext { Request = { QueryString = new QueryString("?agent=" + Coordinator) } });
        var httpFactory = Substitute.For<IHttpClientFactory>();
        httpFactory.CreateClient(Arg.Any<string>()).Returns(new HttpClient());

        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton(services, Substitute.For<ITemporalClientFactory>());
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton(services,
            new WorkflowTypeRegistry(httpFactory, Options.Create(new TemporalBridgeOptions()), NullLogger<WorkflowTypeRegistry>.Instance));
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton(services, cto);
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton(services, Substitute.For<IWorkflowGateStateReader>());
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton(services, Substitute.For<IEpicGrantDecisionForwarder>());
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton(services, accessor);
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton(services, forwarder);
        Microsoft.Extensions.DependencyInjection.LoggingServiceCollectionExtensions.AddLogging(services);
        using var provider = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(services);

        var tool = Microsoft.Extensions.DependencyInjection.ActivatorUtilities.CreateInstance<TemporalWorkflowTools>(provider);
        var result = await tool.StartWorkflowAsync("ExampleWorkflow", input: Input, recovery_operation_id: OperationId.ToString("D"));

        Assert.Contains("did not authorize it (probe)", result);
        await forwarder.ReceivedWithAnyArgs(1).AuthorizeDispatchAsync(default, default!, default);
    }

    // ── Forwarder response mapping ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData(200, "{\"allowed\":true,\"replacementWorkflowId\":\"recovery-x\"}", true, false)]
    [InlineData(409, "{\"allowed\":false,\"reason\":\"already_dispatching\"}", false, false)]
    [InlineData(422, "{\"allowed\":false,\"reason\":\"dispatch_mismatch\"}", false, false)]
    [InlineData(200, "{\"allowed\":false,\"reason\":\"blocked\"}", false, false)]
    [InlineData(401, "", false, false)]
    [InlineData(403, "{\"error\":\"forbidden\"}", false, false)]
    [InlineData(503, "{\"error\":\"unavailable\"}", false, true)]
    [InlineData(200, "not json", false, true)]
    [InlineData(200, "{}", false, true)]
    public void Forwarder_MapAuthorization(int status, string body, bool allowed, bool unknown)
    {
        var result = WorkflowRecoveryForwarder.MapAuthorization(status, body);

        Assert.Equal(allowed, result.Allowed);
        Assert.Equal(unknown, result.Unknown);
    }

    [Fact]
    public async Task Logs_NeverContainInputOrToken()
    {
        var context = Build();
        await context.Tool.StartWorkflowAsync("ExampleWorkflow", input: "{\"Secret\":\"" + WorkflowOutcomeTestValues.SecretText + "\"}",
            recovery_operation_id: OperationId.ToString("D"));

        Assert.NotEmpty(context.Logger.Entries);
        Assert.DoesNotContain(WorkflowOutcomeTestValues.SecretText, context.Logger.AllText);
        Assert.DoesNotContain(WorkflowOutcomeTestValues.AdminToken, context.Logger.AllText);
    }
}
