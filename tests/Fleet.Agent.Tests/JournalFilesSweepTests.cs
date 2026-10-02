using Fleet.Agent.Services;
using Fleet.Agent.Services.JournalFiles;
using Microsoft.Extensions.Logging.Abstractions;
namespace Fleet.Agent.Tests;

public sealed class JournalFilesSweepTests
{
    [Fact]
    public async Task StartupAndHourlySweepExpireFilesAndOrdinarySweeperNeverRecurses()
    {
        var root = Path.Combine(Path.GetTempPath(), "files-sweep-" + Guid.NewGuid().ToString("N"));
        var clock = new Clock(); var files = new JournalFileStore(root, clock);
        using var sweep = new JournalFilesSweepService(files, NullLogger<JournalFilesSweepService>.Instance, clock);
        try
        {
            files.Sweep(); var journal = Path.Combine(root, "journal");
            var stale = Path.Combine(journal, "stale.bin"); await File.WriteAllTextAsync(stale, "synthetic");
            File.SetLastWriteTimeUtc(stale, clock.GetUtcNow().AddHours(-24).UtcDateTime);
            await sweep.StartAsync(default); Assert.False(File.Exists(stale));
            await clock.TimerCreated.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var next = Path.Combine(journal, "next.bin"); await File.WriteAllTextAsync(next, "synthetic");
            File.SetLastWriteTimeUtc(next, clock.GetUtcNow().AddHours(-23).UtcDateTime);
            AttachmentSweeper.SweepExpired(root, 0, NullLogger.Instance); Assert.True(File.Exists(next));
            clock.Advance(TimeSpan.FromHours(1));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (File.Exists(next)) await Task.Delay(10, timeout.Token);
            await sweep.StopAsync(default);
        }
        finally { await sweep.StopAsync(default); Directory.Delete(root, true); }
    }
    private sealed class Clock : TimeProvider
    {
        private readonly object _gate = new(); private DateTimeOffset _now = DateTimeOffset.UtcNow;
        private readonly List<Timer> _timers = [];
        public TaskCompletionSource TimerCreated { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override DateTimeOffset GetUtcNow() { lock (_gate) return _now; }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new Timer(this, callback, state, period);
            lock (_gate) { _timers.Add(timer); timer.Change(dueTime, period); }
            TimerCreated.TrySetResult(); return timer;
        }
        public void Advance(TimeSpan by)
        {
            Timer[] ready;
            lock (_gate) { _now += by; ready = _timers.Where(t => t.At <= _now).ToArray(); foreach (var timer in ready) timer.At = _now + timer.Period; }
            foreach (var timer in ready) timer.Fire();
        }
        private sealed class Timer(Clock clock, TimerCallback callback, object? state, TimeSpan period) : ITimer
        {
            public DateTimeOffset At; public TimeSpan Period = period;
            public bool Change(TimeSpan dueTime, TimeSpan period) { lock (clock._gate) { At = clock._now + dueTime; Period = period; } return true; }
            public void Fire() => callback(state);
            public void Dispose() { lock (clock._gate) clock._timers.Remove(this); }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
