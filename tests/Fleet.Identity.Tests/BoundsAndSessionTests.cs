using System.Diagnostics;
using System.Net;
using System.Text;
using Fleet.Identity.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Openshell.Middleware.V1;

namespace Fleet.Identity.Tests;

public sealed class BoundsAndSessionTests
{
    [Fact]
    public async Task ActualMcpDeleteExpiresOnlyItsDurableSession_WhileOtherSessionWorks()
    {
        await using var f = new IdentityFixture(); await f.StartAsync();
        var first = await f.InitializeAsync(); var second = await f.InitializeAsync();
        using var removed = await f.Delete(first); Assert.True(removed.IsSuccessStatusCode);
        Assert.Equal(Decision.Deny, (await f.Evaluate(f.Evaluation(f.Call, first))).Decision);
        using var valid = await f.Send(f.Call, second); Assert.Contains("owned secret content", await valid.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("DELETE")]
    public async Task ForeignSessionCannotUseBodylessAuthenticatedMethods(string method)
    {
        await using var f = new IdentityFixture(); await f.StartAsync();
        var evaluation = f.Evaluation("", "foreign-session"); evaluation.Target.Method = method;
        Assert.Equal(Decision.Deny, (await f.Evaluate(evaluation)).Decision); Assert.Equal(0, f.ReceiverRequests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IngressAuthorizationAndHeaderBudgetAndCallerCancellationAreBounded_NoSecretLogs(bool cancelCaller)
    {
        await using var f = new IdentityFixture(); await f.StartAsync();
        var result = await f.Evaluate(f.Evaluation(IdentityFixture.Initialize));
        var proof = result.HeaderMutations.Single().Write.Value;
        var context = new DefaultHttpContext(); context.Request.Method = "POST"; context.Request.Path = "/mcp";
        context.Request.Scheme = "https"; context.Request.Host = new(f.Options.IngressHost, f.Options.IngressPort);
        context.Features.Get<IHttpRequestFeature>()!.RawTarget = "/mcp";
        context.Request.ContentType = "application/json"; context.Request.Headers["X-Fleet-Assertion"] = proof;
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(IdentityFixture.Initialize));
        using var cancellation = new CancellationTokenSource(); context.RequestAborted = cancellation.Token;
        var slow = new SlowReceiver(); using var client = new HttpClient(slow) { Timeout = Timeout.InfiniteTimeSpan };
        var ingress = f.CreateIngress(client); var elapsed = Stopwatch.StartNew();
        var task = ingress.HandleAsync(context);
        await slow.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        if (cancelCaller) cancellation.Cancel();
        await task.WaitAsync(TimeSpan.FromSeconds(4));
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(4)); Assert.True(slow.Cancelled);
        if (!cancelCaller) Assert.Equal(503, context.Response.StatusCode);
        Assert.Equal(0, f.ReceiverRequests);
        Assert.All(f.Captured.Messages, message => {
            Assert.DoesNotContain(proof, message); Assert.DoesNotContain(IdentityFixture.Initialize, message);
            Assert.DoesNotContain("sandbox1", message); Assert.DoesNotContain("agent1", message);
        });
    }

    [Fact]
    public async Task ReceiverLosesSessionAfterRestart_NoIngressFallbackToDurableOnly()
    {
        await using var f = new IdentityFixture(); await f.StartAsync();
        var session = await f.InitializeAsync();
        // A session unknown to the receiver cannot reach MapMcp even when the durable outside binding is valid.
        var other = "owned-outside-only-session"; await f.Store.BindSessionAsync(f.Identity, other, default);
        using var denied = await f.Send(f.Call, other); Assert.False(denied.IsSuccessStatusCode);
        using var retained = await f.Send(f.Call, session); Assert.Contains("owned secret content", await retained.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task DurableSessionAcquisitionIsBoundedAndNeverRebindsExpiredHistory()
    {
        await using var f = new IdentityFixture(); await f.StartAsync();
        for (var i = 0; i < 100; i++) await f.Store.BindSessionAsync(f.Identity, "owned-session-" + i, default);
        var denied = await Assert.ThrowsAsync<IdentityDeniedException>(() => f.Store.BindSessionAsync(f.Identity, "overflow", default));
        Assert.Equal("session_limit", denied.Code); Assert.Equal(100, await f.Count("agent_identity_sessions"));
        await f.Store.EndSessionAsync(f.Identity, "owned-session-0", default);
        await f.Store.BindSessionAsync(f.Identity, "new-session", default);
        Assert.Equal(101, await f.Count("agent_identity_sessions"));
    }

    private sealed class SlowReceiver : HttpMessageHandler
    {
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Cancelled;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            catch (OperationCanceledException) { Cancelled = true; throw; }
            throw new InvalidOperationException("unreachable");
        }
    }
}
