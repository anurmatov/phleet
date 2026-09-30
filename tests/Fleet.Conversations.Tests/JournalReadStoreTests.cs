using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Fleet.Comms;
using Fleet.Comms.Configuration;
using Fleet.Comms.Routes;
using Fleet.Conversations.Contracts;
using Fleet.Conversations.Journal;
using Fleet.Protocol;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MySqlConnector;

namespace Fleet.Conversations.Tests;

/// <summary>
/// The journal's read side against a real MySQL 8.0, through the real journal listener and its MCP
/// endpoint (#394 AC8 a–e): restart-stable records and cursors, keyset pages under concurrent
/// inserts, a commit held open across page reads, a late observer row, and a late transcript — plus
/// the scope rule as the SQL applies it.
/// </summary>
/// <remarks>
/// Every test reads as its own subject and writes to its own chat, so the shared schema's other
/// rows are out of its scope by construction rather than by cleanup.
/// </remarks>
[Collection("mysql")]
public sealed class JournalReadStoreTests(MySqlFixture fixture)
{
    private const int Page = 20;

    private string Db => fixture.ConnectionString;

    // ── (a) restart ──────────────────────────────────────────────────────────

    [Fact]
    public async Task The_three_tools_answer_identically_after_the_host_and_store_are_recreated()
    {
        var reader = Subject();
        var chat = Chat();
        var t0 = BaseTime();
        var store = new MySqlJournalStore(Db, NullLogger.Instance);

        for (var i = 1; i <= 5; i++)
        {
            await store.IngestAsync(Message(chat, i, t0.AddMinutes(i), $"restart message {i}",
                transcript: i == 2 ? "spoken words" : null, replyTo: i == 3 ? 1 : null,
                attachments: i == 4 ? [Attachment(0)] : []), reader);
        }

        var conversation = await ConversationIdAsync(chat);
        var firstId = await MessageIdAsync(chat, 1);

        async Task<string[]> ReadAllAsync(ReadHost host, string? searchCursor, string? replayCursor)
        {
            var token = host.ReadToken(reader);
            return
            [
                (await host.CallAsync(token, "search_messages", new { conversation_id = conversation, limit = 2, cursor = searchCursor })).Text,
                (await host.CallAsync(token, "get_message", new { message_id = firstId })).Text,
                (await host.CallAsync(token, "get_message", new { telegram_chat_id = chat, telegram_message_id = 3 })).Text,
                (await host.CallAsync(token, "get_conversation", new { conversation_id = conversation, limit = 2, cursor = replayCursor })).Text,
            ];
        }

        string[] before, beforePageTwo;
        string searchCursor, replayCursor;

        await using (var host = await ReadHost.StartAsync(Db))
        {
            before = await ReadAllAsync(host, null, null);
            searchCursor = JsonDocument.Parse(before[0]).RootElement.GetProperty("next_cursor").GetString()!;
            replayCursor = JsonDocument.Parse(before[3]).RootElement.GetProperty("next_cursor").GetString()!;
            beforePageTwo = await ReadAllAsync(host, searchCursor, replayCursor);
        }

        // A new host, a new store, new pools: nothing survives but the database and the cursors.
        await using (var host = await ReadHost.StartAsync(Db))
        {
            Assert.Equal(before, await ReadAllAsync(host, null, null));
            Assert.Equal(beforePageTwo, await ReadAllAsync(host, searchCursor, replayCursor));
        }

        // The records are the stored ones, not merely stable: reply resolved, transcript, attachment.
        using var third = JsonDocument.Parse(before[2]);
        Assert.Equal(firstId, third.RootElement.GetProperty("reply_to").GetProperty("message_id").GetString());
        Assert.Equal(1, third.RootElement.GetProperty("reply_to").GetProperty("telegram_message_id").GetInt64());
    }

    // ── (b) G1/G2 under concurrent inserts ───────────────────────────────────

    [Fact]
    public async Task Search_pages_under_concurrent_inserts_return_every_existing_row_once()
    {
        var reader = Subject();
        var chat = Chat();
        var t0 = BaseTime();
        var store = new MySqlJournalStore(Db, NullLogger.Instance);

        var existing = new List<string>();
        for (var i = 0; i < 60; i++)
        {
            var result = await store.IngestAsync(Message(chat, 2 * i + 2, t0.AddMinutes(i), $"existing {i}"), reader);
            existing.Add(result.MessageId!);
        }

        await using var host = await ReadHost.StartAsync(Db);
        var token = host.ReadToken(reader);

        // 30 rows whose sent_at falls between the existing ones, across the whole range, written
        // while the traversal runs.
        var writer = Task.Run(async () =>
        {
            var concurrent = new MySqlJournalStore(Db, NullLogger.Instance);
            for (var j = 0; j < 30; j++)
            {
                await concurrent.IngestAsync(Message(chat, 2 * j + 1 + 1000, t0.AddMinutes(2 * j).AddSeconds(30), $"concurrent {j}"), reader);
                await Task.Delay(15);
            }
        });

        var seen = new List<string>();
        string? cursor = null;
        do
        {
            var page = (await host.CallAsync(token, "search_messages", new { limit = Page, cursor })).Json;
            seen.AddRange(Ids(page));
            cursor = page.GetProperty("next_cursor").GetString();
            await Task.Delay(40);
        }
        while (cursor is not null);

        await writer;

        Assert.Equal(seen.Count, seen.Distinct().Count());
        Assert.Empty(existing.Except(seen));
    }

    [Fact]
    public async Task Replay_pages_under_concurrent_inserts_return_every_existing_row_once_in_order()
    {
        var reader = Subject();
        var chat = Chat();
        var t0 = BaseTime();
        var store = new MySqlJournalStore(Db, NullLogger.Instance);

        var existing = new List<string>();
        for (var i = 0; i < 60; i++)
            existing.Add((await store.IngestAsync(Message(chat, 10 * (i + 1), t0.AddMinutes(i), $"existing {i}"), reader)).MessageId!);

        var conversation = await ConversationIdAsync(chat);
        await using var host = await ReadHost.StartAsync(Db);
        var token = host.ReadToken(reader);

        var writer = Task.Run(async () =>
        {
            var concurrent = new MySqlJournalStore(Db, NullLogger.Instance);
            for (var j = 0; j < 30; j++)
            {
                // Platform ids between the existing ones, so they land across the whole order.
                await concurrent.IngestAsync(Message(chat, 20 * (j + 1) + 5, t0.AddMinutes(2 * j), $"concurrent {j}"), reader);
                await Task.Delay(15);
            }
        });

        var seen = new List<(string Id, long TelegramId)>();
        string? cursor = null;
        do
        {
            var page = (await host.CallAsync(token, "get_conversation", new { conversation_id = conversation, limit = Page, cursor })).Json;
            seen.AddRange(page.GetProperty("items").EnumerateArray().Select(item =>
                (item.GetProperty("message_id").GetString()!, item.GetProperty("telegram_message_id").GetInt64())));
            cursor = page.GetProperty("next_cursor").GetString();
            await Task.Delay(40);
        }
        while (cursor is not null);

        await writer;

        Assert.Equal(seen.Count, seen.Select(s => s.Id).Distinct().Count());
        Assert.Empty(existing.Except(seen.Select(s => s.Id)));
        Assert.Equal(seen.Select(s => s.TelegramId).Order(), seen.Select(s => s.TelegramId));
    }

    // ── (c) G3: a commit held open for 6 s ───────────────────────────────────

    [Fact]
    public async Task A_commit_held_open_across_pages_is_returned_once_when_ahead_and_never_when_behind()
    {
        var reader = Subject();
        var chat = Chat();
        var t0 = BaseTime();
        var store = new MySqlJournalStore(Db, NullLogger.Instance);

        var existing = new List<string>();
        for (var i = 0; i < 60; i++)
            existing.Add((await store.IngestAsync(Message(chat, i + 1, t0.AddMinutes(i), $"existing {i}"), reader)).MessageId!);

        var conversation = await ConversationIdAsync(chat);
        await using var host = await ReadHost.StartAsync(Db);
        var token = host.ReadToken(reader);

        // Newest first: page 1 is minutes 59..40 and page 2 is 39..20. X_behind (30.5) is passed by
        // the time it commits; X_ahead (5.5) is still ahead.
        var ahead = Ulid.NewUlid();
        var behind = Ulid.NewUlid();

        await using var held = new MySqlConnection(Db);
        await held.OpenAsync();
        await using var transaction = await held.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted);
        await InsertRawAsync(held, transaction, ahead, conversation, 1001, t0.AddMinutes(5.5), reader);
        await InsertRawAsync(held, transaction, behind, conversation, 1002, t0.AddMinutes(30.5), reader);
        var clock = Stopwatch.StartNew();

        var seen = new List<string>();

        var read = Stopwatch.StartNew();
        var first = (await host.CallAsync(token, "search_messages", new { limit = Page })).Json;
        Assert.True(read.Elapsed < TimeSpan.FromSeconds(3), $"page 1 waited on the open transaction: {read.Elapsed}");
        seen.AddRange(Ids(first));

        await Task.Delay(TimeSpan.FromSeconds(1));

        read.Restart();
        var second = (await host.CallAsync(token, "search_messages",
            new { limit = Page, cursor = first.GetProperty("next_cursor").GetString() })).Json;
        Assert.True(read.Elapsed < TimeSpan.FromSeconds(3), $"page 2 waited on the open transaction: {read.Elapsed}");
        seen.AddRange(Ids(second));

        Assert.DoesNotContain(ahead, seen);
        Assert.DoesNotContain(behind, seen);

        // A timer can fire a hair early against the stopwatch, so wait until the stopwatch agrees.
        while (clock.Elapsed < TimeSpan.FromSeconds(6))
            await Task.Delay(TimeSpan.FromSeconds(6) - clock.Elapsed + TimeSpan.FromMilliseconds(20));
        await transaction.CommitAsync();

        var cursor = second.GetProperty("next_cursor").GetString();
        while (cursor is not null)
        {
            var page = (await host.CallAsync(token, "search_messages", new { limit = Page, cursor })).Json;
            seen.AddRange(Ids(page));
            cursor = page.GetProperty("next_cursor").GetString();
        }

        Assert.Equal(seen.Count, seen.Distinct().Count());
        Assert.Empty(existing.Except(seen));
        Assert.Single(seen, id => id == ahead);
        Assert.DoesNotContain(behind, seen);

        // Committed, visible, and simply behind the cursor: a fresh traversal does return it.
        var fresh = await host.CallAsync(token, "get_message", new { message_id = behind });
        Assert.False(fresh.IsError);
    }

    // ── (d) G3: a late observer row ──────────────────────────────────────────

    [Fact]
    public async Task A_late_observer_row_is_returned_at_most_once_and_only_ahead_of_the_cursor()
    {
        var reader = Subject();
        var other = Subject();
        var chat = Chat();
        var t0 = BaseTime();
        var store = new MySqlJournalStore(Db, NullLogger.Instance);

        for (var i = 0; i < 60; i++)
            await store.IngestAsync(Message(chat, i + 1, t0.AddMinutes(i), $"existing {i}"), reader);

        // Observed only by another runtime, for now.
        var aheadRecord = Message(chat, 1001, t0.AddMinutes(10.5), "late ahead");
        var behindRecord = Message(chat, 1002, t0.AddMinutes(50.5), "late behind");
        var ahead = (await store.IngestAsync(aheadRecord, other)).MessageId!;
        var behind = (await store.IngestAsync(behindRecord, other)).MessageId!;

        await using var host = await ReadHost.StartAsync(Db);
        var token = host.ReadToken(reader);

        var first = (await host.CallAsync(token, "search_messages", new { limit = Page })).Json;
        var seen = Ids(first).ToList();
        Assert.DoesNotContain(ahead, seen);

        // The reader's own runtime now reports both: an observer row each, nothing else.
        Assert.Equal(JournalIngestOutcome.ObserverAdded,
            (await store.IngestAsync(aheadRecord with { EventId = Ulid.NewUlid() }, reader)).Outcome);
        Assert.Equal(JournalIngestOutcome.ObserverAdded,
            (await store.IngestAsync(behindRecord with { EventId = Ulid.NewUlid() }, reader)).Outcome);

        var cursor = first.GetProperty("next_cursor").GetString();
        while (cursor is not null)
        {
            var page = (await host.CallAsync(token, "search_messages", new { limit = Page, cursor })).Json;
            seen.AddRange(Ids(page));
            cursor = page.GetProperty("next_cursor").GetString();
        }

        Assert.Equal(seen.Count, seen.Distinct().Count());
        Assert.Single(seen, id => id == ahead);
        Assert.DoesNotContain(behind, seen);
        Assert.Equal(61, seen.Count);
    }

    // ── (e) G3/G4: a transcript filled in later ──────────────────────────────

    [Fact]
    public async Task A_transcript_filled_between_pages_is_returned_at_most_once_and_never_again()
    {
        var reader = Subject();
        var other = Subject();
        var chat = Chat();
        var t0 = BaseTime();
        var store = new MySqlJournalStore(Db, NullLogger.Instance);

        var matching = new List<JournalRecord>();
        for (var i = 0; i < 60; i++)
        {
            var record = Message(chat, i + 1, t0.AddMinutes(i), $"zebra sighting {i}");
            matching.Add(record);
            await store.IngestAsync(record, reader);
        }

        // Two rows that do not match yet: one ahead of where page 1 will end, one behind it.
        var aheadRecord = Message(chat, 1001, t0.AddMinutes(10.5), "plain words");
        var behindRecord = Message(chat, 1002, t0.AddMinutes(50.5), "plain words");
        var ahead = (await store.IngestAsync(aheadRecord, reader)).MessageId!;
        var behind = (await store.IngestAsync(behindRecord, reader)).MessageId!;

        await using var host = await ReadHost.StartAsync(Db);
        var token = host.ReadToken(reader);

        var first = (await host.CallAsync(token, "search_messages", new { query = "zebra", limit = Page })).Json;
        var seen = Ids(first).ToList();
        Assert.Equal(Page, seen.Count);
        var newest = seen[0];

        // Another observer transcribes three messages: the two plain ones, and one page 1 returned.
        foreach (var record in new[] { aheadRecord, behindRecord, matching[59] })
        {
            var outcome = await store.IngestAsync(
                record with { EventId = Ulid.NewUlid(), Transcript = "a zebra crossing" }, other);
            Assert.Equal(JournalIngestOutcome.ObserverAdded, outcome.Outcome);
        }

        var cursor = first.GetProperty("next_cursor").GetString();
        while (cursor is not null)
        {
            var page = (await host.CallAsync(token, "search_messages", new { query = "zebra", limit = Page, cursor })).Json;
            seen.AddRange(Ids(page));
            cursor = page.GetProperty("next_cursor").GetString();
        }

        Assert.Equal(seen.Count, seen.Distinct().Count());
        Assert.Single(seen, id => id == ahead);
        Assert.DoesNotContain(behind, seen);
        Assert.Single(seen, id => id == newest);
        Assert.Equal(61, seen.Count);

        // And the fill is real: the transcript matches, and the record shows it.
        var filled = (await host.CallAsync(token, "get_message", new { message_id = behind })).Json;
        Assert.Equal("a zebra crossing", filled.GetProperty("transcript").GetString());
    }

    // ── scope, as the SQL applies it ─────────────────────────────────────────

    [Fact]
    public async Task Out_of_scope_rows_answer_exactly_as_missing_rows_do()
    {
        var a = Subject();
        var b = Subject();
        var chat = Chat();
        var otherChat = Chat();
        var t0 = BaseTime();
        var store = new MySqlJournalStore(Db, NullLogger.Instance);

        await store.IngestAsync(Message(chat, 1, t0.AddMinutes(1), "seen by both"), a);
        await store.IngestAsync(Message(chat, 1, t0.AddMinutes(1), "seen by both") with { EventId = Ulid.NewUlid() }, b);
        await store.IngestAsync(Message(chat, 2, t0.AddMinutes(2), "b only, reviewer bait"), b);
        await store.IngestAsync(Message(chat, 3, t0.AddMinutes(3), "a replies to b", replyTo: 2), a);
        await store.IngestAsync(Message(chat, 4, t0.AddMinutes(4), "a replies to nothing", replyTo: 777), a);
        await store.IngestAsync(Message(otherChat, 1, t0.AddMinutes(5), "b elsewhere"), b);
        await store.IngestAsync(Message(otherChat, 2, t0.AddMinutes(6), "a elsewhere"), a);

        var conversation = await ConversationIdAsync(chat);
        var hidden = await MessageIdAsync(chat, 2);
        var foreign = await MessageIdAsync(otherChat, 2);
        var missing = Ulid.NewUlid();

        // A conversation only b can see.
        var bOnlyChat = Chat();
        await store.IngestAsync(Message(bOnlyChat, 1, t0.AddMinutes(7), "b alone"), b);
        var bConversation = await ConversationIdAsync(bOnlyChat);

        await using var host = await ReadHost.StartAsync(Db, readAllSubjects: b);
        var token = host.ReadToken(a);

        // get_message, both forms.
        var missingMessage = await host.CallAsync(token, "get_message", new { message_id = missing });
        Assert.True(missingMessage.IsError);
        Assert.Equal("{\"error\":\"not_found\"}", missingMessage.Text);

        var notFound = missingMessage.Body;
        Assert.Equal(notFound, (await host.CallAsync(token, "get_message", new { message_id = hidden })).Body);
        Assert.Equal(notFound, (await host.CallAsync(token, "get_message", new { telegram_chat_id = chat, telegram_message_id = 2 })).Body);
        Assert.Equal(notFound, (await host.CallAsync(token, "get_message", new { telegram_chat_id = chat, telegram_message_id = 99 })).Body);

        // reply_to: hidden target and missing target are the same null.
        var toHidden = (await host.CallAsync(token, "get_message", new { telegram_chat_id = chat, telegram_message_id = 3 })).Json.GetProperty("reply_to");
        var toMissing = (await host.CallAsync(token, "get_message", new { telegram_chat_id = chat, telegram_message_id = 4 })).Json.GetProperty("reply_to");
        Assert.Equal(JsonValueKind.Null, toHidden.GetProperty("message_id").ValueKind);
        Assert.Equal(JsonValueKind.Null, toMissing.GetProperty("message_id").ValueKind);
        Assert.Equal(2, toHidden.GetProperty("telegram_message_id").GetInt64());

        // get_conversation: hidden conversation, and hidden / missing / foreign anchors.
        var noConversation = (await host.CallAsync(token, "get_conversation", new { conversation_id = missing })).Body;
        Assert.Equal(noConversation, (await host.CallAsync(token, "get_conversation", new { conversation_id = bConversation })).Body);
        foreach (var anchor in new[] { hidden, missing, foreign })
        {
            Assert.Equal(noConversation,
                (await host.CallAsync(token, "get_conversation", new { conversation_id = conversation, from_message_id = anchor })).Body);
        }

        // The visible page skips the hidden row.
        var page = (await host.CallAsync(token, "get_conversation", new { conversation_id = conversation })).Json;
        Assert.Equal([1L, 3L, 4L], page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("telegram_message_id").GetInt64()).ToArray());

        // search: a hidden conversation filter is the same empty page as a missing one.
        var emptySearch = (await host.CallAsync(token, "search_messages", new { conversation_id = missing })).Body;
        Assert.Equal(emptySearch, (await host.CallAsync(token, "search_messages", new { conversation_id = bConversation })).Body);
        Assert.Empty(Ids((await host.CallAsync(token, "search_messages", new { query = "bait" })).Json));

        // Scope `all` sees what a could not, reply target included.
        var reviewer = host.ReadToken(b);
        Assert.Equal(new long[] { 1, 2, 3, 4 },
            (await host.CallAsync(reviewer, "get_conversation", new { conversation_id = conversation })).Json
                .GetProperty("items").EnumerateArray().Select(i => i.GetProperty("telegram_message_id").GetInt64()).ToArray());
        Assert.Equal(hidden,
            (await host.CallAsync(reviewer, "get_message", new { telegram_chat_id = chat, telegram_message_id = 3 })).Json
                .GetProperty("reply_to").GetProperty("message_id").GetString());
    }

    [Fact]
    public async Task Attachments_are_metadata_only_even_when_the_row_holds_more()
    {
        var reader = Subject();
        var chat = Chat();
        var store = new MySqlJournalStore(Db, NullLogger.Instance);
        await store.IngestAsync(Message(chat, 1, BaseTime(), "with files", attachments: [Attachment(1), Attachment(0)]), reader);

        // Fill the columns the read side must never emit, as a committed media row would.
        var id = await MessageIdAsync(chat, 1);
        await fixture.ExecuteAsync(
            $"UPDATE journal_attachments SET sha256 = '{new string('d', 64)}' WHERE message_id = '{id}'");

        await using var host = await ReadHost.StartAsync(Db);
        var record = await host.CallAsync(host.ReadToken(reader), "get_message", new { message_id = id });
        var attachments = record.Json.GetProperty("attachments").EnumerateArray().ToArray();

        Assert.Equal([0, 1], attachments.Select(a => a.GetProperty("ordinal").GetInt32()).ToArray());
        foreach (var attachment in attachments)
        {
            Assert.Equal(
                ["ordinal", "kind", "mime_type", "byte_size", "file_name", "state", "not_archived_reason"],
                attachment.EnumerateObject().Select(p => p.Name).ToArray());
        }

        Assert.DoesNotContain("unique-file-id-", record.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('d', 64), record.Text, StringComparison.Ordinal);
    }

    // ── limits and failure ───────────────────────────────────────────────────

    [Fact]
    public async Task A_replay_page_ends_at_the_text_budget_and_the_traversal_still_returns_everything()
    {
        var reader = Subject();
        var chat = Chat();
        var t0 = BaseTime();
        var store = new MySqlJournalStore(Db, NullLogger.Instance);

        // 120 KiB of text + transcript per record: two fit in 256 KiB, a third does not.
        for (var i = 1; i <= 5; i++)
        {
            await store.IngestAsync(Message(chat, i, t0.AddMinutes(i), new string((char)('a' + i), 61_440),
                transcript: new string('t', 61_440)), reader);
        }

        var conversation = await ConversationIdAsync(chat);
        await using var host = await ReadHost.StartAsync(Db);
        var token = host.ReadToken(reader);

        var seen = new List<long>();
        var sizes = new List<int>();
        string? cursor = null;
        do
        {
            var page = (await host.CallAsync(token, "get_conversation", new { conversation_id = conversation, cursor })).Json;
            var items = page.GetProperty("items").EnumerateArray().ToArray();
            sizes.Add(items.Length);
            seen.AddRange(items.Select(i => i.GetProperty("telegram_message_id").GetInt64()));
            cursor = page.GetProperty("next_cursor").GetString();
        }
        while (cursor is not null);

        Assert.Equal([2, 2, 1], sizes);
        Assert.Equal([1L, 2L, 3L, 4L, 5L], seen);
    }

    [Fact]
    public async Task Full_text_requires_every_term_and_ignores_what_the_index_cannot_hold()
    {
        var reader = Subject();
        var chat = Chat();
        var t0 = BaseTime();
        var store = new MySqlJournalStore(Db, NullLogger.Instance);

        await store.IngestAsync(Message(chat, 1, t0.AddMinutes(1), "the quick brown fox"), reader);
        await store.IngestAsync(Message(chat, 2, t0.AddMinutes(2), "the quick red fox"), reader);
        await store.IngestAsync(Message(chat, 3, t0.AddMinutes(3), "nothing here", transcript: "a brown bear, spoken"), reader);

        await using var host = await ReadHost.StartAsync(Db);
        var token = host.ReadToken(reader);

        async Task<long[]> Hits(string query) =>
            (await host.CallAsync(token, "search_messages", new { query })).Json.GetProperty("items").EnumerateArray()
                .Select(i => i.GetProperty("telegram_message_id").GetInt64()).Order().ToArray();

        Assert.Equal(new long[] { 1, 2 }, await Hits("quick fox"));
        // Operators are stripped, not obeyed: `-red` is no negation, so red and brown are both
        // required and no row has both.
        Assert.Empty(await Hits("+quick -red brown"));
        Assert.Equal(new long[] { 1, 3 }, await Hits("brown"));             // the transcript is searched
        Assert.Equal(new long[] { 1, 2 }, await Hits("the quick fox on"));  // a stopword and a short term are dropped, not required

        var preview = (await host.CallAsync(token, "search_messages", new { query = "bear" })).Json
            .GetProperty("items")[0];
        Assert.True(preview.GetProperty("has_transcript").GetBoolean());
        Assert.False(preview.GetProperty("text_truncated").GetBoolean());
    }

    [Fact]
    public async Task An_unreachable_database_or_a_schema_behind_is_store_unavailable()
    {
        await using (var host = await ReadHost.StartAsync("Server=127.0.0.1;Port=1;Database=unused;User ID=unused;Password=unused;"))
        {
            var response = await host.CallAsync(host.ReadToken(Subject()), "search_messages", new { });
            Assert.True(response.IsError);
            Assert.Equal("{\"error\":\"store_unavailable\",\"retryable\":true}", response.Text);
        }

        await using var scratch = await fixture.CreateScratchDatabaseAsync();
        await JournalDb.MigrateToAsync(scratch.ConnectionString, 4);

        await using (var host = await ReadHost.StartAsync(scratch.ConnectionString))
        {
            var response = await host.CallAsync(host.ReadToken(Subject()), "get_conversation", new { conversation_id = Ulid.NewUlid() });
            Assert.Equal("{\"error\":\"store_unavailable\",\"retryable\":true}", response.Text);
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static string Subject() => "reader-" + Ulid.NewUlid();

    private static long Chat() => -1_000_000_000_000 - Random.Shared.NextInt64(1, 999_999_999);

    private static DateTimeOffset BaseTime() => new(2026, 5, 1, 8, 0, 0, TimeSpan.Zero);

    private static IEnumerable<string> Ids(JsonElement page) =>
        page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("message_id").GetString()!);

    private static JournalAttachment Attachment(int ordinal) => new()
    {
        Ordinal = ordinal,
        Kind = JournalAttachmentKind.Document,
        MimeType = "application/pdf",
        ByteSize = 1234,
        FileName = $"file-{ordinal}.pdf",
        FileUniqueId = $"unique-file-id-{ordinal}",
        NotArchivedReason = JournalNotArchivedReason.MediaDisabled,
    };

    private static JournalRecord Message(
        long chatId, long messageId, DateTimeOffset sentAt, string text, string? transcript = null,
        long? replyTo = null, JournalAttachment[]? attachments = null) => new()
    {
        EventId = Ulid.NewUlid(),
        Telegram = new JournalTelegramRef
        {
            BotId = 7001,
            ChatId = chatId,
            ChatKind = JournalChatKind.Supergroup,
            MessageId = messageId,
            ReplyToMessageId = replyTo,
        },
        Direction = JournalDirection.Inbound,
        Sender = new JournalSender { Kind = JournalSenderKind.Human, Id = "u_1" },
        SentAt = sentAt,
        Text = text,
        TextFormat = JournalTextFormat.Plain,
        Transcript = transcript,
        Origin = JournalRecordOrigin.TelegramUpdate,
        Attachments = attachments ?? [],
    };

    private async Task<string> ConversationIdAsync(long chatId) =>
        await fixture.ScalarRowAsync($"SELECT id FROM journal_conversations WHERE conversation_key = 'tg:group:{chatId}'");

    private async Task<string> MessageIdAsync(long chatId, long messageId) =>
        await fixture.ScalarRowAsync(
            "SELECT m.id FROM journal_messages m JOIN journal_conversations c ON c.id = m.conversation_id "
            + $"WHERE c.conversation_key = 'tg:group:{chatId}' AND m.source_key = 'tg:{messageId}'");

    /// <summary>A message and its observer row, written inside a transaction the test holds open.</summary>
    private static async Task InsertRawAsync(
        MySqlConnection connection, MySqlTransaction transaction, string id, string conversation,
        long telegramId, DateTimeOffset sentAt, string observer)
    {
        await using var command = new MySqlCommand(
            """
            INSERT INTO journal_messages
                (id, conversation_id, source_key, order_key, fingerprint, direction, sender_kind, sender_id,
                 sent_at, recorded_at, text, text_format, transcript_truncated, origin, delivery_state)
            VALUES (@id, @conversation, @source, @order, @fingerprint, 'inbound', 'human', 'u_1',
                    @sent, UTC_TIMESTAMP(6), 'held open', 'plain', 0, 'telegram_update', 'received');
            INSERT INTO journal_message_observers (message_id, observer, event_id, fingerprint, observed_at)
            VALUES (@id, @observer, @event, @fingerprint, UTC_TIMESTAMP(6));
            """, connection, transaction);
        command.Parameters.AddWithValue("@id", id);
        command.Parameters.AddWithValue("@conversation", conversation);
        command.Parameters.AddWithValue("@source", $"tg:{telegramId}");
        command.Parameters.AddWithValue("@order", telegramId);
        command.Parameters.AddWithValue("@fingerprint", new string('0', 64));
        command.Parameters.AddWithValue("@sent", sentAt.UtcDateTime);
        command.Parameters.AddWithValue("@observer", observer);
        command.Parameters.AddWithValue("@event", Ulid.NewUlid());
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>The journal listener over the real stores, built the way <c>Program</c> builds it.</summary>
    private sealed class ReadHost : IAsyncDisposable
    {
        private static readonly string Key =
            Convert.ToBase64String(Enumerable.Range(11, 48).Select(i => (byte)i).ToArray())
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        private static readonly JsonSerializerOptions SkipNulls = new()
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        };

        private readonly WebApplication _app;
        private readonly HttpClient _client;

        private ReadHost(WebApplication app)
        {
            _app = app;
            _client = app.GetTestClient();
        }

        public static async Task<ReadHost> StartAsync(string connectionString, string readAllSubjects = "")
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseSetting(WebHostDefaults.ServerUrlsKey, string.Empty);
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();

            var options = new CommsOptions
            {
                ConversationConnectionString = connectionString,
                Journal = new JournalOptions { Enabled = true, TokenKeys = Key, ReadAllSubjects = readAllSubjects },
            };
            options.ValidateJournal();

            // No read store is passed: the composition builds the MySQL one, as it does in production.
            var app = CommsApp.BuildJournalApp(
                builder, new MySqlJournalStore(connectionString, NullLogger.Instance), options, new JournalRuntimeStats());
            await app.StartAsync();
            return new ReadHost(app);
        }

        public string ReadToken(string subject) =>
            JournalTokens.Mint(JournalTokens.ParseKeys(Key)[0], JournalTokens.PurposeRead, subject);

        public async Task<ToolResponse> CallAsync(string token, string tool, object arguments)
        {
            var payload = $"{{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{{\"name\":\"{tool}\",\"arguments\":{JsonSerializer.Serialize(arguments, SkipNulls)}}}}}";
            var request = new HttpRequestMessage(HttpMethod.Post, JournalMcp.Path)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };
            request.Headers.Accept.ParseAdd("application/json");
            request.Headers.Accept.ParseAdd("text/event-stream");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var response = await _client.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode}: {body}");

            var data = body.Split('\n').Single(line => line.StartsWith("data: ", StringComparison.Ordinal))["data: ".Length..];
            using var document = JsonDocument.Parse(data);
            var result = document.RootElement.GetProperty("result");
            var text = result.GetProperty("content")[0].GetProperty("text").GetString()!;
            var isError = result.TryGetProperty("isError", out var flag) && flag.GetBoolean();
            return new ToolResponse(body, text, isError);
        }

        public async ValueTask DisposeAsync()
        {
            _client.Dispose();
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    private sealed record ToolResponse(string Body, string Text, bool IsError)
    {
        public JsonElement Json => JsonDocument.Parse(Text).RootElement;
    }
}
