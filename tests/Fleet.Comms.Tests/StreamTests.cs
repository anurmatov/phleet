using System.Net.WebSockets;
using System.Text;
using Fleet.Comms.Contracts;
using Fleet.Comms.Routes;
using Fleet.Protocol;

namespace Fleet.Comms.Tests;

/// <summary>
/// The WebSocket stream, over a real upgrade (AC10, AC12, AC14).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Microsoft.AspNetCore.TestHost.TestServer"/> performs the actual handshake, so the
/// frames read here are the frames the endpoint wrote. That matters more than usual for this
/// surface: everything interesting about it — the close codes, what the first frame is, what happens
/// to a client that says something unexpected — is observable only from the socket.
/// </para>
/// <para>
/// The pong deadline and the revocation bound are deliberately NOT tested here. Both are driven by
/// the 20-second ping tick, so asserting them would mean either a twenty-second test or a test-only
/// seam that changes the timing the assertion is about. They belong to acceptance, and the PR body
/// says so rather than leaving a reader to infer it from an absence.
/// </para>
/// </remarks>
public class StreamTests
{
    /// <summary>
    /// The first frame is `hello`, and it carries what a client needs before any event.
    /// </summary>
    [Fact]
    public async Task The_first_frame_is_hello_and_carries_the_server_limits()
    {
        var store = new FakeConversationStore();
        await using var host = await NorthTestHost.StartAsync(conversations: store);
        var token = await AuthorizeAsync(host);

        using var socket = await ConnectAsync(host, store.ConversationId, token);

        var hello = FleetProtocolJson.Deserialize<StreamHello>(await ReceiveAsync(socket));

        Assert.NotNull(hello);
        Assert.Equal("hello", hello!.Kind);
        Assert.Equal(store.ConversationId, hello.ConversationId);
        Assert.Equal(ProtocolLimits.MaxInboundTextBytes, hello.Limits.InboundTextBytes);
        Assert.Equal(256, hello.Limits.OutboundBufferEvents);
    }

    /// <summary>
    /// AC10: an event delivered over the stream is BYTE-IDENTICAL to the same event returned by
    /// catch-up.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The two paths must not drift, because a client reconciles them against each other: it streams
    /// while connected and catches up across a reconnect, and an event that differed between the two
    /// would look like a second event at the same seq.
    /// </para>
    /// <para>
    /// Compared as raw strings. Deserializing both and comparing the objects would pass through a
    /// field-order difference, a null that became an omission, or an enum that serialized two ways —
    /// which is exactly the class of drift this asserts against.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_streamed_event_is_byte_identical_to_the_same_event_from_catch_up()
    {
        var store = new FakeConversationStore();
        store.Events.Add(FakeConversationStore.Stored(20, ConversationEventKind.TurnStarted));
        store.Events.Add(FakeConversationStore.Stored(21, ConversationEventKind.TurnFinal));

        await using var host = await NorthTestHost.StartAsync(conversations: store);
        var token = await AuthorizeAsync(host);

        using var socket = await ConnectAsync(host, store.ConversationId, token);
        await ReceiveAsync(socket); // hello

        var streamed = new List<string>();
        for (var i = 0; i < store.Events.Count; i++)
            streamed.Add(await ReceiveAsync(socket));

        var catchUp = await host.Client.SendAsync(CatchUp(store.ConversationId, token));
        var body = FleetProtocolJson.Deserialize<CatchUpResponse>(
            await catchUp.Content.ReadAsStringAsync())!;

        var fromCatchUp = body.Events.Select(FleetProtocolJson.Serialize).ToList();

        Assert.Equal(fromCatchUp.Count, streamed.Count);
        Assert.Equal(fromCatchUp, streamed);
    }

    /// <summary>
    /// A client resuming from a cursor is sent only what follows it.
    /// </summary>
    [Fact]
    public async Task A_stream_resumes_after_the_requested_cursor()
    {
        var store = new FakeConversationStore();
        store.Events.Add(FakeConversationStore.Stored(20));
        store.Events.Add(FakeConversationStore.Stored(21, ConversationEventKind.TurnFinal));

        await using var host = await NorthTestHost.StartAsync(conversations: store);
        var token = await AuthorizeAsync(host);

        using var socket = await ConnectAsync(host, store.ConversationId, token, afterSeq: 20);
        await ReceiveAsync(socket); // hello

        var first = FleetProtocolJson.Deserialize<ConversationEvent>(await ReceiveAsync(socket));

        Assert.Equal(21, first!.Seq);
    }

    /// <summary>
    /// AC14: any client-to-server frame other than <c>pong</c> closes the connection.
    /// </summary>
    /// <remarks>
    /// There is no command surface on the socket. Accepting one would be a second code path for
    /// everything the HTTPS routes already do, with its own authentication and its own bugs —
    /// submissions, steers, cancels and cursor advances are HTTPS only.
    /// </remarks>
    [Fact]
    public async Task An_unsupported_client_frame_closes_the_connection()
    {
        var store = new FakeConversationStore();
        await using var host = await NorthTestHost.StartAsync(conversations: store);
        var token = await AuthorizeAsync(host);

        using var socket = await ConnectAsync(host, store.ConversationId, token);
        await ReceiveAsync(socket); // hello

        await socket.SendAsync(
            Encoding.UTF8.GetBytes("""{"kind":"submission.create","text":"through the socket"}"""),
            WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);

        await WaitForCloseAsync(socket);

        Assert.NotEqual(WebSocketState.Open, socket.State);
    }

    /// <summary>
    /// A <c>pong</c> does NOT close the connection.
    /// </summary>
    /// <remarks>
    /// The negative control for the test above. Without it, an endpoint that closed on every client
    /// frame — including the one frame it is supposed to accept — would pass.
    /// </remarks>
    [Fact]
    public async Task A_pong_is_accepted_and_leaves_the_connection_open()
    {
        var store = new FakeConversationStore();
        await using var host = await NorthTestHost.StartAsync(conversations: store);
        var token = await AuthorizeAsync(host);

        using var socket = await ConnectAsync(host, store.ConversationId, token);
        await ReceiveAsync(socket); // hello

        await socket.SendAsync(
            Encoding.UTF8.GetBytes("""{"kind":"pong"}"""),
            WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);

        // Give the reader a moment to act on it, then assert it did not.
        await Task.Delay(TimeSpan.FromMilliseconds(250));

        Assert.Equal(WebSocketState.Open, socket.State);
    }

    /// <summary>
    /// AC12: a second upgrade for the same conversation and client instance closes the older one.
    /// </summary>
    /// <remarks>
    /// One active stream per conversation per client instance. Without this, a client that
    /// reconnected without closing cleanly — which is every client on a flaky network — would
    /// accumulate sockets, each holding a tail reader against the store.
    /// </remarks>
    [Fact]
    public async Task A_second_upgrade_supersedes_the_first()
    {
        var store = new FakeConversationStore();
        await using var host = await NorthTestHost.StartAsync(conversations: store);
        var token = await AuthorizeAsync(host);

        using var first = await ConnectAsync(host, store.ConversationId, token);
        await ReceiveAsync(first); // hello

        using var second = await ConnectAsync(host, store.ConversationId, token);
        await ReceiveAsync(second); // hello

        await WaitForCloseAsync(first);

        Assert.NotEqual(WebSocketState.Open, first.State);

        // And the new one is still serving. Asserted, because "the old one closed" is also true of
        // an endpoint that closed both.
        Assert.Equal(WebSocketState.Open, second.State);
    }

    /// <summary>
    /// A stream for another principal's conversation is refused BEFORE the upgrade.
    /// </summary>
    /// <remarks>
    /// A `404` on the HTTP response rather than a socket that opens and then closes: a client that
    /// got an upgrade would reasonably conclude the conversation exists.
    /// </remarks>
    [Fact]
    public async Task A_stream_for_another_principals_conversation_never_upgrades()
    {
        var store = new FakeConversationStore { OwnerPrincipalId = "p_somebody_else" };
        await using var host = await NorthTestHost.StartAsync(conversations: store);
        var token = await AuthorizeAsync(host);

        var client = host.WebSocketClient();
        client.ConfigureRequest = request =>
            request.Headers["Authorization"] = $"Bearer {token}";

        await Assert.ThrowsAnyAsync<Exception>(() => client.ConnectAsync(
            new Uri($"http://localhost/v1/conversations/{store.ConversationId}/stream?clientInstanceId=inst_a1"),
            CancellationToken.None));
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static async Task<string> AuthorizeAsync(NorthTestHost host)
    {
        var (_, _, token) = await host.EnrolledDeviceAsync();
        return token;
    }

    private static Task<WebSocket> ConnectAsync(
        NorthTestHost host, string conversationId, string token, ulong afterSeq = 0,
        string clientInstanceId = "inst_a1")
    {
        var client = host.WebSocketClient();
        client.ConfigureRequest = request =>
            request.Headers["Authorization"] = $"Bearer {token}";

        return client.ConnectAsync(
            new Uri($"http://localhost/v1/conversations/{conversationId}/stream"
                + $"?clientInstanceId={clientInstanceId}&afterSeq={afterSeq}"),
            CancellationToken.None);
    }

    private static async Task<string> ReceiveAsync(WebSocket socket)
    {
        var buffer = new byte[16 * 1024];

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var received = await socket.ReceiveAsync(buffer, deadline.Token);

        return Encoding.UTF8.GetString(buffer, 0, received.Count);
    }

    /// <summary>
    /// Wait for the socket to leave <c>Open</c>, bounded.
    /// </summary>
    /// <remarks>
    /// Bounded rather than indefinite: a test that waited forever for a close that never comes is a
    /// hung CI job, which is worse than a failed assertion because it looks like nothing happened.
    /// </remarks>
    private static async Task WaitForCloseAsync(WebSocket socket)
    {
        var buffer = new byte[4096];
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        try
        {
            while (socket.State == WebSocketState.Open)
                await socket.ReceiveAsync(buffer, deadline.Token);
        }
        catch (Exception)
        {
            // A close arrives as either a close frame or a transport exception, depending on how the
            // peer went away. Both are "not open", which is what the caller asserts.
        }
    }

    private static HttpRequestMessage CatchUp(string conversationId, string token)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Get, $"/v1/conversations/{conversationId}/events?afterSeq=0");
        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return request;
    }
}
