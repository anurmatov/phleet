using System.Net;
using Fleet.Comms.Routes;
using Fleet.Conversations.Contracts;
using Fleet.Conversations.Journal;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;

namespace Fleet.Comms.Tests;

/// <summary>
/// The journal listener's contract: who gets in, what is refused before the store, and what each
/// store outcome answers (#375 AC10–AC13, AC16d).
/// </summary>
public sealed class JournalEndpointTests
{
    private static string Ingest(string subject = "agent1", string? key = null) =>
        JournalTestHost.Token(JournalTokens.PurposeIngest, subject, key);

    // ── authentication (AC11, AC12) ──────────────────────────────────────────

    /// <summary>
    /// No token, a bad MAC, a reserved purpose, the wrong purpose, and an unknown path all answer
    /// the same bytes, and none of them reaches the store.
    /// </summary>
    [Fact]
    public async Task Every_refusal_is_the_same_401_and_nothing_reaches_the_store()
    {
        await using var host = await JournalTestHost.StartAsync();
        var body = JournalRecords.Valid();

        var ingest = Ingest();
        var badMac = ingest[..^2] + (ingest[^2] == 'A' ? "BB" : "AA");

        var responses = new List<HttpResponseMessage>
        {
            await host.PostAsync(body, token: null),
            await host.PostAsync(body, badMac),
            await host.PostAsync(body, JournalTestHost.Token(JournalTokens.PurposeRead)),
            await host.PostAsync(body, JournalTestHost.Token(JournalTokens.PurposeIngestService)),
            await host.PostAsync(body, JournalTestHost.Token(JournalTokens.PurposeStatus)),
            await host.PostAsync(body, "not-a-token"),
            await host.SendAsync(HttpMethod.Get, "/nonexistent", null, token: null),
            await host.SendAsync(HttpMethod.Get, "/nonexistent", null, ingest),
            await host.SendAsync(HttpMethod.Get, "/journal/v1/status", null, ingest),
        };

        var expected = await responses[0].Content.ReadAsByteArrayAsync();
        Assert.Equal("{\"error\":\"unauthorized\"}", System.Text.Encoding.UTF8.GetString(expected));

        foreach (var response in responses)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Equal(expected, await response.Content.ReadAsByteArrayAsync());
        }

        Assert.Equal(0, host.Store.Calls);
    }

    /// <summary>The south bearer is not a journal credential (MUST NOT 1).</summary>
    [Fact]
    public async Task The_south_bearer_is_refused_on_the_journal_listener()
    {
        await using var host = await JournalTestHost.StartAsync();

        var response = await host.PostAsync(JournalRecords.Valid(), "south-suite-bearer-token");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, host.Store.Calls);
    }

    [Fact]
    public async Task The_observer_is_the_token_subject()
    {
        await using var host = await JournalTestHost.StartAsync();

        var response = await host.PostAsync(JournalRecords.Valid(), Ingest("agent-runtime_7"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("agent-runtime_7", Assert.Single(host.Store.Ingested).Observer);
    }

    /// <summary>During rotation both keys verify; once the old key is removed its tokens are refused.</summary>
    [Fact]
    public async Task Rotation_accepts_both_keys_then_only_the_new_one()
    {
        var oldToken = Ingest(key: JournalTestHost.KeyA);
        var newToken = Ingest(key: JournalTestHost.KeyB);

        await using (var rotating = await JournalTestHost.StartAsync($"{JournalTestHost.KeyB},{JournalTestHost.KeyA}"))
        {
            Assert.Equal(HttpStatusCode.Created, (await rotating.PostAsync(JournalRecords.Valid(), oldToken)).StatusCode);
            Assert.Equal(HttpStatusCode.Created, (await rotating.PostAsync(JournalRecords.Valid(), newToken)).StatusCode);
        }

        await using var rotated = await JournalTestHost.StartAsync(JournalTestHost.KeyB);
        Assert.Equal(HttpStatusCode.Unauthorized, (await rotated.PostAsync(JournalRecords.Valid(), oldToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await rotated.PostAsync(JournalRecords.Valid(), newToken)).StatusCode);
    }

    /// <summary>
    /// The journal routes are not on the south listener: with the SOUTH bearer, which gets a caller
    /// past south's pre-routing check, both paths are router 404s (AC10).
    /// </summary>
    [Fact]
    public async Task The_journal_routes_are_404_on_the_south_listener()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseSetting(WebHostDefaults.ServerUrlsKey, string.Empty);
        builder.WebHost.UseTestServer();

        await using var south = CommsApp.BuildSouthApp(builder, new FakeConversationStore(), new Configuration.CommsOptions
        {
            SouthBearerToken = "south-token",
            AgentName = "example-agent",
            ConversationConnectionString = "configured",
        });
        await south.StartAsync();
        var client = south.GetTestClient();

        foreach (var (method, path) in new[] { (HttpMethod.Get, JournalEndpoints.StatusPath), (HttpMethod.Post, JournalEndpoints.MessagesPath) })
        {
            var request = new HttpRequestMessage(method, path);
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer south-token");
            Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(request)).StatusCode);
        }
    }

    // ── the record (AC13, AC16d) ─────────────────────────────────────────────

    public static TheoryData<string, int, string> Refusals() => new()
    {
        { JournalRecords.Valid(extra: ",\"observer\":\"agent2\""), 422, "{\"error\":\"invalid_record\",\"field\":\"observer\"}" },
        { JournalRecords.Valid(origin: "agent_tool"), 422, "{\"error\":\"invalid_record\",\"field\":\"origin\"}" },
        { JournalRecords.Valid(attachments: "[{\"ordinal\":0,\"kind\":\"photo\",\"mimeType\":\"image/jpeg\",\"notArchivedReason\":\"media_disabled\",\"uploadId\":\"u1\"}]"), 409, "{\"error\":\"media_disabled\"}" },
        { JournalRecords.Valid(sentAt: "2012-12-31T23:59:59Z"), 422, "{\"error\":\"invalid_record\",\"field\":\"sentAt\"}" },
        { JournalRecords.Valid(sentAt: DateTimeOffset.UtcNow.AddHours(1).ToString("O")), 422, "{\"error\":\"invalid_record\",\"field\":\"sentAt\"}" },
        { JournalRecords.Valid(sentAt: "2026-01-01T00:00:00"), 422, "{\"error\":\"invalid_record\",\"field\":\"sentAt\"}" },
        { JournalRecords.Valid(text: "\"" + new string('a', 65_537) + "\""), 422, "{\"error\":\"invalid_record\",\"field\":\"text\"}" },
        { JournalRecords.Valid(chatId: 0), 422, "{\"error\":\"invalid_record\",\"field\":\"telegram.chatId\"}" },
        { JournalRecords.Valid(chatKind: "channel"), 422, "{\"error\":\"invalid_record\",\"field\":\"telegram.chatKind\"}" },
        { JournalRecords.Valid(direction: "sideways"), 422, "{\"error\":\"invalid_record\",\"field\":\"direction\"}" },
        { JournalRecords.Valid(eventId: "not-a-ulid"), 422, "{\"error\":\"invalid_record\",\"field\":\"eventId\"}" },
        { JournalRecords.Valid(extra: ",\"sendGroup\":{\"id\":\"01J00000000000000000000000\",\"part\":1,\"parts\":2}"), 422, "{\"error\":\"invalid_record\",\"field\":\"sendGroup\"}" },
        { JournalRecords.Valid(direction: "outbound", extra: ",\"sendGroup\":{\"id\":\"01J00000000000000000000000\",\"part\":3,\"parts\":2}"), 422, "{\"error\":\"invalid_record\",\"field\":\"sendGroup\"}" },
        { JournalRecords.Valid(direction: "outbound", extra: ",\"sendGroup\":{\"id\":\"01J00000000000000000000000\",\"part\":1,\"parts\":65}"), 422, "{\"error\":\"invalid_record\",\"field\":\"sendGroup\"}" },
        { JournalRecords.Valid(extra: ",\"unexpected\":1"), 422, "{\"error\":\"invalid_record\",\"field\":\"unexpected\"}" },
        { "{\"eventId\":", 422, "{\"error\":\"invalid_record\",\"field\":\"body\"}" },
        { "[]", 422, "{\"error\":\"invalid_record\",\"field\":\"body\"}" },
    };

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task A_bad_record_is_refused_before_the_store(string body, int status, string expected)
    {
        await using var host = await JournalTestHost.StartAsync();

        var response = await host.PostAsync(body, Ingest());

        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal(expected, await response.Content.ReadAsStringAsync());
        Assert.Equal(0, host.Store.Calls);
    }

    /// <summary>65,536 UTF-8 bytes is the limit, counted in bytes, not characters.</summary>
    [Fact]
    public async Task Text_is_bounded_in_utf8_bytes()
    {
        await using var host = await JournalTestHost.StartAsync();

        var atLimit = await host.PostAsync(JournalRecords.Valid(text: "\"" + new string('a', 65_536) + "\""), Ingest());
        Assert.Equal(HttpStatusCode.Created, atLimit.StatusCode);

        // 21,846 three-byte characters are 65,538 bytes, though far fewer characters.
        var overInBytes = await host.PostAsync(JournalRecords.Valid(text: "\"" + new string('中', 21_846) + "\""), Ingest());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, overInBytes.StatusCode);
    }

    [Fact]
    public async Task A_body_over_one_mebibyte_is_413_before_parsing()
    {
        await using var host = await JournalTestHost.StartAsync();

        var response = await host.PostAsync(new string(' ', JournalRecordParser.MaxBodyBytes + 1), Ingest());

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal("{\"error\":\"too_large\"}", await response.Content.ReadAsStringAsync());
        Assert.Equal(0, host.Store.Calls);
    }

    /// <summary>An excluded chat is refused before the store is called: nothing can be written (MUST NOT 4).</summary>
    [Fact]
    public async Task An_excluded_chat_never_reaches_the_store()
    {
        await using var host = await JournalTestHost.StartAsync(excludedChatIds: "-100555, ");

        var response = await host.PostAsync(JournalRecords.Valid(chatId: -100555), Ingest());

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("{\"error\":\"excluded_chat\"}", await response.Content.ReadAsStringAsync());
        Assert.Equal(0, host.Store.Calls);
    }

    public static TheoryData<JournalIngestOutcome, int, string> Outcomes() => new()
    {
        { JournalIngestOutcome.Created, 201, "{\"messageId\":\"01J00000000000000000000000\",\"result\":\"created\"}" },
        { JournalIngestOutcome.Duplicate, 200, "{\"messageId\":\"01J00000000000000000000000\",\"result\":\"duplicate\"}" },
        { JournalIngestOutcome.ObserverAdded, 200, "{\"messageId\":\"01J00000000000000000000000\",\"result\":\"observer_added\"}" },
        { JournalIngestOutcome.Conflict, 409, "{\"error\":\"idempotency_conflict\"}" },
        { JournalIngestOutcome.EventIdReused, 409, "{\"error\":\"idempotency_conflict\",\"reason\":\"event_id_reused\"}" },
    };

    [Theory]
    [MemberData(nameof(Outcomes))]
    public async Task Each_store_outcome_has_its_answer(JournalIngestOutcome outcome, int status, string expected)
    {
        await using var host = await JournalTestHost.StartAsync(store: new FakeJournalStore { Outcome = outcome });

        var response = await host.PostAsync(JournalRecords.Valid(), Ingest());

        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal(expected, await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(null, "{\"error\":\"store_unavailable\"}")]
    [InlineData("schema_behind", "{\"error\":\"store_unavailable\",\"reason\":\"schema_behind\"}")]
    public async Task An_unavailable_store_is_503(string? reason, string expected)
    {
        await using var host = await JournalTestHost.StartAsync(
            store: new FakeJournalStore { Unavailable = true, UnavailableReason = reason });

        var response = await host.PostAsync(JournalRecords.Valid(), Ingest());

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(expected, await response.Content.ReadAsStringAsync());
    }

    // ── the per-subject cap (AC16d) ──────────────────────────────────────────

    /// <summary>
    /// A ninth in-flight request from one subject is 429 with Retry-After: 1, while another subject
    /// is still served.
    /// </summary>
    [Fact]
    public async Task A_ninth_concurrent_request_from_one_subject_is_429_and_another_subject_is_served()
    {
        var gate = new SemaphoreSlim(0);
        await using var host = await JournalTestHost.StartAsync(store: new FakeJournalStore { Gate = gate });

        var held = Enumerable.Range(0, JournalAuth.MaxInFlightPerSubject)
            .Select(_ => host.PostAsync(JournalRecords.Valid(), Ingest("agent1")))
            .ToList();

        for (var i = 0; i < JournalAuth.MaxInFlightPerSubject; i++)
            Assert.True(await host.Store.Entered.WaitAsync(TimeSpan.FromSeconds(10)), "a request never reached the store");

        var ninth = await host.PostAsync(JournalRecords.Valid(), Ingest("agent1"));
        Assert.Equal(HttpStatusCode.TooManyRequests, ninth.StatusCode);
        Assert.Equal("1", ninth.Headers.RetryAfter?.ToString());
        Assert.Equal("{\"error\":\"too_many_requests\"}", await ninth.Content.ReadAsStringAsync());

        var other = host.PostAsync(JournalRecords.Valid(), Ingest("agent2"));
        Assert.True(await host.Store.Entered.WaitAsync(TimeSpan.FromSeconds(10)), "another subject was not served");

        gate.Release(JournalAuth.MaxInFlightPerSubject + 1);
        Assert.Equal(HttpStatusCode.Created, (await other).StatusCode);
        foreach (var response in await Task.WhenAll(held))
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    // ── status ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Status_needs_a_status_token_and_carries_no_text()
    {
        await using var host = await JournalTestHost.StartAsync();
        await host.PostAsync(JournalRecords.Valid(text: "\"secret words\""), Ingest());

        var refused = await host.SendAsync(HttpMethod.Get, "/journal/v1/status", null, Ingest());
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);

        var response = await host.SendAsync(HttpMethod.Get, "/journal/v1/status", null,
            JournalTestHost.Token(JournalTokens.PurposeStatus, "operator"));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"schemaVersion\":4", body, StringComparison.Ordinal);
        Assert.Contains("\"enabled\":true", body, StringComparison.Ordinal);
        Assert.Contains("\"observer\":\"agent1\"", body, StringComparison.Ordinal);
        Assert.Contains("\"unauthorized\":1", body, StringComparison.Ordinal);
        Assert.Contains("\"p95Ms\":", body, StringComparison.Ordinal);
        Assert.DoesNotContain("secret words", body, StringComparison.Ordinal);
    }
}
