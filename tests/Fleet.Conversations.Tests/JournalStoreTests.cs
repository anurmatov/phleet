using System.Net;
using Fleet.Comms;
using Fleet.Comms.Auth;
using Fleet.Conversations.Journal;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fleet.Conversations.Tests;

/// <summary>
/// The journal against a real MySQL 8.0, through the real listener: every idempotency row with the
/// exact rows it writes, the conversation keys, observers, transcripts, albums, exclusion, the 0004
/// upgrade and schema-behind readiness (#375 AC1–AC6, AC9, AC15, AC16d).
/// </summary>
[Collection("mysql")]
public sealed class JournalStoreTests(MySqlFixture fixture)
{
    private string Db => fixture.ConnectionString;

    private static JournalDb.Counts None => new(0, 0, 0, 0);

    /// <summary>Posts and returns the status, the body and the row delta in the four tables.</summary>
    private async Task<(HttpStatusCode Status, string Body, JournalDb.Counts Delta)> PostAsync(
        JournalHttpHost host, string json, string subject = "agent1")
    {
        var before = await JournalDb.CountAsync(Db);
        var response = await host.PostAsync(json, subject);
        var body = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, body, await JournalDb.CountAsync(Db) - before);
    }

    // ── AC1: every row of the idempotency table ──────────────────────────────

    [Fact]
    public async Task A_new_natural_key_is_created_with_its_conversation_message_observer_and_attachments()
    {
        await using var host = await JournalHttpHost.StartAsync(Db);
        var record = new JournalJson
        {
            ChatId = -1000000000101,
            Attachments = $"[{JournalJson.Attachment(0)},{JournalJson.Attachment(1, "document")}]",
        };

        var (status, body, delta) = await PostAsync(host, record.ToString());

        Assert.Equal(HttpStatusCode.Created, status);
        Assert.Contains("\"result\":\"created\"", body, StringComparison.Ordinal);
        Assert.Equal(new JournalDb.Counts(1, 1, 1, 2), delta);

        // Derived, not requested.
        Assert.Equal("received|telegram_update|tg:42|42",
            await fixture.ScalarRowAsync(
                "SELECT delivery_state, origin, source_key, order_key FROM journal_messages "
                + $"WHERE conversation_id = (SELECT id FROM journal_conversations WHERE conversation_key = 'tg:group:{record.ChatId}')"));
        Assert.Equal("2", await fixture.ScalarRowAsync(
            "SELECT COUNT(*) FROM journal_attachments a JOIN journal_messages m ON m.id = a.message_id "
            + $"JOIN journal_conversations c ON c.id = m.conversation_id WHERE c.telegram_chat_id = {record.ChatId} "
            + "AND a.state = 'not_archived' AND a.not_archived_reason = 'media_disabled'"));
    }

    [Fact]
    public async Task The_same_event_id_with_the_same_record_is_a_duplicate_that_writes_nothing()
    {
        await using var host = await JournalHttpHost.StartAsync(Db);
        var record = new JournalJson { ChatId = -1000000000102 };
        await host.PostAsync(record.ToString());

        var (status, body, delta) = await PostAsync(host, record.ToString());

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("\"result\":\"duplicate\"", body, StringComparison.Ordinal);
        Assert.Equal(None, delta);
    }

    /// <summary>AC16d: an event id reused for a different message is refused, and nothing is written.</summary>
    [Fact]
    public async Task The_same_event_id_with_a_different_record_is_event_id_reused_and_writes_nothing()
    {
        await using var host = await JournalHttpHost.StartAsync(Db);
        var record = new JournalJson { ChatId = -1000000000103 };
        await host.PostAsync(record.ToString());

        var (status, body, delta) = await PostAsync(host, (record with { MessageId = 43, ChatId = -1000000000104 }).ToString());

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("{\"error\":\"idempotency_conflict\",\"reason\":\"event_id_reused\"}", body);
        Assert.Equal(None, delta);
    }

    [Fact]
    public async Task The_same_message_from_the_same_observer_under_a_new_event_id_is_a_duplicate()
    {
        await using var host = await JournalHttpHost.StartAsync(Db);
        var record = new JournalJson { ChatId = -1000000000105 };
        await host.PostAsync(record.ToString());

        // A new event id and a transcript it did not send the first time: still nothing written.
        var (status, body, delta) = await PostAsync(host,
            (record with { EventId = Fleet.Protocol.Ulid.NewUlid(), Transcript = "late" }).ToString());

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("\"result\":\"duplicate\"", body, StringComparison.Ordinal);
        Assert.Equal(None, delta);
        Assert.Equal("NULL", await fixture.ScalarRowAsync(
            $"SELECT m.transcript FROM journal_messages m JOIN journal_conversations c ON c.id = m.conversation_id WHERE c.telegram_chat_id = {record.ChatId}"));
    }

    /// <summary>AC2: one supergroup message, two bots: one message row, two observer rows.</summary>
    [Fact]
    public async Task A_second_observer_of_a_supergroup_message_is_observer_added()
    {
        await using var host = await JournalHttpHost.StartAsync(Db);
        var record = new JournalJson { ChatId = -1000000000106 };

        var first = await PostAsync(host, record.ToString(), "agent1");
        var second = await PostAsync(host,
            (record with { EventId = Fleet.Protocol.Ulid.NewUlid(), BotId = 7002 }).ToString(), "agent2");

        Assert.Equal(new JournalDb.Counts(1, 1, 1, 0), first.Delta);
        Assert.Equal(HttpStatusCode.OK, second.Status);
        Assert.Contains("\"result\":\"observer_added\"", second.Body, StringComparison.Ordinal);
        Assert.Equal(new JournalDb.Counts(0, 0, 1, 0), second.Delta);
    }

    [Fact]
    public async Task The_same_natural_key_with_a_different_record_is_a_conflict_that_writes_nothing()
    {
        await using var host = await JournalHttpHost.StartAsync(Db);
        var record = new JournalJson { ChatId = -1000000000107 };
        await host.PostAsync(record.ToString());

        var (status, body, delta) = await PostAsync(host,
            (record with { EventId = Fleet.Protocol.Ulid.NewUlid(), Text = "edited" }).ToString(), "agent2");

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("{\"error\":\"idempotency_conflict\"}", body);
        Assert.Equal(None, delta);
    }

    /// <summary>AC6.</summary>
    [Fact]
    public async Task An_excluded_chat_is_422_and_no_table_changes()
    {
        await using var host = await JournalHttpHost.StartAsync(Db, excludedChatIds: "-1000000000108,");

        var (status, body, delta) = await PostAsync(host, new JournalJson { ChatId = -1000000000108 }.ToString());

        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        Assert.Equal("{\"error\":\"excluded_chat\"}", body);
        Assert.Equal(None, delta);
    }

    [Theory]
    [InlineData("{\"eventId\":")]
    [InlineData("{}")]
    public async Task A_field_violation_is_422_and_no_table_changes(string json)
    {
        await using var host = await JournalHttpHost.StartAsync(Db);

        var (status, _, delta) = await PostAsync(host, json);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        Assert.Equal(None, delta);
    }

    [Fact]
    public async Task A_body_over_one_mebibyte_is_413_and_no_table_changes()
    {
        await using var host = await JournalHttpHost.StartAsync(Db);

        var (status, _, delta) = await PostAsync(host, new string(' ', 1024 * 1024 + 1));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, status);
        Assert.Equal(None, delta);
    }

    [Fact]
    public async Task An_unreachable_database_is_503_store_unavailable()
    {
        await using var host = await JournalHttpHost.StartAsync(
            "Server=127.0.0.1;Port=1;Database=none;User ID=none;Password=none;");

        var started = System.Diagnostics.Stopwatch.StartNew();
        var response = await host.PostAsync(new JournalJson { ChatId = -1000000000109 }.ToString());

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("{\"error\":\"store_unavailable\"}", await response.Content.ReadAsStringAsync());
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(15), $"took {started.Elapsed}");
    }

    // ── concurrency: distinct records in parallel never 503 ──────────────────

    /// <summary>
    /// Distinct records posted at once, across many new conversations and into ONE new
    /// conversation, all succeed. Every ingest's first reads look up keys that do not exist yet
    /// (the event id, the conversation key); if those lookups took gap locks, parallel first
    /// messages would deadlock, exhaust the single retry and answer 503.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Parallel_distinct_records_never_answer_503(int round)
    {
        await using var host = await JournalHttpHost.StartAsync(Db);
        const int perKind = 24;
        var baseChat = -1000000200000L - round * 1000;

        var bodies = new List<(string Json, string Subject)>();
        for (var i = 0; i < perKind; i++)
        {
            // One new conversation each...
            bodies.Add((new JournalJson { ChatId = baseChat - 1 - i, MessageId = 1 }.ToString(), $"p{i}"));

            // ...and many messages into one conversation that does not exist yet.
            bodies.Add((new JournalJson { ChatId = baseChat, MessageId = 100 + i }.ToString(), $"q{i}"));
        }

        var before = await JournalDb.CountAsync(Db);
        var responses = await Task.WhenAll(bodies.Select(b => host.PostAsync(b.Json, b.Subject)));
        var statuses = responses.Select(r => (int)r.StatusCode).ToArray();

        Assert.True(statuses.All(status => status == 201),
            "statuses: " + string.Join(",", statuses.GroupBy(x => x).Select(g => $"{g.Key}x{g.Count()}")));
        Assert.Equal(new JournalDb.Counts(perKind + 1, 2 * perKind, 2 * perKind, 0),
            await JournalDb.CountAsync(Db) - before);
    }

    // ── AC3: conversation keys follow the chat KIND ──────────────────────────

    [Theory]
    [InlineData("private", 2)]
    [InlineData("group", 2)]
    [InlineData("supergroup", 1)]
    public async Task Per_bot_chats_are_separate_conversations_and_a_supergroup_is_shared(string kind, int conversations)
    {
        await using var host = await JournalHttpHost.StartAsync(Db);
        var chatId = kind switch { "private" => 5_000_000_301L, "group" => -5_000_000_302L, _ => -1000000000303L };
        var record = new JournalJson { ChatId = chatId, ChatKind = kind, MessageId = 7 };

        var first = await PostAsync(host, record.ToString(), "agent1");
        var second = await PostAsync(host,
            (record with { EventId = Fleet.Protocol.Ulid.NewUlid(), BotId = 7002 }).ToString(), "agent2");

        Assert.Equal(conversations, first.Delta.Conversations + second.Delta.Conversations);
        Assert.Equal(conversations, first.Delta.Messages + second.Delta.Messages);
        Assert.Equal(2, first.Delta.Observers + second.Delta.Observers);
    }

    // ── AC4: transcripts ─────────────────────────────────────────────────────

    [Fact]
    public async Task Different_transcripts_from_two_observers_keep_the_first_non_null()
    {
        await using var host = await JournalHttpHost.StartAsync(Db);
        var record = new JournalJson { ChatId = -1000000000401, Text = null, Transcript = "first words" };

        await host.PostAsync(record.ToString(), "agent1");
        var second = await PostAsync(host,
            (record with { EventId = Fleet.Protocol.Ulid.NewUlid(), BotId = 7002, Transcript = "other words" }).ToString(), "agent2");

        Assert.Equal(HttpStatusCode.OK, second.Status);
        Assert.Contains("\"result\":\"observer_added\"", second.Body, StringComparison.Ordinal);
        Assert.Equal("first words", await fixture.ScalarRowAsync(
            "SELECT m.transcript FROM journal_messages m JOIN journal_conversations c ON c.id = m.conversation_id WHERE c.telegram_chat_id = -1000000000401"));
    }

    [Fact]
    public async Task A_new_observer_fills_a_transcript_that_is_still_null()
    {
        await using var host = await JournalHttpHost.StartAsync(Db);
        var record = new JournalJson { ChatId = -1000000000402, Text = null };

        await host.PostAsync(record.ToString(), "agent1");
        await host.PostAsync((record with { EventId = Fleet.Protocol.Ulid.NewUlid(), Transcript = "heard" }).ToString(), "agent2");

        Assert.Equal("heard", await fixture.ScalarRowAsync(
            "SELECT m.transcript FROM journal_messages m JOIN journal_conversations c ON c.id = m.conversation_id WHERE c.telegram_chat_id = -1000000000402"));
    }

    // ── AC5: albums ──────────────────────────────────────────────────────────

    [Fact]
    public async Task An_album_is_one_row_per_platform_message_sharing_the_group_id()
    {
        await using var host = await JournalHttpHost.StartAsync(Db);

        for (var i = 0; i < 3; i++)
        {
            var response = await host.PostAsync(new JournalJson
            {
                ChatId = -1000000000501,
                MessageId = 500 + i,
                MediaGroupId = "13579",
                Text = null,
                Attachments = $"[{JournalJson.Attachment(0)}]",
            }.ToString());
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }

        Assert.Equal("3|3", await fixture.ScalarRowAsync(
            "SELECT COUNT(DISTINCT m.id), COUNT(a.id) FROM journal_messages m "
            + "JOIN journal_conversations c ON c.id = m.conversation_id "
            + "JOIN journal_attachments a ON a.message_id = m.id "
            + "WHERE c.telegram_chat_id = -1000000000501 AND m.media_group_id = '13579' "
            + "AND a.state = 'not_archived' AND a.not_archived_reason = 'media_disabled'"));
    }

    // ── AC9: 0004 over a populated 0003 database ─────────────────────────────

    [Fact]
    public async Task Applying_0004_to_a_populated_0003_database_changes_no_existing_row()
    {
        await using var scratch = await fixture.CreateScratchDatabaseAsync();
        await JournalDb.MigrateToAsync(scratch.ConnectionString, 3);

        await MySqlFixture.ExecuteOnAsync(scratch.ConnectionString,
            "INSERT INTO conversations (id, channel_id, principal_id, external_ref) "
            + "VALUES ('01J0000000000000000000000A', 'c_1', 'p_1', 'ref-1'), ('01J0000000000000000000000B', 'c_1', 'p_1', 'ref-2')");

        // Every table 0001-0003 created. schema_migrations is the runner's bookkeeping and gains
        // exactly the one row for 0004.
        var tables = (await TablesAsync(scratch.ConnectionString)).Where(t => t != "schema_migrations").ToList();
        Assert.DoesNotContain("journal_messages", tables);
        Assert.Contains("conversations", tables);
        var before = await RowCountsAsync(scratch.ConnectionString, tables);

        var applied = await new MigrationRunner(scratch.ConnectionString).MigrateAsync();

        Assert.Equal([4], applied);
        Assert.Equal(before, await RowCountsAsync(scratch.ConnectionString, tables));
        Assert.True((await new MigrationRunner(scratch.ConnectionString).GetStatusAsync()).Matches);

        foreach (var table in JournalDb.Tables)
            Assert.Contains(table, await TablesAsync(scratch.ConnectionString));
    }

    /// <summary>Every id, key and fingerprint column is ascii_bin, and every journal table InnoDB (MUST NOT 10).</summary>
    [Fact]
    public async Task Every_journal_id_and_key_column_is_ascii_bin_and_every_table_is_innodb()
    {
        var engines = await MySqlFixture.ScalarRowOnAsync(Db,
            "SELECT GROUP_CONCAT(DISTINCT engine) FROM information_schema.tables "
            + "WHERE table_schema = DATABASE() AND table_name LIKE 'journal\\_%'");
        Assert.Equal("InnoDB", engines);

        var wrong = await MySqlFixture.ScalarRowOnAsync(Db,
            """
            SELECT COUNT(*) FROM information_schema.columns
             WHERE table_schema = DATABASE() AND table_name LIKE 'journal\_%'
               AND (column_name IN ('id','conversation_key','conversation_id','source_key','fingerprint',
                                    'reply_to_source_key','media_group_id','send_group_id','message_id',
                                    'observer','event_id','mime_type','sha256','telegram_file_unique_id','object_id')
                    AND collation_name <> 'ascii_bin')
            """);
        Assert.Equal("0", wrong);
    }

    // ── AC15: schema behind ──────────────────────────────────────────────────

    /// <summary>
    /// At 0003 with the journal on, <c>/ready</c> is the existing unhealthy shape and a post is
    /// <c>503 store_unavailable{schema_behind}</c> without touching a journal table. After migrate,
    /// both succeed — immediately, because only a current answer is cached.
    /// </summary>
    [Fact]
    public async Task At_0003_ready_is_unhealthy_and_ingest_is_schema_behind_until_migrate()
    {
        await using var scratch = await fixture.CreateScratchDatabaseAsync();
        await JournalDb.MigrateToAsync(scratch.ConnectionString, 3);

        var conversations = new MySqlConversationStore(
            scratch.ConnectionString, fixture.Options, NullLogger<MySqlConversationStore>.Instance);

        var opsBuilder = WebApplication.CreateBuilder();
        opsBuilder.WebHost.UseTestServer();
        opsBuilder.Logging.ClearProviders();
        await using var ops = CommsApp.BuildOpsApp(opsBuilder, new InMemoryAuthStore(), conversations, scratch.ConnectionString);
        await ops.StartAsync();
        var ready = ops.GetTestClient();

        await using var journal = await JournalHttpHost.StartAsync(scratch.ConnectionString);
        var record = new JournalJson { ChatId = -1000000000901 };

        var readyBefore = await ready.GetAsync("/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, readyBefore.StatusCode);
        var readyBody = await readyBefore.Content.ReadAsStringAsync();
        Assert.Contains("\"status\":\"unhealthy\"", readyBody, StringComparison.Ordinal);
        Assert.Contains("\"schema\":\"schema version 3 is BEHIND", readyBody, StringComparison.Ordinal);

        var behind = await journal.PostAsync(record.ToString());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, behind.StatusCode);
        Assert.Equal("{\"error\":\"store_unavailable\",\"reason\":\"schema_behind\"}", await behind.Content.ReadAsStringAsync());
        Assert.DoesNotContain("journal_messages", await TablesAsync(scratch.ConnectionString));

        await new MigrationRunner(scratch.ConnectionString).MigrateAsync();

        Assert.Equal(HttpStatusCode.OK, (await ready.GetAsync("/ready")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await journal.PostAsync(record.ToString())).StatusCode);
    }

    private static async Task<List<string>> TablesAsync(string connectionString)
    {
        var joined = await MySqlFixture.ScalarRowOnAsync(connectionString,
            "SELECT GROUP_CONCAT(table_name ORDER BY table_name) FROM information_schema.tables WHERE table_schema = DATABASE()");
        return joined.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    private static async Task<Dictionary<string, string>> RowCountsAsync(string connectionString, IEnumerable<string> tables)
    {
        var counts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var table in tables)
            counts[table] = await MySqlFixture.ScalarRowOnAsync(connectionString, $"SELECT COUNT(*) FROM `{table}`");
        return counts;
    }
}
