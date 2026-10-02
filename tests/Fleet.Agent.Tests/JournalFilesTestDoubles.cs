using System.Net;
using System.Security.Cryptography;
using System.Text;
using Fleet.Agent.Models;
using Fleet.Agent.Services;
using Fleet.Conversations.Contracts;
using Fleet.Journal.Client;
using Microsoft.Extensions.Logging.Abstractions;
namespace Fleet.Agent.Tests;

/// <summary>Journal-files test doubles shared by the tool and listener tests.</summary>
internal static class JournalFilesTestDoubles
{
    internal const string Id = "01ARZ3NDEKTSV4RRFFQ69G5FAV";
    internal static readonly byte[] Bytes = Encoding.UTF8.GetBytes("synthetic sentinel");

    internal sealed class Clock : TimeProvider
    {
        private TimeSpan _elapsed; private readonly List<Timer> _timers = [];
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _elapsed.Ticks;
        public void AdvanceWithoutCallbacks(TimeSpan by) => _elapsed += by;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch + _elapsed;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan due, TimeSpan period)
        { var timer = new Timer(this, callback, state, due); _timers.Add(timer); return timer; }
        public void FireDeadline(int index) => _timers[index].Callback(_timers[index].State);
        public void Advance(TimeSpan by) { _elapsed += by; foreach (var timer in _timers.ToArray()) if (!timer.Disposed && timer.At <= _elapsed) timer.Callback(timer.State); }
        private sealed class Timer(Clock clock, TimerCallback callback, object? state, TimeSpan due) : ITimer
        {
            public TimerCallback Callback = callback; public object? State = state; public TimeSpan At = clock._elapsed + due; public bool Disposed;
            public bool Change(TimeSpan dueTime, TimeSpan period) { At = clock._elapsed + dueTime; return !Disposed; }
            public void Dispose() => Disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    internal static TurnBindingPublisher Publisher(JournalHttpClient client) => new(client, NullLogger<TurnBindingPublisher>.Instance);
    internal static void Bind(TurnBindingPublisher binding) { binding.ObserveChat(10, 1, "private"); binding.BeginTurn(10, TaskSource.UserMessage); }
    internal sealed class Handler : HttpMessageHandler
    {
        public int Calls, Fetches, Puts, LostBindings; public bool BadDigest, Block, BlockPuts, DelayCancellation;
        public string Kind = "document";
        public TaskCompletionSource CancelObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CancelRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource PutStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously); public int Status = 200; public string? Body, Header, PutHeader;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++; Header = request.Headers.Authorization?.ToString();
            if (request.Method == HttpMethod.Put) { Puts++; PutHeader = Header; PutStarted.TrySetResult(); if (BlockPuts) await Release.Task.WaitAsync(ct); return new HttpResponseMessage(HttpStatusCode.NoContent); }
            Fetches++; Started.TrySetResult();
            if (Block) try { await Release.Task.WaitAsync(ct); }
                catch (OperationCanceledException) { CancelObserved.TrySetResult(); if (DelayCancellation) await CancelRelease.Task; throw; }
            if (Fetches <= LostBindings) return new HttpResponseMessage(HttpStatusCode.Conflict) { Content = new StringContent(JournalAttachmentRequest.Unavailable("no_bound_conversation")) };
            if (Status != 200) return new HttpResponseMessage((HttpStatusCode)Status) { Content = new StringContent(Body ?? "") };
            var reply = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Bytes) };
            reply.Content.Headers.ContentType = new("text/plain");
            reply.Headers.Add("X-Journal-Message-Id", Id); reply.Headers.Add("X-Journal-Ordinal", "0"); reply.Headers.Add("X-Journal-Kind", Kind);
            reply.Headers.Add("X-Journal-Sha256", BadDigest ? new string('0', 64) : Convert.ToHexStringLower(SHA256.HashData(Bytes)));
            return reply;
        }
    }
}
