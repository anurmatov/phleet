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
    private static async Task<WebApplication> Start(FakeSignalClient fake)
    {
        var builder = WebApplication.CreateBuilder();
        // Keep the owned HTTP fixture independent of application listen configuration.
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection();
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
