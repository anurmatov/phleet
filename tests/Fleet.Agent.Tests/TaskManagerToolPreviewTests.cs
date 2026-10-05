using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Fleet.Agent.Abstractions;
using Fleet.Agent.Configuration;
using Fleet.Agent.Models;
using Fleet.Agent.Services;
using Fleet.Agent.Tests.Harness;
using Fleet.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Fleet.Agent.Tests;

public class TaskManagerToolPreviewTests
{
    private const string Answer = "synthetic-sentinel";
    private static readonly DateTimeOffset Timestamp = DateTimeOffset.Parse("2026-01-01T12:34:56Z");

    private static AgentProgress Command(string command)
    {
        using var executor = new ExecutorScope();
        return executor.Value.BuildItemStartedProgressForTests(new JsonObject
        {
            ["item"] = new JsonObject { ["type"] = "commandExecution", ["command"] = command },
        })!;
    }

    [Fact]
    public async Task StartTask_LongScript_PreviewContainsCommandOnce()
    {
        var tool = Command("python3 - <<'EOF'\nprint('synthetic-sentinel')\n" + new string('x', 20_000));
        var run = await RunAsync(tool);
        var html = Assert.Single(run.Sink.Previews);
        var decoded = PreviewText(html);
        Assert.Equal("... " + tool.Summary, decoded);
        Assert.Equal(1, decoded.Split("python3", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain(tool.Summary + "(", decoded);
        Assert.True(decoded.Length <= 507);
        Assert.True(html.Length < 4096);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartTask_HtmlCommand_EncodesOnceBeforeSending(bool prefix)
    {
        var tool = Command("echo '<b> &amp; </blockquote>'");
        var run = await RunAsync(tool, prefix: prefix);
        Assert.Equal((prefix ? "<b>Test:</b>\n" : "") +
            "<blockquote expandable>" + WebUtility.HtmlEncode("... " + tool.Summary) + "</blockquote>",
            Assert.Single(run.Sink.Previews));
    }

    [Theory]
    [InlineData("mcpToolCall")]
    [InlineData("dynamicToolCall")]
    public async Task StartTask_McpUnderCap_KeepsExistingConsumersAndExcludesArgsFromBuffer(string itemType)
    {
        using var executor = new ExecutorScope();
        var args = new JsonObject { ["value"] = "synthetic-arg-sentinel" };
        var tool = executor.Value.BuildItemStartedProgressForTests(new JsonObject
        {
            ["item"] = new JsonObject { ["type"] = itemType, ["tool"] = "mcp__example__read", ["arguments"] = args },
        })!;
        var run = await RunAsync(tool);
        Assert.Equal("... Using mcp__example__read(" + args.ToJsonString() + ")", PreviewText(Assert.Single(run.Sink.Previews)));
        Assert.Equal("\n<blockquote expandable>read(" + WebUtility.HtmlEncode(args.ToJsonString()) + ")</blockquote>", run.Sink.Reply!.ToolBlockHtml);
        Assert.Equal(("mcp__example__read", "Using mcp__example__read"), Assert.Single(run.ToolUses));
        Assert.Equal("mcp__example__read", Assert.Single(run.Events.Tools).ToolName);
        Assert.DoesNotContain("synthetic-arg-sentinel", run.Buffer.FormatRecentToolUse());
        Assert.DoesNotContain("synthetic-arg-sentinel", Assert.Single(run.ToolUses).Summary);
    }

    [Fact]
    public async Task StartTask_EmptyArgsWithPositiveLimit_KeepsExistingParentheses()
    {
        var run = await RunAsync(ScriptedExecutor.Tool("read", ""));
        Assert.Equal("... Using read()", PreviewText(Assert.Single(run.Sink.Previews)));
        Assert.Equal("\n<blockquote expandable>read()</blockquote>", run.Sink.Reply!.ToolBlockHtml);
    }

    [Theory]
    [InlineData(300)]
    [InlineData(50)]
    [InlineData(5000)]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task StartTask_ConfiguredArgsLimit_BoundsPreviewAndFooter(int limit)
    {
        var args = new string('x', 6000);
        var tool = ScriptedExecutor.Tool("read", args);
        var run = await RunAsync(tool, limit);
        var expectedArgs = limit <= 0 ? "" : args[..limit] + "...";
        Assert.Equal(expectedArgs, Assert.Single(run.Stats.ToolCalls!).Args);
        var expectedPreview = "Using read" + (limit <= 0 ? "" : "(" + expectedArgs + ")");
        if (expectedPreview.Length > 500) expectedPreview = expectedPreview[..500] + "...";
        Assert.Equal("... " + expectedPreview, PreviewText(Assert.Single(run.Sink.Previews)));
        if (limit <= 0) Assert.Equal("\n<blockquote expandable>read()</blockquote>", run.Sink.Reply!.ToolBlockHtml);
        Assert.Equal(("read", "Using read"), Assert.Single(run.ToolUses));
    }

    [Fact]
    public async Task StartTask_WorstCaseHtmlExpansion_StaysUnderTelegramLimit()
    {
        var run = await RunAsync(ScriptedExecutor.Tool("read", new string('"', 6000)), 5000, prefix: true);
        var html = Assert.Single(run.Sink.Previews);
        Assert.True(html.Length < 4096);
        Assert.Equal(507, PreviewText(html).Length);
        Assert.DoesNotContain("&quot", html.Replace("&quot;", ""));
    }

    [Theory]
    [InlineData(300, false)]
    [InlineData(500, false)]
    [InlineData(500, true)]
    public async Task StartTask_UnicodeAtCut_AllConsumerTextIsValid(int boundary, bool inSummary)
    {
        var summary = inSummary ? new string('s', boundary - 1) + "😀tail" : "Using read";
        // The whole-preview boundary includes the summary and opening parenthesis.
        var argOffset = boundary == 500 ? boundary - summary.Length - 2 : boundary - 1;
        var args = inSummary ? "synthetic-arg" : new string('x', argOffset) + "😀tail";
        var tool = new AgentProgress
        {
            EventType = "tool_use", IsSignificant = true, ToolName = "read",
            Summary = summary, ToolArgs = args,
        };
        var run = await RunAsync(tool, boundary == 500 ? 5000 : 300);
        var utf8 = new UTF8Encoding(false, true);
        utf8.GetBytes(PreviewText(Assert.Single(run.Sink.Previews)));
        utf8.GetBytes(Assert.Single(run.Stats.ToolCalls!).Args);
        utf8.GetBytes(Assert.Single(run.ToolUses).Summary);
        utf8.GetBytes(run.Buffer.FormatRecentToolUse());
        Assert.True(PreviewText(run.Sink.Previews.Single()).Length <= 507);
        Assert.True(run.ToolUses.Single().Summary.Length <= 503);
        if (inSummary) Assert.Equal(new string('s', 499) + "...", run.ToolUses.Single().Summary);
    }

    [Fact]
    public async Task StartTask_CodexConsumers_UseShellAndSummaryOnly()
    {
        var command = "python3 - <<'EOF'\n" + new string('x', 3000);
        var tool = Command(command);
        var run = await RunAsync(tool);
        Assert.Equal(("shell", tool.Summary), Assert.Single(run.ToolUses));
        Assert.Equal("Recent actions before last restart:\n  [12:34:56] shell: " + tool.Summary, run.Buffer.FormatRecentToolUse());
        Assert.Equal("shell", Assert.Single(run.Events.Tools).ToolName);
        Assert.Equal("\n<blockquote expandable>shell(" + WebUtility.HtmlEncode(command[..300] + "...") + ")</blockquote>", run.Sink.Reply!.ToolBlockHtml);
    }

    [Fact]
    public async Task StartTask_LongSummary_BoundsBufferWithoutArgs()
    {
        var summary = "Using " + new string('x', 6000);
        var tool = new AgentProgress
        {
            EventType = "tool_use", IsSignificant = true, ToolName = "read",
            Summary = summary, ToolArgs = "synthetic-arg-sentinel",
        };
        var run = await RunAsync(tool);
        Assert.Equal(("read", summary[..500] + "..."), Assert.Single(run.ToolUses));
        Assert.DoesNotContain("synthetic-arg-sentinel", run.Buffer.FormatRecentToolUse());
        Assert.Equal("... " + summary[..500] + "...", PreviewText(Assert.Single(run.Sink.Previews)));
    }

    [Fact]
    public async Task StartTask_RelayLongCommand_LeavesCallbackAndReplyBodyUnchanged()
    {
        var run = await RunAsync(Command("python3 - <<'EOF'\n" + new string('x', 3000)), source: TaskSource.Relay);
        Assert.Equal(Answer, run.Callback);
        Assert.Equal(Answer, run.Sink.Reply!.Content);
        Assert.Equal(OutboundOrigin.Relay, run.Sink.Origin);
        Assert.StartsWith("\n<blockquote expandable>shell(", run.Sink.Reply.ToolBlockHtml);
    }

    [Theory]
    [InlineData("claude")]
    [InlineData("gemini")]
    public async Task StartTask_OtherProviderFixtures_KeepPreviewFooterAndBuffer(string provider)
    {
        IReadOnlyList<AgentProgress> progress;
        if (provider == "claude")
        {
            // Reuse the existing synthetic tool frame, with Bash input in its real mapping shape.
            var frames = ProviderFrameReplay.ReadClaudeFrames("ordinary-turn.ndjson");
            var block = frames.SelectMany(f => f.Message?.Content ?? []).Single(b => b.Type == "tool_use");
            block.Name = "Bash";
            block.Input = new Dictionary<string, object> { ["command"] = "echo synthetic-sentinel" };
            progress = await ProviderFrameReplay.ReplayClaudeFramesAsync(frames);
        }
        else
        {
            progress = ProviderFrameReplay.ReplayGeminiFrames(ProviderFrameReplay.ReadGeminiFrames("ordinary-turn.jsonl")).Progress;
        }
        var tool = Assert.Single(progress, p => p.IsSignificant && p.ToolName is not null);
        var run = await RunAsync(tool, provider: provider);
        var expected = tool.Summary.StartsWith("Using") ? tool.Summary + "(" + tool.ToolArgs + ")" : tool.Summary;
        Assert.Equal("... " + expected, PreviewText(Assert.Single(run.Sink.Previews)));
        Assert.Equal((tool.ToolName!, tool.Summary), Assert.Single(run.ToolUses));
        Assert.Equal("Recent actions before last restart:\n  [12:34:56] " + tool.ToolName + ": " + tool.Summary, run.Buffer.FormatRecentToolUse());
        Assert.Equal("\n<blockquote expandable>" + tool.ToolName + "(" + WebUtility.HtmlEncode(tool.ToolArgs ?? "{}") + ")</blockquote>", run.Sink.Reply!.ToolBlockHtml);
    }

    private static string PreviewText(string html) => WebUtility.HtmlDecode(
        html.Split("<blockquote expandable>")[1].Split("</blockquote>")[0]);

    private static async Task<Run> RunAsync(AgentProgress tool, int limit = 300, bool prefix = false,
        TaskSource source = TaskSource.UserMessage, string provider = "codex")
    {
        var stats = new ExecutionStats { InputTokens = 1 };
        var executor = new ScriptedExecutor([tool, new AgentProgress { EventType = "result", FinalResult = Answer, Summary = Answer, Stats = stats }]);
        var sink = new RecordingSink();
        var events = new RecordingEvents();
        var buffer = new GroupChatBuffer();
        var uses = new List<(string Name, string Summary)>();
        var manager = new TaskManager(Options.Create(new AgentOptions
        {
            Name = "test", Role = "test", WorkDir = Path.GetTempPath(), Provider = provider,
            ToolArgsTruncateLength = limit, PrefixMessages = prefix, ShortName = "test", ShowStats = true,
        }), executor, new SessionManager(), NullLogger<TaskManager>.Instance, events: events, sink: sink);
        string? callback = null;
        manager.OnToolUse += (_, name, summary) => { uses.Add((name, summary)); buffer.AddToolUse(name, summary, Timestamp); };
        manager.OnTaskCompleted += (_, text, _, _, _, _, _, _) => callback = text;
        var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.OnStatusChanged += () => { if (!manager.HasRunningTasks(123)) idle.TrySetResult(); };
        await manager.StartTask(123, "synthetic task", "synthetic task", true, source: source, relaySender: source == TaskSource.Relay ? "workflow" : null);
        await idle.Task.WaitAsync(TimeSpan.FromSeconds(10));
        return new(sink, events, buffer, uses, stats, callback);
    }

    private sealed record Run(RecordingSink Sink, RecordingEvents Events, GroupChatBuffer Buffer,
        List<(string Name, string Summary)> ToolUses, ExecutionStats Stats, string? Callback);

    private sealed class RecordingSink : IMessageSink
    {
        public List<string> Previews { get; } = [];
        public AgentReply? Reply { get; private set; }
        public OutboundOrigin Origin { get; private set; }
        public bool RendersReplies => true;
        public Task SendTextAsync(long chatId, string text, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendTypingAsync(long chatId, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendPhotoAsync(long chatId, string filePath, string? caption, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendHtmlTextAsync(long chatId, string htmlText, CancellationToken ct = default)
        { Previews.Add(htmlText); return Task.CompletedTask; }
        public Task SendReplyAsync(long chatId, AgentReply reply, OutboundOrigin origin, CancellationToken ct = default)
        { Reply = reply; Origin = origin; return Task.CompletedTask; }
    }

    private sealed class RecordingEvents : IConversationEventPublisher
    {
        public ConcurrentQueue<TurnProgressPayload> Tools { get; } = new();
        public void Publish(ConversationEvent evt) { }
        public bool Publish<TPayload>(long runtimeConversationKey, string kind, ConversationIdentity identity, TPayload? payload) where TPayload : class
        { if (payload is TurnProgressPayload { Activity: ProgressActivity.Tool } tool) Tools.Enqueue(tool); return true; }
    }

    private sealed class ExecutorScope : IDisposable
    {
        public CodexExecutor Value { get; } = ProviderFrameReplay.CreateCodexExecutor();
        public void Dispose() => Value.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
