using System.Diagnostics.Metrics;
using Fleet.Conversations.Contracts;
using Fleet.Protocol;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fleet.Conversations.Tests;

/// <summary>
/// What the counters are allowed to label (AC32's metric half).
/// </summary>
/// <remarks>
/// <para>
/// A metric label is retained by the scrape target, duplicated into every dashboard and alert, and
/// shipped wherever those go. A conversation id or a principal id there is a per-user identifier in
/// a system nobody thinks of as holding user data — and it is also unbounded cardinality, so the
/// privacy rule and the operational one point the same way.
/// </para>
/// <para>
/// Asserted by listening to the real meter and reading the tags that were actually emitted, rather
/// than by reading the call sites. The call sites are what would change.
/// </para>
/// </remarks>
[Collection("mysql")]
public sealed class MetricLabelTests(MySqlFixture fixture)
{
    private readonly MySqlConversationStore _store = fixture.CreateStore();

    /// <summary>
    /// The complete set of label KEYS this slice may emit.
    /// </summary>
    /// <remarks>
    /// Committed as data, so adding a label is a deliberate edit to this list rather than something
    /// that happens quietly at a call site.
    /// </remarks>
    private static readonly HashSet<string> Allowed =
        new(StringComparer.Ordinal) { "outcome", "reason", "kind", "outbox", "route", "status", "disposition" };

    /// <summary>
    /// Values that must never appear in a label, whatever the key is called.
    /// </summary>
    private static readonly string[] Forbidden =
        ["p_owner", "inbound:", "hello", "agent-1"];

    [Fact]
    public async Task Claim_and_reconciler_counters_label_only_the_allowed_keys()
    {
        var captured = new List<(string Instrument, string Key, string Value)>();

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == ConversationMetrics.MeterName)
                l.EnableMeasurementEvents(instrument);
        };

        listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
        {
            foreach (var tag in tags)
            {
                lock (captured)
                    captured.Add((instrument.Name, tag.Key, tag.Value?.ToString() ?? string.Empty));
            }
        });

        listener.Start();

        // Drive the real paths: a claim, a redelivered claim, and a reconciler scan that abandons.
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

        var messageId = await fixture.ScalarRowAsync(
            $"SELECT message_id FROM command_outbox WHERE submission_id = '{accept.SubmissionId}'");

        await _store.ClaimDeliveryAsync(new ClaimDeliveryRequest
        {
            MessageId = messageId,
            Owner = "agent-1",
        });

        var reconciler = new Reconciler(
            fixture.ConnectionString, fixture.Options, NullLogger<Reconciler>.Instance);

        await reconciler.RecordHealthyAsync();

        await fixture.ExecuteAsync($"""
            UPDATE execution_attempts
               SET lease_expires_at = DATE_SUB(UTC_TIMESTAMP(6), INTERVAL 1 HOUR)
             WHERE submission_id = '{accept.SubmissionId}'
            """);

        await reconciler.ScanOnceAsync();

        listener.Dispose();

        // The listener really saw something. Without this the two assertions below pass vacuously
        // against a meter that emitted nothing at all.
        Assert.NotEmpty(captured);

        foreach (var (instrument, key, value) in captured)
        {
            Assert.True(Allowed.Contains(key),
                $"{instrument} emitted the label '{key}', which is not on the allowed list.");

            foreach (var forbidden in Forbidden)
            {
                Assert.False(value.Contains(forbidden, StringComparison.Ordinal),
                    $"{instrument} emitted '{value}' for '{key}', which carries request data.");
            }
        }

        // And the outcomes really were recorded, so this is a test of what was emitted rather than
        // of an empty list that satisfies every rule.
        Assert.Contains(captured, c => c is { Key: "outcome", Value: "claimed" });
        Assert.Contains(captured, c => c is { Key: "reason", Value: "attempt_abandoned" });
    }
}
