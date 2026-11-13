using System.Net.WebSockets;
using System.Text;
using Fleet.Orchestrator.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fleet.Orchestrator.Tests.Services;

/// <summary>
/// Dashboard sockets: a closed one is forgotten, a socket gets one send at a time, and a client
/// that stopped reading does not hold up the others.
/// </summary>
public class AgentRegistrySocketTests
{
    private static AgentRegistry NewRegistry(TimeSpan? sendTimeout = null) =>
        new(new TaskHistoryStore(), NullLogger<AgentRegistry>.Instance)
        {
            SendTimeout = sendTimeout ?? TimeSpan.FromSeconds(5),
        };

    [Fact]
    public async Task ASocketItsHandlerRemoved_IsNotSentToAgain()
    {
        var registry = NewRegistry();
        var socket = new FakeSocket();
        registry.AddSocket(socket);
        await registry.BroadcastRawAsync("one");

        registry.RemoveSocket(socket);
        await registry.BroadcastRawAsync("two");

        Assert.Equal(["one"], socket.Received);
        Assert.Equal(0, registry.SocketCount);
    }

    [Fact]
    public async Task ASocketFoundClosed_IsForgotten_SoReconnectsDoNotAccumulate()
    {
        var registry = NewRegistry();
        for (var i = 0; i < 50; i++)
        {
            // A dashboard reconnecting: the old socket closes, a new one is added.
            var socket = new FakeSocket();
            registry.AddSocket(socket);
            socket.Close();
        }
        var live = new FakeSocket();
        registry.AddSocket(live);

        await registry.BroadcastRawAsync("state");

        Assert.Equal(1, registry.SocketCount);
        Assert.Equal(["state"], live.Received);
    }

    [Fact]
    public async Task ASocketWhoseSendFails_IsForgotten_AndTheOthersStillGetTheMessage()
    {
        var registry = NewRegistry();
        var broken = new FakeSocket { FailSends = true };
        var live = new FakeSocket();
        registry.AddSocket(broken);
        registry.AddSocket(live);

        await registry.BroadcastRawAsync("state");

        Assert.Equal(["state"], live.Received);
        Assert.Equal(1, registry.SocketCount);
        Assert.True(broken.Aborted);
    }

    [Fact]
    public async Task ManyBroadcastsAtOnce_NeverOverlapOnOneSocket_AndAllArrive()
    {
        var registry = NewRegistry();
        var socket = new FakeSocket { SendDelay = TimeSpan.FromMilliseconds(2) };
        registry.AddSocket(socket);

        await Task.WhenAll(Enumerable.Range(0, 40).Select(i => Task.Run(() => registry.BroadcastRawAsync($"message-{i}"))));

        // The real WebSocket throws on a second send while one is outstanding; the fake counts it.
        Assert.Equal(0, socket.OverlappingSends);
        Assert.Equal(Enumerable.Range(0, 40).Select(i => $"message-{i}").Order(), socket.Received.Order());
        Assert.Equal(1, registry.SocketCount);
    }

    [Fact]
    public async Task TheFirstSnapshotToANewSocket_GoesThroughTheSameGate()
    {
        var registry = NewRegistry();
        registry.UpdateAgent(new AgentHeartbeat("agent1", "idle", DateTimeOffset.UtcNow));
        registry.UpdateAgent(new AgentHeartbeat("agent2", "idle", DateTimeOffset.UtcNow));
        var socket = new FakeSocket { SendDelay = TimeSpan.FromMilliseconds(5) };
        registry.AddSocket(socket);

        await Task.WhenAll(
            registry.BroadcastAllToSocket(socket),
            Task.Run(() => registry.BroadcastRawAsync("a")),
            Task.Run(() => registry.BroadcastRawAsync("b")));

        Assert.Equal(0, socket.OverlappingSends);
        Assert.Equal(4, socket.Received.Count);
    }

    [Fact]
    public async Task AClientThatStoppedReading_IsDroppedAfterTheTimeout_AndDoesNotBlockTheNextBroadcast()
    {
        var registry = NewRegistry(sendTimeout: TimeSpan.FromMilliseconds(100));
        var stalled = new FakeSocket { NeverCompletes = true };
        var live = new FakeSocket();
        registry.AddSocket(stalled);
        registry.AddSocket(live);

        await registry.BroadcastRawAsync("one").WaitAsync(TimeSpan.FromSeconds(10));
        await registry.BroadcastRawAsync("two").WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(["one", "two"], live.Received);
        Assert.Equal(1, registry.SocketCount);
        Assert.True(stalled.Aborted);
    }

    [Fact]
    public async Task TheStateMonitor_KeepsChecking_AfterACheckThatThrows()
    {
        var checks = 0;
        var third = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var monitor = new AgentStateMonitorService(
            NewRegistry(),
            new DockerService(NullLogger<DockerService>.Instance, new HttpClient { BaseAddress = new Uri("http://localhost") }),
            NullLogger<AgentStateMonitorService>.Instance)
        {
            Interval = TimeSpan.FromMilliseconds(10),
            Check = _ =>
            {
                if (Interlocked.Increment(ref checks) == 3) third.TrySetResult();
                throw new InvalidOperationException("a check that fails");
            },
        };

        await monitor.StartAsync(CancellationToken.None);
        try
        {
            // Before the fix the first throw ended ExecuteAsync, which stops the host.
            await third.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(monitor.ExecuteTask!.IsCompleted);
        }
        finally
        {
            await monitor.StopAsync(CancellationToken.None);
        }
    }

    private sealed class FakeSocket : WebSocket
    {
        private readonly List<string> _received = [];
        private WebSocketState _state = WebSocketState.Open;
        private int _sending;
        private int _overlapping;

        public bool FailSends { get; init; }
        public bool NeverCompletes { get; init; }
        public TimeSpan SendDelay { get; init; }
        public bool Aborted { get; private set; }
        public int OverlappingSends => _overlapping;
        public IReadOnlyList<string> Received { get { lock (_received) return _received.ToList(); } }

        public void Close() => _state = WebSocketState.Closed;

        public override WebSocketState State => _state;
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override string? SubProtocol => null;

        public override async Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, bool endOfMessage, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _sending) > 1) Interlocked.Increment(ref _overlapping);
            try
            {
                if (FailSends) throw new WebSocketException("connection reset");
                if (NeverCompletes) await Task.Delay(Timeout.Infinite, cancellationToken);
                if (SendDelay > TimeSpan.Zero) await Task.Delay(SendDelay, cancellationToken);
                lock (_received) _received.Add(Encoding.UTF8.GetString(buffer));
            }
            finally
            {
                Interlocked.Decrement(ref _sending);
            }
        }

        public override void Abort()
        {
            Aborted = true;
            _state = WebSocketState.Aborted;
        }

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            _state = WebSocketState.Closed;
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) =>
            CloseAsync(closeStatus, statusDescription, cancellationToken);

        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) =>
            Task.Delay(Timeout.Infinite, cancellationToken).ContinueWith<WebSocketReceiveResult>(_ => throw new OperationCanceledException(), TaskScheduler.Default);

        public override void Dispose() => _state = WebSocketState.Closed;
    }
}
