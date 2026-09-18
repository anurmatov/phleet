using Fleet.Conversations.Contracts;
using Fleet.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Fleet.Conversations.Tests;

/// <summary>
/// The maintenance loop's own sequencing: measure, arm, stamp, scan.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ReconcilerTests"/> drives <see cref="Reconciler.ScanOnceAsync"/> directly and sets
/// <c>service_health</c> by hand, which proves the grace <i>predicate</i> and nothing about whether
/// the loop ever reaches it. It did not: the loop stamped before it scanned, so the scan's own check
/// always measured a stamp that tick had just written, and the hold-off could not fire in a
/// deployment while passing every unit test.
/// </para>
/// <para>
/// This class drives the loop's tick with a clock it controls, which is the level the bug lived at.
/// </para>
/// </remarks>
[Collection("mysql")]
public sealed class MaintenanceLoopTests(MySqlFixture fixture)
{
    private readonly MySqlConversationStore _store = fixture.CreateStore();

    private ConversationMaintenanceService Service(ConversationStoreOptions options) =>
        new(
            new Reconciler(fixture.ConnectionString, options, NullLogger<Reconciler>.Instance),
            new GarbageCollector(fixture.ConnectionString, options, NullLogger<GarbageCollector>.Instance),
            options,
            NullLogger<ConversationMaintenanceService>.Instance);

    /// <summary>
    /// After a silence, the loop holds off for the whole grace window and then abandons.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both halves in one test. The first alone would pass against a loop that never abandoned
    /// anything; the second alone would pass against a loop with no hold-off at all — which is
    /// exactly the loop this replaces.
    /// </para>
    /// <para>
    /// The silence is staged by backdating <c>last_healthy_at</c>, and the window is crossed by
    /// moving the tick's <c>now</c>, so a two-minute grace period costs no wall-clock time and the
    /// assertion is about the bound that is configured rather than one a test happened to outlast.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task After_a_silence_the_loop_holds_off_for_the_grace_window_and_then_abandons()
    {
        var options = new ConversationStoreOptions
        {
            ReconcilerGraceAfterRecovery = TimeSpan.FromSeconds(120),
            ReconcilerScanInterval = TimeSpan.FromSeconds(30),
            HeartbeatInterval = TimeSpan.FromSeconds(30),
        };

        var service = Service(options);

        var (_, accept) = await PendingSubmissionAsync();
        await ExpireLeaseAsync(accept.SubmissionId!);

        // The service was away for an hour: every lease in the database looks expired, and none of
        // them looks expired because an agent died.
        await BackdateHealthAsync(TimeSpan.FromHours(1));

        var start = DateTimeOffset.UtcNow;

        var first = await service.TickAsync(start, CancellationToken.None);

        Assert.True(first.HeldOff);
        Assert.Equal(0, first.Abandoned);
        Assert.True(first.Gap > TimeSpan.FromMinutes(30),
            $"the tick measured a gap of {first.Gap}, so it read its own stamp rather than the silence");

        // Every tick inside the window is still held off — including ones whose own measured gap is
        // now tiny, because the previous tick stamped. The hold-off is a deadline, not a re-derived
        // condition.
        foreach (var offset in new[] { 30, 60, 119 })
        {
            var held = await service.TickAsync(start.AddSeconds(offset), CancellationToken.None);

            Assert.True(held.HeldOff, $"the tick at +{offset}s should still be inside the window");
            Assert.Equal(0, held.Abandoned);
        }

        // Nothing was appended while it was held off — asserted on the log, not on the counter.
        Assert.Equal("0", await OutcomeUnknownCountAsync(accept.SubmissionId!));

        // Past the window, the same attempt is abandoned.
        var after = await service.TickAsync(start.AddSeconds(121), CancellationToken.None);

        Assert.False(after.HeldOff);
        Assert.Equal(1, after.Abandoned);
        Assert.Equal("1", await OutcomeUnknownCountAsync(accept.SubmissionId!));
    }

    /// <summary>
    /// An ordinary tick — one that follows another by about a scan interval — does NOT hold off.
    /// </summary>
    /// <remarks>
    /// The negative control. Without it, a loop that held off unconditionally would pass the test
    /// above, and a reconciler that never ran would look exactly like one that was being careful.
    /// </remarks>
    [Fact]
    public async Task An_ordinary_tick_does_not_hold_off()
    {
        var options = new ConversationStoreOptions
        {
            ReconcilerGraceAfterRecovery = TimeSpan.FromSeconds(120),
            ReconcilerScanInterval = TimeSpan.FromSeconds(30),
            HeartbeatInterval = TimeSpan.FromSeconds(30),
        };

        var service = Service(options);
        var now = DateTimeOffset.UtcNow;

        // A stamp from one scan interval ago: the loop is running normally.
        await BackdateHealthAsync(options.ReconcilerScanInterval);

        var tick = await service.TickAsync(now, CancellationToken.None);

        Assert.False(tick.HeldOff);

        // And an attempt whose lease has expired is abandoned on that same ordinary tick, so this
        // is a test of a working reconciler rather than of one that is merely not held off.
        var (_, accept) = await PendingSubmissionAsync();
        await ExpireLeaseAsync(accept.SubmissionId!);

        var second = await service.TickAsync(now.AddSeconds(30), CancellationToken.None);

        Assert.False(second.HeldOff);
        Assert.Equal(1, second.Abandoned);
    }

    /// <summary>
    /// The tick reads the silence BEFORE it stamps.
    /// </summary>
    /// <remarks>
    /// The precise inversion that made the grace period unreachable, pinned on its own. A loop that
    /// stamped first would report a gap of roughly zero here whatever the stored value was.
    /// </remarks>
    [Fact]
    public async Task The_tick_measures_the_silence_before_stamping_it_away()
    {
        var options = new ConversationStoreOptions
        {
            ReconcilerGraceAfterRecovery = TimeSpan.FromSeconds(1),
            ReconcilerScanInterval = TimeSpan.FromSeconds(30),
            HeartbeatInterval = TimeSpan.FromSeconds(30),
        };

        await BackdateHealthAsync(TimeSpan.FromMinutes(10));

        var tick = await Service(options).TickAsync(DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.True(tick.Gap > TimeSpan.FromMinutes(9), $"measured {tick.Gap}");

        // And it stamped afterwards, so the NEXT read sees a fresh record rather than the old one.
        var afterwards = await new Reconciler(
                fixture.ConnectionString, options, NullLogger<Reconciler>.Instance)
            .ReadHealthGapAsync();

        Assert.True(afterwards < TimeSpan.FromMinutes(1), $"measured {afterwards}");
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private async Task<(OpenConversationResult Conversation, AcceptSubmissionResult Accept)>
        PendingSubmissionAsync()
    {
        var conversation = await _store.OpenConversationAsync(new OpenConversationRequest
        {
            ChannelId = "client",
            ExternalRef = "conv-" + Ulid.NewUlid(),
            PrincipalId = "p_owner",
        });

        var accept = await _store.AcceptSubmissionAsync(new AcceptSubmissionRequest
        {
            ConversationId = conversation.ConversationId,
            ExternalSubmissionId = Guid.NewGuid().ToString("n"),
            PayloadFingerprint = PayloadFingerprint.Compute("hello", null, conversation.ConversationId),
            CommandKind = ConversationEventKind.SubmissionCreate,
            CommandPayloadJson = """{"text":"hello"}""",
        });

        return (conversation, accept);
    }

    private Task ExpireLeaseAsync(string submissionId) =>
        fixture.ExecuteAsync($"""
            UPDATE execution_attempts
               SET lease_expires_at = DATE_SUB(UTC_TIMESTAMP(6), INTERVAL 1 HOUR)
             WHERE submission_id = '{submissionId}'
            """);

    private Task BackdateHealthAsync(TimeSpan silence) =>
        fixture.ExecuteAsync(
            "UPDATE service_health "
            + $"SET last_healthy_at = DATE_SUB(UTC_TIMESTAMP(6), INTERVAL {(long)silence.TotalSeconds} SECOND) "
            + "WHERE id = 1");

    private Task<string> OutcomeUnknownCountAsync(string submissionId) =>
        fixture.ScalarRowAsync($"""
            SELECT COUNT(*) FROM conversation_events
             WHERE submission_id = '{submissionId}'
               AND kind = '{ConversationEventKind.TurnOutcomeUnknown}'
            """);
}
