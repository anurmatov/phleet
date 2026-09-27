using System.Net.Http.Headers;
using System.Text;
using Fleet.Comms;
using Fleet.Comms.Configuration;
using Fleet.Conversations.Contracts;
using Fleet.Conversations.Journal;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;

namespace Fleet.Comms.Tests;

/// <summary>
/// The journal application, in process, built through <see cref="CommsApp.BuildJournalApp"/> — the
/// composition <c>Program</c> runs — over a store double.
/// </summary>
internal sealed class JournalTestHost : IAsyncDisposable
{
    public static readonly string KeyA = Key(1);
    public static readonly string KeyB = Key(101);

    private readonly WebApplication _app;

    private JournalTestHost(WebApplication app, FakeJournalStore store, JournalRuntimeStats stats)
    {
        _app = app;
        Store = store;
        Stats = stats;
        Client = app.GetTestClient();
    }

    public FakeJournalStore Store { get; }
    public JournalRuntimeStats Stats { get; }

    /// <summary>A client with no credential.</summary>
    public HttpClient Client { get; }

    public static async Task<JournalTestHost> StartAsync(
        string? keys = null, string excludedChatIds = "", FakeJournalStore? store = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseSetting(WebHostDefaults.ServerUrlsKey, string.Empty);
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        var options = new CommsOptions
        {
            ConversationConnectionString = "Server=unused-by-this-test;",
            Journal = new JournalOptions
            {
                Enabled = true,
                TokenKeys = keys ?? KeyA,
                ExcludedChatIds = excludedChatIds,
            },
        };
        options.ValidateJournal();

        store ??= new FakeJournalStore();
        var stats = new JournalRuntimeStats();
        var app = CommsApp.BuildJournalApp(builder, store, options, stats);

        await app.StartAsync();
        return new JournalTestHost(app, store, stats);
    }

    public static string Token(string purpose, string subject = "agent1", string? key = null) =>
        JournalTokens.Mint(JournalTokens.ParseKeys(key ?? KeyA)[0], purpose, subject);

    public Task<HttpResponseMessage> PostAsync(string json, string? token) =>
        SendAsync(HttpMethod.Post, "/journal/v1/messages", json, token);

    public Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? json, string? token)
    {
        var request = new HttpRequestMessage(method, path);
        if (json is not null)
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return Client.SendAsync(request);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    private static string Key(int seed) =>
        Convert.ToBase64String(Enumerable.Range(seed, 48).Select(i => (byte)i).ToArray())
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>A journal store double: records calls, answers as configured, can block or fail.</summary>
internal sealed class FakeJournalStore : IJournalStore
{
    private int _calls;

    public List<(JournalRecord Record, string Observer)> Ingested { get; } = [];

    public JournalIngestOutcome Outcome { get; set; } = JournalIngestOutcome.Created;

    /// <summary>When set, IngestAsync waits on it — to hold requests in flight.</summary>
    public SemaphoreSlim? Gate { get; set; }

    /// <summary>Signalled once per call that has entered the store.</summary>
    public SemaphoreSlim Entered { get; } = new(0);

    public string? UnavailableReason { get; set; }
    public bool Unavailable { get; set; }

    public int Calls => Volatile.Read(ref _calls);

    public async Task<JournalIngestResult> IngestAsync(JournalRecord record, string observer, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _calls);
        Entered.Release();

        if (Gate is not null) await Gate.WaitAsync(ct);

        if (Unavailable) throw new JournalStoreUnavailableException(UnavailableReason);

        lock (Ingested) Ingested.Add((record, observer));
        return new JournalIngestResult
        {
            Outcome = Outcome,
            MessageId = Outcome is JournalIngestOutcome.Conflict or JournalIngestOutcome.EventIdReused
                ? null
                : "01J00000000000000000000000",
        };
    }

    public Task<JournalStoreStatus> GetStatusAsync(CancellationToken ct = default)
    {
        if (Unavailable) throw new JournalStoreUnavailableException(UnavailableReason);

        return Task.FromResult(new JournalStoreStatus
        {
            SchemaVersion = 4,
            Observers = [new JournalObserverStatus { Observer = "agent1", Messages = 3, LastIngestAt = DateTimeOffset.UnixEpoch }],
        });
    }
}

/// <summary>Journal records as JSON text, varied one field at a time.</summary>
internal static class JournalRecords
{
    public static string Valid(
        string? eventId = null, long chatId = -1001234567890, string chatKind = "supergroup",
        long messageId = 42, string direction = "inbound", string? sentAt = null,
        string text = "\"hello\"", string origin = "telegram_update", string extra = "",
        string attachments = "[]", string telegramExtra = "") =>
        $$"""
        {"eventId":"{{eventId ?? Fleet.Protocol.Ulid.NewUlid()}}","channel":"telegram",
         "telegram":{"botId":7001,"chatId":{{chatId}},"chatKind":"{{chatKind}}","messageId":{{messageId}}{{telegramExtra}}},
         "direction":"{{direction}}","sender":{"kind":"human","id":"u_1"},
         "sentAt":"{{sentAt ?? DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O")}}",
         "text":{{text}},"textFormat":"plain","origin":"{{origin}}","attachments":{{attachments}}{{extra}}}
        """;
}
