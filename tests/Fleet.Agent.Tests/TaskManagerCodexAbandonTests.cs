using Fleet.Agent.Models;

namespace Fleet.Agent.Tests;

public class TaskManagerCodexAbandonTests
{
    [Fact]
    public async Task StartTask_SendFailure_CompletesOnceAndRoutesNextAnswerToItsOwnChat()
    {
        var result = await CodexAbandonedTurnReproTests.RunSendFailureAsync();
        Assert.Equal(CompletionKind.Failed, result.First.Kind);
        Assert.Equal(CompletionKind.Completed, result.Second.Kind);
        Assert.Single(result.All, c => c.Chat == 101);
        Assert.Single(result.All, c => c.Chat == 202);
        Assert.Contains(result.Sink.Sent, s => s.Chat == 202 && s.Text.Contains("answer-2"));
        Assert.DoesNotContain(result.Sink.Sent, s => s.Chat == 101 && s.Text.Contains("answer-2"));
        Assert.DoesNotContain(result.Sink.Sent, s => s.Text.Contains("discarded"));
        Assert.Equal(2, result.Requests.Count(r => (string?)r["method"] == "turn/start"));
        Assert.Single(result.Requests, r => (string?)r["method"] == "turn/interrupt");
    }
}
