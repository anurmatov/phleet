using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Fleet.Comms;
using Fleet.Comms.Auth;
using Fleet.Comms.Configuration;
using Fleet.Comms.Contracts;
using Fleet.Conversations.Contracts;
using Fleet.Protocol;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Fleet.Conversations.Tests;

/// <summary>
/// The north surface against the REAL store, for the two things a substitute cannot answer.
/// </summary>
/// <remarks>
/// <para>
/// <c>Fleet.Comms.Tests</c> drives these routes against a fake, which is right for questions about
/// statuses and bodies. It is wrong for anything where the database is the authority — the width of
/// a column, or the value of a conversation's head — because a fake returns whatever it was told to
/// and agrees with itself.
/// </para>
/// <para>
/// So this class is small on purpose: only the cases where the store is the thing being asked.
/// </para>
/// </remarks>
[Collection("mysql")]
public sealed class NorthSurfaceTests(MySqlFixture fixture)
{
    /// <summary>
    /// <c>hello</c> carries the conversation's REAL head, on a conversation with several events and
    /// a floor that is not 1.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The values were derived rather than read: <c>nextSeq</c> was the probe page's last seq plus
    /// one, and <c>retainedFloorSeq</c> was whatever the gap payload carried — which is <c>null</c>
    /// whenever no gap was produced, so the frame reported a floor of <b>0</b>, a position that does
    /// not exist.
    /// </para>
    /// <para>
    /// Both defaults are invisible on a fresh conversation, where the floor really is 1 and the
    /// probe page really is the tail. This test seeds several events and then advances the floor by
    /// collecting some, so a derived value and a read value are different numbers.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Hello_carries_the_conversations_real_next_seq_and_retained_floor()
    {
        var store = fixture.CreateStore();

        var conversation = await store.OpenConversationAsync(new OpenConversationRequest
        {
            ChannelId = "client",
            ExternalRef = "conv-" + Ulid.NewUlid(),
            PrincipalId = OwnerPrincipal,
        });

        var epoch = Ulid.NewUlid();

        for (ulong ordinal = 1; ordinal <= 6; ordinal++)
        {
            await store.AppendBatchAsync(new AppendBatchRequest
            {
                ConversationId = conversation.ConversationId,
                Epoch = epoch,
                Events =
                [
                    new StagedEvent
                    {
                        Event = new EventDescriptor
                        {
                            Kind = ConversationEventKind.TurnNotice,
                            EventId = Ulid.NewUlid(),
                            PayloadJson = """{"text":"x"}""",
                        },
                        Ordinal = ordinal,
                        RetentionClass = EventRetentionClass.Durable,
                    },
                ],
            });
        }

        // Age the first three past the durable horizon and collect them, so the floor moves off 1.
        await fixture.ExecuteAsync($"""
            UPDATE conversation_events
               SET emitted_at = DATE_SUB(UTC_TIMESTAMP(6), INTERVAL 400 DAY)
             WHERE conversation_id = '{conversation.ConversationId}' AND seq <= 3
            """);

        await new GarbageCollector(fixture.ConnectionString, fixture.Options,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<GarbageCollector>.Instance)
            .SweepOnceAsync();

        var expectedNextSeq = ulong.Parse(await fixture.ScalarRowAsync(
            $"SELECT next_seq FROM conversations WHERE id = '{conversation.ConversationId}'"));
        var expectedFloor = ulong.Parse(await fixture.ScalarRowAsync(
            $"SELECT retained_floor_seq FROM conversations WHERE id = '{conversation.ConversationId}'"));

        // The staging is only meaningful if the two are not the values a derived implementation
        // would have produced. Asserted, so this test cannot quietly stop testing anything.
        Assert.True(expectedNextSeq > 1, "the conversation should have advanced past its first seq");
        Assert.True(expectedFloor > 1, "the floor should have moved off its default");

        await using var host = await NorthHost.StartAsync(store);
        var token = await host.EnrolledTokenAsync();

        using var socket = await host.StreamAsync(conversation.ConversationId, token);

        var buffer = new byte[16 * 1024];
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var received = await socket.ReceiveAsync(buffer, deadline.Token);

        var hello = FleetProtocolJson.Deserialize<StreamHello>(
            Encoding.UTF8.GetString(buffer, 0, received.Count));

        Assert.NotNull(hello);
        Assert.Equal(expectedNextSeq, hello!.NextSeq);
        Assert.Equal(expectedFloor, hello.RetainedFloorSeq);
    }

    /// <summary>
    /// A 128-character <c>submissionId</c> — the maximum the server publishes — is accepted and
    /// stored whole.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The validator has always allowed 128, and <c>GET /v1/session</c> advertises exactly that, but
    /// <c>submissions.external_ref</c> was <c>CHAR(32)</c>. Every identifier between 33 and 128
    /// characters therefore passed validation and then failed at the database — as a <c>503</c>
    /// under a strict SQL mode, telling the client to retry something that could never succeed, or
    /// as a silent truncation under a non-strict one, which collides two submissions that differ
    /// only after the 32nd character.
    /// </para>
    /// <para>
    /// Against the real store, because a fake stores whatever it is handed and the column is the
    /// whole question. The stored value is read back and compared in full: a length assertion alone
    /// would pass against a column that padded it.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_submission_id_at_the_published_maximum_is_accepted_and_stored_whole()
    {
        var store = fixture.CreateStore();

        var conversation = await store.OpenConversationAsync(new OpenConversationRequest
        {
            ChannelId = "client",
            ExternalRef = "conv-" + Ulid.NewUlid(),
            PrincipalId = OwnerPrincipal,
        });

        // Exactly the bound `GET /v1/session` publishes, and deliberately distinct after the 32nd
        // character — the prefix alone would collide under the old column.
        var submissionId = new string('a', 32) + new string('b', CommsLimits.IdentifierMaxLength - 32);
        Assert.Equal(CommsLimits.IdentifierMaxLength, submissionId.Length);

        await using var host = await NorthHost.StartAsync(store);
        var token = await host.EnrolledTokenAsync();

        var response = await host.PostAsync(
            $"/v1/conversations/{conversation.ConversationId}/submissions", token,
            new
            {
                protocol = ProtocolVersion.Current,
                type = "create",
                submissionId,
                text = "hello",
            });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var stored = await fixture.ScalarRowAsync(
            $"SELECT external_ref FROM submissions WHERE conversation_id = '{conversation.ConversationId}'");

        Assert.Equal(submissionId, stored);

        // And a second submission sharing the first 32 characters is a DIFFERENT submission rather
        // than a unique-key collision — which is what truncation would have made it.
        var sibling = new string('a', 32) + new string('c', CommsLimits.IdentifierMaxLength - 32);

        var second = await host.PostAsync(
            $"/v1/conversations/{conversation.ConversationId}/submissions", token,
            new
            {
                protocol = ProtocolVersion.Current,
                type = "create",
                submissionId = sibling,
                text = "hello again",
            });

        Assert.Equal(HttpStatusCode.Created, second.StatusCode);

        Assert.Equal("2", await fixture.ScalarRowAsync(
            $"SELECT COUNT(*) FROM submissions WHERE conversation_id = '{conversation.ConversationId}'"));
    }

    private const string OwnerPrincipal = "p_owner";

    /// <summary>
    /// The north application over <see cref="TestServer"/>, wired to a REAL conversation store.
    /// </summary>
    /// <remarks>
    /// Built through <see cref="CommsApp.BuildNorthApp"/>, the composition <c>Program</c> runs. The
    /// auth store is an in-memory one — these tests are not about device enrollment, only about
    /// getting a valid credential in hand.
    /// </remarks>
    private sealed class NorthHost : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private NorthHost(WebApplication app, HttpClient client)
        {
            _app = app;
            Client = client;
        }

        public HttpClient Client { get; }

        public static async Task<NorthHost> StartAsync(IConversationStore store)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseSetting(WebHostDefaults.ServerUrlsKey, string.Empty);
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();

            var authPath = Path.Combine(
                Directory.CreateTempSubdirectory("fleet-comms-north-").FullName, "auth.db");

            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                // Enabling is a CONFIGURATION fact; the substitute below is what is actually used.
                ["Comms:ConversationConnectionString"] = "Server=unused-by-this-test;",
                ["Comms:AuthStorePath"] = authPath,
            });

            builder.Services.AddSingleton(store);

            // `allowCreate`, because nothing creates the auth database implicitly — a deleted volume
            // has to be an error rather than a silently empty store, so `store init` is the one
            // thing that may create it. A test is its own operator.
            builder.Services.AddSingleton<IAuthStore>(new SqliteAuthStore(authPath, allowCreate: true));

            var app = CommsApp.BuildNorthApp(builder);
            await app.StartAsync();

            return new NorthHost(app, app.GetTestClient());
        }

        public async Task<string> EnrolledTokenAsync()
        {
            var auth = _app.Services.GetRequiredService<AuthService>();
            var code = await auth.IssueEnrollmentCodeAsync(OwnerPrincipal);

            var registration = await Client.PostAsJsonAsync("/v1/auth/devices", new
            {
                protocol = ProtocolVersion.Current,
                enrollmentCode = code,
            });

            var device = await registration.Content.ReadFromJsonAsync<RegisteredDevice>(
                FleetProtocolJson.Options);

            var minted = await Client.PostAsJsonAsync("/v1/auth/token", new
            {
                protocol = ProtocolVersion.Current,
                deviceId = device!.DeviceId,
                deviceSecret = device.DeviceSecret,
            });

            return (await minted.Content.ReadFromJsonAsync<MintedToken>(FleetProtocolJson.Options))!
                .AccessToken;
        }

        public Task<HttpResponseMessage> PostAsync(string path, string token, object body)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = new StringContent(
                    FleetProtocolJson.Serialize(body), Encoding.UTF8, "application/json"),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return Client.SendAsync(request);
        }

        public Task<System.Net.WebSockets.WebSocket> StreamAsync(string conversationId, string token)
        {
            var client = _app.GetTestServer().CreateWebSocketClient();
            client.ConfigureRequest = request =>
                request.Headers["Authorization"] = $"Bearer {token}";

            return client.ConnectAsync(
                new Uri($"http://localhost/v1/conversations/{conversationId}/stream"
                    + "?clientInstanceId=inst_a1"),
                CancellationToken.None);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        private sealed record RegisteredDevice(string DeviceId, string DeviceSecret);

        private sealed record MintedToken(string AccessToken);
    }
}
