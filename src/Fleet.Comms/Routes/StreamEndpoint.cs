using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;
using Fleet.Comms.Auth;
using Fleet.Comms.Contracts;
using Fleet.Comms.Configuration;
using Fleet.Conversations;
using Fleet.Conversations.Contracts;
using Fleet.Protocol;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Fleet.Comms.Routes;

/// <summary>
/// `GET /v1/conversations/{id}/stream` — the WebSocket tail (docs/first-party-api.md §9).
/// </summary>
/// <remarks>
/// <para>
/// <b>The socket is an optimisation and never the source of truth.</b> It reads the committed log
/// through <see cref="IConversationStore.TailAsync"/>, so nothing reaches a client that is not
/// already durable, and a reconnect always catches up from the durable cursor rather than from
/// anything held in memory here.
/// </para>
/// <para>
/// Client-to-server frames are <c>pong</c> and nothing else. Submissions, steers, cancels and cursor
/// advances are HTTPS only — a command surface on the socket would be a second code path for
/// everything the routes already do, with its own authentication and its own bugs.
/// </para>
/// </remarks>
public static class StreamEndpoint
{
    /// <summary>Close codes (§9). Append-only: a deployed client keys its reconnect on the number.</summary>
    public static class Close
    {
        public const int Unauthorized = 4401;
        public const int Forbidden = 4403;
        public const int PongTimeout = 4408;
        public const int Superseded = 4409;
        public const int BufferOverflow = 4413;
        public const int RateLimited = 4429;
        public const int Internal = 4500;
        public const int Unavailable = 4503;
    }

    /// <summary>How often the server pings, and how long it then waits for the pong.</summary>
    public static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(20);

    /// <summary>
    /// The pong deadline. 20 + 10 is the 30-second detection bound §9 publishes, and it is also what
    /// makes revocation close a live socket within 30 seconds: the token is re-validated on every
    /// ping tick, so a revoked credential is caught at the next tick rather than at some interval
    /// nobody specified.
    /// </summary>
    public static readonly TimeSpan PongTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// One active stream per conversation per client instance. A second upgrade supersedes the
    /// first, which is closed <c>4409</c> rather than left to accumulate.
    /// </summary>
    private static readonly ConcurrentDictionary<string, CancellationTokenSource> Live = new();

    public static async Task HandleAsync(
        HttpContext http, string id, AuthService auth, IConversationStore store,
        ILoggerFactory loggerFactory, CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger(typeof(StreamEndpoint).FullName!);

        // Authentication FIRST, before the upgrade shape is even considered.
        //
        // Header only. A token in a query string is a token in every intermediary's access log and
        // in every crash report, and there is no code path here that reads one from there — so a
        // caller who put it there is unauthenticated, full stop.
        //
        // The ordering matters for a second reason: checking `IsWebSocketRequest` first would answer
        // an unauthenticated caller `400` for a malformed upgrade and `401` for a well-formed one,
        // which tells them the difference without a credential.
        var caller = await NorthEndpoints.AuthenticateAsync(http, auth, ct);
        if (caller is null)
        {
            http.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        if (!http.WebSockets.IsWebSocketRequest)
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        // Kept for the per-tick re-validation below. It is held in a local for the lifetime of one
        // socket and is never logged, never written to a frame and never put in a metric label.
        var bearer = http.Request.Headers.Authorization.ToString()["Bearer ".Length..];

        var clientInstanceId = http.Request.Query["clientInstanceId"].ToString();
        if (!ConversationEndpoints.IsValidIdentifier(clientInstanceId))
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        // Authorisation before the upgrade, so a conversation belonging to someone else is a `404`
        // on the HTTP response rather than a socket that opens and then closes.
        ReadConversationResult head;
        try
        {
            head = await store.ReadAsync(new ReadConversationRequest
            {
                ConversationId = id,
                AfterSeq = 0,
                Limit = 1,
                PrincipalId = caller.PrincipalId,
            }, ct);
        }
        catch (ConversationNotFoundException)
        {
            http.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        catch (Exception)
        {
            http.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        var afterSeq = ParseAfterSeq(http.Request.Query["afterSeq"]);

        var key = $"{id}{clientInstanceId}";
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Supersede before accepting, so the old socket is already closing while the new one opens.
        //
        // ⚠️ CANCEL ONLY — never dispose. The previous connection's own `using` owns that token
        // source and is still reading it: disposing it here makes the older pump throw
        // ObjectDisposedException out of its `finally`, which skips the close handshake entirely.
        // The superseded socket then stayed OPEN, which is the opposite of what superseding means,
        // and no other assertion in this repository would have noticed.
        if (Live.TryRemove(key, out var previous))
            await previous.CancelAsync();

        Live[key] = lifetime;

        using var socket = await http.WebSockets.AcceptWebSocketAsync();

        try
        {
            await PumpAsync(socket, store, auth, caller, bearer, id, afterSeq, head, lifetime, logger);
        }
        finally
        {
            Live.TryRemove(new KeyValuePair<string, CancellationTokenSource>(key, lifetime));
        }
    }

    private static async Task PumpAsync(
        WebSocket socket, IConversationStore store, AuthService auth, AuthenticatedPrincipal caller,
        string bearer, string conversationId, ulong afterSeq, ReadConversationResult head,
        CancellationTokenSource lifetime, ILogger logger)
    {
        // Bounded, and DropWrite is deliberately NOT used: §9 says the server closes 4413 and drops
        // nothing. A dropped event would leave a hole no cursor can detect, which is the one failure
        // the whole durable design exists to prevent.
        var outbound = Channel.CreateBounded<string>(new BoundedChannelOptions(
            CommsLimits.OutboundBufferEvents)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
        });

        var pongs = Channel.CreateUnbounded<bool>();
        var overflow = 0;

        var reader = Task.Run(() => ReadFramesAsync(socket, pongs, lifetime, logger));

        var tail = Task.Run(async () =>
        {
            try
            {
                await foreach (var stored in store.TailAsync(conversationId, afterSeq, lifetime.Token))
                {
                    var frame = FleetProtocolJson.Serialize(
                        ConversationEndpoints.ToEnvelope(stored, caller.PrincipalId, conversationId));

                    if (!outbound.Writer.TryWrite(frame))
                    {
                        // The buffer is full: the client is slower than the conversation. Close
                        // rather than block the tail forever or discard the event.
                        Interlocked.Exchange(ref overflow, 1);
                        await lifetime.CancelAsync();
                        return;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Ordinary shutdown.
            }
        });

        // The conversation's REAL head, read from its row rather than derived from the page.
        //
        // These were `NextAfterSeq + 1` and `Gap?.RetainedFloorSeq ?? 0`, and both were wrong in the
        // ordinary case: the first is the last delivered seq plus one, which is only next_seq when
        // the probe page happened to reach the tail, and the second reports a floor of 0 — a
        // position that does not exist — whenever no gap was produced, which is almost always. A
        // client sizing its catch-up from either would ask for the wrong range.
        await SendAsync(socket, FleetProtocolJson.Serialize(new StreamHello
        {
            ConversationId = conversationId,
            NextSeq = head.NextSeq,
            RetainedFloorSeq = head.RetainedFloorSeq,
            Limits = SessionLimitsSnapshot,
        }), lifetime.Token);

        var closeCode = Close.Internal;
        var closeReason = "internal";

        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                var send = outbound.Reader.WaitToReadAsync(lifetime.Token).AsTask();
                var tick = Task.Delay(PingInterval, lifetime.Token);

                var completed = await Task.WhenAny(send, tick);

                if (completed == send && await send)
                {
                    while (outbound.Reader.TryRead(out var frame))
                        await SendAsync(socket, frame, lifetime.Token);

                    continue;
                }

                // Ping tick. Re-validate the credential FIRST: this is the mechanism behind the
                // 30-second revocation bound, and without it that bound is a hope.
                if (!(await auth.AuthenticateAsync(bearer, CancellationToken.None)).Succeeded)
                {
                    closeCode = Close.Unauthorized;
                    closeReason = "unauthorized";
                    break;
                }

                await SendAsync(socket, """{"kind":"ping"}""", lifetime.Token);

                using var deadline = new CancellationTokenSource(PongTimeout);
                try
                {
                    await pongs.Reader.ReadAsync(deadline.Token);
                }
                catch (OperationCanceledException)
                {
                    closeCode = Close.PongTimeout;
                    closeReason = "pong timeout";
                    break;
                }
            }

            if (Volatile.Read(ref overflow) == 1)
            {
                closeCode = Close.BufferOverflow;
                closeReason = "buffer overflow";
            }
            else if (lifetime.IsCancellationRequested && closeCode == Close.Internal)
            {
                closeCode = Close.Superseded;
                closeReason = "superseded";
            }
        }
        catch (OperationCanceledException)
        {
            closeCode = Volatile.Read(ref overflow) == 1 ? Close.BufferOverflow : Close.Superseded;
            closeReason = closeCode == Close.BufferOverflow ? "buffer overflow" : "superseded";
        }
        catch (Exception e)
        {
            // Type only. The message can carry provider text, a path or a session id.
            logger.LogWarning("conversation stream failed: {Error}", e.GetType().Name);
            closeCode = Close.Internal;
            closeReason = "internal";
        }
        finally
        {
            // Defensive: the token source may already have been cancelled by a superseding
            // connection, and the close handshake must run regardless. Losing it is how a socket
            // stays open after the server has decided it should not.
            try
            {
                await lifetime.CancelAsync();
            }
            catch (ObjectDisposedException)
            {
                // Already torn down; nothing left to cancel.
            }

            await CloseAsync(socket, closeCode, closeReason);
            await Task.WhenAll(reader, tail);
        }
    }

    /// <summary>
    /// Read client frames. <c>pong</c> and nothing else.
    /// </summary>
    /// <remarks>
    /// Any other frame closes the connection. Accepting one would mean a second command surface with
    /// its own parsing, and §9 is explicit that there is not one.
    /// </remarks>
    private static async Task ReadFramesAsync(
        WebSocket socket, ChannelWriter<bool> pongs, CancellationTokenSource lifetime, ILogger logger)
    {
        var buffer = new byte[1024];

        try
        {
            while (socket.State == WebSocketState.Open && !lifetime.IsCancellationRequested)
            {
                var received = await socket.ReceiveAsync(buffer, lifetime.Token);

                if (received.MessageType == WebSocketMessageType.Close)
                    break;

                var text = Encoding.UTF8.GetString(buffer, 0, received.Count);

                if (text.Contains("\"pong\"", StringComparison.Ordinal))
                {
                    pongs.TryWrite(true);
                    continue;
                }

                logger.LogInformation("conversation stream received an unsupported client frame");
                await lifetime.CancelAsync();
                break;
            }
        }
        catch (Exception)
        {
            await lifetime.CancelAsync();
        }
        finally
        {
            pongs.TryComplete();
        }
    }

    private static async Task SendAsync(WebSocket socket, string frame, CancellationToken ct)
    {
        if (socket.State != WebSocketState.Open) return;

        await socket.SendAsync(
            Encoding.UTF8.GetBytes(frame), WebSocketMessageType.Text, endOfMessage: true, ct);
    }

    private static async Task CloseAsync(WebSocket socket, int code, string reason)
    {
        if (socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived)) return;

        try
        {
            await socket.CloseAsync(
                (WebSocketCloseStatus)code, reason, CancellationToken.None);
        }
        catch (WebSocketException)
        {
            // The peer is already gone; there is nothing to tell it.
        }
    }

    private static ulong ParseAfterSeq(string? raw) =>
        ulong.TryParse(raw, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;

    private static SessionLimits SessionLimitsSnapshot => new()
    {
        InboundTextBytes = ProtocolLimits.MaxInboundTextBytes,
        CatchUpLimitDefault = CommsLimits.CatchUpLimitDefault,
        CatchUpLimitMax = CommsLimits.CatchUpLimitMax,
        IdentifierMaxLength = CommsLimits.IdentifierMaxLength,
        OutboundBufferEvents = CommsLimits.OutboundBufferEvents,
    };
}
