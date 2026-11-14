using System.Net;
using Fleet.Temporal.Configuration;
using Fleet.Temporal.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using RabbitMQ.Client;

namespace Fleet.Temporal.Tests.Services.DurableDelegation;

/// <summary>
/// What the real relay says about a cancel when the call has ended. The durable service holds a
/// same-id successor's directive back unless the answer is <see cref="CancelDelivery.Settled"/>
/// (<see cref="DurableDelegationRelayOrderTests"/>), so "settled" must only ever mean that nothing
/// is still on its way to the agent.
/// </summary>
public sealed class DelegationRelayCancelOutcomeTests
{
    private sealed class Orchestrator(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> answer) : HttpMessageHandler
    {
        public int Requests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Requests);
            return answer(request, cancellationToken);
        }
    }

    private static (DelegationRelay Relay, Orchestrator Orchestrator, List<string> Broadcasts) Build(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> answer, string orchestratorUrl = "http://orchestrator.invalid")
    {
        var broadcasts = new List<string>();
        var channel = Substitute.For<IChannel>();
        channel.BasicPublishAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(),
                Arg.Any<BasicProperties>(), Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                lock (broadcasts) broadcasts.Add(System.Text.Encoding.UTF8.GetString(call.ArgAt<ReadOnlyMemory<byte>>(4).Span));
                return ValueTask.CompletedTask;
            });
        var connection = Substitute.For<IConnection>();
        connection.CreateChannelAsync(Arg.Any<CreateChannelOptions?>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(channel));
        var connections = Substitute.For<IConnectionFactory>();
        connections.CreateConnectionAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(connection));

        var orchestrator = new Orchestrator(answer);
        var http = Substitute.For<IHttpClientFactory>();
        http.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(orchestrator, disposeHandler: false));

        var relay = new DelegationRelay(
            Options.Create(new RabbitMqOptions()),
            Options.Create(new TemporalBridgeOptions { OrchestratorUrl = orchestratorUrl }),
            connections, http, NullLogger<DelegationRelay>.Instance);
        return (relay, orchestrator, broadcasts);
    }

    private static PendingDelegation Record() => new()
    {
        TaskId = "wf-1/review",
        CurrentTaskId = "wf-1/review",
        Agent = "reviewer-one",
        WorkflowId = "wf-1",
        WorkflowType = "example-workflow",
    };

    private static Task<HttpResponseMessage> Status(HttpStatusCode code) => Task.FromResult(new HttpResponseMessage(code));

    /// <summary>The orchestrator answered for the agent, or knows no such agent: nothing is on its way.</summary>
    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task AnAnswerThatLeavesNothingOnItsWay_IsSettled(HttpStatusCode code)
    {
        var (relay, orchestrator, broadcasts) = Build((_, _) => Status(code));

        Assert.Equal(CancelDelivery.Settled, await relay.SendCancelAsync(Record()));
        Assert.Equal(1, orchestrator.Requests);
        Assert.Empty(broadcasts);
    }

    /// <summary>The orchestrator could not say what the agent did (it gave up on it, or failed).</summary>
    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task AnAnswerThatDoesNotSayWhatTheAgentDid_IsUnknown(HttpStatusCode code)
    {
        var (relay, _, broadcasts) = Build((_, _) => Status(code));

        Assert.Equal(CancelDelivery.Unknown, await relay.SendCancelAsync(Record()));
        Assert.Empty(broadcasts);
    }

    /// <summary>The broadcast is read by the agent whenever it gets to it, so it is never settled.</summary>
    [Fact]
    public async Task ACancelThatFellBackToTheBroadcast_IsUnknown()
    {
        var (unreachable, _, afterFailure) = Build((_, _) => Task.FromException<HttpResponseMessage>(new HttpRequestException("refused")));
        Assert.Equal(CancelDelivery.Unknown, await unreachable.SendCancelAsync(Record()));
        Assert.Contains("/cancel all", Assert.Single(afterFailure));

        var (noUrl, orchestrator, withoutUrl) = Build((_, _) => Status(HttpStatusCode.OK), orchestratorUrl: "");
        Assert.Equal(CancelDelivery.Unknown, await noUrl.SendCancelAsync(Record()));
        Assert.Equal(0, orchestrator.Requests);
        Assert.Single(withoutUrl);
    }

    /// <summary>
    /// The caller stopped waiting while the request was out. The call ends at once, says it does
    /// not know, and sends nothing more: a broadcast started then could arrive at any later time.
    /// </summary>
    [Fact]
    public async Task ACallThatIsToldToStop_EndsAtOnce_IsUnknown_AndSendsNoBroadcast()
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (relay, _, broadcasts) = Build(async (_, ct) =>
        {
            reached.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var stop = new CancellationTokenSource();

        var call = relay.SendCancelAsync(Record(), stop.Token);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await stop.CancelAsync();

        Assert.Equal(CancelDelivery.Unknown, await call.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Empty(broadcasts);
    }

    /// <summary>
    /// "Settled" rests on the path a cancel takes after the bridge: the orchestrator's route waits
    /// for the agent's answer before it gives its own, and the agent's route applies the cancel
    /// before it answers. If either stops being true (a queue, a fire-and-forget) a 2xx no longer
    /// means the cancel has been applied, and this has to be looked at again. Read from the source.
    /// </summary>
    [Fact]
    public void TheCancelRoute_IsAnsweredOnlyAfterTheAgentHasAppliedIt()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Fleet.sln"))) root = root.Parent;

        string Handler(string project, string route)
        {
            var file = Directory.EnumerateFiles(Path.Combine(root!.FullName, "src", project), "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                .Select(File.ReadAllText)
                .Single(text => text.Contains(route, StringComparison.Ordinal));
            var from = file.IndexOf(route, StringComparison.Ordinal);
            // Up to the next route.
            var to = file.IndexOf("app.Map", from + route.Length, StringComparison.Ordinal);
            return to < 0 ? file[from..] : file[from..to];
        }

        var orchestrator = Handler("Fleet.Orchestrator", "MapPost(\"/api/agents/{name}/cancel/{**taskId}\"");
        Assert.Contains("await client.PostAsync(agentUrl, null)", orchestrator);
        Assert.Contains("response.IsSuccessStatusCode", orchestrator);
        Assert.DoesNotContain("Task.Run", orchestrator);
        Assert.DoesNotContain("_ =", orchestrator);

        var agent = Handler("Fleet.Agent", "MapPost(\"/cancel/{**taskId}\"");
        Assert.Contains("await taskManager.CancelByBridgeTaskIdAsync(decoded)", agent);
        Assert.DoesNotContain("Task.Run", agent);
    }
}
