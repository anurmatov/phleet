using Fleet.Comms.Routes;
using Fleet.Conversations.Contracts;
using Fleet.Conversations.Journal;
namespace Fleet.Comms.Tests;
public sealed class JournalSendSourceResolverTests
{
    private const string Id = "01K00000000000000000000000";
    private sealed class Source : IJournalSendSource
    {
        public JournalSendSource? Row; public int Calls;
        public Task<JournalSendSource?> FindSendSourceAsync(string subject, string boundKey, long requester, string? messageId, long? telegramMessageId, int ordinal, CancellationToken ct = default)
        { Assert.Equal("agent1", subject); Assert.Equal(303, requester); Calls++; return Task.FromResult(Row); }
    }
    private sealed class Auth : IJournalCrossChatAuthorization
    {
        public bool Available = true, Effective = true; public string? Member = "member";
        public int Calls; public long? Chat, User;
        public Task<(bool Available, bool Effective, string? Member)> CheckAsync(string subject, long bot, long? chat, long? user, CancellationToken ct)
        { Calls++; Chat = chat; User = user; Assert.Equal(101, bot); return Task.FromResult((Available, Effective, Member)); }
    }
    private static JournalSendSource Row(string kind = "group", bool present = true) => new(new(Id, 0, "document", "application/pdf", 9, "not_archived", "media_disabled", null, null, null, null, null), "tg:group:-202", kind, -202, present, "synthetic-file", 101);
    private static (JournalSendSourceResolver Resolver, Source Source, Auth Auth) Setup(bool bind = true, long[]? excluded = null)
    {
        var bindings = new JournalTurnBindings(TimeProvider.System);
        if (bind) bindings.Put("agent1", new("epoch", 1, "bound", "private", 101, 303));
        var source = new Source(); var auth = new Auth();
        return (new(source, new(bindings, (excluded ?? []).ToHashSet()), auth), source, auth);
    }
    [Theory]
    [InlineData(false, true, 503)]
    [InlineData(true, false, 401)]
    [InlineData(true, true, 404)]
    public async Task Authorization_MasksMissingSource(bool available, bool effective, int status)
    {
        var (resolver, _, auth) = Setup(); auth.Available = available; auth.Effective = effective;
        var result = await resolver.ResolveAsync("agent1", new(Id), true, default);
        Assert.Equal(status, result.Status); Assert.Equal(1, auth.Calls); Assert.Null(auth.Chat); Assert.Null(auth.User);
    }
    [Theory]
    [InlineData("private", true, "private_source")]
    [InlineData("other", true, "unsupported_source")]
    [InlineData("group", false, "requester_not_participant")]
    public async Task DeniedSource_IsHeldUntilSwitchCheck(string kind, bool present, string reason)
    {
        var (resolver, source, auth) = Setup(); source.Row = Row(kind, present);
        var result = await resolver.ResolveAsync("agent1", new(Id), true, default);
        Assert.Equal(403, result.Status); Assert.Contains(reason, result.Error!); Assert.Null(auth.Chat); Assert.Equal(1, auth.Calls);
        auth.Effective = false;
        Assert.Equal(401, (await resolver.ResolveAsync("agent1", new(Id), true, default)).Status);
    }
    [Theory]
    [InlineData("member", 200)]
    [InlineData("not_member", 403)]
    [InlineData("unverified", 403)]
    public async Task GroupSource_AlwaysChecksLiveMembership(string member, int status)
    {
        var (resolver, source, auth) = Setup(); source.Row = Row(); auth.Member = member;
        Assert.Equal(status, (await resolver.ResolveAsync("agent1", new(Id), true, default)).Status);
        Assert.Equal(-202, auth.Chat); Assert.Equal(303, auth.User);
    }
    [Fact]
    public async Task SameRoute_RefusesCrossWithoutAuthorizationCall()
    {
        var (resolver, source, auth) = Setup(); source.Row = Row();
        var result = await resolver.ResolveAsync("agent1", new(Id), false, default);
        Assert.Equal(403, result.Status); Assert.Contains("cross_chat_disabled", result.Error!); Assert.Equal(0, auth.Calls);
    }
    [Fact]
    public async Task Exclusion_DoesNotRequestMembership()
    {
        var (resolver, source, auth) = Setup(excluded: [-202]); source.Row = Row();
        var result = await resolver.ResolveAsync("agent1", new(Id), true, default);
        Assert.Equal(409, result.Status); Assert.Contains("conversation_not_journaled", result.Error!); Assert.Null(auth.Chat);
    }
    [Fact]
    public async Task Unbound_DoesNoLookupOrAuthorization()
    {
        var (resolver, source, auth) = Setup(false);
        Assert.Equal(409, (await resolver.ResolveAsync("agent1", new(Id), true, default)).Status);
        Assert.Equal(0, source.Calls + auth.Calls);
    }
}
