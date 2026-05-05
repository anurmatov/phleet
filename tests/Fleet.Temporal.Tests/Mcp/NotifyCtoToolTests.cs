using System.Text.Json;
using Fleet.Temporal.Configuration;
using Fleet.Temporal.Mcp;
using Microsoft.AspNetCore.Http;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Fleet.Temporal.Tests.Mcp;

public sealed class NotifyCtoToolTests
{
    private static NotifyCtoTool BuildTool(
        string ctoAgent,
        IWorkflowDispatcher? dispatcher = null,
        string? agentQueryParam = null)
    {
        var config = Substitute.For<CtoAgentConfigService>();
        config.GetCtoAgent().Returns(ctoAgent);

        dispatcher ??= Substitute.For<IWorkflowDispatcher>();

        var httpContext = new DefaultHttpContext();
        if (agentQueryParam is not null)
            httpContext.Request.QueryString = new QueryString($"?agent={agentQueryParam}");

        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns(httpContext);

        return new NotifyCtoTool(config, dispatcher, accessor);
    }

    [Fact]
    public async Task NotifyAsync_CtoAgentUnset_ReturnsToolError()
    {
        var tool = BuildTool(ctoAgent: "");

        var result = await tool.NotifyAsync("hello");

        var doc = JsonDocument.Parse(result);
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("FLEET_CTO_AGENT", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task NotifyAsync_CtoAgentWhitespaceOnly_ReturnsToolError()
    {
        var tool = BuildTool(ctoAgent: "   ");

        var result = await tool.NotifyAsync("hello");

        var doc = JsonDocument.Parse(result);
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("FLEET_CTO_AGENT", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task NotifyAsync_AgentParamMissing_CallerIsAnAgent_Succeeds()
    {
        var dispatcher = Substitute.For<IWorkflowDispatcher>();
        dispatcher.FireAndForgetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("wf-123");

        // No agentQueryParam → sender falls back to "an agent"
        var tool = BuildTool(ctoAgent: "acto", dispatcher: dispatcher, agentQueryParam: null);

        var result = await tool.NotifyAsync("some message");

        var doc = JsonDocument.Parse(result);
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());

        // TaskDescription must contain "[notification from an agent]"
        await dispatcher.Received(1).FireAndForgetAsync(
            "acto",
            Arg.Is<string>(s => s.Contains("[notification from an agent]")),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("acto", "acto")]
    [InlineData("ACTO", "acto")]
    [InlineData("acto", "ACTO")]
    public async Task NotifyAsync_SelfNotification_ReturnsToolError(string ctoAgent, string senderParam)
    {
        var tool = BuildTool(ctoAgent: ctoAgent, agentQueryParam: senderParam);

        var result = await tool.NotifyAsync("ping");

        var doc = JsonDocument.Parse(result);
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("self-notification", doc.RootElement.GetProperty("error").GetString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("x")]   // valid 1-char edge
    public async Task NotifyAsync_EmptyMessage_ReturnsToolError(string message)
    {
        // Empty only — "x" is valid so only test ""
        if (message == "x") return;

        var tool = BuildTool(ctoAgent: "acto", agentQueryParam: "adev");

        var result = await tool.NotifyAsync(message);

        var doc = JsonDocument.Parse(result);
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("at least 1", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task NotifyAsync_MessageTooLong_ReturnsToolError()
    {
        var tool = BuildTool(ctoAgent: "acto", agentQueryParam: "adev");
        var longMsg = new string('a', 2001);

        var result = await tool.NotifyAsync(longMsg);

        var doc = JsonDocument.Parse(result);
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("too long", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task NotifyAsync_MessageExactly2000Chars_Succeeds()
    {
        var dispatcher = Substitute.For<IWorkflowDispatcher>();
        dispatcher.FireAndForgetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("wf-abc");

        var tool = BuildTool(ctoAgent: "acto", dispatcher: dispatcher, agentQueryParam: "adev");
        var msg = new string('a', 2000);

        var result = await tool.NotifyAsync(msg);

        var doc = JsonDocument.Parse(result);
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task NotifyAsync_DispatcherThrows_ReturnsToolError_DoesNotRethrow()
    {
        var dispatcher = Substitute.For<IWorkflowDispatcher>();
        dispatcher.FireAndForgetAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("temporal unavailable"));

        var tool = BuildTool(ctoAgent: "acto", dispatcher: dispatcher, agentQueryParam: "adev");

        var result = await tool.NotifyAsync("help");

        var doc = JsonDocument.Parse(result);
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("temporal unavailable", doc.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task NotifyAsync_HappyPath_ReturnsWorkflowId_CorrectPayload()
    {
        var dispatcher = Substitute.For<IWorkflowDispatcher>();
        dispatcher.FireAndForgetAsync("acto", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("notify-cto-12345");

        var tool = BuildTool(ctoAgent: "acto", dispatcher: dispatcher, agentQueryParam: "adev");

        var result = await tool.NotifyAsync("tool X keeps returning 500");

        var doc = JsonDocument.Parse(result);
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("notify-cto-12345", doc.RootElement.GetProperty("workflow_id").GetString());

        await dispatcher.Received(1).FireAndForgetAsync(
            "acto",
            Arg.Is<string>(s =>
                s.StartsWith("[notification from adev] tool X keeps returning 500") &&
                s.Contains("ACTION: do NOT forward verbatim") &&
                s.Contains("Triage as follows")),
            Arg.Any<CancellationToken>());
    }
}
