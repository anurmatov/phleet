using System.Text.Json;
using Fleet.Temporal.Configuration;
using Fleet.Temporal.Mcp;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Temporalio.Client;

namespace Fleet.Temporal.Tests.Mcp;

public sealed class TemporalWorkflowToolsTests
{
    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    private sealed class ToolContext
    {
        public TemporalWorkflowTools Tool { get; set; } = null!;
        public required WorkflowHandle Handle { get; init; }
        public required RecordingLogger<TemporalWorkflowTools> Logger { get; init; }
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
    [InlineData("design-approval")]
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
}
