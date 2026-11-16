using System.Net;
using System.Text.Json.Nodes;
using Fleet.Agent.Abstractions;
using Fleet.Agent.Configuration;
using Fleet.Agent.Models;
using Fleet.Agent.Services;
using Fleet.Agent.Tests.Harness;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Fleet.Agent.Tests;

/// <summary>
/// The tool lines a running turn sends to the chat: every call is shown, the first at once, the
/// rest five to a message, and what is left over ahead of whatever ends the turn. Driven through
/// the real <see cref="TaskManager"/> with a sink that records what it was sent, in order.
/// </summary>
public class ToolMessageDeliveryTests
{
    private const long Chat = 7001;
    private const string Answer = "Synthetic answer A.";

    // ── every call, in few messages ──────────────────────────────────────────

    [Fact]
    public async Task EveryToolCall_IsShown_TheFirstAtOnce_ThenFiveToAMessage_ThenTheRestBeforeTheAnswer()
    {
        var steps = Tools(1, 13).Append(ScriptedExecutor.Final(Answer)).ToList();
        var run = await RunAsync(new ScriptedExecutor(steps));

        var sent = run.Sink.Sent;
        Assert.Equal(["html", "html", "html", "html", "text"], sent.Select(s => s.Kind));
        Assert.Equal([1, 5, 5, 2], sent.Take(4).Select(s => Lines(s.Text).Count));
        Assert.Equal(Enumerable.Range(1, 13).Select(Line), sent.Take(4).SelectMany(s => Lines(s.Text)));
        Assert.Equal(Answer, sent[4].Text);
    }

    /// <summary>The old rule sent calls 1, 6 and 11 and nothing else; a turn of five showed one.</summary>
    [Theory]
    [InlineData(1, new[] { 1 })]
    [InlineData(2, new[] { 1, 1 })]
    [InlineData(5, new[] { 1, 4 })]
    [InlineData(6, new[] { 1, 5 })]
    [InlineData(7, new[] { 1, 5, 1 })]
    public async Task TheNumberOfMessages_FollowsTheNumberOfCalls(int calls, int[] linesPerMessage)
    {
        var steps = Tools(1, calls).Append(ScriptedExecutor.Final(Answer)).ToList();
        var run = await RunAsync(new ScriptedExecutor(steps));

        var html = run.Sink.Sent.Where(s => s.Kind == "html").ToList();
        Assert.Equal(linesPerMessage, html.Select(s => Lines(s.Text).Count));
    }

    [Fact]
    public async Task AGroup_IsOneExpandableBlockquote_OneLinePerCall_UnderTheAgentPrefix()
    {
        var steps = Tools(1, 3).Append(ScriptedExecutor.Final(Answer)).ToList();
        var run = await RunAsync(new ScriptedExecutor(steps), o => { o.PrefixMessages = true; o.ShortName = "agent1"; });

        var html = run.Sink.Sent.Where(s => s.Kind == "html").Select(s => s.Text).ToList();
        Assert.Equal(
        [
            "<b>Agent1:</b>\n<blockquote expandable>" + WebUtility.HtmlEncode("... " + Line(1)) + "</blockquote>",
            "<b>Agent1:</b>\n<blockquote expandable>" + WebUtility.HtmlEncode("... " + Line(2)) + "\n"
                + WebUtility.HtmlEncode("... " + Line(3)) + "</blockquote>",
        ], html);
    }

    // ── a turn that ends without a normal answer ─────────────────────────────

    [Fact]
    public async Task ATurnThatThrows_StillShowsItsCollectedCalls_BeforeTheError()
    {
        var executor = new ScriptedExecutor(Tools(1, 3)) { ThrowAfterScript = new InvalidOperationException("synthetic failure") };
        var run = await RunAsync(executor);

        var sent = run.Sink.Sent;
        Assert.Equal(["html", "html", "text"], sent.Select(s => s.Kind));
        Assert.Equal([Line(1)], Lines(sent[0].Text));
        Assert.Equal([Line(2), Line(3)], Lines(sent[1].Text));
        Assert.Equal("Error: synthetic failure", sent[2].Text);
    }

    [Fact]
    public async Task ACancelledTurn_StillShowsItsCollectedCalls_BeforeTheCancelNotice()
    {
        var executor = new ScriptedExecutor(Tools(1, 4)) { BlockUntilCancelled = true };
        var run = await StartAsync(executor);
        await executor.ScriptConsumed.WaitAsync(TimeSpan.FromSeconds(10));

        await run.Manager.CancelAllAsync();
        await run.Completed;

        var sent = run.Sink.Sent;
        Assert.Equal(["html", "html", "text"], sent.Select(s => s.Kind));
        Assert.Equal([Line(1)], Lines(sent[0].Text));
        Assert.Equal([Line(2), Line(3), Line(4)], Lines(sent[1].Text));
        Assert.Equal("Task cancelled.", sent[2].Text);
    }

    [Fact]
    public async Task AnErrorResultWithNoText_StillShowsItsCollectedCalls_BeforeTheFailure()
    {
        var failed = new AgentProgress { Summary = "result", EventType = "result", IsErrorResult = true };
        var run = await RunAsync(new ScriptedExecutor(Tools(1, 2).Append(failed).ToList()));

        var sent = run.Sink.Sent;
        Assert.Equal(["html", "html", "text"], sent.Select(s => s.Kind));
        Assert.Equal([Line(2)], Lines(sent[1].Text));
        Assert.StartsWith("Task failed:", sent[2].Text, StringComparison.Ordinal);
    }

    /// <summary>A tool line that cannot be delivered at the end of a turn must not cost the turn its answer.</summary>
    [Fact]
    public async Task AFailedFlush_DoesNotCostTheTurnItsAnswer()
    {
        var steps = Tools(1, 2).Append(ScriptedExecutor.Final(Answer)).ToList();
        var run = await RunAsync(new ScriptedExecutor(steps), sink: new RecordingSink { FailHtmlAfter = 1 });

        var sent = run.Sink.Sent;
        Assert.Equal(["html", "text"], sent.Select(s => s.Kind));
        Assert.Equal(Answer, sent[1].Text);
        Assert.False(run.CompletedWithError);
    }

    // ── more than one executor turn in one run ───────────────────────────────

    /// <summary>
    /// A process exit followed by a resume: the calls collected before the exit are sent before the
    /// resumed turn starts, and the resumed turn again shows its first call at once.
    /// </summary>
    [Fact]
    public async Task AResumeAfterAProcessExit_FlushesTheFirstRead_AndStartsTheNextWithAnImmediateCall()
    {
        var executor = new ScriptedExecutor(Tools(1, 3).Append(ScriptedExecutor.ProcessExit("Provider process exited.")).ToList())
        {
            BlockFirstTurnUntilReleased = true,
            InjectionResult = MidTurnInjectionResult.Injected,
        };
        executor.Enqueue(Tools(4, 2).Append(ScriptedExecutor.Final(Answer)).ToList());

        var run = await StartAsync(executor);
        await executor.FirstTurnStarted;
        await run.Manager.StartTask(Chat, "task-2", "task-2", isSessionTask: true);
        executor.Release();
        await run.Completed;

        Assert.Equal(2, executor.Tasks.Count);
        var sent = run.Sink.Sent.Where(s => s.Kind == "html" || s.Text.Contains(Answer, StringComparison.Ordinal)).ToList();
        Assert.Equal(["html", "html", "html", "html", "text"], sent.Select(s => s.Kind));
        Assert.Equal([[Line(1)], [Line(2), Line(3)], [Line(4)], [Line(5)]], sent.Take(4).Select(s => Lines(s.Text)));
    }

    /// <summary>
    /// A message that waited for the end of the turn runs as a continuation inside the same run:
    /// the first turn's calls and answer are complete before the continuation's first call.
    /// </summary>
    [Fact]
    public async Task AContinuationTurn_KeepsEachTurnsCallsAheadOfItsOwnAnswer()
    {
        var executor = new ScriptedExecutor(Tools(1, 3).Append(ScriptedExecutor.Final("Synthetic answer A.")).ToList())
        {
            BlockFirstTurnUntilReleased = true,
        };
        executor.Enqueue(Tools(4, 3).Append(ScriptedExecutor.Final("Synthetic answer B.")).ToList());

        var run = await StartAsync(executor);
        await executor.FirstTurnStarted;
        await run.Manager.StartTask(Chat, "task-2", "task-2", isSessionTask: true);
        executor.Release();
        await WaitForAsync(() => run.Sink.Sent.Any(s => s.Text.Contains("Synthetic answer B.", StringComparison.Ordinal)));

        var sent = run.Sink.Sent
            .Where(s => s.Kind == "html" || s.Text.Contains("Synthetic answer", StringComparison.Ordinal))
            .Select(s => s.Kind == "html" ? string.Join(" | ", Lines(s.Text)) : s.Text)
            .ToList();
        Assert.Equal(
        [
            Line(1),
            Line(2) + " | " + Line(3),
            "Synthetic answer A.",
            Line(4),
            Line(5) + " | " + Line(6),
            "Synthetic answer B.",
        ], sent);
    }

    // ── what stays as it was ─────────────────────────────────────────────────

    [Fact]
    public async Task SuppressToolMessages_SendsNoToolLine()
    {
        var steps = Tools(1, 7).Append(ScriptedExecutor.Final(Answer)).ToList();
        var run = await RunAsync(new ScriptedExecutor(steps), o => o.SuppressToolMessages = true);

        Assert.Equal(["text"], run.Sink.Sent.Select(s => s.Kind));
    }

    [Theory]
    [InlineData(TaskSource.CheckIn)]
    [InlineData(TaskSource.DebouncedGroupBatch)]
    public async Task ACheckInOrADebouncedBatch_SendsNoToolLine(TaskSource source)
    {
        var steps = Tools(1, 7).Append(ScriptedExecutor.Final(Answer)).ToList();
        var run = await RunAsync(new ScriptedExecutor(steps), source: source);

        Assert.DoesNotContain(run.Sink.Sent, s => s.Kind == "html");
    }

    // ── truncation: one knob ─────────────────────────────────────────────────

    [Fact]
    public void TheKnob_DefaultsToAThousandCharacters() =>
        Assert.Equal(1000, new AgentOptions { Name = "test", Role = "test", WorkDir = TestTemp.Root }.ToolArgsTruncateLength);

    [Theory]
    [InlineData(1000, 1000)] // the default
    [InlineData(40, 40)]
    [InlineData(0, 5000)]    // no limit
    public async Task TheKnob_CutsTheArguments_AndZeroMeansNoLimit(int knob, int shown)
    {
        var args = new string('a', 5000);
        var steps = new List<AgentProgress> { ScriptedExecutor.Tool("write_file", args), ScriptedExecutor.Final(Answer) };
        var run = await RunAsync(new ScriptedExecutor(steps), o => o.ToolArgsTruncateLength = knob);

        var expected = shown == 5000 ? args : new string('a', shown) + "...";
        Assert.Equal($"... Using write_file({expected})", ShownText(run));
    }

    /// <summary>
    /// A summary that carries the bulk itself (a shell command, which has no separate arguments)
    /// is cut at the same knob. With no limit it is shown whole, over as many messages as it needs.
    /// </summary>
    [Theory]
    [InlineData(1000, 1000)]
    [InlineData(40, 40)]
    [InlineData(0, 5009)]
    public async Task TheKnob_CutsALongSummaryToo(int knob, int shown)
    {
        var summary = "Running: " + new string('c', 5000);
        var call = new AgentProgress
        {
            EventType = "tool_use", Summary = summary, ToolName = "shell", ToolArgs = summary[9..], IsSignificant = true,
        };
        var run = await RunAsync(new ScriptedExecutor([call, ScriptedExecutor.Final(Answer)]), o => o.ToolArgsTruncateLength = knob);

        Assert.Equal("... " + (shown == 5009 ? summary : summary[..shown] + "..."), ShownText(run));
    }

    /// <summary>
    /// Five long calls do not fit one Telegram message. The group is split between two lines, so
    /// every message stays under the sender's hard cut and no line is broken in two.
    /// </summary>
    [Fact]
    public async Task AGroupTooLongForOneMessage_IsSplitBetweenLines()
    {
        var steps = Enumerable.Range(1, 6)
            .Select(i => ScriptedExecutor.Tool($"tool{i}", $"\"{new string((char)('a' + i), 1500)}\""))
            .Append(ScriptedExecutor.Final(Answer))
            .ToList();
        var run = await RunAsync(new ScriptedExecutor(steps));

        var html = run.Sink.Sent.Where(s => s.Kind == "html").Select(s => s.Text).ToList();
        Assert.True(html.Count > 2, "five 1000-character lines cannot share one message");
        Assert.All(html, message =>
        {
            Assert.True(message.Length <= 4000, $"a tool message of {message.Length} characters would be cut mid-line by the sender");
            Assert.StartsWith("<blockquote expandable>", message, StringComparison.Ordinal);
            Assert.EndsWith("</blockquote>", message, StringComparison.Ordinal);
        });
        var lines = html.SelectMany(Lines).ToList();
        Assert.Equal(6, lines.Count);
        Assert.All(lines.Select((line, index) => (line, index)), entry =>
            Assert.Equal($"Using tool{entry.index + 1}(\"{new string((char)('a' + entry.index + 1), 999)}...)", entry.line));
    }

    // ── a line too long for one message by itself ───────────────────────────

    /// <summary>
    /// At the default cap a command of 786 ampersands is shown whole, and its line encodes to more
    /// than one message. It goes out over several, each a whole blockquote, and the turn answers.
    /// </summary>
    [Fact]
    public async Task ALineThatEncodesPastOneMessage_IsSentWholeOverSeveral_EachWellFormed()
    {
        Assert.True(
            ("<blockquote expandable>" + WebUtility.HtmlEncode("... Running: " + OversizedCommand) + "</blockquote>").Length > 4000,
            "the line must not fit one message");
        var call = CodexToolCall(new JsonObject { ["type"] = "commandExecution", ["command"] = OversizedCommand });
        var run = await RunAsync(new ScriptedExecutor([call, ScriptedExecutor.Final(Answer)]));

        Assert.True(run.Sink.Sent.Count(s => s.Kind == "html") >= 2);
        Assert.Equal("... Running: " + OversizedCommand, ShownText(run));
        Assert.Equal(("text", Answer), run.Sink.Sent[^1]);
        Assert.False(run.CompletedWithError);
    }

    /// <summary>
    /// The same line through the real transport: one message past 4000 characters was cut by the
    /// transport inside its closing tag and reached the Bot API as <c>&lt;/bl&lt;/blockquote&gt;</c>
    /// and <c>ockquote&gt;</c>, which Telegram refuses. Every text the Bot API gets is now whole HTML.
    /// </summary>
    [Fact]
    public async Task ThroughTheTransport_ALineThatEncodesPastOneMessage_ReachesTheBotApiAsWholeHtml()
    {
        var root = Path.Combine(Path.GetTempPath(), "tool-lines-" + Guid.NewGuid().ToString("N"));
        try
        {
            var rig = ReplyRig.Build(root, journal: false);
            var call = CodexToolCall(new JsonObject { ["type"] = "commandExecution", ["command"] = OversizedCommand });
            await rig.RunTurnAsync(GoldenRow.Chat, [call, ScriptedExecutor.Final(Answer)]);

            var calls = rig.Bot.Calls;
            Assert.All(calls, c => Assert.True(c.Ok && c.Method == "sendMessage", $"{c.Method} ok={c.Ok}"));
            var tool = calls.Where(c => c.Text != Answer).Select(c => c.Text!).ToList();
            Assert.True(tool.Count >= 2);
            Assert.All(tool, AssertSendableToolMessage);
            Assert.Equal("... Running: " + OversizedCommand, string.Concat(tool.Select(Body)));
            Assert.Equal(Answer, calls[^1].Text);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>
    /// With no cap a call's whole text is shown however long it is: over as many messages as it
    /// needs, cut between characters, never inside an entity or a surrogate pair.
    /// </summary>
    [Fact]
    public async Task WithNoCap_AVeryLongCall_IsShownWholeOverSeveralMessages()
    {
        var args = "\"" + string.Concat(Enumerable.Repeat("a&b<c>d\"e'f é 😀 ", 1500)) + "\"";
        var steps = new List<AgentProgress> { ScriptedExecutor.Tool("bulk", args), ScriptedExecutor.Final(Answer) };
        var run = await RunAsync(new ScriptedExecutor(steps), o =>
        {
            o.ToolArgsTruncateLength = 0;
            o.PrefixMessages = true;
            o.ShortName = "agent1";
        });

        var html = run.Sink.Sent.Where(s => s.Kind == "html").Select(s => s.Text).ToList();
        Assert.True(html.Count > 10);
        Assert.All(html, message => Assert.StartsWith("<b>Agent1:</b>\n<blockquote expandable>", message, StringComparison.Ordinal));
        Assert.Equal($"... Using bulk({args})", ShownText(run));
        Assert.Equal(("text", Answer), run.Sink.Sent[^1]);
    }

    // ── the providers' own calls, as the chat shows them ─────────────────────

    [Fact]
    public async Task ACodexShellCall_ShowsItsCommandOnce_Whole()
    {
        const string command = "grep -rn --include=*.cs \"synthetic-marker\" /workspace/example/src /workspace/example/tests | sort | head -50";
        var call = CodexToolCall(new JsonObject { ["type"] = "commandExecution", ["command"] = command });
        var run = await RunAsync(new ScriptedExecutor([call, ScriptedExecutor.Final(Answer)]));

        var line = Assert.Single(Lines(Assert.Single(run.Sink.Sent, s => s.Kind == "html").Text));
        Assert.Equal("Running: " + command, line);
    }

    [Fact]
    public async Task AClaudeBashCall_ShowsItsCommandWhole()
    {
        var command = "cd /workspace/example && dotnet test tests/Example.Tests --filter \"FullyQualifiedName~Synthetic\" --logger \"console;verbosity=detailed\" | tail -40";
        Assert.True(command.Length > 80);
        var call = ClaudeToolCall("Bash", new Dictionary<string, object> { ["command"] = command });
        var run = await RunAsync(new ScriptedExecutor([call, ScriptedExecutor.Final(Answer)]));

        var line = Assert.Single(Lines(Assert.Single(run.Sink.Sent, s => s.Kind == "html").Text));
        Assert.Equal("Running: " + command, line);
    }

    // ── rig ──────────────────────────────────────────────────────────────────

    private static List<AgentProgress> Tools(int from, int count) =>
        Enumerable.Range(from, count).Select(i => ScriptedExecutor.Tool($"tool{i}", $"{{\"n\":{i}}}")).ToList();

    /// <summary>The chat line of the call <see cref="Tools"/> makes for <paramref name="i"/>.</summary>
    private static string Line(int i) => $"Using tool{i}({{\"n\":{i}}})";

    /// <summary>The lines of one tool message, decoded, without its wrapper and the "... " each line starts with.</summary>
    private static List<string> Lines(string html)
    {
        const string open = "<blockquote expandable>";
        const string close = "</blockquote>";
        var start = html.IndexOf(open, StringComparison.Ordinal);
        Assert.True(start >= 0 && html.EndsWith(close, StringComparison.Ordinal), $"not a tool message: {html}");
        var body = html[(start + open.Length)..^close.Length];
        return body.Split('\n').Select(encoded =>
        {
            var line = WebUtility.HtmlDecode(encoded);
            Assert.StartsWith("... ", line, StringComparison.Ordinal);
            return line[4..];
        }).ToList();
    }

    /// <summary>A shell command short enough to be shown whole, whose line encodes past one message.</summary>
    private static readonly string OversizedCommand = "printf '%s' '" + new string('&', 786) + "'";

    private static readonly System.Text.RegularExpressions.Regex Entity = new(@"\G&(amp|lt|gt|quot|#[0-9]+);");

    /// <summary>
    /// A tool message as Telegram's HTML parser needs it, and short enough that the sender sends it
    /// as it is: one blockquote under an optional prefix, every tag whole and closed in order,
    /// every entity whole.
    /// </summary>
    private static void AssertSendableToolMessage(string html)
    {
        var open = new Stack<string>();
        for (var i = 0; i < html.Length; i++)
        {
            if (html[i] == '<')
            {
                var end = html.IndexOf('>', i);
                Assert.True(end > i, $"a tag at {i} is not closed");
                var tag = html[(i + 1)..end];
                Assert.Matches("^(b|blockquote expandable|/b|/blockquote)$", tag);
                if (tag[0] == '/')
                    Assert.True(open.Count > 0 && open.Pop() == tag[1..], $"<{tag}> at {i} closes nothing open");
                else
                    open.Push(tag.Split(' ')[0]);
                i = end;
            }
            else if (html[i] == '&')
            {
                Assert.True(Entity.IsMatch(html, i), $"an entity at {i} is cut");
            }
        }

        Assert.Empty(open);
        Assert.Matches(@"^(<b>[^<]*</b>\n)?<blockquote expandable>", html);
        Assert.EndsWith("</blockquote>", html, StringComparison.Ordinal);
        Assert.True(html.Length <= 4000, $"a tool message of {html.Length} characters is cut by the sender");
    }

    /// <summary>The text of every tool message of a run, in order, each checked to be sendable as it is.</summary>
    private static string ShownText(Run run)
    {
        var html = run.Sink.Sent.Where(s => s.Kind == "html").Select(s => s.Text).ToList();
        Assert.All(html, AssertSendableToolMessage);
        return string.Concat(html.Select(Body));
    }

    /// <summary>The text of one tool message, decoded, without its prefix and its blockquote.</summary>
    private static string Body(string html)
    {
        const string open = "<blockquote expandable>";
        var start = html.IndexOf(open, StringComparison.Ordinal) + open.Length;
        return WebUtility.HtmlDecode(html[start..^"</blockquote>".Length]);
    }

    private static AgentProgress CodexToolCall(JsonObject item)
    {
        var options = Options.Create(new AgentOptions { Name = "agent-a", Role = "test", WorkDir = TestTemp.Root, Provider = "codex" });
        var executor = new CodexExecutor(
            options, Options.Create(new TelegramOptions()),
            new PromptBuilder(options, NullLogger<PromptBuilder>.Instance), NullLogger<CodexExecutor>.Instance);
        return executor.BuildItemStartedProgressForTests(new JsonObject { ["item"] = item })!;
    }

    private static AgentProgress ClaudeToolCall(string name, Dictionary<string, object> input)
    {
        var options = Options.Create(new AgentOptions { Name = "agent-a", Role = "test", WorkDir = TestTemp.Root, Provider = "claude" });
        var executor = new ClaudeExecutor(options, NullLogger<ClaudeExecutor>.Instance, new PromptBuilder(options, NullLogger<PromptBuilder>.Instance));
        return executor.ParseProgressForTests(new ClaudeStreamEvent
        {
            Type = "assistant",
            Message = new ClaudeMessage { Content = [new ClaudeContentBlock { Type = "tool_use", Name = name, Id = "toolu_1", Input = input }] },
        });
    }

    private sealed class Run
    {
        public required TaskManager Manager { get; init; }
        public required RecordingSink Sink { get; init; }
        public required Task Completed { get; init; }
        public bool CompletedWithError { get; set; }
    }

    private static async Task<Run> RunAsync(
        IAgentExecutor executor, Action<AgentOptions>? configure = null,
        TaskSource source = TaskSource.UserMessage, RecordingSink? sink = null)
    {
        var run = await StartAsync(executor, configure, source, sink);
        await run.Completed;
        return run;
    }

    private static async Task<Run> StartAsync(
        IAgentExecutor executor, Action<AgentOptions>? configure = null,
        TaskSource source = TaskSource.UserMessage, RecordingSink? sink = null)
    {
        var agentOptions = new AgentOptions { Name = "test", Role = "test", WorkDir = TestTemp.Root };
        configure?.Invoke(agentOptions);
        sink ??= new RecordingSink();
        var manager = new TaskManager(
            Options.Create(agentOptions), executor, new SessionManager(), NullLogger<TaskManager>.Instance, sink: sink);

        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = new Run { Manager = manager, Sink = sink, Completed = completed.Task.WaitAsync(TimeSpan.FromSeconds(10)) };
        manager.OnTaskCompleted += (_, _, _, _, isError, _, _, _) =>
        {
            run.CompletedWithError = isError;
            completed.TrySetResult();
        };

        await manager.StartTask(Chat, "task-1", "task-1", isSessionTask: true, source: source);
        return run;
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition())
            await Task.Delay(10, timeout.Token);
    }

    /// <summary>Records every text and HTML send, in order. Not a mock: the default interface members run.</summary>
    private sealed class RecordingSink : IMessageSink
    {
        private readonly List<(string Kind, string Text)> _sent = [];
        private int _html;

        /// <summary>HTML sends after this many throw, as a transport that lost the chat would.</summary>
        public int FailHtmlAfter { get; init; } = int.MaxValue;

        public List<(string Kind, string Text)> Sent
        {
            get { lock (_sent) return [.. _sent]; }
        }

        public Task SendTextAsync(long chatId, string text, CancellationToken ct = default)
        {
            lock (_sent) _sent.Add(("text", text));
            return Task.CompletedTask;
        }

        public Task SendHtmlTextAsync(long chatId, string htmlText, CancellationToken ct = default)
        {
            lock (_sent)
            {
                if (++_html > FailHtmlAfter)
                    throw new InvalidOperationException("synthetic send failure");
                _sent.Add(("html", htmlText));
            }
            return Task.CompletedTask;
        }

        public Task SendTypingAsync(long chatId, CancellationToken ct = default) => Task.CompletedTask;

        public Task SendPhotoAsync(long chatId, string filePath, string? caption, CancellationToken ct = default) =>
            Task.CompletedTask;
    }
}
