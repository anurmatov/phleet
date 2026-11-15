using System.Text;
using System.Text.Json;
using Fleet.Agent.Configuration;
using Fleet.Agent.Models;
using Fleet.Agent.Services;
using Fleet.Contracts;
using Fleet.WireFixtures;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using RabbitMQ.Client;

namespace Fleet.Agent.Tests;

/// <summary>
/// The cumulative-to-delta rule, the frozen row and the bounded, best-effort queue and publisher.
/// </summary>
public class UsageReporterTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 2, 3, 4, 5, 678, TimeSpan.Zero);

    [Fact]
    public void Off_BeginsNoTurn_AndQueuesNothing()
    {
        var reporter = Reporter(enabled: false);

        var turn = reporter.BeginTurn();
        reporter.Complete(turn, Cumulative(input: 10));

        Assert.Null(turn);
        Assert.False(reporter.Rows.TryRead(out _));
    }

    [Fact]
    public void Row_MatchesTheWireSample_NullsLeftOut_UtcMilliseconds()
    {
        var reporter = Reporter();

        reporter.Complete(reporter.BeginTurn(), new ProviderUsageSnapshot
        {
            Provider = "claude", Model = "model-a", SessionId = "session-1", ProcessGeneration = 1, IsCumulative = true,
            InputTokens = 1830, OutputTokens = 742, CacheReadTokens = 41233, CacheCreationTokens = null, CostUsd = 0.04127m,
            ContextWindow = 200000, NumTurns = 6, DurationMs = 18422,
        });

        Assert.True(reporter.Rows.TryRead(out var row));
        WireGolden.AssertMatches("orchestrator.usage.json", row.Body);
    }

    [Fact]
    public void FirstCumulative_IsAReset_WhoseShareIsTheTotals()
    {
        var reporter = Reporter();

        var row = Complete(reporter, Cumulative(input: 100, output: 40, cost: 0.5m));

        Assert.True(row.DeltaReset);
        Assert.Equal((100L, 40L, 0.5m), (row.InputTokens!.Value, row.OutputTokens!.Value, row.CostUsd!.Value));
        Assert.Equal((100L, 40L, 0.5m), (row.CumInputTokens!.Value, row.CumOutputTokens!.Value, row.CumCostUsd!.Value));
    }

    [Fact]
    public void SameSession_IsDifferenced_PerTurnFieldsAreNot()
    {
        var reporter = Reporter();
        Complete(reporter, Cumulative(input: 100, output: 40, cost: 0.5m) with { DurationMs = 10, NumTurns = 1, ContextWindow = 1000 });

        var row = Complete(reporter, Cumulative(input: 130, output: 41, cost: 0.75m) with { DurationMs = 20, NumTurns = 3, ContextWindow = 1000 });

        Assert.False(row.DeltaReset);
        Assert.Equal((30L, 1L, 0.25m), (row.InputTokens!.Value, row.OutputTokens!.Value, row.CostUsd!.Value));
        Assert.Equal((20L, 3L, 1000L), (row.DurationMs!.Value, row.NumTurns!.Value, row.ContextWindow!.Value));
        Assert.Equal(130L, row.CumInputTokens);
    }

    public static TheoryData<ProviderUsageSnapshot> Changes => new()
    {
        Cumulative(input: 130) with { SessionId = "session-2" },
        Cumulative(input: 130) with { ProcessGeneration = 2 },
        Cumulative(input: 130) with { Model = ProviderUsageSnapshot.MultipleModels },
    };

    [Theory]
    [MemberData(nameof(Changes))]
    public void SessionProcessOrModelChange_IsAReset_WithTheNewTotals(ProviderUsageSnapshot next)
    {
        var reporter = Reporter();
        Complete(reporter, Cumulative(input: 100));

        var row = Complete(reporter, next);

        Assert.True(row.DeltaReset);
        Assert.Equal(130L, row.InputTokens);
    }

    [Fact]
    public void DecreasingCounter_IsAReset_ClampedToZero_OnlyInTheDelta()
    {
        var reporter = Reporter();
        Complete(reporter, Cumulative(input: 100, output: 40));

        var row = Complete(reporter, Cumulative(input: 60, output: 50));

        Assert.True(row.DeltaReset);
        Assert.Equal(0L, row.InputTokens);
        Assert.Equal(10L, row.OutputTokens);
        Assert.Equal(60L, row.CumInputTokens);   // raw cumulative kept
    }

    [Fact]
    public void ComponentThePreviousSnapshotLacked_StaysNullForItsFirstComparison()
    {
        var reporter = Reporter();
        Complete(reporter, Cumulative(input: 100, cacheCreation: null));

        var second = Complete(reporter, Cumulative(input: 120, cacheCreation: 7));
        var third = Complete(reporter, Cumulative(input: 125, cacheCreation: 9));

        Assert.Null(second.CacheCreationTokens);
        Assert.Equal(20L, second.InputTokens);
        Assert.Equal(2L, third.CacheCreationTokens);
    }

    [Fact]
    public void PerTurnSnapshot_IsNeverDifferenced_AndCarriesNoCumulative()
    {
        var reporter = Reporter();
        var snapshot = new ProviderUsageSnapshot { Provider = "gemini", Model = "model-g", DurationMs = 5 };

        var first = Complete(reporter, snapshot);
        var second = Complete(reporter, snapshot);

        Assert.False(first.DeltaReset);
        Assert.False(second.DeltaReset);
        Assert.Null(second.InputTokens);
        Assert.Null(second.CumInputTokens);
        Assert.Equal(5L, second.DurationMs);
    }

    [Fact]
    public void SecondCompletion_OfOneTurn_IsIgnored()
    {
        var reporter = Reporter();
        var turn = reporter.BeginTurn();

        reporter.Complete(turn, Cumulative(input: 1));
        reporter.Complete(turn, Cumulative(input: 2));

        Assert.True(reporter.Rows.TryRead(out _));
        Assert.False(reporter.Rows.TryRead(out _));
    }

    [Fact]
    public void FullQueue_DropsAndCounts_ButTheBaselineStillMoves()
    {
        var reporter = Reporter();
        for (var i = 1; i <= UsageReporter.QueueCapacity; i++)
            reporter.Complete(reporter.BeginTurn(), Cumulative(input: i * 10));

        reporter.Complete(reporter.BeginTurn(), Cumulative(input: 5000));   // dropped: queue full
        Assert.Equal(1, reporter.Dropped);

        while (reporter.Rows.TryRead(out _)) { }
        var next = Complete(reporter, Cumulative(input: 5100));

        Assert.False(next.DeltaReset);
        Assert.Equal(100L, next.InputTokens);   // measured from the dropped turn, not from the last delivered one
    }

    [Fact]
    public void OversizedRow_IsDropped_NotTruncated()
    {
        var reporter = Reporter();

        reporter.Complete(reporter.BeginTurn(), Cumulative(input: 1) with { Model = new string('m', AgentTurnUsage.MaxBytes) });

        Assert.False(reporter.Rows.TryRead(out _));
        Assert.Equal(1, reporter.Dropped);
    }

    [Fact]
    public async Task Publisher_SendsTheFrozenBytes_OnItsOwnChannel_WithAMinuteExpiry()
    {
        var reporter = new UsageReporter(true, "Agent1", NullLogger<UsageReporter>.Instance,
            () => DateTimeOffset.UtcNow, () => Guid.NewGuid().ToString());
        reporter.Complete(reporter.BeginTurn(), Cumulative(input: 1));
        Assert.True(reporter.Rows.TryPeek(out var queued));

        var channel = Substitute.For<IChannel>();
        var sent = new List<(string Exchange, string Key, BasicProperties Props, byte[] Body)>();
        channel.BasicPublishAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<BasicProperties>(),
                Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                sent.Add((call.ArgAt<string>(0), call.ArgAt<string>(1), call.ArgAt<BasicProperties>(3), call.ArgAt<ReadOnlyMemory<byte>>(4).ToArray()));
                return ValueTask.CompletedTask;
            });

        await RunPublisherUntilDrainedAsync(reporter, channel);

        var (exchange, key, props, body) = Assert.Single(sent);
        Assert.Equal(OrchestratorBroker.Exchange, exchange);
        Assert.Equal("usage.agent1", key);
        Assert.Equal(OrchestratorBroker.UsageExpirationMilliseconds, props.Expiration);
        Assert.Equal(queued.Body, body);
    }

    [Fact]
    public async Task Publisher_DropsAFailedOrExpiredRow_AndKeepsGoing()
    {
        var clock = T0;
        var reporter = new UsageReporter(true, "Agent1", NullLogger<UsageReporter>.Instance, () => clock, () => Guid.NewGuid().ToString());
        clock = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(5);
        reporter.Complete(reporter.BeginTurn(), Cumulative(input: 1));    // frozen five minutes ago: expired
        clock = DateTimeOffset.UtcNow;
        reporter.Complete(reporter.BeginTurn(), Cumulative(input: 2));    // the broker refuses it
        reporter.Complete(reporter.BeginTurn(), Cumulative(input: 3));    // published

        var channel = Substitute.For<IChannel>();
        var calls = 0;
        channel.BasicPublishAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<BasicProperties>(),
                Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<CancellationToken>())
            .Returns(_ => ++calls == 1 ? ValueTask.FromException(new InvalidOperationException("closed")) : ValueTask.CompletedTask);

        await RunPublisherUntilDrainedAsync(reporter, channel);

        Assert.Equal(2, calls);
        Assert.Equal(2, reporter.Dropped);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static UsageReporter Reporter(bool enabled = true)
    {
        var ids = 0;
        return new UsageReporter(enabled, "Agent1", NullLogger<UsageReporter>.Instance,
            () => T0, () => $"00000000-0000-4000-8000-{++ids:D12}");
    }

    private static ProviderUsageSnapshot Cumulative(
        long? input = null, long? output = null, long? cacheCreation = null, decimal? cost = null) => new()
    {
        Provider = "claude", Model = "model-a", SessionId = "session-1", ProcessGeneration = 1, IsCumulative = true,
        InputTokens = input, OutputTokens = output, CacheCreationTokens = cacheCreation, CostUsd = cost,
    };

    private static AgentTurnUsage Complete(UsageReporter reporter, ProviderUsageSnapshot snapshot)
    {
        reporter.Complete(reporter.BeginTurn(), snapshot);
        Assert.True(reporter.Rows.TryRead(out var row));
        return JsonSerializer.Deserialize<AgentTurnUsage>(row.Body)!;
    }

    private static async Task RunPublisherUntilDrainedAsync(UsageReporter reporter, IChannel channel)
    {
        var options = new AgentOptions { Name = "fleet-agent1", ShortName = "Agent1", Role = "developer", WorkDir = "/tmp" };
        var heartbeat = new OrchestratorHeartbeatService(
            Options.Create(options), Options.Create(new RabbitMqOptions()),
            MidTurnIsolationTests.Manager("claude", new MidTurnIsolationTests.ControlledExecutor(MidTurnInjectionStatus.Injected)),
            Substitute.For<IFleetConnectionState>(), NullLogger<OrchestratorHeartbeatService>.Instance, usage: reporter);

        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var loop = heartbeat.PublishUsageLoopAsync(reporter, channel, stop.Token);
        while (reporter.Rows.Count > 0 && !stop.IsCancellationRequested)
            await Task.Delay(10);
        await Task.Delay(50);
        stop.Cancel();
        await loop;
    }
}
