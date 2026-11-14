using System.Net;
using Fleet.Orchestrator.Endpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Fleet.Orchestrator.Tests.Endpoints;

/// <summary>
/// The orchestrator's hop in a workflow cancel: temporal bridge → <c>POST
/// /api/agents/{name}/cancel/{taskId}</c> → the agent's <c>/cancel/{taskId}</c>.
///
/// <para>
/// A durable delegation's cancel names the execution it is for in the optional query parameter
/// <c>executionId</c>. The orchestrator does not judge it: it passes it on and returns the agent's
/// own answer. A request without it is forwarded and answered exactly as it was before the
/// parameter existed, which is what an older bridge and the in-process delegation path send.
/// </para>
/// </summary>
public sealed class AgentTaskCancelTests
{
    private const string AgentBase = "http://fleet-reviewer-one:8080";

    private sealed class Agent(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<(string Method, string Url)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.Method.Method, request.RequestUri!.AbsoluteUri));
            return Task.FromResult(answer(request));
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    private static async Task<(int Status, string Body)> AnswerOfAsync(IResult result)
    {
        var context = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
        context.Response.Body = new MemoryStream();
        await result.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        return (context.Response.StatusCode, await new StreamReader(context.Response.Body).ReadToEndAsync());
    }

    /// <summary>Without the parameter: the same request to the agent and the same answer as before.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task ACancelWithoutAnExecutionId_IsForwardedAndAnsweredAsBefore(string? executionId)
    {
        var agent = new Agent(_ => Json("""{"cancelled":true,"taskId":"wf-1/review"}"""));

        var (status, body) = await AnswerOfAsync(await AgentTaskCancel.ForwardAsync(
            new HttpClient(agent), AgentBase, "reviewer-one", "wf-1/review", executionId));

        Assert.Equal(("POST", "http://fleet-reviewer-one:8080/cancel/wf-1%2Freview"), Assert.Single(agent.Requests));
        Assert.Equal(200, status);
        Assert.Equal("""{"message":"Cancel requested for task 'wf-1/review' on agent 'reviewer-one'"}""", body);
    }

    /// <summary>With it: the agent is given the id, and its answer comes back as it was given.</summary>
    [Fact]
    public async Task ACancelThatNamesAnExecution_CarriesTheIdToTheAgent_AndReturnsTheAgentsAnswer()
    {
        const string agentSaid = """{"cancelled":false,"taskId":"wf-1/review~1.0","executionId":"a b&c","outcome":"other_execution"}""";
        var agent = new Agent(_ => Json(agentSaid));

        var (status, body) = await AnswerOfAsync(await AgentTaskCancel.ForwardAsync(
            new HttpClient(agent), AgentBase, "reviewer-one", "wf-1/review~1.0", "a b&c"));

        Assert.Equal(
            ("POST", "http://fleet-reviewer-one:8080/cancel/wf-1%2Freview~1.0?executionId=a%20b%26c"),
            Assert.Single(agent.Requests));
        Assert.Equal(200, status);
        Assert.Equal(agentSaid, body);
    }

    /// <summary>
    /// An agent from before the id ignores the parameter, matches by task id and answers without
    /// an <c>executionId</c>. That answer is returned untouched, so the bridge can see that the id
    /// was not compared.
    /// </summary>
    [Fact]
    public async Task AnAgentThatDoesNotKnowTheId_IsAnsweredThroughUnchanged()
    {
        const string agentSaid = """{"cancelled":true,"taskId":"wf-1/review"}""";
        var agent = new Agent(_ => Json(agentSaid));

        var (status, body) = await AnswerOfAsync(await AgentTaskCancel.ForwardAsync(
            new HttpClient(agent), AgentBase, "reviewer-one", "wf-1/review", "execution-1"));

        Assert.Equal(200, status);
        Assert.Equal(agentSaid, body);
        Assert.DoesNotContain("executionId", body);
    }

    /// <summary>An agent that fails, or cannot be reached, is not a 2xx with or without the id.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("execution-1")]
    public async Task AnAgentThatFailsOrCannotBeReached_IsNeverReportedAsDone(string? executionId)
    {
        var failing = new Agent(_ => Json("""{"error":"x"}""", HttpStatusCode.InternalServerError));
        var (failed, _) = await AnswerOfAsync(await AgentTaskCancel.ForwardAsync(
            new HttpClient(failing), AgentBase, "reviewer-one", "wf-1/review", executionId));
        Assert.Equal(500, failed);

        var unreachable = new Agent(_ => throw new HttpRequestException("connection refused"));
        var (unreached, _) = await AnswerOfAsync(await AgentTaskCancel.ForwardAsync(
            new HttpClient(unreachable), AgentBase, "reviewer-one", "wf-1/review", executionId));
        Assert.Equal(500, unreached);
    }

    /// <summary>
    /// The route is declared in Program.cs, which a test cannot host, so its two lines are read
    /// from the source: the parameter is optional and comes from the query, and the route waits
    /// for the forward before it answers. The bridge treats a 2xx as "the agent has dealt with
    /// it", which is only true while that wait is there.
    /// </summary>
    [Fact]
    public void TheRoute_TakesAnOptionalExecutionId_AndWaitsForTheAgent()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Fleet.sln"))) root = root.Parent;
        var program = File.ReadAllText(Path.Combine(root!.FullName, "src/Fleet.Orchestrator/Program.cs"));
        var from = program.IndexOf("app.MapPost(\"/api/agents/{name}/cancel/{**taskId}\"", StringComparison.Ordinal);
        var route = program[from..program.IndexOf("\n});", from, StringComparison.Ordinal)];

        Assert.Contains("(string name, string taskId, string? executionId, ", route);
        Assert.Contains("return await AgentTaskCancel.ForwardAsync(", route);
        Assert.Contains("decodedTaskId, executionId);", route);
        Assert.Equal(TimeSpan.FromSeconds(10), AgentTaskCancel.AgentTimeout);
    }
}
