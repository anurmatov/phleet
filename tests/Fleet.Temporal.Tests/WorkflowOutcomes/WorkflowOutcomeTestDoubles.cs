using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Fleet.Temporal.Services;
using Microsoft.Extensions.Logging;
using NSubstitute;
using RabbitMQ.Client;

namespace Fleet.Temporal.Tests.WorkflowOutcomes;

/// <summary>One message the fake broker accepted (or may have accepted, for an unknown confirm).</summary>
internal sealed record PublishedMessage(string Queue, byte[] Body, string MessageId, IReadOnlyDictionary<string, object?> Headers)
{
    public BasicProperties Properties() => new() { MessageId = MessageId, Headers = new Dictionary<string, object?>(Headers), Persistent = true };
}

/// <summary>
/// In-memory broker. Each publish takes the next scripted result (default confirmed); a confirmed
/// publish — and an unknown one with <see cref="UnknownStillEnqueues"/>, which is what a lost
/// confirm looks like — lands in <see cref="Queues"/>. Every step is written to the shared log so
/// a test can assert ordering against the delivery's ACK.
/// </summary>
internal sealed class FakeOutcomeBroker(List<string> log) : IWorkflowOutcomeBroker
{
    public Queue<WorkflowOutcomePublishResult> Results { get; } = new();
    public Dictionary<string, List<PublishedMessage>> Queues { get; } = new(StringComparer.Ordinal);
    public List<PublishedMessage> Attempts { get; } = [];
    public string? ActivationError { get; set; }
    public bool UnknownStillEnqueues { get; set; }

    /// <summary>When set, the confirm is withheld until the test completes it.</summary>
    public TaskCompletionSource? ConfirmGate { get; set; }

    public TaskCompletionSource PublishStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<string?> ActivateAsync(CancellationToken cancellationToken) => Task.FromResult(ActivationError);

    public async Task<WorkflowOutcomePublishResult> PublishAsync(
        string queue, ReadOnlyMemory<byte> body, string messageId, IReadOnlyDictionary<string, object?> headers, CancellationToken cancellationToken)
    {
        var message = new PublishedMessage(queue, body.ToArray(), messageId, new Dictionary<string, object?>(headers));
        Attempts.Add(message);
        log.Add($"publish:{queue}");
        PublishStarted.TrySetResult();
        if (ConfirmGate is not null) await ConfirmGate.Task;

        var result = Results.Count > 0 ? Results.Dequeue() : WorkflowOutcomePublishResult.Confirmed;
        if (result.IsConfirmed || (result.Status == WorkflowOutcomePublishStatus.Unknown && UnknownStillEnqueues))
        {
            if (!Queues.TryGetValue(queue, out var list)) Queues[queue] = list = [];
            list.Add(message);
        }

        log.Add($"confirm:{queue}:{result.Status}");
        return result;
    }

    public IReadOnlyList<PublishedMessage> In(string queue) => Queues.TryGetValue(queue, out var list) ? list : [];
}

internal sealed class RecordingDelivery(List<string> log) : IWorkflowOutcomeDelivery
{
    public int Acks { get; private set; }
    public int Rejects { get; private set; }
    public List<(TimeSpan Backoff, string Reason)> Pauses { get; } = [];

    public Task AckAsync()
    {
        Acks++;
        log.Add("ack");
        return Task.CompletedTask;
    }

    public Task RejectWithoutRequeueAsync()
    {
        Rejects++;
        log.Add("reject");
        return Task.CompletedTask;
    }

    public void Pause(TimeSpan backoff, string reason)
    {
        Pauses.Add((backoff, reason));
        log.Add("pause");
    }

    public bool Settled => Acks + Rejects > 0;
}

/// <summary>
/// A clock that never really waits: every timer fires at once and moves "now" forward by its due
/// time, so a 60 s wait costs nothing and is still recorded in <see cref="Delays"/>.
/// </summary>
internal sealed class AutoAdvanceTimeProvider(DateTimeOffset start) : TimeProvider
{
    private readonly Lock _gate = new();
    private DateTimeOffset _now = start;

    public List<TimeSpan> Delays { get; } = [];

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate) return _now;
    }

    public void Advance(TimeSpan by)
    {
        lock (_gate) _now += by;
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        if (dueTime != Timeout.InfiniteTimeSpan)
        {
            lock (_gate)
            {
                Delays.Add(dueTime);
                _now += dueTime;
            }

            ThreadPool.QueueUserWorkItem(_ => callback(state));
        }

        return new NoopTimer();
    }

    private sealed class NoopTimer : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>
/// The orchestrator's late-results route as the bridge sees it: scripted failures first, then
/// idempotent storage on (namespace, taskId, resultSha256) — 201 for a new result, 200 for a
/// duplicate — like the contract in plan.md.
/// </summary>
internal sealed class FakeLateResultsApi : HttpMessageHandler
{
    public Queue<HttpStatusCode> Failures { get; } = new();
    public Func<HttpStatusCode>? AlwaysFail { get; set; }
    public Exception? Throw { get; set; }
    public List<(string Path, string Body, string? Authorization)> Requests { get; } = [];
    public Dictionary<string, Guid> Stored { get; } = new(StringComparer.Ordinal);
    public List<string>? Log { get; set; }
    /// <summary>How many readiness probes answer 503 before the API reports ready.</summary>
    public int NotReadyProbes { get; set; }
    public int Probes { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Method == HttpMethod.Get)
        {
            Probes++;
            return Probes > NotReadyProbes
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"healthy\":true}") }
                : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        }

        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add((request.RequestUri!.AbsolutePath, body, request.Headers.Authorization?.ToString()));
        Log?.Add("post");
        if (Throw is not null) throw Throw;
        if (AlwaysFail is not null) return new HttpResponseMessage(AlwaysFail());
        if (Failures.Count > 0) return new HttpResponseMessage(Failures.Dequeue());

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var key = (root.GetProperty("namespace").ValueKind == JsonValueKind.Null ? "" : root.GetProperty("namespace").GetString()) +
                  "\u001f" + root.GetProperty("taskId").GetString() + "\u001f" + root.GetProperty("resultSha256").GetString();
        var created = !Stored.ContainsKey(key);
        if (created) Stored[key] = Guid.NewGuid();
        return new HttpResponseMessage(created ? HttpStatusCode.Created : HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { id = Stored[key], status = created ? "stored" : "duplicate" }), Encoding.UTF8, "application/json"),
        };
    }

    public IHttpClientFactory Factory(string clientName)
    {
        var client = new HttpClient(this) { BaseAddress = new Uri("https://orchestrator.example/") };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", WorkflowOutcomeTestValues.AdminToken);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(clientName).Returns(client);
        return factory;
    }
}

internal sealed class RecordingLogger<T> : ILogger<T>
{
    private readonly Lock _gate = new();
    private readonly List<(LogLevel Level, string Message)> _entries = [];

    public IReadOnlyList<(LogLevel Level, string Message)> Entries
    {
        get { lock (_gate) return [.. _entries]; }
    }

    public string AllText => string.Join("\n", Entries.Select(e => e.Message));

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (_gate) _entries.Add((logLevel, formatter(state, exception)));
    }
}

internal static class WorkflowOutcomeTestValues
{
    public const string AdminToken = "test-admin-token-0123456789";
    public const string SecretText = "SECRET-BODY-TEXT-must-never-be-logged";

    /// <summary>An agent reply exactly as an agent publishes it to the relay queue.</summary>
    public static byte[] Reply(string taskId, string text = "[status: completed] " + SecretText, string type = "response") =>
        JsonSerializer.SerializeToUtf8Bytes(new Fleet.Temporal.Models.RelayMessage(
            ChatId: 0, Sender: "agent1", Text: text, Timestamp: DateTimeOffset.Parse("2026-10-24T00:00:00Z"), Type: type, TaskId: taskId));
}
