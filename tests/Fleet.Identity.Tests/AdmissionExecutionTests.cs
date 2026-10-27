using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Xunit.Abstractions;

namespace Fleet.Identity.Tests;

public sealed class AdmissionExecutionTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualMapMcpSlowReadAndWriteCompleteOncePastAdmissionDeadline(bool write)
    {
        await using var f = new IdentityFixture(); await f.StartAsync();
        var session = await f.InitializeAsync();
        f.ReadDispatchDelay = write ? TimeSpan.Zero : TimeSpan.FromSeconds(3);
        f.Embedding.Delay = TimeSpan.FromSeconds(3);
        var body = write ? StoreCall : f.Call;
        var clock = Stopwatch.StartNew();
        using var response = await f.Send(body, session);
        var text = await response.Content.ReadAsStringAsync();
        output.WriteLine($"write={write} status={(int)response.StatusCode} elapsed_ms={clock.ElapsedMilliseconds} actual_reads={f.Reads.GetSnapshot().Sum(r => r.Total)} actual_index_attempts={f.Embedding.Executions} documents={Directory.GetFiles(Path.Combine(f.DirectoryPath, "memories"), "*.md", SearchOption.AllDirectories).Length} receiver_requests={f.ReceiverRequests}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(clock.Elapsed >= TimeSpan.FromSeconds(2));
        Assert.Contains(write ? "Stored memory" : "owned secret content", text);
        Assert.Equal(write ? 1 : 0, f.Embedding.Executions);
        Assert.Equal(write ? 0 : 1, f.Reads.GetSnapshot().Sum(r => r.Total));
        Assert.Equal(write ? 2 : 1, Directory.GetFiles(Path.Combine(f.DirectoryPath, "memories"), "*.md", SearchOption.AllDirectories).Length);
        Assert.Equal(2, f.ReceiverRequests); Assert.Equal(2, await f.Count("identity_request_nonces"));
    }
    [Fact]
    public async Task UpstreamWriteTimeoutIsUnconfirmedAndNeverAutomaticallyRetried()
    {
        await using var f = new IdentityFixture(); await f.StartAsync();
        var session = await f.InitializeAsync(); f.Options.UpstreamTimeoutSeconds = 1;
        f.Embedding.Delay = TimeSpan.FromSeconds(3);
        using var response = await f.Send(StoreCall, session);
        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        // The real store committed before its slow indexing dependency returned. Never resend that write.
        await f.Embedding.Finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        output.WriteLine($"timeout_status={(int)response.StatusCode} actual_index_attempts={f.Embedding.Executions} documents={Directory.GetFiles(Path.Combine(f.DirectoryPath, "memories"), "*.md", SearchOption.AllDirectories).Length} receiver_requests={f.ReceiverRequests}");
        Assert.Equal(1, f.Embedding.Executions); Assert.Equal(2, f.ReceiverRequests);
        Assert.Equal(2, Directory.GetFiles(Path.Combine(f.DirectoryPath, "memories"), "*.md", SearchOption.AllDirectories).Length);
        Assert.Contains(f.Captured.Messages, m => m.Contains("upstream_unconfirmed"));
    }

    [Theory]
    [InlineData("foreign")]
    [InlineData("revoked")]
    [InlineData("signature")]
    public async Task ExpiredSessionDoesNotTurnForeignRevokedOrInvalidIdentityInto404(string fault)
    {
        await using var f = new IdentityFixture(); await f.StartAsync();
        var session = await f.InitializeAsync();
        var proof = (await f.Evaluate(f.Evaluation(f.Call, session))).HeaderMutations.Single().Write.Value;
        await f.Store.EndSessionAsync(f.Identity, session, default);
        if (fault == "foreign") await f.Sql("UPDATE agent_identity_sessions SET IdentityEpoch='00000000-0000-0000-0000-000000000001'");
        if (fault == "revoked") await f.Sql("UPDATE agent_open_shell_launches SET RevokedAt='2020-01-01'");
        if (fault == "signature") { var parts = proof.Split('.'); parts[2] = (parts[2][0] == 'A' ? "B" : "A") + parts[2][1..]; proof = string.Join('.', parts); }
        var calls = f.ReceiverRequests;
        using var response = await f.Send(f.Call, session, proof);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode); Assert.Equal(calls, f.ReceiverRequests);
        Assert.Empty(f.Reads.GetSnapshot());
    }

    [Fact]
    public async Task UnknownDurableOwnershipDoesNotBecomeSessionRecovery404()
    {
        await using var f = new IdentityFixture(); await f.StartAsync();
        var session = await f.InitializeAsync(); f.ReceiverSessions = new(); f.OwnershipUnavailable = true;
        using var response = await f.Send(f.Call, session);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Empty(f.Reads.GetSnapshot()); Assert.Equal(1, f.ReceiverAdmitted);
    }

    internal static readonly string StoreCall = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 8, method = "tools/call",
        @params = new { name = "memory_store", arguments = new { type = "learning", title = "owned slow write", content = "synthetic persisted once", project = "alpha" } } });

    [Theory]
    [InlineData("expired")]
    [InlineData("deleted")]
    [InlineData("receiver-reset")]
    public async Task AuthenticatedOwnSessionLossReturns404_ThenFreshInitializationWorks(string reason)
    {
        await using var f = new IdentityFixture(); await f.StartAsync();
        var old = await f.InitializeAsync(); var retained = await f.InitializeAsync();
        if (reason == "expired") await f.Store.EndSessionAsync(f.Identity, old, default);
        if (reason == "deleted") { using var deleted = await f.Delete(old); Assert.True(deleted.IsSuccessStatusCode); }
        if (reason == "receiver-reset") f.ReceiverSessions = new();
        using var expired = await f.Send(f.Call, old);
        Assert.Equal(HttpStatusCode.NotFound, expired.StatusCode);
        Assert.Empty(f.Reads.GetSnapshot());
        var fresh = await f.InitializeAsync(); Assert.NotEqual(old, fresh);
        using var call = await f.Send(f.Call, fresh); Assert.Contains("owned secret content", await call.Content.ReadAsStringAsync());
        if (reason != "receiver-reset") { using var existing = await f.Send(f.Call, retained); Assert.True(existing.IsSuccessStatusCode); }
    }
}
