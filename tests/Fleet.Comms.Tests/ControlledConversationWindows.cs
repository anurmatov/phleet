using System.Collections.Concurrent;
using System.Threading.RateLimiting;
using Fleet.Comms.Configuration;

namespace Fleet.Comms.Tests;

/// <summary>
/// The production conversation budget with its window renewal in the test's hands.
/// </summary>
/// <remarks>
/// <para>
/// The framework's fixed-window limiter renews on its own monotonic clock, so a test that spends a
/// whole window against it is racing that clock: on a loaded runner the window can renew partway
/// through and the expected <c>429</c> never comes. Here each partition is the real
/// <see cref="FixedWindowRateLimiter"/> built from <see cref="ConversationRateLimits.LimiterOptions"/>
/// — same permits, queue and window, so the same admission and the same <c>Retry-After</c> — and the
/// only thing that starts a new window is <see cref="Renew"/>.
/// </para>
/// <para>
/// The partition key is still the production one: the factory is handed whatever key the real
/// policy computed, so independence between credentials is asserted, not assumed.
/// </para>
/// </remarks>
internal sealed class ControlledConversationWindows : ConversationRateLimiterFactory
{
    private readonly ConcurrentDictionary<string, ControlledWindow> _windows = new(StringComparer.Ordinal);

    public override RateLimitPartition<string> Partition(string key) =>
        RateLimitPartition.Get(key, k => _windows.GetOrAdd(k, _ => new ControlledWindow(ConversationRateLimits.LimiterOptions())));

    /// <summary>Start a new window for every partition seen so far.</summary>
    public void Renew()
    {
        foreach (var window in _windows.Values)
            window.Renew();
    }

    /// <summary>
    /// Not a <see cref="ReplenishingRateLimiter"/>, so the partitioned limiter's heartbeat never
    /// replenishes it, and never idle, so the heartbeat never evicts it either.
    /// </summary>
    private sealed class ControlledWindow(FixedWindowRateLimiterOptions production) : RateLimiter
    {
        private FixedWindowRateLimiter _current = Open(production);

        // AutoReplenishment off and nothing calling TryReplenish: this window never renews by itself.
        private static FixedWindowRateLimiter Open(FixedWindowRateLimiterOptions options) => new(new FixedWindowRateLimiterOptions
        {
            PermitLimit = options.PermitLimit,
            Window = options.Window,
            QueueLimit = options.QueueLimit,
            QueueProcessingOrder = options.QueueProcessingOrder,
            AutoReplenishment = false,
        });

        public void Renew() => Interlocked.Exchange(ref _current, Open(production)).Dispose();

        public override TimeSpan? IdleDuration => null;

        public override RateLimiterStatistics? GetStatistics() => _current.GetStatistics();

        protected override RateLimitLease AttemptAcquireCore(int permitCount) => _current.AttemptAcquire(permitCount);

        protected override ValueTask<RateLimitLease> AcquireAsyncCore(int permitCount, CancellationToken cancellationToken) =>
            _current.AcquireAsync(permitCount, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _current.Dispose();
        }
    }
}
