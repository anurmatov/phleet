using System.Net.Http.Headers;
using System.Text;
using Fleet.Comms;
using Fleet.Comms.Configuration;
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
/// The journal listener over the REAL <see cref="MySqlJournalStore"/>, built through
/// <see cref="CommsApp.BuildJournalApp"/> — so a response and the rows behind it are asserted together.
/// </summary>
internal sealed class JournalHttpHost : IAsyncDisposable
{
    public static readonly string Key =
        Convert.ToBase64String(Enumerable.Range(7, 48).Select(i => (byte)i).ToArray())
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private readonly WebApplication _app;

    private JournalHttpHost(WebApplication app)
    {
        _app = app;
        Client = app.GetTestClient();
    }

    public HttpClient Client { get; }

    public static async Task<JournalHttpHost> StartAsync(string connectionString, string excludedChatIds = "")
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseSetting(WebHostDefaults.ServerUrlsKey, string.Empty);
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        var options = new CommsOptions
        {
            ConversationConnectionString = connectionString,
            Journal = new JournalOptions { Enabled = true, TokenKeys = Key, ExcludedChatIds = excludedChatIds },
        };
        options.ValidateJournal();

        var app = CommsApp.BuildJournalApp(
            builder, new MySqlJournalStore(connectionString, NullLogger.Instance), options, new JournalRuntimeStats());

        await app.StartAsync();
        return new JournalHttpHost(app);
    }

    public Task<HttpResponseMessage> PostAsync(string json, string subject = "agent1")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/journal/v1/messages")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer",
            JournalTokens.Mint(JournalTokens.ParseKeys(Key)[0], JournalTokens.PurposeIngest, subject));
        return Client.SendAsync(request);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

/// <summary>A journal record as JSON, one field at a time.</summary>
internal sealed record JournalJson
{
    public string EventId { get; init; } = Ulid.NewUlid();
    public long BotId { get; init; } = 7001;
    public long ChatId { get; init; } = -1001234567890;
    public string ChatKind { get; init; } = "supergroup";
    public long MessageId { get; init; } = 42;
    public string? MediaGroupId { get; init; }
    public string Direction { get; init; } = "inbound";
    public string SenderId { get; init; } = "u_1";
    public string? SenderDisplay { get; init; }
    public DateTimeOffset SentAt { get; init; } = new(2026, 1, 2, 3, 4, 5, 678, TimeSpan.Zero);
    public string? Text { get; init; } = "hello";
    public string? Transcript { get; init; }
    public string? ChatTitle { get; init; }
    public string Attachments { get; init; } = "[]";

    public static string Attachment(int ordinal, string kind = "photo") =>
        $$"""{"ordinal":{{ordinal}},"kind":"{{kind}}","mimeType":"image/jpeg","byteSize":1000,"fileUniqueId":"f{{ordinal}}","notArchivedReason":"media_disabled"}""";

    public override string ToString() =>
        $$"""
        {"eventId":"{{EventId}}","channel":"telegram",
         "telegram":{"botId":{{BotId}},"chatId":{{ChatId}},"chatKind":"{{ChatKind}}","messageId":{{MessageId}}{{(ChatTitle is null ? "" : $",\"chatTitle\":\"{ChatTitle}\"")}}{{(MediaGroupId is null ? "" : $",\"mediaGroupId\":\"{MediaGroupId}\"")}}},
         "direction":"{{Direction}}","sender":{"kind":"human","id":"{{SenderId}}"{{(SenderDisplay is null ? "" : $",\"display\":\"{SenderDisplay}\"")}}},
         "sentAt":"{{SentAt:O}}",
         "text":{{(Text is null ? "null" : $"\"{Text}\"")}},"textFormat":"plain",
         "transcript":{{(Transcript is null ? "null" : $"\"{Transcript}\"")}},
         "origin":"telegram_update","attachments":{{Attachments}}}
        """;
}

/// <summary>Row counts and schema helpers for the journal suites.</summary>
internal static class JournalDb
{
    /// <summary>
    /// <c>journal_objects</c> is in the list from slice 4: a test that counts rows without it would
    /// not notice an object row it failed to write, and the object table is exactly where slice 4's
    /// failures show up.
    /// </summary>
    public static readonly string[] Tables =
        ["journal_conversations", "journal_messages", "journal_message_observers", "journal_attachments",
         "journal_objects"];

    public sealed record Counts(long Conversations, long Messages, long Observers, long Attachments, long Objects)
    {
        public static Counts operator -(Counts a, Counts b) =>
            new(a.Conversations - b.Conversations, a.Messages - b.Messages,
                a.Observers - b.Observers, a.Attachments - b.Attachments, a.Objects - b.Objects);
    }

    public static async Task<Counts> CountAsync(string connectionString)
    {
        var values = new long[Tables.Length];
        for (var i = 0; i < Tables.Length; i++)
        {
            values[i] = long.Parse(await MySqlFixture.ScalarRowOnAsync(
                connectionString, $"SELECT COUNT(*) FROM {Tables[i]}"), System.Globalization.CultureInfo.InvariantCulture);
        }

        return new Counts(values[0], values[1], values[2], values[3], values[4]);
    }

    /// <summary>
    /// Applies scripts 1..<paramref name="version"/> and records them exactly as the runner does, so
    /// a later <see cref="MigrationRunner.MigrateAsync"/> applies only what is above it.
    /// </summary>
    public static async Task MigrateToAsync(string connectionString, int version)
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();

        await using (var bookkeeping = new MySqlCommand(
            """
            CREATE TABLE IF NOT EXISTS schema_migrations (
              version     INT UNSIGNED NOT NULL,
              script_name VARCHAR(191) NOT NULL,
              checksum    CHAR(64) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
              applied_at  DATETIME(6) NOT NULL DEFAULT (UTC_TIMESTAMP(6)),
              PRIMARY KEY (version)
            ) ENGINE=InnoDB
            """, connection))
        {
            await bookkeeping.ExecuteNonQueryAsync();
        }

        foreach (var script in MigrationRunner.Scripts.Where(s => s.Version <= version))
        {
            foreach (var statement in MigrationRunner.SplitStatements(script.Sql))
            {
                await using var command = new MySqlCommand(statement, connection);
                await command.ExecuteNonQueryAsync();
            }

            await using var record = new MySqlCommand(
                "INSERT INTO schema_migrations (version, script_name, checksum) VALUES (@v, @n, @c)", connection);
            record.Parameters.AddWithValue("@v", script.Version);
            record.Parameters.AddWithValue("@n", script.Name);
            record.Parameters.AddWithValue("@c", script.Checksum);
            await record.ExecuteNonQueryAsync();
        }
    }

    public static JournalRecord Record(
        long chatId, long messageId, DateTimeOffset sentAt, JournalChatKind kind = JournalChatKind.Supergroup,
        int attachments = 0) => new()
    {
        EventId = Ulid.NewUlid(),
        Telegram = new JournalTelegramRef { BotId = 7001, ChatId = chatId, ChatKind = kind, MessageId = messageId },
        Direction = JournalDirection.Inbound,
        Sender = new JournalSender { Kind = JournalSenderKind.Human, Id = "u_1" },
        SentAt = sentAt,
        Text = "hello",
        TextFormat = JournalTextFormat.Plain,
        Origin = JournalRecordOrigin.TelegramUpdate,
        Attachments = Enumerable.Range(0, attachments).Select(i => new JournalAttachment
        {
            Ordinal = i,
            Kind = JournalAttachmentKind.Photo,
            MimeType = "image/jpeg",
            NotArchivedReason = JournalNotArchivedReason.MediaDisabled,
        }).ToList(),
    };
}

/// <summary>A clock a test moves by hand.</summary>
internal sealed class AdjustableTime(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}
