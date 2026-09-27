using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Fleet.Conversations.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fleet.Journal.Client.Tests;

/// <summary>A clock the test moves by hand.</summary>
internal sealed class ManualTime(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>A temporary spool directory, removed at the end of the test.</summary>
internal sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "journal-test-" + Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Path);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
    }
}

/// <summary>
/// The journal listener as the drainer sees it: each request is answered by <see cref="Respond"/>,
/// which receives the posted record.
/// </summary>
internal sealed class FakeListener : HttpMessageHandler
{
    public Func<JsonObject, HttpResponseMessage> Respond { get; set; } = _ => Status(201, "{\"result\":\"created\"}");

    public List<JsonObject> Posted { get; } = [];
    public List<string?> Authorizations { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject();
        lock (Posted)
        {
            Posted.Add(body);
            Authorizations.Add(request.Headers.Authorization?.ToString());
        }

        return Respond(body);
    }

    public static HttpResponseMessage Status(int status, string? json = null)
    {
        var response = new HttpResponseMessage((HttpStatusCode)status);
        if (json is not null) response.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return response;
    }

    public static HttpResponseMessage Refused() => throw new HttpRequestException("connection refused");
}

internal static class Records
{
    public const string Token = "cj1.ingest.agent1.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    /// <summary>A valid inbound DM record; <paramref name="messageId"/> tells records apart.</summary>
    public static JournalRecord Record(long messageId, string? text = "hello", IReadOnlyList<JournalAttachment>? attachments = null) => new()
    {
        EventId = Fleet.Protocol.Ulid.NewUlid(),
        Telegram = new JournalTelegramRef
        {
            BotId = 7001,
            ChatId = 111,
            ChatKind = JournalChatKind.Private,
            MessageId = messageId,
        },
        Direction = JournalDirection.Inbound,
        Sender = new JournalSender { Kind = JournalSenderKind.Human, Id = "111", Display = "someone" },
        SentAt = new DateTimeOffset(2026, 9, 27, 10, 0, 0, TimeSpan.Zero),
        Text = text,
        TextFormat = text is null ? null : JournalTextFormat.Plain,
        Origin = JournalRecordOrigin.TelegramUpdate,
        Attachments = attachments ?? [],
    };

    public static JsonObject Json(JournalRecord record) => JsonNode.Parse(JournalRecordJson.Serialize(record))!.AsObject();

    public static long MessageId(JsonObject posted) => posted["telegram"]!["messageId"]!.GetValue<long>();
}

/// <summary>A spool, a fake listener and a drainer on a manual clock.</summary>
internal sealed class DrainerRig : IDisposable
{
    private readonly TempDir _dir = new();

    public DrainerRig(DateTimeOffset? start = null)
    {
        Time = new ManualTime(start ?? new DateTimeOffset(2026, 9, 27, 12, 0, 0, TimeSpan.Zero));
        Spool = new JournalSpool(_dir.Path, Time);
        Drainer = NewDrainer(Spool);
    }

    public ManualTime Time { get; }
    public JournalSpool Spool { get; }
    public FakeListener Listener { get; } = new();
    public JournalCounters Counters { get; } = new();
    public CapturingLogger<JournalDrainer> Log { get; } = new();
    public JournalDrainer Drainer { get; }
    public string Root => _dir.Path;

    public JournalDrainer NewDrainer(JournalSpool spool, HttpMessageHandler? handler = null, TimeSpan? timeout = null)
    {
        var http = new HttpClient(handler ?? Listener) { BaseAddress = new Uri("http://journal.test") };
        return new JournalDrainer(spool, new JournalHttpClient(http, Records.Token, timeout), Counters, Log, Time);
    }

    /// <summary>Spools <paramref name="record"/> and returns its spool id.</summary>
    public string Write(JournalRecord record, IReadOnlyList<SpoolMedia>? media = null)
    {
        var before = Spool.PendingIds().ToHashSet();
        Assert.Equal(SpoolWriteOutcome.Written, Spool.Write(Records.Json(record), "inbound", media ?? []));
        return Spool.PendingIds().Single(id => !before.Contains(id));
    }

    public void Dispose() => _dir.Dispose();
}

/// <summary>Keeps every formatted log line, so a test can assert what was — and was not — logged.</summary>
internal sealed class CapturingLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message)> Lines { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullLogger.Instance.BeginScope(state);

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (Lines) Lines.Add((logLevel, formatter(state, exception)));
    }

    public int Count(LogLevel level) { lock (Lines) return Lines.Count(l => l.Level == level); }
}
