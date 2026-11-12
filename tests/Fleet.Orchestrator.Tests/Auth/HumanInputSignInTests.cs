using System.Net;
using Fleet.Orchestrator.Endpoints;
using Fleet.Orchestrator.Models;

namespace Fleet.Orchestrator.Tests.Auth;

public class HumanInputSignInTests
{
    [Theory]
    [InlineData("browser", 200)]
    [InlineData("app", 200)]
    [InlineData("anonymous", 401)]
    [InlineData("browser-no-header", 403)]
    [InlineData("stale", 409)]
    [InlineData("history-down", 503)]
    public async Task HumanInput_UsesProductionSignInAndExactHistory(string kind, int status)
    {
        var io = new SignalIO { Down = kind == "history-down" };
        await using var host = await SignInHost.StartAsync(signalClient: io);
        var (cookie, _, _, token) = await host.EnrolledAppAsync();
        var response = await host.SendAsync(HttpMethod.Post, "/api/workflows/fleet/signal/request",
            bearer: kind == "app" ? token : null,
            cookie: kind is "anonymous" or "app" ? null : cookie,
            requestHeader: kind != "browser-no-header",
            body: new { signalName = "request-input", workflowType = "UweHumanInputWorkflow", runId = "run",
                expectedGateRef = kind == "stale" ? "old" : "run", payload = "{\"Value\":\"approved\",\"Comment\":\" unchanged \"}" });
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal(status == 200 ? 1 : 0, io.Sends);
    }

    private sealed class SignalIO : IWorkflowSignalClient
    {
        public bool IsConfigured => true;
        public bool Down;
        public int Sends;
        public Task<WorkflowSignalGate> DescribeSignalGateAsync(string ns, string id, string runId, CancellationToken ct)
        { Assert.Equal(("fleet", "request", "run"), (ns, id, runId)); return Task.FromResult(new WorkflowSignalGate("Running", "request-input", "run", "visit", "UweHumanInputWorkflow")); }
        public Task<List<WorkflowEventSummary>> FetchWorkflowEventsAsync(string ns, string id, string? runId = null, CancellationToken ct = default)
        {
            Assert.Equal(("fleet", "request", "run"), (ns, id, runId));
            if (Down) throw new IOException("synthetic outage");
            return Task.FromResult(new List<WorkflowEventSummary> { new(1, "WorkflowStarted", DateTimeOffset.UtcNow, null, null,
                "{\"Question\":\"q\",\"Context\":\"c\",\"AnswerMode\":\"approve-reject\",\"Origin\":{\"Kind\":\"agent\",\"RequesterAgent\":\"agent1\"}}", null, null, null) });
        }
        public Task<bool> SignalWorkflowAsync(string ns, string id, string? runId, string signal, string payload, CancellationToken ct)
        { Sends++; return Task.FromResult(true); }
    }
}
