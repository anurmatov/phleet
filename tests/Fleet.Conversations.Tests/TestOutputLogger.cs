using Xunit.Abstractions;
using Microsoft.Extensions.Logging;

namespace Fleet.Conversations.Tests;

/// <summary>
/// Routes component logs into xunit's per-test output.
/// </summary>
/// <remarks>
/// <para>
/// Written for the round-trip suite, where every interesting failure is silent at the assertion
/// level: a claim refused with 401, a queue nothing publishes to, a disposition the store rejects and
/// an agent that never attached all present identically as "no events after the timeout". The log
/// line is the only thing that distinguishes them, and xunit shows it precisely when the test fails.
/// </para>
/// <para>
/// Writes are guarded: xunit disposes the output helper when a test ends, and a background loop that
/// logs one tick later would otherwise fault the run with an unrelated exception.
/// </para>
/// </remarks>
internal sealed class TestOutputLoggerProvider(ITestOutputHelper output) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new TestOutputLogger(output, categoryName);

    public void Dispose() { }
}

internal sealed class TestOutputLogger(ITestOutputHelper output, string category) : ILogger
{
    private readonly string _short = category.Split('.')[^1];

    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;

        try
        {
            var message = formatter(state, exception);
            output.WriteLine($"[{logLevel,-11}] {_short}: {message}");

            if (exception is not null)
                output.WriteLine($"              {exception.GetType().Name}: {exception.Message}");
        }
        catch (Exception)
        {
            // The test has ended and its output helper is gone. A log write must never be the thing
            // that fails a run.
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}
