using System.Collections.Concurrent;
using System.Threading.Channels;
using Fleet.Agent.Models;
using Fleet.Conversations.Contracts;
using Fleet.Journal.Client;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Fleet.Agent.Services;

/// <summary>Publishes the latest turn scope without putting HTTP on the dispatch path.</summary>
public sealed class TurnBindingPublisher(
    JournalHttpClient client, ILogger<TurnBindingPublisher> logger, TimeProvider? time = null) : BackgroundService
{
    public static readonly TimeSpan RenewalInterval = TimeSpan.FromSeconds(60);
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<long, (long BotId, string Kind)> _observed = new();
    private readonly Channel<bool> _changed = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest });
    private readonly ConcurrentDictionary<string, long> _counts = new(StringComparer.Ordinal);
    private JournalTurnBinding _current = new(Guid.NewGuid().ToString("N"), 1, "unbound");
    private int _failed;

    public JournalTurnBinding Current { get { lock (_gate) return _current; } }
    public bool BindingFailed => Volatile.Read(ref _failed) != 0;
    public long Count(string result) => _counts.GetValueOrDefault(result);

    public void ObserveChat(long chatId, long botId, string kind)
    {
        if (chatId != 0 && botId > 0 && !ConversationRegistry.IsReservedKey(chatId)
            && kind is "private" or "group" or "supergroup")
            _observed[chatId] = (botId, kind);
    }

    /// <summary>Called only at dispatch, never on a same-turn injected message.</summary>
    public long BeginTurn(long chatId, TaskSource source)
    {
        lock (_gate)
        {
            var next = _current.Seq + 1;
            _current = source is TaskSource.UserMessage or TaskSource.NewCommand
                    or TaskSource.DebouncedGroupBatch or TaskSource.CheckIn
                && !ConversationRegistry.IsReservedKey(chatId) && _observed.TryGetValue(chatId, out var chat)
                ? new(_current.Epoch, next, "bound", chat.Kind, chat.BotId, chatId)
                : new(_current.Epoch, next, "unbound");
            _changed.Writer.TryWrite(true);
            return next;
        }
    }

    public void EndTurn(long sequence)
    {
        lock (_gate)
        {
            // An old task's teardown must not unbind a newly dispatched task.
            if (_current.Seq != sequence) return;
            _current = new(_current.Epoch, _current.Seq + 1, "unbound");
            _changed.Writer.TryWrite(true);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var backoffSeconds = 1;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                while (_changed.Reader.TryRead(out _)) { }
                var sent = Current;
                var response = await client.PutTurnBindingAsync(sent, stoppingToken);
                var result = response.Status switch
                {
                    204 => "accepted", 409 => "stale", 401 => "unauthorized", 404 => "route_missing", _ => "failed",
                };
                _counts.AddOrUpdate(result, 1, (_, count) => count + 1);
                lock (_gate)
                    if (_current.Seq == sent.Seq) Volatile.Write(ref _failed, response.Status == 204 ? 0 : 1);
                logger.LogDebug("Turn binding: {Result}", result);
                var delay = response.Status == 204 ? RenewalInterval : TimeSpan.FromSeconds(backoffSeconds);
                backoffSeconds = response.Status == 204 ? 1 : Math.Min(30, backoffSeconds * 2);
                await WaitForChangeAsync(delay, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    private async Task WaitForChangeAsync(TimeSpan delay, CancellationToken ct)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var changed = _changed.Reader.ReadAsync(wait.Token).AsTask();
        var renewed = Task.Delay(delay, _time, wait.Token);
        await Task.WhenAny(changed, renewed);
        await wait.CancelAsync();
        try { await changed; } catch (OperationCanceledException) when (wait.IsCancellationRequested) { }
        try { await renewed; } catch (OperationCanceledException) when (wait.IsCancellationRequested) { }
        ct.ThrowIfCancellationRequested();
    }
}
