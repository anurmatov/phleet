using Fleet.Conversations.Contracts;
using Fleet.Protocol;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fleet.Conversations.Tests;

/// <summary>
/// Reading an agent's own first-party chat back, against a real MySQL: the scope is the agent and
/// is in the statement, only things someone said are messages, and a page ends where the next begins.
/// </summary>
[Collection("mysql")]
public sealed class ChatHistoryReadStoreTests(MySqlFixture fixture)
{
    private readonly MySqlConversationStore _store = fixture.CreateStore();
    private readonly MySqlChatHistoryReadStore _history = new(fixture.ConnectionString, NullLogger.Instance);

    private const string Owner = "p_owner";

    private async Task<string> OpenAsync(string agent, string channel = "client") =>
        (await _store.OpenConversationAsync(new OpenConversationRequest
        {
            ChannelId = channel, ExternalRef = "conv-" + Ulid.NewUlid(), PrincipalId = Owner, AgentName = agent,
        })).ConversationId;

    /// <summary>
    /// Writes one event straight into the log at the next seq, at a chosen time. The agent's own
    /// append path needs a claimed delivery and a turn; what is read here is the stored row.
    /// </summary>
    private async Task<string> WriteAsync(string conversationId, string kind, string text, int minute)
    {
        var id = Ulid.NewUlid();
        var payload = text.Replace("\\", "\\\\").Replace("'", "''");
        await fixture.ExecuteAsync(
            $"""
             INSERT INTO conversation_events (conversation_id, seq, event_id, kind, payload_json, emitted_at)
             SELECT '{conversationId}', next_seq, '{id}', '{kind}', JSON_OBJECT('text', '{payload}'),
                    TIMESTAMP('2026-11-14 09:{minute:D2}:00')
               FROM conversations WHERE id = '{conversationId}';
             UPDATE conversations SET next_seq = next_seq + 1 WHERE id = '{conversationId}';
             """);
        return id;
    }

    private static ChatHistoryQuery All(int limit = 20) => new() { Limit = limit };

    [Fact]
    public async Task An_agent_reads_its_own_chat_newest_first_and_only_what_was_said()
    {
        var agent = "agent-" + Ulid.NewUlid().ToLowerInvariant();
        var other = "agent-" + Ulid.NewUlid().ToLowerInvariant();
        var mine = await OpenAsync(agent);
        var theirs = await OpenAsync(other);

        var question = await WriteAsync(mine, ConversationEventKind.SubmissionText, "what is open?", 1);
        await WriteAsync(mine, ConversationEventKind.TurnStarted, "", 2);
        await WriteAsync(mine, ConversationEventKind.TurnProgress, "", 3);
        var said = await WriteAsync(mine, ConversationEventKind.TurnMessage, "Looking at the board.", 4);
        var answer = await WriteAsync(mine, ConversationEventKind.TurnFinal, "Three issues are open.", 5);
        await WriteAsync(theirs, ConversationEventKind.SubmissionText, "something private", 6);

        var page = await _history.SearchAsync(agent, All());

        Assert.Equal([answer, said, question], page.Items.Select(m => m.EventId));
        Assert.Equal([false, false, true], page.Items.Select(m => m.FromUser));
        Assert.Equal("Three issues are open.", page.Items[0].Text);
        Assert.Equal(new DateTimeOffset(2026, 11, 14, 9, 5, 0, TimeSpan.Zero), page.Items[0].At);
        Assert.Null(page.Next);

        // The other agent's chat is not this one's to read, by search or by id.
        Assert.DoesNotContain(page.Items, m => m.Text.Contains("private", StringComparison.Ordinal));
        var theirMessage = Assert.Single((await _history.SearchAsync(other, All())).Items);
        Assert.Null(await _history.GetAsync(agent, theirMessage.EventId));
        Assert.Equal("something private", (await _history.GetAsync(other, theirMessage.EventId))!.Text);

        // An event that is not a message is not one by id either.
        Assert.Equal("what is open?", (await _history.GetAsync(agent, question))!.Text);
        Assert.Null(await _history.GetAsync(agent, Ulid.NewUlid()));
    }

    /// <summary>A conversation on another channel with the same agent is not first-party chat.</summary>
    [Fact]
    public async Task Only_the_client_channel_is_chat()
    {
        var agent = "agent-" + Ulid.NewUlid().ToLowerInvariant();
        var elsewhere = await OpenAsync(agent, channel: "example-adapter");
        await WriteAsync(elsewhere, ConversationEventKind.SubmissionText, "not chat", 1);

        Assert.Empty((await _history.SearchAsync(agent, All())).Items);
    }

    [Fact]
    public async Task Filters_narrow_the_callers_chat_and_text_is_matched_as_written_without_case()
    {
        var agent = "agent-" + Ulid.NewUlid().ToLowerInvariant();
        var conversation = await OpenAsync(agent);

        await WriteAsync(conversation, ConversationEventKind.SubmissionText, "Is the Minimum Width fixed?", 1);
        await WriteAsync(conversation, ConversationEventKind.TurnFinal, "The minimum width is 720px.", 2);
        await WriteAsync(conversation, ConversationEventKind.SubmissionText, "100% of rows? what about a_b", 3);
        await WriteAsync(conversation, ConversationEventKind.TurnFinal, "Nothing else matches 100 percent axb", 4);

        async Task<string[]> Texts(ChatHistoryQuery query) =>
            (await _history.SearchAsync(agent, query)).Items.Select(m => m.Text).ToArray();

        Assert.Equal(
            ["The minimum width is 720px.", "Is the Minimum Width fixed?"],
            await Texts(All() with { Contains = "MINIMUM width" }));

        Assert.Equal(["Is the Minimum Width fixed?"], await Texts(All() with { Contains = "minimum width", FromUser = true }));
        Assert.Equal(["The minimum width is 720px."], await Texts(All() with { Contains = "minimum width", FromUser = false }));

        // `%` and `_` are what they look like, not wildcards.
        Assert.Equal(["100% of rows? what about a_b"], await Texts(All() with { Contains = "100%" }));
        Assert.Equal(["100% of rows? what about a_b"], await Texts(All() with { Contains = "a_b" }));

        var noon = new DateTimeOffset(2026, 11, 14, 9, 2, 0, TimeSpan.Zero);
        Assert.Equal(3, (await Texts(All() with { Since = noon })).Length);
        Assert.Equal(["Is the Minimum Width fixed?"], await Texts(All() with { Until = noon }));
    }

    [Fact]
    public async Task Pages_do_not_overlap_and_end_with_no_next()
    {
        var agent = "agent-" + Ulid.NewUlid().ToLowerInvariant();
        var conversation = await OpenAsync(agent);
        var ids = new List<string>();
        // Two at the same instant, so the id has to break the tie.
        foreach (var minute in new[] { 1, 2, 2, 3, 4 })
            ids.Add(await WriteAsync(conversation, ConversationEventKind.SubmissionText, $"message at {minute}", minute));

        var seen = new List<string>();
        ChatHistoryPosition? before = null;
        for (var pages = 0; pages < 10; pages++)
        {
            var page = await _history.SearchAsync(agent, All(limit: 2) with { Before = before });
            seen.AddRange(page.Items.Select(m => m.EventId));
            Assert.True(page.Items.Count <= 2);
            before = page.Next;
            if (before is null) break;
        }

        Assert.Equal(5, seen.Count);
        Assert.Equal(ids.Order(StringComparer.Ordinal), seen.Order(StringComparer.Ordinal));
        Assert.Equal(seen.Distinct(StringComparer.Ordinal).Count(), seen.Count);
    }
}
