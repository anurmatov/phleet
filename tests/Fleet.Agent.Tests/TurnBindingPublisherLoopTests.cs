using System.Net;
using Fleet.Agent.Services;
using Fleet.Journal.Client;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fleet.Agent.Tests;

public sealed class TurnBindingPublisherLoopTests
{
    [Fact]
    public async Task Renewal_RestoresServerStateAfterRestart_WithoutIncreasingSequence()
    {
        var clock = new Clock();
        var handler = new Handler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://journal.test") };
        using var publisher = new TurnBindingPublisher(new(http, "synthetic-token"), NullLogger<TurnBindingPublisher>.Instance, clock);
        publisher.ObserveChat(101, 7001, "private");
        publisher.BeginTurn(101, Fleet.Agent.Models.TaskSource.UserMessage);
        await publisher.StartAsync(default);
        await Until(() => handler.Calls == 1 && clock.HasTimer);
        var original = handler.Body;
        handler.Body = null; // Simulated Comms restart loses its in-memory state.
        clock.Advance(59);
        Assert.Equal(1, handler.Calls);
        clock.Advance(1);
        await Until(() => handler.Calls == 2);
        Assert.Equal(original, handler.Body);
        await publisher.StopAsync(default);
    }

    [Fact]
    public async Task Failures_BackOffToThirtySeconds_AndSuccessfulRetryClearsFailure()
    {
        var clock = new Clock();
        var handler = new Handler { Status = HttpStatusCode.ServiceUnavailable };
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://journal.test") };
        using var publisher = new TurnBindingPublisher(new(http, "synthetic-token"), NullLogger<TurnBindingPublisher>.Instance, clock);
        await publisher.StartAsync(default);
        await Until(() => publisher.Count("failed") == 1 && clock.HasTimer);
        Assert.True(publisher.BindingFailed);
        foreach (var delay in new[] { 1, 2, 4, 8, 16, 30, 30 })
        {
            var calls = handler.Calls;
            clock.Advance(delay - 1);
            Assert.Equal(calls, handler.Calls);
            clock.Advance(1);
            await Until(() => publisher.Count("failed") == calls + 1 && clock.HasTimer);
        }
        handler.Status = HttpStatusCode.NoContent;
        clock.Advance(30);
        await Until(() => publisher.Count("accepted") == 1);
        Assert.False(publisher.BindingFailed);
        await publisher.StopAsync(default);
    }

    [Theory]
    [InlineData(204, "accepted", false)]
    [InlineData(409, "stale", true)]
    [InlineData(401, "unauthorized", true)]
    [InlineData(404, "route_missing", true)]
    [InlineData(503, "failed", true)]
    public async Task Loop_ReportsFixedResultCodes(int status, string result, bool failed)
    {
        var handler = new Handler { Status = (HttpStatusCode)status };
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://journal.test") };
        using var publisher = new TurnBindingPublisher(new(http, "synthetic-token"), NullLogger<TurnBindingPublisher>.Instance);
        await publisher.StartAsync(default);
        await Until(() => publisher.Count(result) == 1 && publisher.BindingFailed == failed);
        await publisher.StopAsync(default);
    }

    private static async Task Until(Func<bool> condition)
    {
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, ct.Token);
    }

    private sealed class Handler : HttpMessageHandler
    {
        public HttpStatusCode Status = HttpStatusCode.NoContent;
        public int Calls;
        public string? Body;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Body = await request.Content!.ReadAsStringAsync(ct);
            Interlocked.Increment(ref Calls);
            return new(Status);
        }
    }

    private sealed class Clock : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<Timer> _timers = [];
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() { lock (_gate) return _now; }
        public bool HasTimer { get { lock (_gate) return _timers.Any(t => t.Due != DateTimeOffset.MaxValue); } }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new Timer(this, callback, state);
            lock (_gate) _timers.Add(timer);
            timer.Change(dueTime, period);
            return timer;
        }
        public void Advance(int seconds)
        {
            Timer[] ready;
            lock (_gate)
            {
                _now += TimeSpan.FromSeconds(seconds);
                ready = _timers.Where(t => t.Due <= _now).ToArray();
                foreach (var timer in ready) timer.Due = DateTimeOffset.MaxValue;
            }
            foreach (var timer in ready) timer.Fire();
        }
        private sealed class Timer(Clock clock, TimerCallback callback, object? state) : ITimer
        {
            public DateTimeOffset Due = DateTimeOffset.MaxValue;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (clock._gate) Due = dueTime == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : clock._now + dueTime;
                return true;
            }
            public void Fire() => callback(state);
            public void Dispose() { lock (clock._gate) clock._timers.Remove(this); }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
