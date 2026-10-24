using System.Diagnostics;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace Fleet.Identity.Tests;

public sealed class AdmissionBudgetTests
{
    [Fact]
    public async Task StalledBodyStillHasTwoSecondAdmissionBudget_WithoutAnyReceiverOrNonce()
    {
        await using var f = new IdentityFixture(); await f.StartAsync();
        var proof = (await f.Evaluate(f.Evaluation(IdentityFixture.Initialize))).HeaderMutations.Single().Write.Value;
        var context = new DefaultHttpContext(); context.Request.Method = "POST"; context.Request.Path = "/mcp";
        context.Request.Scheme = "https"; context.Request.Host = new(f.Options.IngressHost, f.Options.IngressPort);
        context.Features.Get<IHttpRequestFeature>()!.RawTarget = "/mcp";
        context.Request.ContentType = "application/json"; context.Request.Headers["X-Fleet-Assertion"] = proof;
        context.Request.Body = new StalledBody(); var clock = Stopwatch.StartNew();
        await f.CreateIngress(f.ReceiverClient).HandleAsync(context).WaitAsync(TimeSpan.FromSeconds(4));
        Assert.Equal(503, context.Response.StatusCode); Assert.True(clock.Elapsed >= TimeSpan.FromSeconds(1.8));
        Assert.Equal(0, f.ReceiverRequests); Assert.Equal(0, await f.Count("identity_request_nonces"));
    }
    private sealed class StalledBody : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        { await Task.Delay(Timeout.InfiniteTimeSpan, ct); return 0; }
    }
}
