using Fleet.Comms.Operations;
using Fleet.Conversations.Journal;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fleet.Conversations.Tests;

/// <summary>
/// Retention, the garbage-collection tick, and the operator commands (#375 AC7, AC8, and the
/// <c>journal token|status|purge</c> contract).
/// </summary>
/// <remarks>
/// Each test runs on its OWN migrated scratch database: a sweep with an advanced clock deletes every
/// old row in its schema, and the class fixture's schema is shared with the other journal suites.
/// The environment is process-wide, so this class is in the <c>mysql</c> collection.
/// </remarks>
[Collection("mysql")]
public sealed class JournalRetentionTests(MySqlFixture fixture) : IDisposable
{
    private const string RuntimeKey = "Comms__ConversationConnectionString";
    private const string KeysKey = "Comms__Journal__TokenKeys";

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(RuntimeKey, null);
        Environment.SetEnvironmentVariable(KeysKey, null);
    }

    private async Task<ScratchDatabase> MigratedAsync()
    {
        var scratch = await fixture.CreateScratchDatabaseAsync();
        await new MigrationRunner(scratch.ConnectionString).MigrateAsync();
        return scratch;
    }

    /// <summary>
    /// AC7: retention 1 day, clock advanced 2 days. The old message, its observer, its attachment
    /// and its now-empty conversation go; a message one hour old survives.
    /// </summary>
    [Fact]
    public async Task One_sweep_removes_expired_messages_their_rows_and_empty_conversations()
    {
        await using var scratch = await MigratedAsync();
        var real = DateTimeOffset.UtcNow;
        var clock = new AdjustableTime(real);
        var store = new MySqlJournalStore(scratch.ConnectionString, NullLogger.Instance, clock);

        await store.IngestAsync(JournalDb.Record(-100701, 1, real.AddHours(-1), attachments: 1), "agent1");
        await store.IngestAsync(JournalDb.Record(-100702, 1, real.AddDays(2).AddHours(-1), attachments: 1), "agent1");
        Assert.Equal(new JournalDb.Counts(2, 2, 2, 2, 0), await JournalDb.CountAsync(scratch.ConnectionString));

        clock.Now = real.AddDays(2);
        var stats = new JournalRuntimeStats(clock);
        var retention = new JournalRetention(
            scratch.ConnectionString, TimeSpan.FromDays(1), batchSize: 100, NullLogger.Instance, stats, clock);

        var result = await retention.SweepOnceAsync();

        Assert.Equal(new JournalRetention.SweepResult(1, 1), result);
        Assert.Equal(new JournalDb.Counts(1, 1, 1, 1, 0), await JournalDb.CountAsync(scratch.ConnectionString));
        Assert.Equal("-100702", await MySqlFixture.ScalarRowOnAsync(
            scratch.ConnectionString, "SELECT telegram_chat_id FROM journal_conversations"));

        var snapshot = stats.Read();
        Assert.Equal(clock.Now, snapshot.GcLastRunAt);
        Assert.Equal(1, snapshot.GcDeletedMessages);
        Assert.Equal(1, snapshot.GcDeletedConversations);
    }

    /// <summary>Messages are deleted in batches until none is left, not one batch per tick.</summary>
    [Fact]
    public async Task A_sweep_continues_past_one_batch()
    {
        await using var scratch = await MigratedAsync();
        var real = DateTimeOffset.UtcNow;
        var store = new MySqlJournalStore(scratch.ConnectionString, NullLogger.Instance);

        for (var i = 0; i < 5; i++)
            await store.IngestAsync(JournalDb.Record(-100711, i + 1, real.AddDays(-3)), "agent1");

        var result = await new JournalRetention(
            scratch.ConnectionString, TimeSpan.FromDays(1), batchSize: 2, NullLogger.Instance).SweepOnceAsync();

        Assert.Equal(new JournalRetention.SweepResult(5, 1), result);
        Assert.Equal(new JournalDb.Counts(0, 0, 0, 0, 0), await JournalDb.CountAsync(scratch.ConnectionString));
    }

    /// <summary>
    /// The sweep rides the existing GC tick, runs last, and a failing sweep neither throws out of the
    /// tick nor stops the conversation steps.
    /// </summary>
    [Fact]
    public async Task The_garbage_collector_runs_the_sweep_and_survives_its_failure()
    {
        await using var scratch = await MigratedAsync();
        var store = new MySqlJournalStore(scratch.ConnectionString, NullLogger.Instance);
        await store.IngestAsync(JournalDb.Record(-100721, 1, DateTimeOffset.UtcNow.AddDays(-3)), "agent1");

        var collector = new GarbageCollector(scratch.ConnectionString, fixture.Options, NullLogger.Instance,
            journal: new JournalRetention(scratch.ConnectionString, TimeSpan.FromDays(1), 100, NullLogger.Instance));

        var swept = await collector.SweepOnceAsync();
        Assert.Equal(1, swept.JournalMessages);
        Assert.Equal(1, swept.JournalConversations);

        var stats = new JournalRuntimeStats();
        var broken = new GarbageCollector(scratch.ConnectionString, fixture.Options, NullLogger.Instance,
            journal: new JournalRetention("Server=127.0.0.1;Port=1;Database=none;User ID=none;Password=none;Connection Timeout=2;",
                TimeSpan.FromDays(1), 100, NullLogger.Instance, stats));

        var tick = await broken.SweepOnceAsync();
        Assert.Equal(0, tick.JournalMessages);
        Assert.Equal(1, stats.Read().GcFailures);
    }

    // ── AC8: purge ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Purge_by_telegram_chat_prints_counts_and_without_confirm_changes_nothing()
    {
        await using var scratch = await MigratedAsync();
        var store = new MySqlJournalStore(scratch.ConnectionString, NullLogger.Instance);
        var sent = DateTimeOffset.UtcNow.AddHours(-1);

        await store.IngestAsync(JournalDb.Record(-100801, 1, sent, attachments: 2), "agent1");
        await store.IngestAsync(JournalDb.Record(-100801, 2, sent), "agent1");
        await store.IngestAsync(JournalDb.Record(-100801, 2, sent) with { EventId = Fleet.Protocol.Ulid.NewUlid() }, "agent2");
        await store.IngestAsync(JournalDb.Record(-100802, 1, sent, attachments: 1), "agent1");

        Environment.SetEnvironmentVariable(RuntimeKey, scratch.ConnectionString);
        var before = await JournalDb.CountAsync(scratch.ConnectionString);

        var dry = await RunAsync("journal", "purge", "--telegram-chat", "-100801");

        Assert.Equal(0, dry.Exit);
        Assert.Contains("would delete: 2 message(s), 3 observer row(s), 2 attachment row(s), 1 conversation(s), 0 object(s)", dry.Stdout, StringComparison.Ordinal);
        Assert.Equal(before, await JournalDb.CountAsync(scratch.ConnectionString));

        var confirmed = await RunAsync("journal", "purge", "--telegram-chat", "-100801", "--confirm");

        Assert.Equal(0, confirmed.Exit);
        Assert.Contains("deleted: 2 message(s), 3 observer row(s), 2 attachment row(s), 1 conversation(s), 0 object(s)", confirmed.Stdout, StringComparison.Ordinal);
        Assert.Equal(new JournalDb.Counts(1, 1, 1, 1, 0), await JournalDb.CountAsync(scratch.ConnectionString));
        Assert.Equal("-100802", await MySqlFixture.ScalarRowOnAsync(
            scratch.ConnectionString, "SELECT telegram_chat_id FROM journal_conversations"));
    }

    [Fact]
    public async Task Purge_by_message_and_before_touch_only_what_they_name()
    {
        await using var scratch = await MigratedAsync();
        var store = new MySqlJournalStore(scratch.ConnectionString, NullLogger.Instance);
        var now = DateTimeOffset.UtcNow;

        var first = await store.IngestAsync(JournalDb.Record(-100811, 1, now.AddDays(-10)), "agent1");
        await store.IngestAsync(JournalDb.Record(-100811, 2, now.AddDays(-1)), "agent1");
        Environment.SetEnvironmentVariable(RuntimeKey, scratch.ConnectionString);

        var conversation = await MySqlFixture.ScalarRowOnAsync(scratch.ConnectionString, "SELECT id FROM journal_conversations");
        var before = await RunAsync("journal", "purge", "--conversation", conversation,
            "--before", now.AddDays(-5).ToString("O"), "--confirm");
        Assert.Contains("deleted: 1 message(s), 1 observer row(s), 0 attachment row(s), 0 conversation(s), 0 object(s)", before.Stdout, StringComparison.Ordinal);
        Assert.Equal("NULL", await MySqlFixture.ScalarRowOnAsync(scratch.ConnectionString,
            $"SELECT MAX(id) FROM journal_messages WHERE id = '{first.MessageId}'"));

        var remaining = await MySqlFixture.ScalarRowOnAsync(scratch.ConnectionString, "SELECT id FROM journal_messages");
        var last = await RunAsync("journal", "purge", "--message", remaining, "--confirm");
        Assert.Contains("deleted: 1 message(s), 1 observer row(s), 0 attachment row(s), 1 conversation(s), 0 object(s)", last.Stdout, StringComparison.Ordinal);
        Assert.Equal(new JournalDb.Counts(0, 0, 0, 0, 0), await JournalDb.CountAsync(scratch.ConnectionString));
    }

    [Theory]
    [InlineData("journal", "purge")]
    [InlineData("journal", "purge", "--message", "a", "--conversation", "b")]
    [InlineData("journal", "purge", "--telegram-chat", "0")]
    [InlineData("journal", "purge", "--telegram-chat", "-1", "--before", "2026-01-01T00:00:00")]
    public async Task A_malformed_purge_exits_1_and_deletes_nothing(params string[] args)
    {
        await using var scratch = await MigratedAsync();
        Environment.SetEnvironmentVariable(RuntimeKey, scratch.ConnectionString);

        var run = await RunAsync(args);

        Assert.Equal(1, run.Exit);
    }

    /// <summary>A database error mid-purge rolls back: exit 1 with the class, nothing deleted.</summary>
    [Fact]
    public async Task A_purge_against_an_unreachable_database_exits_1_with_the_class()
    {
        Environment.SetEnvironmentVariable(RuntimeKey, "Server=127.0.0.1;Port=1;Database=none;User ID=none;Password=none;Connection Timeout=2;");

        var run = await RunAsync("journal", "purge", "--telegram-chat", "-1", "--confirm");

        Assert.Equal(1, run.Exit);
        Assert.Contains("journal purge failed (MySqlException); nothing was deleted.", run.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("Password", run.Stderr, StringComparison.Ordinal);
    }

    // ── token and status ─────────────────────────────────────────────────────

    [Fact]
    public async Task Token_mints_with_the_first_key_and_refuses_reserved_purposes()
    {
        var other = Convert.ToBase64String(Enumerable.Range(90, 48).Select(i => (byte)i).ToArray())
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Environment.SetEnvironmentVariable(KeysKey, $"{JournalHttpHost.Key},{other}");

        var minted = await RunAsync("journal", "token", "--purpose", "ingest", "--subject", "agent1");

        Assert.Equal(0, minted.Exit);
        var token = minted.Stdout.Trim();
        Assert.True(JournalTokens.TryVerify(token, "ingest", JournalTokens.ParseKeys(JournalHttpHost.Key), out var subject));
        Assert.Equal("agent1", subject);

        Assert.Equal(1, (await RunAsync("journal", "token", "--purpose", "read", "--subject", "agent1")).Exit);
        Assert.Equal(1, (await RunAsync("journal", "token", "--purpose", "ingest", "--subject", "bad.subject")).Exit);

        Environment.SetEnvironmentVariable(KeysKey, "short");
        var invalid = await RunAsync("journal", "token", "--purpose", "status", "--subject", "agent1");
        Assert.Equal(1, invalid.Exit);
        Assert.Contains("journal_key_invalid", invalid.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("short", invalid.Stderr.Replace("journal_key_invalid", ""), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Status_prints_each_observer_and_its_last_ingest()
    {
        await using var scratch = await MigratedAsync();
        var store = new MySqlJournalStore(scratch.ConnectionString, NullLogger.Instance);
        var sent = DateTimeOffset.UtcNow.AddHours(-1);
        await store.IngestAsync(JournalDb.Record(-100901, 1, sent), "agent1");
        await store.IngestAsync(JournalDb.Record(-100901, 2, sent), "agent1");
        await store.IngestAsync(JournalDb.Record(-100901, 2, sent), "agent2");
        Environment.SetEnvironmentVariable(RuntimeKey, scratch.ConnectionString);

        var run = await RunAsync("journal", "status");

        Assert.Equal(0, run.Exit);
        Assert.Contains("schema version: 5", run.Stdout, StringComparison.Ordinal);
        Assert.Matches(@"agent1\s+2 \d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}Z", run.Stdout);
        Assert.Matches(@"agent2\s+1 ", run.Stdout);
    }

    private static async Task<(int Exit, string Stdout, string Stderr)> RunAsync(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = await OperatorCommands.RunAsync(args, stdout, stderr);
        return (exit, stdout.ToString(), stderr.ToString());
    }
}
