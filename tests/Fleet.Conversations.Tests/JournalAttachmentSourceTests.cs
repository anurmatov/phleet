using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Fleet.Comms.Routes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;
using Fleet.Conversations.Contracts;
using Fleet.Conversations.Journal;
using Fleet.Protocol;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fleet.Conversations.Tests;

public sealed partial class JournalReadStoreTests
{
    [Fact]
    public async Task Attachment_source_refuses_invalid_identifiers_before_opening_a_database()
    {
        IJournalAttachmentSource source = new MySqlJournalReadStore(
            "Server=127.0.0.1;Port=1;Database=unused;User ID=unused;Password=unused;", NullLogger.Instance);
        var reader = new JournalReader("agent1", JournalReadScope.All);
        await Assert.ThrowsAsync<ArgumentException>(() => source.FindAttachmentAsync(reader, "tg:group:-101", null, null, 0));
        await Assert.ThrowsAsync<ArgumentException>(() => source.FindAttachmentAsync(reader, "tg:group:-101", Ulid.NewUlid(), 5, 0));
        await Assert.ThrowsAsync<ArgumentException>(() => source.FindAttachmentAsync(reader, "tg:group:-101", "not-an-id", null, 0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => source.FindAttachmentAsync(reader, "tg:group:-101", null, 5, 256));
    }

    [Fact]
    public async Task Attachment_source_applies_observership_and_binding_to_both_id_forms()
    {
        var subject = Subject();
        var other = Subject();
        var a = Chat();
        var b = Chat();
        var write = new MySqlJournalStore(Db, NullLogger.Instance);
        var first = (await write.IngestAsync(Message(a, 5, BaseTime(), "alpha", attachments: [Attachment(0)]), subject)).MessageId!;
        var foreign = (await write.IngestAsync(Message(b, 5, BaseTime(), "bravo", attachments: [Attachment(0)]), subject)).MessageId!;
        var hidden = (await write.IngestAsync(Message(a, 6, BaseTime(), "hidden", attachments: [Attachment(0)]), other)).MessageId!;
        var objectId = Ulid.NewUlid();
        var digest = new string('a', 64);
        await fixture.ExecuteAsync($"""
            INSERT INTO journal_objects
              (id, object_key, owner, sha256, committed_sha256, byte_size, mime_type, state, created_at, updated_at)
            VALUES ('{objectId}', 'synthetic/{objectId}', '{other}', '{digest}', '{digest}', 1234,
                    'application/pdf', 'committed', UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
            UPDATE journal_attachments SET object_id = '{objectId}', state = 'committed',
              not_archived_reason = NULL, sha256 = '{digest}' WHERE message_id = '{first}';
            """);
        IJournalAttachmentSource source = new MySqlJournalReadStore(Db, NullLogger.Instance);
        var observed = new JournalReader(subject, JournalReadScope.Observed);
        var key = $"tg:group:{a}";
        var byUlid = await source.FindAttachmentAsync(observed, key, first, null, 0);
        var byTelegram = await source.FindAttachmentAsync(observed, key, null, 5, 0);
        Assert.Equal(byUlid, byTelegram);
        Assert.NotNull(byUlid);
        Assert.Equal(first, byUlid.MessageId);
        Assert.Equal("committed", byUlid.AttachmentState);
        Assert.Equal("committed", byUlid.ObjectState);
        Assert.Equal($"synthetic/{objectId}", byUlid.ObjectKey);
        Assert.Equal(digest, byUlid.Sha256);
        Assert.Equal(digest, byUlid.ObjectSha256);
        Assert.Equal(nameof(JournalAttachmentLocator), byUlid.ToString());
        Assert.Equal(1234, byUlid.ObjectByteSize);
        Assert.Null(await source.FindAttachmentAsync(observed, key, foreign, null, 0));
        Assert.Null(await source.FindAttachmentAsync(observed, key, hidden, null, 0));
        Assert.Null(await source.FindAttachmentAsync(observed, key, null, 6, 0));
        Assert.Null(await source.FindAttachmentAsync(observed, key, first, null, 255));
        Assert.Null(await source.FindAttachmentAsync(observed, key, Ulid.NewUlid(), null, 0));
        var all = new JournalReader(subject, JournalReadScope.All);
        Assert.Null(await source.FindAttachmentAsync(all, key, foreign, null, 0));
        var unavailable = await source.FindAttachmentAsync(all, key, hidden, null, 0);
        Assert.NotNull(unavailable);
        Assert.Equal("not_archived", unavailable.AttachmentState);
        Assert.Equal("media_disabled", unavailable.NotArchivedReason);
        Assert.Null(unavailable.ObjectState);
        Assert.Null(unavailable.ObjectKey);
        await fixture.ExecuteAsync($"UPDATE journal_attachments SET state = 'lost' WHERE message_id = '{first}';");
        Assert.Equal("lost", (await source.FindAttachmentAsync(observed, key, first, null, 0))!.AttachmentState);
    }
    [Fact]
    public async Task Content_route_intersects_real_SQL_scope_and_binding_without_writes()
    {
        var subject = Subject(); var other = Subject(); var a = Chat(); var b = Chat(); var c = Chat();
        var bytes = Encoding.UTF8.GetBytes("synthetic sentinel");
        var digest = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var write = new MySqlJournalStore(Db, NullLogger.Instance);
        var bucket = new FakeBucket();
        async Task<string> Seed(long chat, string observer, string mime)
        {
            var record = Message(chat, 5, BaseTime(), "synthetic", attachments:
                [Attachment(0) with { ByteSize = bytes.Length, MimeType = mime,
                    Kind = chat == a ? JournalAttachmentKind.Photo : JournalAttachmentKind.Document }]);
            if (chat == a) record = record with { Telegram = record.Telegram with { ChatKind = JournalChatKind.Private } };
            var id = (await write.IngestAsync(record, observer)).MessageId!; var objectId = Ulid.NewUlid();
            await fixture.ExecuteAsync($"""
                INSERT INTO journal_objects
                  (id, object_key, owner, sha256, byte_size, mime_type, state, created_at, updated_at)
                VALUES ('{objectId}', 'synthetic/{objectId}', '{observer}', '{digest}', {bytes.Length},
                        '{mime}', 'uploaded', UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
                UPDATE journal_attachments SET object_id = '{objectId}', state = 'committed',
                  not_archived_reason = NULL, sha256 = '{digest}' WHERE message_id = '{id}';
                """);
            bucket.Objects[$"synthetic/{objectId}"] = bytes;
            return id;
        }
        // The byte store is a double; SQL scope, bindings, authentication and route are real.
        var first = await Seed(a, subject, "image/png");
        var second = await Seed(b, subject, "application/pdf");
        var hidden = await Seed(c, other, "application/pdf");
        var key = Enumerable.Repeat((byte)42, 32).ToArray();
        var bindings = new JournalTurnBindings(TimeProvider.System);
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseTestServer(); builder.Logging.ClearProviders();
        await using var app = builder.Build(); var stats = new JournalRuntimeStats();
        JournalAuth.Use(app, [key], stats);
        var endpoint = new JournalAttachmentContentEndpoint(new MySqlJournalReadStore(Db, NullLogger.Instance), bucket,
            new JournalReadGrants(new HashSet<string>()), new JournalBindingScope(bindings, new HashSet<long>()), stats);
        app.MapPost(JournalAttachmentRequest.ContentPath, endpoint.HandleAsync); await app.StartAsync();
        using var client = app.GetTestClient();
        var epoch = Ulid.NewUlid();
        void Bind(long chat, long seq) => bindings.Put(subject, new(epoch, seq, "bound", chat == a ? "private" : "supergroup", 7001, chat));
        async Task<HttpResponseMessage> Fetch(string body)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, JournalAttachmentRequest.ContentPath)
            { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", JournalTokens.Mint(key, "read", subject));
            return await client.SendAsync(request);
        }
        Bind(a, 1);
        var before = await fixture.ScalarRowAsync("SELECT COUNT(*) FROM journal_messages");
        var objectsBefore = await fixture.ScalarRowAsync("SELECT COUNT(*) FROM journal_objects");
        var rowsBefore = new Dictionary<string, string>();
        foreach (var table in new[] { "journal_conversations", "journal_messages", "journal_message_observers", "journal_attachments", "journal_objects" })
            rowsBefore[table] = await fixture.ScalarRowAsync($"SELECT COUNT(*) FROM {table}");
        var keysBefore = bucket.Keys.Order().ToArray();
        for (var i = 0; i < 20; i++)
        {
            using var reply = await Fetch("{\"telegram_message_id\":5}");
            Assert.Equal(HttpStatusCode.OK, reply.StatusCode); Assert.Equal(bytes, await reply.Content.ReadAsByteArrayAsync());
            Assert.Equal(first, reply.Headers.GetValues("X-Journal-Message-Id").Single());
        }
        foreach (var id in new[] { second, hidden, Ulid.NewUlid() })
        {
            using var reply = await Fetch($"{{\"message_id\":\"{id}\"}}");
            Assert.Equal(HttpStatusCode.NotFound, reply.StatusCode);
            Assert.Equal("{\"error\":\"not_found\"}", await reply.Content.ReadAsStringAsync());
        }
        using (var absent = await Fetch("{\"telegram_message_id\":5,\"ordinal\":255}"))
            Assert.Equal(HttpStatusCode.NotFound, absent.StatusCode);
        var openBeforeInvalid = bucket.Keys.Order().ToArray();
        using (var invalid = await Fetch("{\"telegram_message_id\":5,\"telegram_chat_id\":1}"))
        { Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
          Assert.Equal(JournalAttachmentRequest.Invalid("telegram_chat_id"), await invalid.Content.ReadAsStringAsync()); }
        Assert.Equal(openBeforeInvalid, bucket.Keys.Order().ToArray());
        Bind(c, 2);
        using (var reply = await Fetch("{\"telegram_message_id\":5}"))
        { Assert.Equal(HttpStatusCode.NotFound, reply.StatusCode); Assert.Equal("{\"error\":\"not_found\"}", await reply.Content.ReadAsStringAsync()); }
        Bind(b, 3);
        using (var reply = await Fetch("{\"telegram_message_id\":5}"))
        { Assert.Equal(HttpStatusCode.OK, reply.StatusCode); Assert.Equal("application/pdf", reply.Content.Headers.ContentType!.MediaType);
          Assert.Equal(second, reply.Headers.GetValues("X-Journal-Message-Id").Single()); }
        bindings.Put(subject, new(epoch, 4, "unbound"));
        using (var reply = await Fetch("{\"telegram_message_id\":5}"))
        { Assert.Equal(HttpStatusCode.Conflict, reply.StatusCode);
          Assert.Equal(JournalAttachmentRequest.Unavailable("no_bound_conversation"), await reply.Content.ReadAsStringAsync()); }
        Assert.Equal(before, await fixture.ScalarRowAsync("SELECT COUNT(*) FROM journal_messages"));
        Assert.Equal(objectsBefore, await fixture.ScalarRowAsync("SELECT COUNT(*) FROM journal_objects"));
        foreach (var (table, count) in rowsBefore) Assert.Equal(count, await fixture.ScalarRowAsync($"SELECT COUNT(*) FROM {table}"));
        Assert.Equal(keysBefore, bucket.Keys.Order().ToArray());
    }

    [Theory]
    [InlineData("lost", 409, "attachment_lost")]
    [InlineData("uploaded", 200, "")]
    [InlineData("committed", 200, "")]
    [InlineData("aborted", 409, "object_missing")]
    [InlineData("deleting", 409, "object_missing")]
    [InlineData("absent", 409, "object_missing")]
    [InlineData("not_archived", 422, "over_size_cap")]
    [InlineData("excluded", 409, "conversation_not_journaled")]
    public async Task Content_route_real_SQL_state_matrix_applies_to_both_identifiers(string state, int status, string reason)
    {
        var subject = Subject(); var chat = Chat(); var bytes = Encoding.UTF8.GetBytes("synthetic state sentinel " + subject);
        var digest = Convert.ToHexStringLower(SHA256.HashData(bytes)); var objectId = Ulid.NewUlid();
        var write = new MySqlJournalStore(Db, NullLogger.Instance);
        var id = (await write.IngestAsync(Message(chat, 5, BaseTime(), "synthetic", attachments:
            [Attachment(0) with { ByteSize = bytes.Length }]), subject)).MessageId!;
        if (state != "absent") await fixture.ExecuteAsync($"""
            INSERT INTO journal_objects
              (id, object_key, owner, sha256, committed_sha256, byte_size, mime_type, state, created_at, updated_at)
            VALUES ('{objectId}', 'synthetic/{objectId}', '{subject}', '{digest}', '{digest}', {bytes.Length},
                    'application/pdf', '{(state is "aborted" or "deleting" or "committed" ? state : "uploaded")}', UTC_TIMESTAMP(6), UTC_TIMESTAMP(6));
            """);
        await fixture.ExecuteAsync($"""
            UPDATE journal_attachments SET object_id = {(state == "absent" ? "NULL" : $"'{objectId}'")},
              state = '{(state is "lost" or "not_archived" ? state : "committed")}',
              not_archived_reason = {(state == "not_archived" ? "'over_size_cap'" : "NULL")},
              sha256 = '{digest}' WHERE message_id = '{id}';
            """);
        var bucket = new FakeBucket(); bucket.Objects[$"synthetic/{objectId}"] = bytes;
        var key = Enumerable.Repeat((byte)42, 32).ToArray(); var stats = new JournalRuntimeStats();
        var bindings = new JournalTurnBindings(TimeProvider.System);
        bindings.Put(subject, new(Ulid.NewUlid(), 1, "bound", "supergroup", 7001, chat));
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseTestServer(); builder.Logging.ClearProviders();
        await using var app = builder.Build(); JournalAuth.Use(app, [key], stats);
        var endpoint = new JournalAttachmentContentEndpoint(new MySqlJournalReadStore(Db, NullLogger.Instance), bucket,
            new JournalReadGrants(new HashSet<string>()),
            new JournalBindingScope(bindings, state == "excluded" ? new HashSet<long> { chat } : new HashSet<long>()), stats);
        app.MapPost(JournalAttachmentRequest.ContentPath, endpoint.HandleAsync); await app.StartAsync();
        using var client = app.GetTestClient();
        foreach (var body in new[] { $"{{\"message_id\":\"{id}\"}}", "{\"telegram_message_id\":5}" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, JournalAttachmentRequest.ContentPath)
            { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", JournalTokens.Mint(key, "read", subject));
            using var reply = await client.SendAsync(request); Assert.Equal(status, (int)reply.StatusCode);
            if (status == 200) Assert.Equal(bytes, await reply.Content.ReadAsByteArrayAsync());
            else Assert.Equal(state == "not_archived" ? "{\"error\":\"not_archived\",\"reason\":\"over_size_cap\"}"
                : JournalAttachmentRequest.Unavailable(reason), await reply.Content.ReadAsStringAsync());
            Assert.Equal(0, stats.AttachmentFetchInFlight);
        }
    }

}
