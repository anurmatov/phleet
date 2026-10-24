using System.Net;
using System.Text;
using System.Text.Json;
using Fleet.Shared;
using Fleet.Temporal.Configuration;
using Fleet.Temporal.Mcp;
using Fleet.Temporal.Models;
using Fleet.Temporal.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace Fleet.Temporal.Tests.WorkflowOutcomes;

/// <summary>
/// #476 AC17 at the unit level: the REAL listener and consumer logic, driven through the broker
/// seam (<see cref="IWorkflowOutcomeBroker"/> / <see cref="IWorkflowOutcomeDelivery"/>) with an
/// in-memory broker, a fake late-results API and a clock that never really waits. The same paths
/// run against a real RabbitMQ in <c>RealBroker.WorkflowOutcomeRabbitMqBrokerTests</c>.
/// </summary>
[Trait("WorkflowOutcomeSuite", "late-results")]
public sealed class WorkflowOutcomeLateResultTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-10-24T12:00:00Z");
    private const string LateQueue = "fleet.workflow-outcomes.late";
    private const string QuarantineQueue = "fleet.workflow-outcomes.quarantine";
    private const string HoldQueue = "fleet.workflow-outcomes.hold";
    private const string TaskId = "wf-1/implement";

    private sealed class Harness
    {
        public List<string> Log { get; } = [];
        public FakeOutcomeBroker Broker { get; }
        public TaskCompletionRegistry Registry { get; } = new(NullLogger<TaskCompletionRegistry>.Instance);
        public AutoAdvanceTimeProvider Time { get; } = new(T0);
        public FakeLateResultsApi Api { get; } = new();
        public RecordingLogger<TemporalRelayListener> ListenerLog { get; } = new();
        public RecordingLogger<WorkflowOutcomeLateResultConsumer> ConsumerLog { get; } = new();
        public TemporalBridgeOptions Options { get; }
        public TemporalRelayListener Listener { get; }
        public WorkflowOutcomeLateResultConsumer Consumer { get; }

        public Harness(bool captureEnabled = true)
        {
            Broker = new FakeOutcomeBroker(Log);
            Api.Log = Log;
            Options = new TemporalBridgeOptions { WorkflowOutcomeLateCaptureEnabled = captureEnabled };
            Listener = new TemporalRelayListener(
                Microsoft.Extensions.Options.Options.Create(new RabbitMqOptions()),
                Registry,
                new TemporalClientFactory(Microsoft.Extensions.Options.Options.Create(new TemporalBridgeOptions())),
                ListenerLog,
                Microsoft.Extensions.Options.Options.Create(Options),
                Broker,
                Time);
            Consumer = NewConsumer();
        }

        /// <summary>A fresh consumer on the same broker, API and clock — a process restart.</summary>
        public WorkflowOutcomeLateResultConsumer NewConsumer() => new(
            new RabbitMqOptions(), Options, Broker, Api.Factory(WorkflowOutcomeLateResultConsumer.HttpClientName), ConsumerLog, Time, null);

        public RecordingDelivery Delivery() => new(Log);
    }

    private static BasicProperties LateProperties(
        int? attempt = 0, object? dueAt = null, string? taskId = TaskId, string messageId = "message-1")
    {
        var headers = new Dictionary<string, object?>
        {
            [WorkflowOutcomeLateEnvelope.ReceivedAtHeader] = T0.ToUnixTimeMilliseconds(),
            [WorkflowOutcomeLateEnvelope.SourceQueueHeader] = TemporalBridgeOptions.RelayQueue,
        };
        if (attempt is not null) headers[WorkflowOutcomeLateEnvelope.AttemptHeader] = attempt.Value;
        headers[WorkflowOutcomeLateEnvelope.DueAtHeader] = dueAt ?? T0.ToUnixTimeMilliseconds();
        if (taskId is not null) headers[WorkflowOutcomeLateEnvelope.TaskIdHeader] = taskId;
        return new BasicProperties { MessageId = messageId, Headers = headers, Persistent = true };
    }

    // ── Listener: matched and capture-off paths are unchanged ─────────────────────────────────

    [Fact]
    public async Task HandleDelivery_MatchedTask_AcksWithoutAnyCapture()
    {
        var h = new Harness();
        var waiter = h.Registry.Register(TaskId);
        var delivery = h.Delivery();

        await h.Listener.HandleDeliveryAsync(WorkflowOutcomeTestValues.Reply(TaskId), null, delivery);

        Assert.Equal(["ack"], h.Log);
        Assert.Empty(h.Broker.Attempts);
        Assert.Equal("completed", (await waiter.Task).Status);
    }

    [Fact]
    public async Task HandleDelivery_CaptureOff_OrphanIsRejectedWithoutRequeueAsBefore()
    {
        var h = new Harness(captureEnabled: false);
        var delivery = h.Delivery();

        await h.Listener.HandleDeliveryAsync(WorkflowOutcomeTestValues.Reply(TaskId), null, delivery);

        Assert.Equal(["reject"], h.Log);
        Assert.Empty(h.Broker.Attempts);
        Assert.Contains(h.ListenerLog.Entries, e => e.Message.Contains("Orphan response for TaskId=" + TaskId + " — nacking without requeue"));
    }

    [Theory]
    [InlineData("{\"Sender\":\"agent1\",\"Text\":\"x\",\"Type\":\"directive\",\"ChatId\":0,\"Timestamp\":\"2026-10-24T00:00:00Z\"}")]
    [InlineData("{\"Sender\":\"agent1\",\"Text\":\"x\",\"Type\":\"response\",\"ChatId\":0,\"Timestamp\":\"2026-10-24T00:00:00Z\"}")]
    public async Task HandleDelivery_NonCapturableMessages_AreSettledAsBefore(string json)
    {
        var h = new Harness();
        await h.Listener.HandleDeliveryAsync(Encoding.UTF8.GetBytes(json), null, h.Delivery());

        Assert.Equal(["ack"], h.Log);
        Assert.Empty(h.Broker.Attempts);
    }

    // ── Listener: ACK only after the confirmed durable handoff ────────────────────────────────

    [Fact]
    public async Task HandleDelivery_Orphan_AcksOnlyAfterTheLateQueueCopyIsConfirmed()
    {
        var h = new Harness();
        h.Broker.ConfirmGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var body = WorkflowOutcomeTestValues.Reply(TaskId);
        var delivery = h.Delivery();

        var handling = h.Listener.HandleDeliveryAsync(body, new BasicProperties { MessageId = "agent-message-7" }, delivery);
        await h.Broker.PublishStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // The copy is in flight and unconfirmed: the original must still be with the broker.
        Assert.False(delivery.Settled);
        Assert.Empty(delivery.Pauses);

        h.Broker.ConfirmGate.SetResult();
        await handling;

        Assert.Equal(["publish:" + LateQueue, "confirm:" + LateQueue + ":Confirmed", "ack"], h.Log);
        var copy = Assert.Single(h.Broker.In(LateQueue));
        Assert.Equal(body, copy.Body);                                  // the ORIGINAL bytes
        Assert.Equal("agent-message-7", copy.MessageId);                 // the original MessageId
        Assert.Equal(0, copy.Headers[WorkflowOutcomeLateEnvelope.AttemptHeader]);
        Assert.Equal(T0.ToUnixTimeMilliseconds(), copy.Headers[WorkflowOutcomeLateEnvelope.DueAtHeader]);
        Assert.Equal(TaskId, copy.Headers[WorkflowOutcomeLateEnvelope.TaskIdHeader]);
        Assert.Equal(TemporalBridgeOptions.RelayQueue, copy.Headers[WorkflowOutcomeLateEnvelope.SourceQueueHeader]);
    }

    [Fact]
    public async Task HandleDelivery_OrphanWithoutMessageId_UsesTheBodySha256()
    {
        var h = new Harness();
        var body = WorkflowOutcomeTestValues.Reply(TaskId);

        await h.Listener.HandleDeliveryAsync(body, new BasicProperties(), h.Delivery());

        Assert.Equal(WorkflowOutcomeHashes.Sha256Hex(body), Assert.Single(h.Broker.In(LateQueue)).MessageId);
    }

    private static async Task UntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("timed out waiting for " + what);
            await Task.Delay(10);
        }
    }

    [Theory]
    [InlineData(WorkflowOutcomePublishStatus.Returned, "returned_unroutable")]
    [InlineData(WorkflowOutcomePublishStatus.Nacked, "nacked")]                     // includes reject-publish at the quota
    [InlineData(WorkflowOutcomePublishStatus.Unknown, "confirm_timeout")]
    [InlineData(WorkflowOutcomePublishStatus.Unavailable, "broker_lacks_publisher_confirms")]
    public async Task HandleDelivery_LateHandoffRefused_ParkedInTheHoldQueueThenAcked(WorkflowOutcomePublishStatus status, string reason)
    {
        var h = new Harness();
        h.Broker.Results.Enqueue(new WorkflowOutcomePublishResult(status, reason));
        var body = WorkflowOutcomeTestValues.Reply(TaskId);
        var delivery = h.Delivery();

        await h.Listener.HandleDeliveryAsync(body, null, delivery);

        // Acknowledged only after the confirmed hold copy; never rejected, dropped, held or paused.
        Assert.Equal(["publish:" + LateQueue, $"confirm:{LateQueue}:{status}", "publish:" + HoldQueue, $"confirm:{HoldQueue}:Confirmed", "ack"], h.Log);
        Assert.Equal(body, Assert.Single(h.Broker.In(HoldQueue)).Body);
        Assert.Empty(delivery.Pauses);
        Assert.Empty(h.Time.Delays);
        Assert.Contains(h.ListenerLog.Entries, e => e.Message.Contains("parked in " + HoldQueue) && e.Message.Contains(reason));
    }

    [Fact]
    public async Task HandleDelivery_LateAndHoldBothRefused_HeldUnackedThenAckedAfterConfirmedRetry()
    {
        var h = new Harness();
        h.Broker.Results.Enqueue(new WorkflowOutcomePublishResult(WorkflowOutcomePublishStatus.Nacked, "nacked"));
        h.Broker.Results.Enqueue(new WorkflowOutcomePublishResult(WorkflowOutcomePublishStatus.Unknown, "confirm_timeout"));
        var delivery = h.Delivery();

        await h.Listener.HandleDeliveryAsync(WorkflowOutcomeTestValues.Reply(TaskId), null, delivery);
        await UntilAsync(() => delivery.Acks == 1, "the held reply to be acknowledged after its confirmed retry");

        Assert.Equal(0, delivery.Rejects);
        Assert.Empty(delivery.Pauses);
        Assert.Equal(["publish:" + LateQueue, $"confirm:{LateQueue}:Nacked", "publish:" + HoldQueue, $"confirm:{HoldQueue}:Unknown",
            "publish:" + LateQueue, $"confirm:{LateQueue}:Confirmed", "ack"], h.Log);
        Assert.Contains(h.ListenerLog.Entries, e => e.Message.Contains("not captured") && e.Message.Contains("held"));
    }

    [Fact]
    public async Task HandleDelivery_OrphanHeld_LiveMatchedRepliesKeepFlowing()
    {
        var h = new Harness();
        h.Broker.ConfirmGate = null;
        for (var i = 0; i < 4; i++) // late and hold refused twice: held, then retried
            h.Broker.Results.Enqueue(new WorkflowOutcomePublishResult(WorkflowOutcomePublishStatus.Nacked, "nacked"));
        var orphan = h.Delivery();
        await h.Listener.HandleDeliveryAsync(WorkflowOutcomeTestValues.Reply(TaskId), null, orphan);

        var waiter = h.Registry.Register("wf-live/step");
        var live = h.Delivery();
        await h.Listener.HandleDeliveryAsync(WorkflowOutcomeTestValues.Reply("wf-live/step"), null, live);

        Assert.Equal("completed", (await waiter.Task.WaitAsync(TimeSpan.FromSeconds(10))).Status);
        Assert.Equal(1, live.Acks);
        Assert.Empty(orphan.Pauses);
        await UntilAsync(() => orphan.Acks == 1, "the held orphan to be captured");
        Assert.Single(h.Broker.In(LateQueue));
        Assert.Empty(h.Broker.In(HoldQueue));
    }

    [Fact]
    public async Task HandleDelivery_NoBrokerConfigured_HeldInsteadOfLegacyDrop()
    {
        var h = new Harness();
        var listener = new TemporalRelayListener(
            Options.Create(new RabbitMqOptions()), h.Registry,
            new TemporalClientFactory(Options.Create(new TemporalBridgeOptions())), h.ListenerLog,
            Options.Create(h.Options), outcomeBroker: null, timeProvider: h.Time);
        var delivery = h.Delivery();

        await listener.HandleDeliveryAsync(WorkflowOutcomeTestValues.Reply(TaskId), null, delivery);
        await Task.Delay(100);

        Assert.False(delivery.Settled);
        Assert.Empty(delivery.Pauses);
        Assert.Contains(h.ListenerLog.Entries, e => e.Message.Contains("capture_not_configured"));
        Assert.Empty(h.Time.Delays); // nothing can ever confirm: no retry loop
    }

    /// <summary>
    /// Repeated failures back off 5, 10, 20, 40, 60, 60 s in the background retry — the reply is
    /// never acknowledged or dropped meanwhile — and the first confirmed handoff resets the backoff.
    /// </summary>
    [Fact]
    public async Task HandleDelivery_RepeatedFailures_BackOffFiveToSixtyWithoutHotLoop_ThenResetOnSuccess()
    {
        var h = new Harness();
        var body = WorkflowOutcomeTestValues.Reply(TaskId);
        for (var i = 0; i < 14; i++) // every round refuses both the late and the hold copy
            h.Broker.Results.Enqueue(new WorkflowOutcomePublishResult(WorkflowOutcomePublishStatus.Nacked, "nacked"));
        var delivery = h.Delivery();

        await h.Listener.HandleDeliveryAsync(body, null, delivery);
        await UntilAsync(() => delivery.Acks == 1, "the reply to be captured after the retries");

        Assert.Equal(new[] { 5, 10, 20, 40, 60, 60, 60 }.Select(s => TimeSpan.FromSeconds(s)), h.Time.Delays);
        Assert.Equal(0, delivery.Rejects);

        h.Broker.Results.Enqueue(new WorkflowOutcomePublishResult(WorkflowOutcomePublishStatus.Unknown, "confirm_timeout"));
        h.Broker.Results.Enqueue(new WorkflowOutcomePublishResult(WorkflowOutcomePublishStatus.Unknown, "confirm_timeout"));
        var again = h.Delivery();
        await h.Listener.HandleDeliveryAsync(body, null, again);
        await UntilAsync(() => again.Acks == 1, "the second reply to be captured");
        Assert.Equal(TimeSpan.FromSeconds(5), h.Time.Delays[^1]);
    }

    [Fact]
    public void Backoff_IsBoundedBetweenFiveAndSixtySeconds()
    {
        var backoff = new WorkflowOutcomeBackoff();
        var values = Enumerable.Range(0, 20).Select(_ => backoff.Next()).ToList();

        Assert.All(values, v => Assert.InRange(v, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(60)));
        Assert.Equal(TimeSpan.FromSeconds(5), values[0]);
        Assert.Equal(TimeSpan.FromSeconds(60), values[^1]);
    }

    [Fact]
    public void QueueArguments_AreTheQuotaAndRejectPublish_WithNoTtlOrDeadLettering()
    {
        var arguments = WorkflowOutcomeLateEnvelope.QueueArguments();

        Assert.Equal(2, arguments.Count);
        Assert.Equal(268435456L, arguments["x-max-length-bytes"]);
        Assert.Equal("reject-publish", arguments["x-overflow"]);
        Assert.DoesNotContain(arguments.Keys, k => k.Contains("ttl") || k.Contains("dead-letter") || k.Contains("expires"));
    }

    // ── Consumer: store, delayed replacements, quarantine ─────────────────────────────────────

    [Fact]
    public async Task Process_ApiCommits_AcksOnlyAfter2xx_WithTheContractBody()
    {
        var h = new Harness();
        var body = WorkflowOutcomeTestValues.Reply(TaskId);

        await h.Consumer.ProcessAsync(body, LateProperties(), h.Delivery(), CancellationToken.None);

        Assert.Equal(["post", "ack"], h.Log);
        var (path, json, authorization) = Assert.Single(h.Api.Requests);
        Assert.Equal("/" + WorkflowOutcomeLateResultConsumer.LateResultsPath, path);
        Assert.Equal("Bearer " + WorkflowOutcomeTestValues.AdminToken, authorization);

        var request = JsonSerializer.Deserialize<WorkflowOutcomeLateResultRequest>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Null(request.Namespace);
        Assert.Equal(TaskId, request.TaskId);
        Assert.Equal("message-1", request.MessageId);
        Assert.Equal("completed", request.Status);
        Assert.Equal(WorkflowOutcomeTestValues.SecretText, request.Text);
        Assert.Equal(WorkflowOutcomeHashes.Sha256Hex(WorkflowOutcomeTestValues.SecretText), request.ResultSha256);
        Assert.Equal(0, request.Attempt);
        Assert.Equal(T0, request.ReceivedAt);
        Assert.Contains("\"namespace\":null", json);                    // camelCase on the wire
        Assert.Empty(h.Broker.Attempts);
    }

    /// <summary>
    /// The API is down for the whole run: attempt 0 fails, replacements 1/2/3 are published with
    /// UTC due times +5/+30/+60 s (each confirmed BEFORE the previous copy is acknowledged), the
    /// consumer waits exactly those delays in place, and the third delayed failure moves the
    /// message to quarantine with one deduplicated alarm. Nothing is dropped, nothing hot-loops.
    /// </summary>
    [Fact]
    public async Task Process_ApiOutage_Replaces5_30_60ThenQuarantinesWithOneAlarm()
    {
        var h = new Harness();
        h.Api.AlwaysFail = () => HttpStatusCode.ServiceUnavailable;
        var body = WorkflowOutcomeTestValues.Reply(TaskId);
        IReadOnlyBasicProperties properties = LateProperties();

        for (var attempt = 0; attempt <= 3; attempt++)
        {
            h.Log.Clear();
            var delivery = h.Delivery();
            await h.Consumer.ProcessAsync(body, properties, delivery, CancellationToken.None);

            Assert.Equal(1, delivery.Acks);
            Assert.Empty(delivery.Pauses);
            var target = attempt < 3 ? LateQueue : QuarantineQueue;
            Assert.Equal(["post", "publish:" + target, "confirm:" + target + ":Confirmed", "ack"], h.Log);

            var published = h.Broker.Attempts[^1];
            Assert.Equal(body, published.Body);
            Assert.Equal("message-1", published.MessageId);
            if (attempt < 3)
            {
                Assert.Equal(attempt + 1, published.Headers[WorkflowOutcomeLateEnvelope.AttemptHeader]);
                var due = DateTimeOffset.FromUnixTimeMilliseconds((long)published.Headers[WorkflowOutcomeLateEnvelope.DueAtHeader]!);
                Assert.Equal(WorkflowOutcomeLateEnvelope.DelayBefore(attempt + 1), due - h.Time.GetUtcNow());
                properties = published.Properties();
            }
            else
            {
                Assert.Equal("store_failed_after_retries", published.Headers[WorkflowOutcomeLateEnvelope.QuarantineReasonHeader]);
            }
        }

        Assert.Equal(new[] { 5, 30, 60 }.Select(s => TimeSpan.FromSeconds(s)), h.Time.Delays);
        Assert.Equal([0, 1, 2, 3], h.Api.Requests.Select(r => JsonDocument.Parse(r.Body).RootElement.GetProperty("attempt").GetInt32()));
        Assert.Single(h.Broker.In(QuarantineQueue));
        Assert.Equal(1, h.Consumer.Alarm.Counts["store_failed_after_retries"]);
        Assert.Single(h.ConsumerLog.Entries, e => e.Level == LogLevel.Error);
    }

    [Fact]
    public async Task Process_TransportFailure_TakesTheSameDelayedPath()
    {
        var h = new Harness();
        h.Api.Throw = new HttpRequestException("connection refused to an internal address");

        var delivery = h.Delivery();
        await h.Consumer.ProcessAsync(WorkflowOutcomeTestValues.Reply(TaskId), LateProperties(), delivery, CancellationToken.None);

        Assert.Equal(1, delivery.Acks);
        Assert.Equal(1, Assert.Single(h.Broker.In(LateQueue)).Headers[WorkflowOutcomeLateEnvelope.AttemptHeader]);
        Assert.DoesNotContain("internal address", h.ConsumerLog.AllText);
    }

    [Theory]
    [InlineData(WorkflowOutcomePublishStatus.Nacked)]
    [InlineData(WorkflowOutcomePublishStatus.Returned)]
    [InlineData(WorkflowOutcomePublishStatus.Unknown)]
    public async Task Process_ReplacementNotConfirmed_PausesWithoutAck(WorkflowOutcomePublishStatus status)
    {
        var h = new Harness();
        h.Api.AlwaysFail = () => HttpStatusCode.ServiceUnavailable;
        h.Broker.Results.Enqueue(new WorkflowOutcomePublishResult(status, "x"));
        var delivery = h.Delivery();

        await h.Consumer.ProcessAsync(WorkflowOutcomeTestValues.Reply(TaskId), LateProperties(), delivery, CancellationToken.None);

        Assert.False(delivery.Settled);
        Assert.Equal(TimeSpan.FromSeconds(5), Assert.Single(delivery.Pauses).Backoff);
    }

    [Fact]
    public async Task Process_QuarantineNotConfirmed_PausesWithoutAckOrAlarm()
    {
        var h = new Harness();
        h.Broker.Results.Enqueue(new WorkflowOutcomePublishResult(WorkflowOutcomePublishStatus.Nacked, "nacked"));
        var delivery = h.Delivery();

        await h.Consumer.ProcessAsync(WorkflowOutcomeTestValues.Reply(TaskId), LateProperties(attempt: 9), delivery, CancellationToken.None);

        Assert.False(delivery.Settled);
        Assert.Single(delivery.Pauses);
        Assert.Empty(h.Consumer.Alarm.Counts);
    }

    [Theory]
    [InlineData(null, "invalid_attempt")]
    [InlineData(-1, "invalid_attempt")]
    [InlineData(4, "invalid_attempt")]
    public async Task Process_InvalidAttempt_IsQuarantinedWithoutPosting(int? attempt, string reason)
    {
        var h = new Harness();
        var delivery = h.Delivery();

        await h.Consumer.ProcessAsync(WorkflowOutcomeTestValues.Reply(TaskId), LateProperties(attempt: attempt), delivery, CancellationToken.None);

        Assert.Empty(h.Api.Requests);
        Assert.Equal(1, delivery.Acks);
        Assert.Equal(reason, Assert.Single(h.Broker.In(QuarantineQueue)).Headers[WorkflowOutcomeLateEnvelope.QuarantineReasonHeader]);
    }

    [Theory]
    [InlineData("not-a-number")]
    [InlineData("-5")]
    public async Task Process_InvalidDueAt_IsQuarantinedWithoutPosting(string dueAt)
    {
        var h = new Harness();
        var delivery = h.Delivery();

        await h.Consumer.ProcessAsync(
            WorkflowOutcomeTestValues.Reply(TaskId), LateProperties(dueAt: Encoding.UTF8.GetBytes(dueAt)), delivery, CancellationToken.None);

        Assert.Empty(h.Api.Requests);
        Assert.Equal("invalid_due_at", Assert.Single(h.Broker.In(QuarantineQueue)).Headers[WorkflowOutcomeLateEnvelope.QuarantineReasonHeader]);
    }

    /// <summary>The source correlation beside the bytes names another task (or none): not attributable, quarantined.</summary>
    [Theory]
    [InlineData("wf-2/implement")]
    [InlineData(null)]
    public async Task Process_WrongRunCorrelation_IsQuarantined(string? headerTaskId)
    {
        var h = new Harness();
        var delivery = h.Delivery();

        await h.Consumer.ProcessAsync(
            WorkflowOutcomeTestValues.Reply(TaskId), LateProperties(taskId: headerTaskId), delivery, CancellationToken.None);

        Assert.Empty(h.Api.Requests);
        Assert.Equal("correlation_mismatch", Assert.Single(h.Broker.In(QuarantineQueue)).Headers[WorkflowOutcomeLateEnvelope.QuarantineReasonHeader]);
    }

    [Fact]
    public async Task Process_UnparseableBody_IsQuarantined()
    {
        var h = new Harness();
        await h.Consumer.ProcessAsync(Encoding.UTF8.GetBytes("not json"), LateProperties(), h.Delivery(), CancellationToken.None);

        Assert.Empty(h.Api.Requests);
        Assert.Equal("unparseable", Assert.Single(h.Broker.In(QuarantineQueue)).Headers[WorkflowOutcomeLateEnvelope.QuarantineReasonHeader]);
    }

    [Fact]
    public async Task Process_Oversize_IsQuarantinedWithAnAlarmAndNeverPosted()
    {
        var h = new Harness();
        var body = WorkflowOutcomeTestValues.Reply(TaskId, text: new string('x', WorkflowOutcomeLimits.MaxLateResultBytes));
        var delivery = h.Delivery();

        await h.Consumer.ProcessAsync(body, LateProperties(), delivery, CancellationToken.None);

        Assert.Empty(h.Api.Requests);
        Assert.Equal(1, delivery.Acks);
        Assert.Equal(body, Assert.Single(h.Broker.In(QuarantineQueue)).Body);
        Assert.Equal(1, h.Consumer.Alarm.Counts["oversize"]);
        Assert.Contains(h.ConsumerLog.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("reason=oversize"));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "store_rejected_400")]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, "store_rejected_413")]
    public async Task Process_DefinitiveApiRefusal_IsQuarantinedWithoutRetry(HttpStatusCode status, string reason)
    {
        var h = new Harness();
        h.Api.Failures.Enqueue(status);

        await h.Consumer.ProcessAsync(WorkflowOutcomeTestValues.Reply(TaskId), LateProperties(), h.Delivery(), CancellationToken.None);

        Assert.Single(h.Api.Requests);
        Assert.Empty(h.Broker.In(LateQueue));
        Assert.Equal(reason, Assert.Single(h.Broker.In(QuarantineQueue)).Headers[WorkflowOutcomeLateEnvelope.QuarantineReasonHeader]);
    }

    [Fact]
    public async Task Process_DueAtFarAhead_WaitsAtMostSixtySeconds()
    {
        var h = new Harness();

        await h.Consumer.ProcessAsync(
            WorkflowOutcomeTestValues.Reply(TaskId), LateProperties(dueAt: T0.AddHours(5).ToUnixTimeMilliseconds()), h.Delivery(), CancellationToken.None);

        Assert.Equal([TimeSpan.FromSeconds(60)], h.Time.Delays);
        Assert.Single(h.Api.Requests);
    }

    [Fact]
    public async Task Alarm_IsDeduplicatedPerReason()
    {
        var h = new Harness();
        for (var i = 0; i < 3; i++)
            await h.Consumer.ProcessAsync(WorkflowOutcomeTestValues.Reply(TaskId), LateProperties(attempt: 7), h.Delivery(), CancellationToken.None);

        Assert.Equal(3, h.Consumer.Alarm.Counts["invalid_attempt"]);
        Assert.Single(h.ConsumerLog.Entries, e => e.Level == LogLevel.Error);

        h.Time.Advance(TimeSpan.FromMinutes(16));
        await h.Consumer.ProcessAsync(WorkflowOutcomeTestValues.Reply(TaskId), LateProperties(attempt: 7), h.Delivery(), CancellationToken.None);
        Assert.Equal(2, h.ConsumerLog.Entries.Count(e => e.Level == LogLevel.Error));
    }

    // ── Restart, duplicates, privacy ──────────────────────────────────────────────────────────

    /// <summary>
    /// Shutdown while a message waits for its due time leaves it unacknowledged (the channel close
    /// returns it to the queue); a fresh consumer after the restart stores and acknowledges it.
    /// </summary>
    [Fact]
    public async Task Process_ShutdownDuringWait_LeavesTheMessage_AndARestartedConsumerStoresIt()
    {
        var h = new Harness();
        var properties = LateProperties(attempt: 1, dueAt: T0.AddSeconds(5).ToUnixTimeMilliseconds());
        using var stopping = new CancellationTokenSource();
        stopping.Cancel();
        var first = h.Delivery();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            h.Consumer.ProcessAsync(WorkflowOutcomeTestValues.Reply(TaskId), properties, first, stopping.Token));
        Assert.False(first.Settled);
        Assert.Empty(first.Pauses);
        Assert.Empty(h.Api.Requests);
        Assert.Empty(h.Broker.Attempts);

        var restarted = h.NewConsumer();
        var second = h.Delivery();
        await restarted.ProcessAsync(WorkflowOutcomeTestValues.Reply(TaskId), properties, second, CancellationToken.None);

        Assert.Equal(1, second.Acks);
        Assert.Single(h.Api.Stored);
    }

    /// <summary>
    /// A lost confirm: the broker DID store the late copy but the listener never heard, so it
    /// paused and the original was redelivered and captured again. Two copies, one stable
    /// MessageId and one result hash: the API stores it once, both copies are acknowledged.
    /// </summary>
    [Fact]
    public async Task UnknownConfirm_DuplicateCopiesAreDedupedByStableIdAndResultHash()
    {
        var h = new Harness();
        h.Broker.UnknownStillEnqueues = true;
        h.Broker.Results.Enqueue(new WorkflowOutcomePublishResult(WorkflowOutcomePublishStatus.Unknown, "confirm_timeout"));
        var body = WorkflowOutcomeTestValues.Reply(TaskId);

        // The unknown late confirm parks a second copy in the hold queue; both carry the stable id.
        var lost = h.Delivery();
        await h.Listener.HandleDeliveryAsync(body, null, lost);
        Assert.Equal(1, lost.Acks);

        var copies = h.Broker.In(LateQueue).Concat(h.Broker.In(HoldQueue)).ToList();
        Assert.Equal(2, copies.Count);
        Assert.Equal(copies[0].MessageId, copies[1].MessageId);

        foreach (var copy in copies)
        {
            var delivery = h.Delivery();
            await h.Consumer.ProcessAsync(copy.Body, copy.Properties(), delivery, CancellationToken.None);
            Assert.Equal(1, delivery.Acks);
        }

        Assert.Equal(2, h.Api.Requests.Count);
        Assert.Single(h.Api.Stored);
        Assert.Single(h.Api.Requests.Select(r => JsonDocument.Parse(r.Body).RootElement.GetProperty("messageId").GetString()).Distinct());
    }

    /// <summary>The listener and the consumer together: relay delivery → confirmed late copy → stored → both acknowledged.</summary>
    [Fact]
    public async Task EndToEnd_OrphanIsCapturedThenStored_EachAckFollowsItsCommit()
    {
        var h = new Harness();
        var body = WorkflowOutcomeTestValues.Reply(TaskId, type: RelayMessageType.PartialResponse, text: "partial " + WorkflowOutcomeTestValues.SecretText);

        var relay = h.Delivery();
        await h.Listener.HandleDeliveryAsync(body, null, relay);
        var copy = Assert.Single(h.Broker.In(LateQueue));
        var late = h.Delivery();
        await h.Consumer.ProcessAsync(copy.Body, copy.Properties(), late, CancellationToken.None);

        Assert.Equal(
            ["publish:" + LateQueue, "confirm:" + LateQueue + ":Confirmed", "ack", "post", "ack"], h.Log);
        Assert.Equal("incomplete", JsonDocument.Parse(Assert.Single(h.Api.Requests).Body).RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Logs_NeverContainBodiesOrTokens()
    {
        var h = new Harness();
        h.Api.Failures.Enqueue(HttpStatusCode.ServiceUnavailable);
        h.Broker.Results.Enqueue(new WorkflowOutcomePublishResult(WorkflowOutcomePublishStatus.Nacked, "nacked"));
        var body = WorkflowOutcomeTestValues.Reply(TaskId);

        var held = h.Delivery();
        await h.Listener.HandleDeliveryAsync(body, null, held);
        Assert.Equal(1, held.Acks); // parked in the hold queue
        await h.Listener.HandleDeliveryAsync(body, null, h.Delivery());
        var copy = h.Broker.In(LateQueue)[0];
        await h.Consumer.ProcessAsync(copy.Body, copy.Properties(), h.Delivery(), CancellationToken.None);
        await h.Consumer.ProcessAsync(body, LateProperties(attempt: 8), h.Delivery(), CancellationToken.None);

        var all = h.ListenerLog.AllText + "\n" + h.ConsumerLog.AllText;
        Assert.NotEmpty(all.Trim());
        Assert.DoesNotContain(WorkflowOutcomeTestValues.SecretText, all);
        Assert.DoesNotContain(WorkflowOutcomeTestValues.AdminToken, all);
    }

    // ── Staged activation ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task WaitForStoreReady_ConsumesOnlyOnceTheApiAnswers()
    {
        var h = new Harness();
        h.Api.NotReadyProbes = 3;

        await h.Consumer.WaitForStoreReadyAsync(CancellationToken.None);

        Assert.Equal(4, h.Api.Probes);
        Assert.Equal(new[] { 5, 10, 20 }.Select(s => TimeSpan.FromSeconds(s)), h.Time.Delays);
        Assert.Single(h.ConsumerLog.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("not ready (http_503)"));
        Assert.Empty(h.Api.Requests);
    }

    // ── Options ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Options_DefaultOff_WithTheContractQueueNames()
    {
        var options = new TemporalBridgeOptions();

        Assert.False(options.WorkflowOutcomeLateCaptureEnabled);
        Assert.Equal(LateQueue, options.WorkflowOutcomeLateQueue);
        Assert.Equal(QuarantineQueue, options.WorkflowOutcomeQuarantineQueue);
        Assert.Equal(HoldQueue, options.WorkflowOutcomeHoldQueue);
        Assert.Null(options.ValidateWorkflowOutcomeLateCapture());
    }

    [Theory]
    [InlineData("", QuarantineQueue)]
    [InlineData(" padded", QuarantineQueue)]
    [InlineData(LateQueue, LateQueue)]
    [InlineData(TemporalBridgeOptions.RelayQueue, QuarantineQueue)]
    [InlineData("amq.late", QuarantineQueue)]
    [InlineData(HoldQueue, QuarantineQueue)]
    [InlineData(LateQueue, HoldQueue)]
    public void Options_InvalidQueueNamesFailWhenEnabled(string late, string quarantine)
    {
        var options = new TemporalBridgeOptions
        {
            WorkflowOutcomeLateCaptureEnabled = true,
            WorkflowOutcomeLateQueue = late,
            WorkflowOutcomeQuarantineQueue = quarantine,
            OrchestratorUrl = "http://orchestrator.example:8080",
            OrchestratorAuthToken = "placeholder-token",
        };

        Assert.NotNull(options.ValidateWorkflowOutcomeLateCapture());
        options.WorkflowOutcomeLateCaptureEnabled = false;
        Assert.Null(options.ValidateWorkflowOutcomeLateCapture());
    }

    [Theory]
    [InlineData("", "placeholder-token", "OrchestratorUrl")]
    [InlineData("orchestrator.example:8080", "placeholder-token", "OrchestratorUrl")]
    [InlineData("ftp://orchestrator.example", "placeholder-token", "OrchestratorUrl")]
    [InlineData("http://orchestrator.example:8080", "", "OrchestratorAuthToken")]
    [InlineData("http://orchestrator.example:8080", "   ", "OrchestratorAuthToken")]
    public void Options_CaptureEnabledWithoutTheOrchestratorEndpoint_FailsStartup(string url, string token, string missing)
    {
        var options = new TemporalBridgeOptions { WorkflowOutcomeLateCaptureEnabled = true, OrchestratorUrl = url, OrchestratorAuthToken = token };

        Assert.Contains(missing, options.ValidateWorkflowOutcomeLateCapture());
        options.OrchestratorUrl = "http://orchestrator.example:8080";
        options.OrchestratorAuthToken = "placeholder-token";
        Assert.Null(options.ValidateWorkflowOutcomeLateCapture());
    }

    [Fact]
    public void AppSettings_ShipCaptureOff()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "seed.example.json")))
            dir = dir.Parent;
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir!.FullName, "src", "Fleet.Temporal", "appsettings.json")));
        var bridge = document.RootElement.GetProperty("TemporalBridge");

        Assert.False(bridge.GetProperty("WorkflowOutcomeLateCaptureEnabled").GetBoolean());
        Assert.Equal(LateQueue, bridge.GetProperty("WorkflowOutcomeLateQueue").GetString());
        Assert.Equal(QuarantineQueue, bridge.GetProperty("WorkflowOutcomeQuarantineQueue").GetString());
        Assert.Equal(HoldQueue, bridge.GetProperty("WorkflowOutcomeHoldQueue").GetString());
    }

    [Fact]
    public void SupportsPublisherConfirms_ReadsTheServerCapability()
    {
        Assert.True(RabbitMqWorkflowOutcomeBroker.SupportsPublisherConfirms(new Dictionary<string, object?>
        {
            ["capabilities"] = new Dictionary<string, object?> { ["publisher_confirms"] = true },
        }));
        Assert.False(RabbitMqWorkflowOutcomeBroker.SupportsPublisherConfirms(new Dictionary<string, object?>
        {
            ["capabilities"] = new Dictionary<string, object?> { ["publisher_confirms"] = false },
        }));
        Assert.False(RabbitMqWorkflowOutcomeBroker.SupportsPublisherConfirms(new Dictionary<string, object?>()));
        Assert.False(RabbitMqWorkflowOutcomeBroker.SupportsPublisherConfirms(null));
    }
}
