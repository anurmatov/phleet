using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Fleet.Comms.Routes;
using Fleet.Conversations.Contracts;

namespace Fleet.Comms.Tests;

public sealed class JournalTurnBindingTests
{
    [Fact]
    public void EpochAndSequence_RejectLateAndConflictingWrites()
    {
        var clock = new Clock();
        var bindings = new JournalTurnBindings(clock);
        var first = Bound("one", 1);
        Assert.Equal(204, bindings.Put("agent1", first));
        Assert.Equal(204, bindings.Put("agent1", first));
        Assert.Equal(409, bindings.Put("agent1", first with { ChatId = 202 }));
        Assert.Equal(204, bindings.Put("agent1", first with { Seq = 2 }));
        Assert.Equal(409, bindings.Put("agent1", first));
        Assert.Equal(204, bindings.Put("agent1", Bound("two", 1)));
        Assert.Equal(409, bindings.Put("agent1", first with { Seq = 99 }));
        Assert.Equal("two", bindings.Get("agent1")!.Epoch);
        Assert.Null(bindings.Get("Agent1"));
    }

    [Fact]
    public void Renewal_RefreshesTtl_AndExpiryDoesNotAcceptAnOldSequence()
    {
        var clock = new Clock();
        var bindings = new JournalTurnBindings(clock);
        var body = Bound("one", 2);
        bindings.Put("agent1", body);
        clock.Advance(179);
        Assert.NotNull(bindings.Get("agent1"));
        Assert.Equal(204, bindings.Put("agent1", body));
        clock.Advance(180);
        Assert.Null(bindings.Get("agent1"));
        Assert.Equal((0, 1), bindings.Counts());
        Assert.Equal(409, bindings.Put("agent1", body with { Seq = 1 }));
        Assert.Equal(204, bindings.Put("agent1", body));
        Assert.NotNull(bindings.Get("agent1"));
    }

    [Fact]
    public void Capacity_EvictsOnlyExpiredSubjects()
    {
        var clock = new Clock();
        var bindings = new JournalTurnBindings(clock);
        for (var i = 0; i < JournalTurnBindings.MaxSubjects; i++)
            Assert.Equal(204, bindings.Put($"subject{i}", Bound("one", 1)));
        Assert.Equal(503, bindings.Put("extra", Bound("one", 1)));
        Assert.Equal(204, bindings.Put("subject0", Bound("one", 2)));
        clock.Advance(180);
        Assert.Equal(204, bindings.Put("extra", Bound("one", 1)));
        Assert.Equal((1, 0), bindings.Counts());
    }

    [Theory]
    [InlineData("read")]
    [InlineData("status")]
    [InlineData("ingest-service")]
    public async Task WrongPurpose_IsUnauthorizedBeforeMalformedBody(string purpose)
    {
        await using var host = await JournalTestHost.StartAsync();
        using var response = await host.SendAsync(HttpMethod.Put, JournalTurnBindings.Path,
            "not json", JournalTestHost.Token(purpose));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("{\"error\":\"unauthorized\"}", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"epoch\":\"one\",\"seq\":1,\"state\":\"bound\"}")]
    [InlineData("{\"epoch\":\"one\",\"seq\":1,\"state\":\"unbound\",\"chatId\":101}")]
    [InlineData("{\"epoch\":\"one\",\"epoch\":\"two\",\"seq\":1,\"state\":\"unbound\"}")]
    [InlineData("{\"epoch\":\"one\",\"seq\":1,\"state\":\"unbound\",\"unknown\":1}")]
    public async Task MalformedBody_IsBadRequest(string body)
    {
        await using var host = await JournalTestHost.StartAsync();
        using var response = await host.SendAsync(HttpMethod.Put, JournalTurnBindings.Path,
            body, JournalTestHost.Token("ingest"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Route_AcceptsBindingAndUnbinding_RejectsOversizeAndStale()
    {
        await using var host = await JournalTestHost.StartAsync();
        var token = JournalTestHost.Token("ingest");
        foreach (var body in new[] {
            "{\"epoch\":\"one\",\"seq\":1,\"state\":\"bound\",\"chatKind\":\"private\",\"botId\":7001,\"chatId\":101}",
            "{\"epoch\":\"one\",\"seq\":2,\"state\":\"unbound\"}" })
        {
            using var response = await host.SendAsync(HttpMethod.Put, JournalTurnBindings.Path, body, token);
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }
        using var stale = await host.SendAsync(HttpMethod.Put, JournalTurnBindings.Path,
            "{\"epoch\":\"one\",\"seq\":1,\"state\":\"unbound\"}", token);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("{\"error\":\"stale\"}", await stale.Content.ReadAsStringAsync());
        using var large = await host.SendAsync(HttpMethod.Put, JournalTurnBindings.Path, new string(' ', 1025), token);
        Assert.Equal(HttpStatusCode.BadRequest, large.StatusCode);
    }

    [Fact]
    public void LastFourEpochs_CannotTakeBackTheCurrentBinding()
    {
        var bindings = new JournalTurnBindings(new Clock());
        for (var i = 1; i <= 5; i++)
            Assert.Equal(204, bindings.Put("agent1", Bound($"epoch{i}", 1)));
        for (var i = 2; i <= 4; i++)
            Assert.Equal(409, bindings.Put("agent1", Bound($"epoch{i}", 999)));
        Assert.Equal("epoch5", bindings.Get("agent1")!.Epoch);
    }

    [Fact]
    public async Task UnauthorizedBinding_DoesNotReadAnyBodyBytes()
    {
        await using var host = await JournalTestHost.StartAsync();
        using var body = new UnreadableBody();
        var response = await host.Server.SendAsync(context =>
        {
            context.Request.Method = "PUT";
            context.Request.Path = JournalTurnBindings.Path;
            context.Request.Body = body;
            context.Request.Headers.Authorization = "Bearer " + JournalTestHost.Token("read");
        });
        Assert.Equal(401, response.Response.StatusCode);
        Assert.Equal(0, body.Reads);
    }

    [Fact]
    public async Task Status_ReportsLiveAndExpiredBindings_OnTheCommsClock()
    {
        var clock = new Clock();
        await using var host = await JournalTestHost.StartAsync(time: clock);
        using var put = await host.SendAsync(HttpMethod.Put, JournalTurnBindings.Path,
            "{\"epoch\":\"one\",\"seq\":1,\"state\":\"bound\",\"chatKind\":\"private\",\"botId\":7001,\"chatId\":101}", JournalTestHost.Token("ingest"));
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        foreach (var expired in new[] { false, true })
        {
            if (expired) clock.Advance(180);
            using var status = await host.SendAsync(HttpMethod.Get, "/journal/v1/status", null, JournalTestHost.Token("status"));
            using var document = JsonDocument.Parse(await status.Content.ReadAsStringAsync());
            var counts = document.RootElement.GetProperty("turnBindings");
            Assert.Equal(expired ? 0 : 1, counts.GetProperty("active").GetInt32());
            Assert.Equal(expired ? 1 : 0, counts.GetProperty("expired").GetInt32());
        }
    }

    private sealed class UnreadableBody : MemoryStream
    {
        public int Reads;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            Reads++;
            throw new InvalidOperationException("body must not be read before authentication");
        }
    }

    [Fact]
    public async Task Route_CapacityAnswersFixed503_AndUnboundNeverCountsAsActive()
    {
        await using var host = await JournalTestHost.StartAsync();
        var bindings = host.Services.GetRequiredService<JournalTurnBindings>();
        for (var i = 0; i < JournalTurnBindings.MaxSubjects; i++)
            Assert.Equal(204, bindings.Put($"subject{i}", new("one", 1, "unbound")));
        Assert.Null(bindings.Get("subject0"));
        Assert.Equal((0, 0), bindings.Counts());
        using var response = await host.SendAsync(HttpMethod.Put, JournalTurnBindings.Path,
            "{\"epoch\":\"one\",\"seq\":1,\"state\":\"unbound\"}", JournalTestHost.Token("ingest"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("{\"error\":\"binding_capacity\"}", await response.Content.ReadAsStringAsync());
    }

    private static JournalTurnBinding Bound(string epoch, long seq) =>
        new(epoch, seq, "bound", "private", 7001, 101);

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(int seconds) => _now += TimeSpan.FromSeconds(seconds);
    }
}
