using System.Net;
using System.Text;
using Fleet.Memory.Configuration;
using Fleet.Memory.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Fleet.Memory.Tests;

// Actual ACL acquisition and policy evaluation over an owned in-process HTTP handler.
// This is not an identity assertion, verified listener or daemon integration test.
public sealed class VerifiedAclTests
{
    [Fact]
    public async Task WarmCacheCannotAuthorizeAfterFetchFailure_AndNoRetryOccurs()
    {
        var unavailable = true;
        var handler = new StubHandler(_ => Task.FromResult(unavailable
            ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : Json("{\"acl\":{}}")));
        await using var service = Service(handler);
        service.InjectAclForTesting(new() { ["agent1"] = ["alpha"] });
        Assert.True(service.CanRead("agent1", "alpha").allowed);
        await Assert.ThrowsAsync<VerifiedAclUnavailableException>(() => service.GetVerifiedSnapshotAsync());
        Assert.Equal(1, handler.Calls);
        unavailable = false;
        var recovered = await service.GetVerifiedSnapshotAsync();
        Assert.False(service.CanRead(recovered, "agent1", "alpha").allowed);
        Assert.True(service.CanRead("agent1", "alpha").allowed); // legacy warm cache is not authority
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task EachAcquisitionReadsAuthority_RevocationDoesNotChangeAnAuthorizedInflightSnapshot()
    {
        var allowed = true;
        var handler = new StubHandler(_ => Task.FromResult(Json(allowed
            ? "{\"acl\":{\"agent1\":[\"alpha\"]}}" : "{\"acl\":{}}")));
        await using var service = Service(handler);
        var first = await service.GetVerifiedSnapshotAsync();
        allowed = false;
        var second = await service.GetVerifiedSnapshotAsync();
        Assert.True(service.CanRead(first, "agent1", "alpha").allowed);
        Assert.False(service.CanRead(second, "agent1", "alpha").allowed);
        Assert.Equal(2, handler.Calls);
        Assert.False(service.IsAvailable); // verified snapshots never populate the legacy cache
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"acl\":null}")]
    [InlineData("{\"acl\":{\"agent1\":null}}")]
    [InlineData("{\"acl\":{\"agent1\":[null]}}")]
    [InlineData("{\"acl\":{\"agent1\":[\"alpha\"],\"AGENT1\":[\"*\"]}}")]
    [InlineData("{\"acl\":{\"agent1\":[\"alpha\"],\"agent1\":[\"*\"]}}")]
    [InlineData("not json")]
    public async Task MalformedAuthorityFailsClosed(string body)
    {
        var handler = new StubHandler(_ => Task.FromResult(Json(body)));
        await using var service = Service(handler);
        await Assert.ThrowsAsync<VerifiedAclUnavailableException>(() => service.GetVerifiedSnapshotAsync());
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task PolicyIsCapturedImmutably_AndRetainsExistingReadRules()
    {
        var options = new AclOptions { EnableProjectScopedAcl = true, AclPublicProjects = ["public"], AclOperatorAgent = "operator1" };
        await using var service = Service(new StubHandler(_ => Task.FromResult(Json(
            "{\"acl\":{\"agent1\":[\"ALPHA\"],\"operator1\":[\"*\"]}}"))), options);
        var snapshot = await service.GetVerifiedSnapshotAsync();
        options.AclPublicProjects[0] = "private";
        options.AclAllowNoProject = true;
        options.EnableProjectScopedAcl = false;
        Assert.True(service.CanRead(snapshot, "AGENT1", " Alpha ").allowed);
        Assert.True(service.CanRead(snapshot, "unknown", "public").allowed);
        Assert.False(service.CanRead(snapshot, "unknown", "private").allowed);
        Assert.False(service.CanRead(snapshot, "agent1", null).allowed);
        Assert.True(service.CanRead(snapshot, "operator1", null).allowed);
        await Assert.ThrowsAsync<VerifiedAclUnavailableException>(() => service.GetVerifiedSnapshotAsync());
    }

    [Fact]
    public async Task MissingOperatorWildcardBlocksEvenWhenLegacyCacheIsWarm()
    {
        await using var service = Service(new StubHandler(_ => Task.FromResult(Json("{\"acl\":{\"agent1\":[\"*\"]}}"))),
            new AclOptions { EnableProjectScopedAcl = true, AclOperatorAgent = "operator1" });
        service.InjectAclForTesting(new() { ["operator1"] = ["*"] });
        await Assert.ThrowsAsync<VerifiedAclUnavailableException>(() => service.GetVerifiedSnapshotAsync());
    }

    [Fact]
    public async Task ConfigChangeInvalidatesAnAcquisitionBeforeItCompletes()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var handler = new StubHandler(async ct =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                started.TrySetResult();
                await release.Task.WaitAsync(ct);
            }
            return Json("{\"acl\":{\"agent1\":[\"alpha\"]}}");
        });
        await using var service = Service(handler);
        var pending = service.GetVerifiedSnapshotAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var refresh = service.TriggerRefresh(); // generation changes before the task is returned
        release.SetResult();
        await Assert.ThrowsAsync<VerifiedAclUnavailableException>(() => pending);
        await refresh.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task CanceledRequestCannotYieldSnapshot_OrRetry()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new StubHandler(async ct => { started.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, ct); return Json("{}"); });
        await using var service = Service(handler);
        using var canceled = new CancellationTokenSource();
        var pending = service.GetVerifiedSnapshotAsync(canceled.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task OwnDeadlineCancelsHungAuthority_AndNoWarmFallbackOccurs()
    {
        var handler = new StubHandler(async ct =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return Json("{}");
        });
        await using var service = Service(handler);
        service.InjectAclForTesting(new() { ["agent1"] = ["*"] });
        await Assert.ThrowsAsync<VerifiedAclUnavailableException>(() =>
            service.GetVerifiedSnapshotAsync().WaitAsync(TimeSpan.FromSeconds(8)));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task DisabledAclDoesNotFetchOrInheritLegacyAllowAll()
    {
        var handler = new StubHandler(_ => Task.FromResult(Json("{\"acl\":{}}")));
        await using var service = Service(handler, new AclOptions { EnableProjectScopedAcl = false });
        Assert.True(service.CanRead("agent1", "alpha").allowed);
        await Assert.ThrowsAsync<VerifiedAclUnavailableException>(() => service.GetVerifiedSnapshotAsync());
        Assert.Equal(0, handler.Calls);
    }

    private static AclCacheService Service(StubHandler handler, AclOptions? options = null) => new(
        Options.Create(options ?? new AclOptions { EnableProjectScopedAcl = true }),
        Options.Create(new OrchestratorOptions { BaseUrl = "https://authority.example.invalid" }),
        NullLogger<AclCacheService>.Instance, handler);

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
        { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class StubHandler(Func<CancellationToken, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            Assert.Equal("/internal/agent-project-access", request.RequestUri!.AbsolutePath);
            return response(ct);
        }
    }
}
