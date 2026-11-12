using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Fleet.Orchestrator.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;

namespace Fleet.Orchestrator.Tests.Endpoints;

public class SignalGateIdentityTests
{
    [Theory]
    [InlineData("merge-approval", "merge-approval", "gate-new", "gate-new", "run-1", "Running", 200, "gate-new")]
    [InlineData("acceptance-decision", "acceptance-continue", "gate-new", "gate-new", "run-1", "Running", 200, "gate-new")]
    [InlineData("acceptance-decision", "acceptance-blocked", "gate-new", "gate-new", "run-1", "Running", 200, "gate-new")]
    [InlineData("merge-approval", "merge-approval", "gate-new", "gate-old", "run-1", "Running", 409, null)]
    [InlineData("merge-approval", "merge-approval", "gate-new", "gate-new", "run-1", "Completed", 409, null)]
    [InlineData("merge-approval", "revision", "gate-new", "gate-new", "run-1", "Running", 409, null)]
    [InlineData("acceptance-decision", "acceptance-blocked", null, null, "run-1", "Running", 409, null)]
    [InlineData("merge-approval", "merge-approval", null, null, "run-1", "Running", 200, "client-forged")]
    [InlineData("merge-approval", "revision", null, null, "run-1", "Running", 200, "client-forged")]
    [InlineData("merge-approval", "merge-approval", null, "gate-old", "run-1", "Running", 409, null)]
    [InlineData("merge-approval", "merge-approval", "gate-new", null, "run-1", "Running", 409, null)]
    [InlineData("merge-approval", "merge-approval", "gate-new", "gate-new", null, "Running", 400, null)]
    public async Task Signal_ActualEndpoint_GateChecksPrecedeExactRunSend(string signal, string phase, string? current, string? expected, string? run, string status, int code, string? sentRef)
    {
        var fake = new FakeSignalClient { State = new(status, phase, current) };
        await using var app = await Start(fake);
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        var response = await client.PostAsJsonAsync("/api/workflows/fleet/signal/wf-1", new
        {
            signalName = signal,
            workflowType = "UwePrImplementationWorkflow",
            payload = "{\"Decision\":\"approved\",\"Ref\":\"client-forged\"}",
            runId = run,
            expectedGateRef = expected
        });
        Assert.Equal(code, (int)response.StatusCode);
        Assert.Equal(code == 200 ? 1 : 0, fake.Sends);
        Assert.Equal(run is null ? 0 : 1, fake.Describes);
        if (code == 200)
        {
            Assert.Equal("run-1", fake.SentRun);
            Assert.Equal(sentRef, JsonDocument.Parse(fake.Payload!).RootElement.GetProperty("Ref").GetString());
            if (current is null) Assert.Equal("{\"Decision\":\"approved\",\"Ref\":\"client-forged\"}", fake.Payload);
        }
        if (code == 409 && current is not null && phase == "merge-approval" && status == "Running")
        {
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("stale_gate", body.GetProperty("error").GetString());
            Assert.Equal(expected, body.GetProperty("expectedGateRef").ValueKind == JsonValueKind.Null ? null : body.GetProperty("expectedGateRef").GetString());
            Assert.Equal(current, body.GetProperty("currentGateRef").GetString());
        }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Describe_ErrorOrTenSecondTimeout_Returns503WithoutSignal(bool hangs)
    {
        var fake = new FakeSignalClient { Hangs = hangs, Throws = !hangs };
        await using var app = await Start(fake);
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()), Timeout = TimeSpan.FromSeconds(20) };
        var response = await client.PostAsJsonAsync("/api/workflows/fleet/signal/wf-1", new
        {
            signalName = "merge-approval",
            runId = "run-1",
            expectedGateRef = "gate-new",
            payload = "{}"
        });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode); Assert.Equal(0, fake.Sends);
    }
    [Fact]
    public async Task Signal_WithoutWorkflowType_CannotBypassGateBinding()
    {
        var fake = new FakeSignalClient(); await using var app = await Start(fake);
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        var response = await client.PostAsJsonAsync("/api/workflows/fleet/signal/wf-1", new { signalName = "merge-approval", payload = "{}" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); Assert.Equal(0, fake.Describes); Assert.Equal(0, fake.Sends);
    }
    [Theory]
    [InlineData(null, "operator", "approved", "Running", "request-approval", "run-1", "visit-1", "UweHumanApprovalRequestWorkflow", 401)]
    [InlineData("operator", null, "approved", "Running", "request-approval", "run-1", "visit-1", "UweHumanApprovalRequestWorkflow", 401)]
    [InlineData("operator", "operator", "approved", "Running", "request-approval", "run-1", "visit-1", "UweHumanApprovalRequestWorkflow", 200)]
    [InlineData("operator", "operator", "rejected", "Running", "request-approval", "run-1", "visit-1", "UweHumanApprovalRequestWorkflow", 200)]
    [InlineData("operator", "operator", "APPROVED", "Running", "request-approval", "run-1", "visit-1", "UweHumanApprovalRequestWorkflow", 400)]
    [InlineData("operator", "operator", "approved", "Completed", "request-approval", "run-1", "visit-1", "UweHumanApprovalRequestWorkflow", 409)]
    [InlineData("operator", "operator", "approved", "Running", "other", "run-1", "visit-1", "UweHumanApprovalRequestWorkflow", 409)]
    [InlineData("operator", "operator", "approved", "Running", "request-approval", null, "visit-1", "UweHumanApprovalRequestWorkflow", 409)]
    [InlineData("operator", "operator", "approved", "Running", "request-approval", "run-1", null, "UweHumanApprovalRequestWorkflow", 409)]
    [InlineData("operator", "operator", "approved", "Running", "request-approval", "run-1", "visit-1", "OtherWorkflow", 409)]
    public async Task RequestApproval_AuthAndExactDescribedGate_Required(string? token, string? configured, string decision,
        string status, string phase, string? gateRef, string? visit, string type, int code)
    {
        var fake = new FakeSignalClient { State = new(status, phase, gateRef, visit, type) };
        await using var app = await Start(fake, configured);
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        if (token != null) client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        var response = await client.PostAsJsonAsync("/api/workflows/fleet/signal/wf-1", new
        {
            signalName = "request-approval", runId = "run-1", expectedGateRef = "run-1",
            payload = JsonSerializer.Serialize(new { Decision = decision, Comment = "\"literal\"\n{{input.Action}}" })
        });
        Assert.Equal(code, (int)response.StatusCode);
        Assert.Equal(code == 200 ? 1 : 0, fake.Sends);
        if (code == 200)
        {
            var payload = JsonSerializer.Deserialize<JsonElement>(fake.Payload!);
            Assert.Equal("run-1", payload.GetProperty("Ref").GetString());
            Assert.Equal("visit-1", payload.GetProperty("VisitId").GetString());
        }
    }

    [Theory]
    [InlineData("{\"Decision\":\"approved\",\"Decision\":\"rejected\"}")]
    [InlineData("{\"decision\":\"approved\"}")]
    [InlineData("{\"Decision\":\"approved\",\"Ref\":\"forged\"}")]
    [InlineData("{\"Decision\":\"approved\",\"Comment\":null}")]
    [InlineData("{\"Decision\":\"approved\",\"comment\":\"x\"}")]
    public async Task RequestApproval_InvalidPayload_Refused(string payload)
    {
        var fake = new FakeSignalClient { State = new("Running", "request-approval", "run-1", "visit-1", "UweHumanApprovalRequestWorkflow") };
        await using var app = await Start(fake, "operator");
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        client.DefaultRequestHeaders.Authorization = new("Bearer", "operator");
        var response = await client.PostAsJsonAsync("/api/workflows/fleet/signal/wf-1", new
        { signalName = "request-approval", runId = "run-1", expectedGateRef = "run-1", payload });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, fake.Sends);
    }
    private static async Task<WebApplication> Start(FakeSignalClient fake, string? token = null)
    {
        var builder = WebApplication.CreateBuilder();
        // Keep the owned HTTP fixture independent of application listen configuration.
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Orchestrator:AuthToken"] = token });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton<IWorkflowSignalClient>(fake);
        var app = builder.Build(); app.MapWorkflowSignalEndpoints(); await app.StartAsync(); return app;
    }
    private sealed class FakeSignalClient : IWorkflowSignalClient
    {
        public bool IsConfigured => true;
        public WorkflowSignalGate State = new("Running", "merge-approval", "gate-new");
        public bool Hangs, Throws; public int Describes, Sends; public string? SentRun, Payload;
        public async Task<WorkflowSignalGate> DescribeSignalGateAsync(string ns, string id, string runId, CancellationToken ct)
        {
            Describes++; Assert.Equal("run-1", runId);
            if (Throws) throw new InvalidOperationException("untrusted error detail");
            if (Hangs) await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return State;
        }
        public Task<bool> SignalWorkflowAsync(string ns, string id, string? runId, string signal, string payload, CancellationToken ct)
        { Sends++; SentRun = runId; Payload = payload; return Task.FromResult(true); }
    }
}
