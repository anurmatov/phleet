using System.Text.Json;
using Fleet.Temporal.Configuration;
using Fleet.Temporal.Mcp;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Temporalio.Api.Enums.V1;
using Temporalio.Client;

namespace Fleet.Temporal.Tests.Mcp;

public sealed class TemporalWorkflowToolsTests
{
    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message, string? Template)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception), TemplateOf(state)));

        private static string? TemplateOf<TState>(TState state) =>
            state is IReadOnlyList<KeyValuePair<string, object?>> values
                ? values.FirstOrDefault(pair => pair.Key == "{OriginalFormat}").Value as string
                : null;
    }

    /// <summary>
    /// Stands in for the Temporal describe. Defaults to a workflow parked at the design gate, and
    /// counts calls so a test can prove the lookup never happens for merge-approval or for a call
    /// that an earlier identity, config or payload check already blocked.
    /// </summary>
    private sealed class FakeGateStateReader : IWorkflowGateStateReader
    {
        public WorkflowExecutionStatus Status { get; set; } = WorkflowExecutionStatus.Running;
        public string? Phase { get; set; } = "design-approval";
        public Exception? Throws { get; set; }
        public int Calls { get; private set; }

        public Task<(WorkflowExecutionStatus Status, string? Phase)> ReadAsync(
            WorkflowHandle handle,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            if (Throws is not null)
                throw Throws;
            return Task.FromResult((Status, Phase));
        }
    }

    private sealed class ToolContext
    {
        public TemporalWorkflowTools Tool { get; set; } = null!;
        public required WorkflowHandle Handle { get; init; }
        public required RecordingLogger<TemporalWorkflowTools> Logger { get; init; }
        public required FakeGateStateReader GateReader { get; init; }
        public string? SentSignal { get; set; }
        public IReadOnlyCollection<object?>? SentArgs { get; set; }
    }

    private static ToolContext BuildTool(string ctoAgent = "cto-agent", string? caller = "cto-agent")
    {
        var client = Substitute.For<ITemporalClient>();
        var handle = Substitute.For<WorkflowHandle>(client, "workflow-1", null!, null!, null!);
        var context = new ToolContext
        {
            Handle = handle,
            Logger = new RecordingLogger<TemporalWorkflowTools>(),
            GateReader = new FakeGateStateReader(),
            Tool = null!
        };

        handle.SignalAsync(
                Arg.Do<string>(value => context.SentSignal = value),
                Arg.Do<IReadOnlyCollection<object?>>(value => context.SentArgs = value),
                Arg.Any<WorkflowSignalOptions?>())
            .Returns(Task.CompletedTask);
        client.GetWorkflowHandle(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>())
            .Returns(handle);

        var clientFactory = Substitute.For<ITemporalClientFactory>();
        clientFactory.GetClientAsync(Arg.Any<string>()).Returns(client);

        var ctoConfig = Substitute.For<CtoAgentConfigService>();
        ctoConfig.GetCtoAgent().Returns(ctoAgent);

        var httpContext = new DefaultHttpContext();
        if (caller is not null)
            httpContext.Request.QueryString = new QueryString($"?agent={Uri.EscapeDataString(caller)}");

        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns(httpContext);

        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient("orchestrator").Returns(new HttpClient());
        var registry = new WorkflowTypeRegistry(
            httpClientFactory,
            Options.Create(new TemporalBridgeOptions()),
            NullLogger<WorkflowTypeRegistry>.Instance);

        context.Tool = new TemporalWorkflowTools(
            clientFactory,
            registry,
            ctoConfig,
            context.GateReader,
            Substitute.For<IEpicGrantDecisionForwarder>(),
            accessor,
            context.Logger);
        return context;
    }

    [Fact]
    public async Task SignalWorkflowAsync_CtoChangesRequestedWithComment_SendsCanonicalSignalAndPayload()
    {
        var context = BuildTool(ctoAgent: "cto-agent", caller: "CTO-AGENT");
        const string payload = "{\"Decision\":\"changes_requested\",\"Comment\":\"needs a runtime fix\"}";

        var result = await context.Tool.SignalWorkflowAsync(
            "workflow-1",
            "merge-approval",
            payload);

        Assert.Equal("merge-approval", context.SentSignal);
        var arg = Assert.IsType<JsonElement>(Assert.Single(context.SentArgs!));
        Assert.Equal("changes_requested", arg.GetProperty("Decision").GetString());
        Assert.Equal("needs a runtime fix", arg.GetProperty("Comment").GetString());
        Assert.Equal("signalled", JsonDocument.Parse(result).RootElement.GetProperty("status").GetString());
        Assert.Contains(context.Logger.Entries, entry =>
            entry.Level == LogLevel.Information &&
            entry.Message.Contains("workflow-1") &&
            entry.Message.Contains("changes_requested") &&
            entry.Message.Contains("CTO-AGENT") &&
            !entry.Message.Contains("needs a runtime fix"));
    }

    [Theory]
    [InlineData("{\"Decision\":\"changes_requested\"}")]
    [InlineData("{\"Decision\":\"changes_requested\",\"Comment\":null}")]
    [InlineData("{\"Decision\":\"changes_requested\",\"Comment\":\"\"}")]
    [InlineData("{\"Decision\":\"changes_requested\",\"Comment\":\"   \"}")]
    public async Task SignalWorkflowAsync_CtoChangesRequestedWithoutComment_Blocks(string payload)
    {
        var context = BuildTool();

        var result = await context.Tool.SignalWorkflowAsync("workflow-1", "merge-approval", payload);

        Assert.Contains("Comment", result);
        Assert.Null(context.SentSignal);
    }

    [Fact]
    public async Task SignalWorkflowAsync_CtoApprovedMergeApproval_BlocksWithoutLoggingComment()
    {
        var context = BuildTool();
        const string secretComment = "comment-must-not-reach-logs";

        var result = await context.Tool.SignalWorkflowAsync(
            "workflow-1",
            "merge-approval",
            $"{{\"Decision\":\"approved\",\"Comment\":\"{secretComment}\"}}");

        Assert.Contains("changes_requested", result);
        Assert.Null(context.SentSignal);
        Assert.Contains(context.Logger.Entries, entry =>
            entry.Level == LogLevel.Warning && entry.Message.Contains("approved"));
        Assert.DoesNotContain(context.Logger.Entries, entry => entry.Message.Contains(secretComment));
    }

    [Fact]
    public async Task SignalWorkflowAsync_CtoRejectedMergeApproval_Blocks()
    {
        var context = BuildTool();

        var result = await context.Tool.SignalWorkflowAsync(
            "workflow-1",
            "merge-approval",
            "{\"Decision\":\"rejected\",\"Comment\":\"no\"}");

        Assert.Contains("changes_requested", result);
        Assert.Null(context.SentSignal);
    }

    [Fact]
    public async Task SignalWorkflowAsync_NonCtoChangesRequested_Blocks()
    {
        var context = BuildTool(caller: "another-agent");

        var result = await context.Tool.SignalWorkflowAsync(
            "workflow-1",
            "merge-approval",
            "{\"Decision\":\"changes_requested\",\"Comment\":\"fix it\"}");

        Assert.Contains("configured CTO", result);
        Assert.Null(context.SentSignal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task SignalWorkflowAsync_UnresolvedCallerChangesRequested_Blocks(string? caller)
    {
        var context = BuildTool(caller: caller);

        var result = await context.Tool.SignalWorkflowAsync(
            "workflow-1",
            "merge-approval",
            "{\"Decision\":\"changes_requested\",\"Comment\":\"fix it\"}");

        Assert.Contains("unresolved", result);
        Assert.Null(context.SentSignal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task SignalWorkflowAsync_CtoAgentUnset_Blocks(string ctoAgent)
    {
        var context = BuildTool(ctoAgent: ctoAgent, caller: null);

        var result = await context.Tool.SignalWorkflowAsync(
            "workflow-1",
            "merge-approval",
            "{\"Decision\":\"changes_requested\",\"Comment\":\"fix it\"}");

        Assert.Contains("CTO agent is not configured", result);
        Assert.Null(context.SentSignal);
    }

    [Fact]
    public async Task SignalWorkflowAsync_MalformedMergeApprovalArgs_ReturnsInvalidJsonError()
    {
        var context = BuildTool();

        var result = await context.Tool.SignalWorkflowAsync(
            "workflow-1",
            "merge-approval",
            "{not-json");

        Assert.Contains("invalid JSON in args", result);
        Assert.Null(context.SentSignal);
        Assert.Contains(context.Logger.Entries, entry => entry.Level == LogLevel.Warning);
    }

    [Theory]
    [InlineData("doc-review")]
    [InlineData("advisory-review")]
    public async Task SignalWorkflowAsync_OtherCeoOnlySignalFromCto_Blocks(string signalName)
    {
        var context = BuildTool();

        var result = await context.Tool.SignalWorkflowAsync(
            "workflow-1",
            signalName,
            "{\"Decision\":\"changes_requested\",\"Comment\":\"fix it\"}");

        Assert.Contains("CEO-only gate", result);
        Assert.Null(context.SentSignal);
    }

    [Fact]
    public async Task SignalWorkflowAsync_OrdinarySignal_SendsWithoutCtoChecks()
    {
        var context = BuildTool(ctoAgent: "", caller: null);

        var result = await context.Tool.SignalWorkflowAsync(
            "workflow-1",
            "human-review",
            "{\"Decision\":\"approved\"}");

        Assert.Equal("human-review", context.SentSignal);
        Assert.Equal("signalled", JsonDocument.Parse(result).RootElement.GetProperty("status").GetString());
    }

    [Theory]
    [InlineData("Changes_Requested")]
    [InlineData("CHANGES_REQUESTED")]
    [InlineData("changes requested")]
    public async Task SignalWorkflowAsync_NonExactChangesRequestedDecision_Blocks(string decision)
    {
        var context = BuildTool();

        var result = await context.Tool.SignalWorkflowAsync(
            "workflow-1",
            "merge-approval",
            $"{{\"Decision\":\"{decision}\",\"Comment\":\"fix it\"}}");

        Assert.Contains("exactly 'changes_requested'", result);
        Assert.Null(context.SentSignal);
    }

    [Theory]
    [InlineData("MERGE-APPROVAL")]
    [InlineData("Merge-Approval")]
    public async Task SignalWorkflowAsync_NonCanonicalMergeApprovalName_SendsCanonicalName(string signalName)
    {
        var context = BuildTool();

        var result = await context.Tool.SignalWorkflowAsync(
            "workflow-1",
            signalName,
            "{\"Decision\":\"changes_requested\",\"Comment\":\"fix it\"}");

        Assert.Equal("merge-approval", context.SentSignal);
        Assert.Equal("merge-approval", JsonDocument.Parse(result).RootElement.GetProperty("signalName").GetString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("\"changes_requested\"")]
    [InlineData("{\"Comment\":\"fix it\"}")]
    [InlineData("{\"Decision\":1,\"Comment\":\"fix it\"}")]
    [InlineData("{\"Decision\":\"changes_requested\",\"Comment\":1}")]
    public async Task SignalWorkflowAsync_InvalidMergeApprovalPayload_Blocks(string? payload)
    {
        var context = BuildTool();

        var result = await context.Tool.SignalWorkflowAsync("workflow-1", "merge-approval", payload);

        Assert.Contains("Error:", result);
        Assert.Null(context.SentSignal);
    }

    // ── #430: CTO design-approval feedback ──────────────────────────────────

    private const string FeedbackNonce = "design-nonce-430";
    private const string ExactDesignFeedback =
        "{\"Decision\":\"changes_requested\",\"Comment\":\"" + FeedbackNonce + "\"}";

    private static void AssertNonceNeverLogged(ToolContext context) =>
        Assert.DoesNotContain(context.Logger.Entries, entry => entry.Message.Contains(FeedbackNonce));

    private static void AssertBlockedDesignWarning(ToolContext context) =>
        Assert.Contains(context.Logger.Entries, entry =>
            entry.Level == LogLevel.Warning &&
            entry.Message.StartsWith("Blocked design-approval signal for workflow workflow-1;", StringComparison.Ordinal));

    /// <summary>AC1. The configured CTO, any casing of the name, at the gate: sent canonically.</summary>
    [Theory]
    [InlineData("design-approval")]
    [InlineData("DESIGN-APPROVAL")]
    [InlineData("Design-Approval")]
    public async Task SignalWorkflowAsync_CtoDesignFeedbackAtGate_SendsCanonicalSignalAndPayload(string signalName)
    {
        var context = BuildTool(ctoAgent: "cto-agent", caller: "CTO-AGENT");

        var result = await context.Tool.SignalWorkflowAsync("workflow-1", signalName, ExactDesignFeedback);

        Assert.Equal("design-approval", context.SentSignal);
        var arg = Assert.IsType<JsonElement>(Assert.Single(context.SentArgs!));
        Assert.Equal("changes_requested", arg.GetProperty("Decision").GetString());
        Assert.Equal(FeedbackNonce, arg.GetProperty("Comment").GetString());
        var json = JsonDocument.Parse(result).RootElement;
        Assert.Equal("signalled", json.GetProperty("status").GetString());
        Assert.Equal("design-approval", json.GetProperty("signalName").GetString());
        Assert.Equal(1, context.GateReader.Calls);
        Assert.Contains(context.Logger.Entries, entry =>
            entry.Level == LogLevel.Information &&
            entry.Message.Contains("workflow-1") &&
            entry.Message.Contains("design-approval") &&
            entry.Message.Contains("changes_requested") &&
            entry.Message.Contains("CTO-AGENT"));
        AssertNonceNeverLogged(context);
    }

    /// <summary>AC2. Every blocked payload row of the matrix, for design-approval.</summary>
    [Theory]
    [InlineData("{\"Decision\":\"approved\",\"Comment\":\"" + FeedbackNonce + "\"}", "Decision must be exactly 'changes_requested'")]
    [InlineData("{\"Decision\":\"rejected\",\"Comment\":\"" + FeedbackNonce + "\"}", "Decision must be exactly 'changes_requested'")]
    [InlineData("{\"Decision\":\"Changes_Requested\",\"Comment\":\"" + FeedbackNonce + "\"}", "Decision must be exactly 'changes_requested'")]
    [InlineData("{\"Decision\":\"CHANGES_REQUESTED\",\"Comment\":\"" + FeedbackNonce + "\"}", "Decision must be exactly 'changes_requested'")]
    [InlineData("{\"Decision\":\"changes requested\",\"Comment\":\"" + FeedbackNonce + "\"}", "Decision must be exactly 'changes_requested'")]
    [InlineData("{\"Decision\":1,\"Comment\":\"" + FeedbackNonce + "\"}", "Decision must be exactly 'changes_requested'")]
    [InlineData("{\"Decision\":\"changes_requested\"}", "Comment must be a nonblank string")]
    [InlineData("{\"Decision\":\"changes_requested\",\"Comment\":null}", "Comment must be a nonblank string")]
    [InlineData("{\"Decision\":\"changes_requested\",\"Comment\":\"\"}", "Comment must be a nonblank string")]
    [InlineData("{\"Decision\":\"changes_requested\",\"Comment\":\"   \"}", "Comment must be a nonblank string")]
    [InlineData("{\"Decision\":\"changes_requested\",\"Comment\":1}", "Comment must be a nonblank string")]
    [InlineData(null, "the payload must be a JSON object")]
    [InlineData("null", "the payload must be a JSON object")]
    [InlineData("[]", "the payload must be a JSON object")]
    [InlineData("\"changes_requested\"", "the payload must be a JSON object")]
    [InlineData("{\"Comment\":\"" + FeedbackNonce + "\"}", "Decision must be exactly 'changes_requested'")]
    public async Task SignalWorkflowAsync_InvalidDesignFeedbackPayload_Blocks(string? payload, string expectedReason)
    {
        var context = BuildTool();

        var result = await context.Tool.SignalWorkflowAsync("workflow-1", "design-approval", payload);

        Assert.Contains("'design-approval' remains a CEO-only gate", result);
        Assert.Contains(expectedReason, result);
        Assert.Null(context.SentSignal);
        Assert.Equal(0, context.GateReader.Calls);
        AssertBlockedDesignWarning(context);
        AssertNonceNeverLogged(context);
    }

    /// <summary>AC3. Identity and config are checked first and never reach Temporal.</summary>
    [Theory]
    [InlineData("cto-agent", "other-agent", "the caller is not the configured CTO agent")]
    [InlineData("cto-agent", null, "the caller identity is unresolved")]
    [InlineData("cto-agent", "", "the caller identity is unresolved")]
    [InlineData("cto-agent", "   ", "the caller identity is unresolved")]
    [InlineData("", "cto-agent", "the configured CTO agent is not configured")]
    [InlineData("   ", "cto-agent", "the configured CTO agent is not configured")]
    public async Task SignalWorkflowAsync_DesignFeedbackIdentityOrConfigInvalid_Blocks(
        string ctoAgent,
        string? caller,
        string expectedReason)
    {
        var context = BuildTool(ctoAgent: ctoAgent, caller: caller);

        var result = await context.Tool.SignalWorkflowAsync("workflow-1", "design-approval", ExactDesignFeedback);

        Assert.Contains(expectedReason, result);
        Assert.Null(context.SentSignal);
        Assert.Equal(0, context.GateReader.Calls);
        AssertBlockedDesignWarning(context);
        AssertNonceNeverLogged(context);
    }

    /// <summary>AC4. Malformed JSON: the invalid-JSON error, a Warning with decision unavailable.</summary>
    [Fact]
    public async Task SignalWorkflowAsync_MalformedDesignFeedbackArgs_ReturnsInvalidJsonError()
    {
        var context = BuildTool();

        var result = await context.Tool.SignalWorkflowAsync("workflow-1", "design-approval", "{not json");

        Assert.StartsWith("Error: invalid JSON in args — ", result);
        Assert.Null(context.SentSignal);
        Assert.Equal(0, context.GateReader.Calls);
        Assert.Contains(context.Logger.Entries, entry =>
            entry.Level == LogLevel.Warning &&
            entry.Message.Contains("Blocked design-approval signal") &&
            entry.Message.Contains("decision=unavailable"));
    }

    /// <summary>AC2b. Running but not reporting the gate phase: refused, nothing sent.</summary>
    [Theory]
    [InlineData(null, "phase=missing")]
    [InlineData("", "phase=missing")]
    [InlineData("consensus", "phase=consensus")]
    [InlineData("revise", "phase=revise")]
    [InlineData("Design-Approval", "phase=Design-Approval")]
    public async Task SignalWorkflowAsync_DesignFeedbackRunningOffGate_Blocks(string? phase, string expectedPhase)
    {
        var context = BuildTool();
        context.GateReader.Phase = phase;

        var result = await context.Tool.SignalWorkflowAsync("workflow-1", "design-approval", ExactDesignFeedback);

        Assert.Contains(
            $"the workflow is not waiting at the design-approval gate (status=Running, {expectedPhase})",
            result);
        Assert.Null(context.SentSignal);
        Assert.Equal(1, context.GateReader.Calls);
        AssertBlockedDesignWarning(context);
        AssertNonceNeverLogged(context);
    }

    /// <summary>AC2b. Any non-running status blocks, even when Phase still says design-approval.</summary>
    [Theory]
    [InlineData(WorkflowExecutionStatus.Completed)]
    [InlineData(WorkflowExecutionStatus.Failed)]
    [InlineData(WorkflowExecutionStatus.Canceled)]
    [InlineData(WorkflowExecutionStatus.Terminated)]
    [InlineData(WorkflowExecutionStatus.TimedOut)]
    [InlineData(WorkflowExecutionStatus.ContinuedAsNew)]
    [InlineData(WorkflowExecutionStatus.Paused)]
    public async Task SignalWorkflowAsync_DesignFeedbackNotRunning_Blocks(WorkflowExecutionStatus status)
    {
        var context = BuildTool();
        context.GateReader.Status = status;

        var result = await context.Tool.SignalWorkflowAsync("workflow-1", "design-approval", ExactDesignFeedback);

        Assert.Contains(
            $"the workflow is not waiting at the design-approval gate (status={status}, phase=design-approval)",
            result);
        Assert.Null(context.SentSignal);
        AssertBlockedDesignWarning(context);
        AssertNonceNeverLogged(context);
    }

    /// <summary>AC2b. A failed lookup blocks, and only the exception type is echoed.</summary>
    [Fact]
    public async Task SignalWorkflowAsync_DesignGateLookupRpcError_BlocksWithoutEchoingDetail()
    {
        var context = BuildTool();
        context.GateReader.Throws = new Temporalio.Exceptions.RpcException(
            Temporalio.Exceptions.RpcException.StatusCode.NotFound,
            "lookup-detail-must-not-be-echoed",
            Array.Empty<byte>());

        var result = await context.Tool.SignalWorkflowAsync("workflow-1", "design-approval", ExactDesignFeedback);

        Assert.Contains("the workflow gate could not be verified (RpcException)", result);
        Assert.DoesNotContain("lookup-detail-must-not-be-echoed", result);
        Assert.Null(context.SentSignal);
        AssertBlockedDesignWarning(context);
        AssertNonceNeverLogged(context);
    }

    [Fact]
    public async Task SignalWorkflowAsync_DesignGateLookupInvalidOperation_Blocks()
    {
        var context = BuildTool();
        context.GateReader.Throws = new InvalidOperationException("lookup-detail-must-not-be-echoed");

        var result = await context.Tool.SignalWorkflowAsync("workflow-1", "design-approval", ExactDesignFeedback);

        Assert.Contains("the workflow gate could not be verified (InvalidOperationException)", result);
        Assert.DoesNotContain("lookup-detail-must-not-be-echoed", result);
        Assert.Null(context.SentSignal);
        AssertBlockedDesignWarning(context);
    }

    /// <summary>
    /// CEO feedback F1. A describe that times out (the reader's 10-second RPC deadline, or a
    /// client-side timeout) is refused like any other failed lookup: no signal, one Warning, and
    /// neither the error detail nor the Comment in the return or any log line.
    /// </summary>
    public static TheoryData<Exception, string> DescribeTimeouts => new()
    {
        {
            new Temporalio.Exceptions.RpcException(
                Temporalio.Exceptions.RpcException.StatusCode.DeadlineExceeded,
                "timeout-detail-must-not-be-echoed",
                Array.Empty<byte>()),
            "RpcException"
        },
        { new TimeoutException("timeout-detail-must-not-be-echoed"), "TimeoutException" },
    };

    [Theory]
    [MemberData(nameof(DescribeTimeouts))]
    public async Task SignalWorkflowAsync_DesignGateLookupTimesOut_BlocksWithZeroSignals(
        Exception timeout,
        string expectedType)
    {
        var context = BuildTool();
        context.GateReader.Throws = timeout;

        var result = await context.Tool.SignalWorkflowAsync("workflow-1", "design-approval", ExactDesignFeedback);

        Assert.Contains($"the workflow gate could not be verified ({expectedType})", result);
        Assert.DoesNotContain("timeout-detail-must-not-be-echoed", result);
        Assert.DoesNotContain(FeedbackNonce, result);
        Assert.Null(context.SentSignal);
        await context.Handle.DidNotReceiveWithAnyArgs().SignalAsync(default!, default!, default);
        Assert.Single(context.Logger.Entries, entry =>
            entry.Level == LogLevel.Warning &&
            entry.Message.StartsWith("Blocked design-approval signal for workflow workflow-1;", StringComparison.Ordinal));
        Assert.DoesNotContain(context.Logger.Entries, entry => entry.Message.Contains("timeout-detail-must-not-be-echoed"));
        AssertNonceNeverLogged(context);
    }

    /// <summary>AC2b. merge-approval never describes the workflow, whatever its state would say.</summary>
    [Fact]
    public async Task SignalWorkflowAsync_MergeFeedback_NeverReadsGateState()
    {
        var context = BuildTool();
        context.GateReader.Throws = new InvalidOperationException("must not be called");

        var result = await context.Tool.SignalWorkflowAsync(
            "workflow-1",
            "merge-approval",
            "{\"Decision\":\"changes_requested\",\"Comment\":\"fix it\"}");

        Assert.Equal("merge-approval", context.SentSignal);
        Assert.Equal("signalled", JsonDocument.Parse(result).RootElement.GetProperty("status").GetString());
        Assert.Equal(0, context.GateReader.Calls);
    }

    /// <summary>
    /// AC6. The merge-approval strings are pinned to their pre-#430 text, rendered and template
    /// alike, so a gate-parametrised refactor cannot drift them.
    /// </summary>
    [Fact]
    public async Task SignalWorkflowAsync_MergeFeedbackStrings_AreUnchanged()
    {
        const string allowedTemplate =
            "Allowed merge-approval signal for workflow {WorkflowId}; signal={Signal}; decision={Decision}; caller={Caller}";
        const string blockedTemplate =
            "Blocked merge-approval signal for workflow {WorkflowId}; signal={Signal}; decision={Decision}; caller={Caller}";

        var allowed = BuildTool();
        await allowed.Tool.SignalWorkflowAsync(
            "workflow-1",
            "merge-approval",
            "{\"Decision\":\"changes_requested\",\"Comment\":\"fix it\"}");
        var allowedEntry = Assert.Single(allowed.Logger.Entries);
        Assert.Equal(LogLevel.Information, allowedEntry.Level);
        Assert.Equal(
            "Allowed merge-approval signal for workflow workflow-1; signal=merge-approval; decision=changes_requested; caller=cto-agent",
            allowedEntry.Message);
        Assert.Equal(allowedTemplate, allowedEntry.Template);

        var blocked = BuildTool();
        var error = await blocked.Tool.SignalWorkflowAsync(
            "workflow-1",
            "merge-approval",
            "{\"Decision\":\"approved\",\"Comment\":\"fix it\"}");
        var blockedEntry = Assert.Single(blocked.Logger.Entries);
        Assert.Equal(LogLevel.Warning, blockedEntry.Level);
        Assert.Equal(
            "Blocked merge-approval signal for workflow workflow-1; signal=merge-approval; decision=approved; caller=cto-agent",
            blockedEntry.Message);
        Assert.Equal(blockedTemplate, blockedEntry.Template);
        Assert.Equal(
            "Error: 'merge-approval' remains a CEO-only gate because Decision must be exactly 'changes_requested'. " +
            "Only the configured CTO agent may send Decision 'changes_requested' with a nonblank Comment via this tool.",
            error);
    }

    /// <summary>AC7. The shared reserved list is untouched: still exactly four names.</summary>
    [Fact]
    public void CeoGateSignals_StillReservesDesignApproval()
    {
        Assert.True(CeoGateSignals.IsReserved("design-approval"));
        Assert.Equal(
            new[] { "advisory-review", "design-approval", "doc-review", "merge-approval" },
            CeoGateSignals.All.Order(StringComparer.Ordinal).ToArray());
    }

    /// <summary>AC8. A padded name is not the exception; it falls to the reserved-list refusal.</summary>
    [Theory]
    [InlineData(" design-approval ")]
    [InlineData(" merge-approval ")]
    public async Task SignalWorkflowAsync_PaddedFeedbackGateName_BlockedByReservedList(string signalName)
    {
        var context = BuildTool();

        var result = await context.Tool.SignalWorkflowAsync("workflow-1", signalName, ExactDesignFeedback);

        Assert.Contains("is a CEO-only gate and cannot be sent via the MCP tool", result);
        Assert.Null(context.SentSignal);
        Assert.Equal(0, context.GateReader.Calls);
    }
}
