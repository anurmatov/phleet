using System.Net;
using System.Net.Http.Headers;
using System.Text;
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

/// <summary>
/// AC-B1 (#436): the configured CTO's <c>approved</c> with a <c>GrantId</c> on a delegable gate is
/// forwarded to the orchestrator exactly once and never signalled by the bridge; everything else
/// keeps today's path.
/// </summary>
public sealed class EpicGrantForwardTests
{
    private const string GrantId = "11111111-2222-3333-4444-555555555555";
    private const string EvidenceUrl = "https://example.com/evidence/review-1";
    private const string ReviewRef = "0123456789abcdef0123456789abcdef01234567";

    private static string DelegatedPayload(
        string visitId = "merge-approval:2", string grantField = "GrantId", string grantValue = "\"" + GrantId + "\"") =>
        $"{{\"Decision\":\"approved\",\"{grantField}\":{grantValue},\"VisitId\":\"{visitId}\"," +
        $"\"ArtifactRef\":\"{ReviewRef}\",\"Evidence\":\"{EvidenceUrl}\"}}";

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    private sealed class FakeGateStateReader : IWorkflowGateStateReader
    {
        public int Calls { get; private set; }

        public Task<(WorkflowExecutionStatus Status, string? Phase)> ReadAsync(
            WorkflowHandle handle, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult((WorkflowExecutionStatus.Running, (string?)"design-approval"));
        }
    }

    private sealed class FakeForwarder : IEpicGrantDecisionForwarder
    {
        public List<(string GrantId, EpicGrantDecisionRequest Request)> Calls { get; } = [];
        public EpicGrantDecisionResult Result { get; set; } =
            new() { Result = "sent", DecisionId = 7, StatusCode = 200 };
        public Exception? Throws { get; set; }

        public Task<EpicGrantDecisionResult> ForwardAsync(
            string grantId, EpicGrantDecisionRequest request, CancellationToken cancellationToken = default)
        {
            Calls.Add((grantId, request));
            if (Throws is not null) throw Throws;
            return Task.FromResult(Result);
        }
    }

    /// <summary>A stand-in for the orchestrator: records every request and answers as told.</summary>
    private sealed class RecordingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
        : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Uri, string? Body, string? MediaType, string? Authorization)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((
                request.Method,
                request.RequestUri!.AbsoluteUri,
                body,
                request.Content?.Headers.ContentType?.MediaType,
                request.Headers.Authorization?.ToString()));
            return await respond(request, cancellationToken);
        }

        public static RecordingHandler Json(HttpStatusCode status, string json) =>
            new((_, _) => Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            }));
    }

    private static OrchestratorEpicGrantForwarder RealForwarder(
        RecordingHandler handler, bool withBaseAddress = true, TimeSpan? timeout = null)
    {
        var client = new HttpClient(handler) { Timeout = timeout ?? TimeSpan.FromSeconds(30) };
        if (withBaseAddress)
        {
            client.BaseAddress = new Uri("https://example.com/");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-admin-token");
        }

        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(OrchestratorEpicGrantForwarder.HttpClientName).Returns(client);
        return new OrchestratorEpicGrantForwarder(factory);
    }

    private sealed class ToolContext
    {
        public TemporalWorkflowTools Tool { get; set; } = null!;
        public required ITemporalClientFactory ClientFactory { get; init; }
        public required WorkflowHandle Handle { get; init; }
        public required RecordingLogger<TemporalWorkflowTools> Logger { get; init; }
        public required FakeGateStateReader GateReader { get; init; }
        public string? SentSignal { get; set; }
        public IReadOnlyCollection<object?>? SentArgs { get; set; }

        public string AllLogText => string.Join("\n", Logger.Entries.Select(e => e.Message));
    }

    private static ToolContext BuildTool(
        IEpicGrantDecisionForwarder forwarder, string ctoAgent = "cto-agent", string? caller = "cto-agent")
    {
        var client = Substitute.For<ITemporalClient>();
        var handle = Substitute.For<WorkflowHandle>(client, "workflow-1", null!, null!, null!);
        var clientFactory = Substitute.For<ITemporalClientFactory>();
        var context = new ToolContext
        {
            ClientFactory = clientFactory,
            Handle = handle,
            Logger = new RecordingLogger<TemporalWorkflowTools>(),
            GateReader = new FakeGateStateReader(),
        };

        handle.SignalAsync(
                Arg.Do<string>(value => context.SentSignal = value),
                Arg.Do<IReadOnlyCollection<object?>>(value => context.SentArgs = value),
                Arg.Any<WorkflowSignalOptions?>())
            .Returns(Task.CompletedTask);
        client.GetWorkflowHandle(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>()).Returns(handle);
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
            httpClientFactory, Options.Create(new TemporalBridgeOptions()), NullLogger<WorkflowTypeRegistry>.Instance);

        context.Tool = new TemporalWorkflowTools(
            clientFactory, registry, ctoConfig, context.GateReader, forwarder, accessor, context.Logger);
        return context;
    }

    private static async Task AssertNothingSignalledAsync(ToolContext context)
    {
        Assert.Null(context.SentSignal);
        await context.Handle.DidNotReceiveWithAnyArgs().SignalAsync(default!, default!, default);
    }

    // ── Forwarded: exactly once, the contract body, and no signal from the bridge ─

    [Fact]
    public async Task CtoApprovedWithGrantId_IsForwardedOnceWithTheContractBody_AndTheBridgeSignalsNothing()
    {
        var handler = RecordingHandler.Json(HttpStatusCode.OK, """{"result":"sent","decisionId":42,"reason":null}""");
        var context = BuildTool(RealForwarder(handler), ctoAgent: "cto-agent", caller: "CTO-AGENT");

        var result = await context.Tool.SignalWorkflowAsync("workflow-1", "merge-approval", DelegatedPayload());

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal($"https://example.com/api/epic-grants/{GrantId}/decisions", request.Uri);
        Assert.Equal("application/json", request.MediaType);
        Assert.Equal("Bearer test-admin-token", request.Authorization);
        Assert.Equal(
            "{\"namespace\":\"fleet\",\"workflowId\":\"workflow-1\",\"gate\":\"merge-approval\"," +
            "\"decision\":\"approved\",\"visitId\":\"merge-approval:2\"," +
            $"\"artifactRef\":\"{ReviewRef}\",\"evidence\":\"{EvidenceUrl}\",\"caller\":\"CTO-AGENT\"}}",
            request.Body);

        var json = JsonDocument.Parse(result).RootElement;
        Assert.Equal("workflow-1", json.GetProperty("workflowId").GetString());
        Assert.Equal("merge-approval", json.GetProperty("signalName").GetString());
        Assert.Equal("delegated", json.GetProperty("status").GetString());
        Assert.Equal("sent", json.GetProperty("result").GetString());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("reason").ValueKind);

        await AssertNothingSignalledAsync(context);
        await context.ClientFactory.DidNotReceiveWithAnyArgs().GetClientAsync(default!);

        var entry = Assert.Single(context.Logger.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Contains("workflow-1", entry.Message);
        Assert.Contains("merge-approval", entry.Message);
        Assert.Contains("decision=approved", entry.Message);
        Assert.Contains("caller=CTO-AGENT", entry.Message);
        Assert.Contains($"grant={GrantId}", entry.Message);
        Assert.DoesNotContain(EvidenceUrl, context.AllLogText);
    }

    [Theory]
    [InlineData("merge-approval", "merge-approval")]
    [InlineData("design-approval", "design-approval")]
    [InlineData("Design-Approval", "design-approval")]
    [InlineData("doc-review", "doc-review")]
    [InlineData("DOC-REVIEW", "doc-review")]
    public async Task EveryDelegableGate_IsForwardedUnderItsCanonicalName(string signalName, string canonical)
    {
        var forwarder = new FakeForwarder();
        var context = BuildTool(forwarder);

        var result = await context.Tool.SignalWorkflowAsync(
            "workflow-1", signalName, DelegatedPayload(visitId: canonical + ":1"));

        var call = Assert.Single(forwarder.Calls);
        Assert.Equal(GrantId, call.GrantId);
        Assert.Equal(
            new EpicGrantDecisionRequest("fleet", "workflow-1", canonical, "approved",
                canonical + ":1", ReviewRef, EvidenceUrl, "cto-agent"),
            call.Request);
        Assert.Equal(canonical, JsonDocument.Parse(result).RootElement.GetProperty("signalName").GetString());
        // No describe either: the orchestrator checks the gate visit itself.
        Assert.Equal(0, context.GateReader.Calls);
        await AssertNothingSignalledAsync(context);
    }

    [Fact]
    public async Task TheNamespaceArgument_IsForwarded()
    {
        var forwarder = new FakeForwarder();
        var context = BuildTool(forwarder);

        await context.Tool.SignalWorkflowAsync("workflow-1", "merge-approval", DelegatedPayload(), "other-ns");

        Assert.Equal("other-ns", Assert.Single(forwarder.Calls).Request.Namespace);
    }

    [Theory]
    [InlineData("grantid")]
    [InlineData("GRANTID")]
    [InlineData("grantId")]
    public async Task GrantId_InAnyCasing_IsForwarded(string field)
    {
        var forwarder = new FakeForwarder();
        var context = BuildTool(forwarder);

        await context.Tool.SignalWorkflowAsync("workflow-1", "merge-approval", DelegatedPayload(grantField: field));

        Assert.Equal(GrantId, Assert.Single(forwarder.Calls).GrantId);
    }

    [Fact]
    public async Task MissingOrNonStringFields_AreForwardedAsEmptyStrings_ForTheOrchestratorToRefuse()
    {
        var forwarder = new FakeForwarder();
        var context = BuildTool(forwarder);

        await context.Tool.SignalWorkflowAsync(
            "workflow-1", "merge-approval",
            $"{{\"Decision\":\"approved\",\"GrantId\":\"{GrantId}\",\"VisitId\":2}}");

        var request = Assert.Single(forwarder.Calls).Request;
        Assert.Equal("", request.VisitId);
        Assert.Equal("", request.ArtifactRef);
        Assert.Equal("", request.Evidence);
    }

    [Fact]
    public async Task AnOrchestratorRefusal_IsReturnedAsDelegated_AndNothingIsSignalled()
    {
        var forwarder = new FakeForwarder { Result = new() { Result = "refused", Reason = "stale_visit", StatusCode = 409 } };
        var context = BuildTool(forwarder);

        var result = await context.Tool.SignalWorkflowAsync("workflow-1", "merge-approval", DelegatedPayload());

        var json = JsonDocument.Parse(result).RootElement;
        Assert.Equal("delegated", json.GetProperty("status").GetString());
        Assert.Equal("refused", json.GetProperty("result").GetString());
        Assert.Equal("stale_visit", json.GetProperty("reason").GetString());
        Assert.Single(forwarder.Calls);
        await AssertNothingSignalledAsync(context);
        Assert.Single(context.Logger.Entries);
    }

    // ── Not delegated: today's path, zero forwards ────────────────────────────────

    [Theory]
    [InlineData("merge-approval", "Decision must be exactly 'changes_requested'")]
    [InlineData("design-approval", "Decision must be exactly 'changes_requested'")]
    [InlineData("doc-review", "is a CEO-only gate and cannot be sent via the MCP tool")]
    public async Task CtoApprovedWithoutGrantId_KeepsTheExistingRefusal_ZeroForwards(string gate, string expected)
    {
        var forwarder = new FakeForwarder();
        var context = BuildTool(forwarder);

        var result = await context.Tool.SignalWorkflowAsync(
            "workflow-1", gate,
            $"{{\"Decision\":\"approved\",\"VisitId\":\"{gate}:1\",\"ArtifactRef\":\"{ReviewRef}\",\"Evidence\":\"{EvidenceUrl}\"}}");

        Assert.Contains(expected, result);
        Assert.Empty(forwarder.Calls);
        await AssertNothingSignalledAsync(context);
    }

    [Fact]
    public async Task CtoApprovedWithoutGrantId_OnMergeApproval_IsTheExactPreExistingRefusal()
    {
        var forwarder = new FakeForwarder();
        var context = BuildTool(forwarder);

        var result = await context.Tool.SignalWorkflowAsync(
            "workflow-1", "merge-approval", "{\"Decision\":\"approved\",\"Comment\":\"fix it\"}");

        Assert.Equal(
            "Error: 'merge-approval' remains a CEO-only gate because Decision must be exactly 'changes_requested'. " +
            "Only the configured CTO agent may send Decision 'changes_requested' with a nonblank Comment via this tool.",
            result);
        var entry = Assert.Single(context.Logger.Entries);
        Assert.Equal(
            "Blocked merge-approval signal for workflow workflow-1; signal=merge-approval; decision=approved; caller=cto-agent",
            entry.Message);
        Assert.Empty(forwarder.Calls);
    }

    [Theory]
    [InlineData("Approved")]
    [InlineData("APPROVED")]
    [InlineData(" approved")]
    public async Task ANonOrdinalApprovedDecision_WithGrantId_IsNotDelegated(string decision)
    {
        var forwarder = new FakeForwarder();
        var context = BuildTool(forwarder);

        var result = await context.Tool.SignalWorkflowAsync(
            "workflow-1", "merge-approval", $"{{\"Decision\":\"{decision}\",\"GrantId\":\"{GrantId}\"}}");

        Assert.Contains("Decision must be exactly 'changes_requested'", result);
        Assert.Empty(forwarder.Calls);
        await AssertNothingSignalledAsync(context);
    }

    [Fact]
    public async Task AdvisoryReviewWithGrantId_IsStillRefusedAsCeoOnly()
    {
        var forwarder = new FakeForwarder();
        var context = BuildTool(forwarder);

        var result = await context.Tool.SignalWorkflowAsync(
            "workflow-1", "advisory-review", DelegatedPayload(visitId: "advisory-review:1"));

        Assert.Contains("'advisory-review' is a CEO-only gate and cannot be sent via the MCP tool", result);
        Assert.Empty(forwarder.Calls);
        await AssertNothingSignalledAsync(context);
    }

    [Theory]
    [InlineData(" merge-approval ")]
    [InlineData("doc-review ")]
    public async Task APaddedGateName_WithGrantId_IsRefusedByTheReservedList(string signalName)
    {
        var forwarder = new FakeForwarder();
        var context = BuildTool(forwarder);

        var result = await context.Tool.SignalWorkflowAsync("workflow-1", signalName, DelegatedPayload());

        Assert.Contains("is a CEO-only gate and cannot be sent via the MCP tool", result);
        Assert.Empty(forwarder.Calls);
        await AssertNothingSignalledAsync(context);
    }

    [Fact]
    public async Task ChangesRequestedWithGrantId_FollowsTodaysFeedbackPath()
    {
        var forwarder = new FakeForwarder();
        var context = BuildTool(forwarder);
        const string payload = "{\"Decision\":\"changes_requested\",\"Comment\":\"fix it\",\"GrantId\":\"" + GrantId + "\"}";

        var result = await context.Tool.SignalWorkflowAsync("workflow-1", "merge-approval", payload);

        Assert.Empty(forwarder.Calls);
        Assert.Equal("merge-approval", context.SentSignal);
        var arg = Assert.IsType<JsonElement>(Assert.Single(context.SentArgs!));
        Assert.Equal("changes_requested", arg.GetProperty("Decision").GetString());
        Assert.Equal("signalled", JsonDocument.Parse(result).RootElement.GetProperty("status").GetString());
        Assert.Equal(
            "Allowed merge-approval signal for workflow workflow-1; signal=merge-approval; decision=changes_requested; caller=cto-agent",
            Assert.Single(context.Logger.Entries).Message);
    }

    [Fact]
    public async Task ChangesRequestedWithGrantId_OnDocReview_StaysCeoOnly()
    {
        var forwarder = new FakeForwarder();
        var context = BuildTool(forwarder);

        var result = await context.Tool.SignalWorkflowAsync(
            "workflow-1", "doc-review",
            "{\"Decision\":\"changes_requested\",\"Comment\":\"fix it\",\"GrantId\":\"" + GrantId + "\"}");

        Assert.Contains("'doc-review' is a CEO-only gate", result);
        Assert.Empty(forwarder.Calls);
        await AssertNothingSignalledAsync(context);
    }

    [Fact]
    public async Task MalformedArgs_MentioningGrantId_KeepTheExistingInvalidJsonError()
    {
        var forwarder = new FakeForwarder();
        var context = BuildTool(forwarder);

        var result = await context.Tool.SignalWorkflowAsync(
            "workflow-1", "merge-approval", "{\"Decision\":\"approved\",\"GrantId\":");

        Assert.StartsWith("Error: invalid JSON in args — ", result);
        Assert.Empty(forwarder.Calls);
        await AssertNothingSignalledAsync(context);
    }

    // ── Delegated, but refused by the bridge: zero forwards ───────────────────────

    [Theory]
    [InlineData("another-agent", "the caller is not the configured CTO agent")]
    [InlineData(null, "the caller identity is unresolved")]
    [InlineData("", "the caller identity is unresolved")]
    [InlineData("   ", "the caller identity is unresolved")]
    public async Task ANonCtoOrUnresolvedCaller_WithGrantId_IsRefused_ZeroForwards(string? caller, string expected)
    {
        var forwarder = new FakeForwarder();
        var context = BuildTool(forwarder, caller: caller);

        var result = await context.Tool.SignalWorkflowAsync("workflow-1", "merge-approval", DelegatedPayload());

        Assert.StartsWith("Error: the delegated 'merge-approval' approval was refused because " + expected, result);
        Assert.Contains("Nothing was sent", result);
        Assert.Empty(forwarder.Calls);
        await AssertNothingSignalledAsync(context);
        var entry = Assert.Single(context.Logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.DoesNotContain(EvidenceUrl, context.AllLogText);
    }

    [Theory]
    [InlineData("", "cto-agent")]
    [InlineData("   ", "cto-agent")]
    [InlineData("", null)]
    public async Task AnUnsetCto_IsRefusedBeforeTheCallerIsCompared_ZeroForwards(string ctoAgent, string? caller)
    {
        var forwarder = new FakeForwarder();
        var context = BuildTool(forwarder, ctoAgent: ctoAgent, caller: caller);

        var result = await context.Tool.SignalWorkflowAsync("workflow-1", "doc-review", DelegatedPayload("doc-review:1"));

        Assert.Contains("the configured CTO agent is not configured", result);
        Assert.Empty(forwarder.Calls);
        await AssertNothingSignalledAsync(context);
    }

    [Theory]
    [InlineData("\"\"")]
    [InlineData("\"   \"")]
    [InlineData("null")]
    [InlineData("1")]
    [InlineData("{}")]
    [InlineData("[\"" + GrantId + "\"]")]
    public async Task ABlankOrNonStringGrantId_IsAnError_ZeroForwards(string grantValue)
    {
        var forwarder = new FakeForwarder();
        var context = BuildTool(forwarder);

        var result = await context.Tool.SignalWorkflowAsync(
            "workflow-1", "merge-approval", DelegatedPayload(grantValue: grantValue));

        Assert.Contains("GrantId must be a nonblank string", result);
        Assert.Empty(forwarder.Calls);
        await AssertNothingSignalledAsync(context);
    }

    // ── Forward failures: an error, and nothing else ──────────────────────────────

    [Fact]
    public async Task AForwarderTransportError_ReturnsErrorText_AndSendsNothingElse()
    {
        var handler = new RecordingHandler((_, _) => throw new HttpRequestException("transport-detail-must-not-be-echoed"));
        var context = BuildTool(RealForwarder(handler));

        var result = await context.Tool.SignalWorkflowAsync("workflow-1", "merge-approval", DelegatedPayload());

        Assert.StartsWith("Error: the delegated 'merge-approval' approval could not be forwarded to the orchestrator", result);
        Assert.Contains("HttpRequestException", result);
        // Honest about a timeout: the bridge sent nothing and did not retry, but the orchestrator
        // may have; the caller is pointed at the decisions table instead of told "nothing was sent".
        Assert.Contains("This tool sent no signal and made no retry", result);
        Assert.Contains("decisions table", result);
        Assert.DoesNotContain("transport-detail-must-not-be-echoed", result);
        Assert.DoesNotContain("transport-detail-must-not-be-echoed", context.AllLogText);
        Assert.Single(handler.Requests);   // one attempt, no retry
        await AssertNothingSignalledAsync(context);
        await context.ClientFactory.DidNotReceiveWithAnyArgs().GetClientAsync(default!);
        Assert.Equal(LogLevel.Warning, Assert.Single(context.Logger.Entries).Level);
    }

    [Fact]
    public async Task AForwarderThatThrows_ReturnsErrorText_AndSendsNothingElse()
    {
        var forwarder = new FakeForwarder { Throws = new InvalidOperationException("detail") };
        var context = BuildTool(forwarder);

        var result = await context.Tool.SignalWorkflowAsync("workflow-1", "merge-approval", DelegatedPayload());

        Assert.StartsWith("Error: the delegated 'merge-approval' approval could not be forwarded", result);
        Assert.Single(forwarder.Calls);
        await AssertNothingSignalledAsync(context);
    }

    // ── OrchestratorEpicGrantForwarder ────────────────────────────────────────────

    private static EpicGrantDecisionRequest SampleRequest() =>
        new("fleet", "workflow-1", "doc-review", "approved", "doc-review:3", ReviewRef, EvidenceUrl, "cto-agent");

    [Fact]
    public async Task Forwarder_EscapesTheGrantIdIntoThePath()
    {
        var handler = RecordingHandler.Json(HttpStatusCode.Conflict, """{"result":"refused","reason":"grant_inactive"}""");

        await RealForwarder(handler).ForwardAsync("a/../b c", SampleRequest());

        Assert.Equal("https://example.com/api/epic-grants/a%2F..%2Fb%20c/decisions", Assert.Single(handler.Requests).Uri);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, """{"result":"sent","decisionId":42,"reason":null}""", "sent", null, 42L)]
    [InlineData(HttpStatusCode.Conflict, """{"result":"refused","reason":"already_decided"}""", "refused", "already_decided", null)]
    [InlineData(HttpStatusCode.BadGateway, """{"result":"send_failed","decisionId":9,"reason":"send_failed"}""", "send_failed", "send_failed", 9L)]
    [InlineData(HttpStatusCode.BadRequest, """{"result":"refused","reason":"bad_request"}""", "refused", "bad_request", null)]
    public async Task Forwarder_MapsEveryContractResponse(
        HttpStatusCode status, string body, string result, string? reason, long? decisionId)
    {
        var handler = RecordingHandler.Json(status, body);

        var outcome = await RealForwarder(handler).ForwardAsync(GrantId, SampleRequest());

        Assert.Null(outcome.Error);
        Assert.Equal(result, outcome.Result);
        Assert.Equal(reason, outcome.Reason);
        Assert.Equal(decisionId, outcome.DecisionId);
        Assert.Equal((int)status, outcome.StatusCode);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadGateway, "<html>bad gateway</html>", "non-JSON")]
    [InlineData(HttpStatusCode.OK, "", "non-JSON")]
    [InlineData(HttpStatusCode.Unauthorized, """{"error":"Unauthorized"}""", "no decision result")]
    [InlineData(HttpStatusCode.OK, """["sent"]""", "no decision result")]
    [InlineData(HttpStatusCode.OK, """{"result":1}""", "no decision result")]
    public async Task Forwarder_AnUnusableResponse_IsAnError(HttpStatusCode status, string body, string expected)
    {
        var handler = new RecordingHandler((_, _) => Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/html"),
        }));

        var outcome = await RealForwarder(handler).ForwardAsync(GrantId, SampleRequest());

        Assert.NotNull(outcome.Error);
        Assert.Contains(expected, outcome.Error);
        Assert.Contains($"HTTP {(int)status}", outcome.Error);
        Assert.Null(outcome.Result);
    }

    [Fact]
    public async Task Forwarder_ATimeout_IsAnError_AfterOneAttempt()
    {
        var handler = new RecordingHandler(async (_, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var outcome = await RealForwarder(handler, timeout: TimeSpan.FromMilliseconds(200))
            .ForwardAsync(GrantId, SampleRequest());

        Assert.Contains("timeout", outcome.Error);
        Assert.Null(outcome.Result);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Forwarder_ATransportFailure_IsAnError_AfterOneAttempt()
    {
        var handler = new RecordingHandler((_, _) => throw new HttpRequestException("detail"));

        var outcome = await RealForwarder(handler).ForwardAsync(GrantId, SampleRequest());

        Assert.Equal("the orchestrator could not be reached (HttpRequestException)", outcome.Error);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Forwarder_WithoutABaseAddress_IsAnError_AndMakesNoRequest()
    {
        var handler = RecordingHandler.Json(HttpStatusCode.OK, """{"result":"sent"}""");

        var outcome = await RealForwarder(handler, withBaseAddress: false).ForwardAsync(GrantId, SampleRequest());

        Assert.Equal("the orchestrator URL is not configured", outcome.Error);
        Assert.Empty(handler.Requests);
    }
}
