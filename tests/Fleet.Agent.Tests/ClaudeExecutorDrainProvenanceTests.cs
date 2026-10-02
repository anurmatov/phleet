using System.Threading.Channels;
using Fleet.Agent.Configuration;
using Fleet.Agent.Models;
using Fleet.Agent.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Fleet.Agent.Tests;

/// <summary>
/// S14 (#406): the stale drain splits preserved text at each stale result and tags it by
/// provenance, while the preserved text and the recovered_answer Summary stay byte-identical.
/// </summary>
public sealed class ClaudeExecutorDrainProvenanceTests
{
    [Fact]
    public void Drain_TagsSegmentsByTheResultThatEndsThem_AndKeepsThePreservedTextUnchanged()
    {
        var (executor, channel) = Build();
        Script(channel);

        executor.DrainStaleTurnEventsForTests();

        Assert.Equal("A\nB\nC", executor.PreservedDrainedAnswerTextForTests);
        Assert.Equal(
            [new RecoveredSegment("A", RecoveredSegment.User), new RecoveredSegment("B", RecoveredSegment.Notification),
                new RecoveredSegment("C", RecoveredSegment.Open)],
            executor.PreservedDrainedSegmentsForTests!);
    }

    [Fact]
    public void Drain_WithNoStaleEvents_PreservesNothing()
    {
        var (executor, _) = Build();
        executor.DrainStaleTurnEventsForTests();
        Assert.Null(executor.PreservedDrainedAnswerTextForTests);
        Assert.Null(executor.PreservedDrainedSegmentsForTests);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NextExecuteAsync_RecoveredAnswerCarriesTheSameSummaryAndSegments(bool stale)
    {
        var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "/bin/cat", RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false,
        })!;
        try
        {
            var (executor, channel) = Build();
            executor.SetProcessForTests(process);
            executor.SetStdinForTests(process.StandardInput);
            if (stale) Script(channel);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var events = new List<AgentProgress>();
            var turn = Task.Run(async () =>
            {
                await foreach (var p in executor.ExecuteAsync("next task", ct: cts.Token)) events.Add(p);
            });
            await Task.Delay(150, cts.Token);
            channel.Writer.TryWrite(new ClaudeStreamEvent { Type = "result", Result = "next answer" });
            await turn;

            var recovered = events.Where(p => p.EventType == "recovered_answer").ToList();
            if (!stale) { Assert.Empty(recovered); return; }
            var answer = Assert.Single(recovered);
            Assert.Equal("A\nB\nC", answer.Summary);
            Assert.Equal(["A", "B", "C"], answer.RecoveredSegments!.Select(s => s.Text));
            Assert.Equal([RecoveredSegment.User, RecoveredSegment.Notification, RecoveredSegment.Open],
                answer.RecoveredSegments!.Select(s => s.Origin));
            Assert.Null(executor.PreservedDrainedSegmentsForTests);
        }
        finally
        {
            process.Kill();
            process.Dispose();
        }
    }

    // [assistant A, result no origin], [assistant B, result origin task-notification], [assistant C, no result]
    private static void Script(Channel<ClaudeStreamEvent> channel)
    {
        channel.Writer.TryWrite(Assistant("A"));
        channel.Writer.TryWrite(new ClaudeStreamEvent { Type = "result", Result = "A" });
        channel.Writer.TryWrite(Assistant("B"));
        channel.Writer.TryWrite(new ClaudeStreamEvent { Type = "result", Result = "B", Origin = new ClaudeMessageOrigin { Kind = "task-notification" } });
        channel.Writer.TryWrite(Assistant("C"));
    }

    private static ClaudeStreamEvent Assistant(string text) => new()
    {
        Type = "assistant",
        Message = new ClaudeMessage { Content = [new ClaudeContentBlock { Type = "text", Text = text }] },
    };

    private static (ClaudeExecutor, Channel<ClaudeStreamEvent>) Build()
    {
        var options = Options.Create(new AgentOptions { Name = "test", Role = "test", WorkDir = "/tmp", Provider = "claude" });
        var executor = new ClaudeExecutor(options, NullLogger<ClaudeExecutor>.Instance, new PromptBuilder(options, NullLogger<PromptBuilder>.Instance));
        var channel = Channel.CreateUnbounded<ClaudeStreamEvent>();
        executor.SetEventChannelForTests(channel);
        return (executor, channel);
    }
}
