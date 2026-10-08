using System.Security.Cryptography;
using System.Text.Json;
using Fleet.Agent.Abstractions;
using Fleet.Agent.Models;
using Fleet.Conversations.Contracts;
using Microsoft.Extensions.Logging;
namespace Fleet.Agent.Services.MessageCopy;

public sealed class MessageCopyCoordinator(
    Func<(long ChatId, RunningTask? Turn)> currentTurn, AllowlistHolder allowlist,
    Func<ITelegramCopyClient?> client, MessageCopyCounter counter,
    ILogger<MessageCopyCoordinator> logger, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly object _lock = new();
    private Pending? _pending;

    private enum State { Awaiting, Copying, Done, Expired }
    private sealed class Pending(long chat, RunningTask turn, string turnId, long destination,
        DateTimeOffset started, CancellationToken token)
    {
        public long Chat { get; } = chat;
        public RunningTask Turn { get; } = turn;
        public string TurnId { get; } = turnId;
        public long Destination { get; } = destination;
        public DateTimeOffset Started { get; } = started;
        public CancellationToken Token { get; } = token;
        public string Nonce { get; } = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        public int PromptId { get; set; }
        public State Status { get; set; } = State.Awaiting;
        public string? CallbackId { get; set; }
        public TaskCompletionSource<string> Tap { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private bool DestinationAllowed(long chat) => chat > 0 ? allowlist.IsUserAllowed(chat) : chat < 0 && allowlist.IsGroupAllowed(chat);
    private static bool Human(long chat, RunningTask? turn) => chat > 0 && turn is not null && turn.UserId == chat
        && !turn.Closed && !turn.Cts.IsCancellationRequested
        && turn.Source is TaskSource.UserMessage or TaskSource.NewCommand
        && turn.Identity?.ChannelId == ChannelIds.Telegram && turn.Identity.TurnId is not null;
    private string? Revalidate(Pending pending)
    {
        var (chat, turn) = currentTurn();
        if (chat != pending.Chat || !ReferenceEquals(turn, pending.Turn) || !Human(chat, turn)
            || turn!.Identity!.TurnId != pending.TurnId || !allowlist.IsUserAllowed(turn.UserId))
            return "not_a_human_request";
        return DestinationAllowed(pending.Destination) ? null : "destination_not_allowed";
    }

    public async Task<string> CopyAsync(int messageId, long destination, CancellationToken ct)
    {
        var started = _clock.GetUtcNow();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(50), _clock);
        string outcome = "not_a_human_request";
        int? copiedId = null, retryAfter = null;
        string? description = null;
        Pending? pending = null;
        ITelegramCopyClient? bot = null;
        bool copyStarted = false;
        try
        {
            if (messageId <= 0 || destination == 0) { outcome = "invalid_argument"; return Result(); }
            var (chat, turn) = currentTurn();
            if (chat < 0) { outcome = "group_source_unsupported"; return Result(); }
            if (!Human(chat, turn) || !allowlist.IsUserAllowed(chat)) return Result();
            if (destination == chat || !DestinationAllowed(destination)) { outcome = "destination_not_allowed"; return Result(); }
            bot = client();
            if (bot is null) { outcome = "prompt_failed"; return Result(); }
            using var whole = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, ct, turn!.Cts.Token);
            lock (_lock)
            {
                if (_pending is not null) { outcome = "busy"; return Result(); }
                pending = new(chat, turn, turn.Identity!.TurnId!, destination, started, whole.Token);
                _pending = pending;
            }
            string label;
            try { label = await Limited(token => bot.GetChatAsync(destination, token), 5, whole.Token); }
            catch (Exception) { outcome = "destination_unreachable"; return Result(); }
            try
            {
                var prompt = await Limited(token => bot.SendPromptAsync(chat, messageId, label[..Math.Min(64, label.Length)], pending.Nonce, token), 5, whole.Token);
                lock (_lock) pending.PromptId = prompt;
            }
            catch (TelegramCopyException e)
            {
                outcome = IsMissing(e.Message) ? "source_not_found" : "prompt_failed"; return Result();
            }
            catch (Exception) { outcome = "prompt_failed"; return Result(); }

            var waitLeft = started + TimeSpan.FromSeconds(38) - _clock.GetUtcNow();
            using var waitDeadline = new CancellationTokenSource(waitLeft > TimeSpan.Zero ? waitLeft : TimeSpan.Zero, _clock);
            using var waiting = CancellationTokenSource.CreateLinkedTokenSource(whole.Token, waitDeadline.Token);
            try { outcome = await pending.Tap.Task.WaitAsync(waiting.Token); }
            catch (OperationCanceledException)
            {
                lock (_lock) pending.Status = State.Expired;
                outcome = "confirmation_timeout";
                return Result();
            }
            if (outcome != "copy") return Result();
            // The callback has already revalidated and consumed consent under _lock.
            // Do not send if cancellation won immediately after that authorization.
            if (whole.IsCancellationRequested) { outcome = "confirmation_timeout"; return Result(); }
            copyStarted = true;
            try
            {
                var id = await Limited(token => bot.CopyMessageAsync(destination, chat, messageId, token), 10, whole.Token);
                outcome = id == 0 ? "scheduled" : "copied";
                copiedId = id > 0 ? id : null;
            }
            catch (TelegramCopyException e)
            {
                outcome = MapError(e); retryAfter = e.Status == 429 ? e.RetryAfter : null;
                if (outcome == "telegram_rejected") description = e.Message[..Math.Min(200, e.Message.Length)];
            }
            catch (Exception) { outcome = "ambiguous"; }
            return Result();
        }
        catch (ObjectDisposedException) { outcome = "not_a_human_request"; return Result(); }
        catch (OperationCanceledException) { outcome = copyStarted ? "ambiguous" : "confirmation_timeout"; return Result(); }
        finally
        {
            if (pending is not null)
            {
                lock (_lock) pending.Status = copyStarted ? State.Done : State.Expired;
                if (pending.PromptId > 0 && bot is not null) await Cleanup(pending, bot, outcome);
                lock (_lock) { if (ReferenceEquals(_pending, pending)) _pending = null; }
            }
            counter.Record(outcome);
            logger.LogInformation("Message copy finished: outcome={outcome} destination_kind={kind} elapsed_ms={elapsed}",
                outcome, destination < 0 ? "group" : "private", (_clock.GetUtcNow() - started).TotalMilliseconds);
        }
        string Result()
        {
            var result = new Dictionary<string, object?>
            {
                ["ok"] = outcome is "copied" or "scheduled", ["outcome"] = outcome,
                ["destination_chat_id"] = destination, ["destination_message_id"] = copiedId,
                ["source_message_id"] = messageId, ["journal_recorded"] = false,
            };
            if (retryAfter is not null) result["retry_after"] = retryAfter;
            if (description is not null) result["description"] = description;
            if (outcome == "ambiguous") result["reason"] = "Ask the person to check the destination first. Do not retry.";
            return JsonSerializer.Serialize(result);
        }
    }

    private async Task<T> Limited<T>(Func<CancellationToken, Task<T>> call, int seconds, CancellationToken whole)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(seconds), _clock);
        using var token = CancellationTokenSource.CreateLinkedTokenSource(whole, limit.Token);
        token.Token.ThrowIfCancellationRequested();
        return await call(token.Token);
    }

    public async Task HandleCallbackAsync(CopyCallback callback)
    {
        Pending? pending;
        lock (_lock)
        {
            pending = _pending;
            if (pending is not null && pending.Status == State.Awaiting && pending.PromptId > 0
                && _clock.GetUtcNow() < pending.Started + TimeSpan.FromSeconds(38) && !pending.Token.IsCancellationRequested
                && callback.UserId == pending.Turn.UserId && callback.ChatId == pending.Chat
                && callback.MessageId == pending.PromptId
                && (callback.Data == $"cp1:{pending.Nonce}:y" || callback.Data == $"cp1:{pending.Nonce}:n"))
            {
                pending.CallbackId = callback.Id;
                var denied = Revalidate(pending);
                var outcome = denied ?? (callback.Data.EndsWith(":n", StringComparison.Ordinal) ? "cancelled" : "copy");
                pending.Status = outcome == "copy" ? State.Copying : State.Expired;
                pending.Tap.TrySetResult(outcome);
                return;
            }
        }
        var bot = client();
        if (bot is null) return;
        var remaining = pending is null ? TimeSpan.FromSeconds(2) : pending.Started + TimeSpan.FromSeconds(50) - _clock.GetUtcNow();
        if (remaining <= TimeSpan.Zero) return;
        using var limit = new CancellationTokenSource(remaining < TimeSpan.FromSeconds(2) ? remaining : TimeSpan.FromSeconds(2), _clock);
        try { await bot.AnswerCallbackAsync(callback.Id, "Not available", limit.Token); }
        catch (Exception) { logger.LogWarning("Message copy callback answer failed"); }
    }

    private async Task Cleanup(Pending pending, ITelegramCopyClient bot, string outcome)
    {
        // Created HERE, not at entry. Independent of both cancellation sources, but never
        // extends the original 50-second deadline. Both calls share this one cleanup budget.
        var remaining = pending.Started + TimeSpan.FromSeconds(50) - _clock.GetUtcNow();
        if (remaining <= TimeSpan.Zero) { logger.LogWarning("Message copy cleanup skipped: deadline"); return; }
        using var cleanup = new CancellationTokenSource(remaining < TimeSpan.FromSeconds(2) ? remaining : TimeSpan.FromSeconds(2), _clock);
        if (pending.CallbackId is { } callback)
        {
            try { await bot.AnswerCallbackAsync(callback, outcome is "not_a_human_request" or "destination_not_allowed" ? "Not available" : outcome, cleanup.Token); }
            catch (Exception) { logger.LogWarning("Message copy callback cleanup failed"); }
        }
        try { await bot.EditPromptAsync(pending.Chat, pending.PromptId, outcome == "confirmation_timeout" ? "expired" : outcome, cleanup.Token); }
        catch (Exception) { logger.LogWarning("Message copy prompt cleanup failed"); }
    }

    private static bool IsMissing(string description) => description.Contains("not found", StringComparison.OrdinalIgnoreCase);
    private static string MapError(TelegramCopyException error)
    {
        var text = error.Message;
        if (error.Status >= 500 || error.Status <= 0) return "ambiguous";
        if (error.Status == 429) return "rate_limited";
        if (error.Status == 403 || error.Status == 400 && text.Contains("chat not found", StringComparison.OrdinalIgnoreCase)) return "destination_unreachable";
        if (error.Status == 400)
        {
            if (text.Contains("protected", StringComparison.OrdinalIgnoreCase)) return "protected_content";
            if (text.Contains("can't be copied", StringComparison.OrdinalIgnoreCase) || text.Contains("cannot be copied", StringComparison.OrdinalIgnoreCase)) return "unsupported_message";
            if (IsMissing(text)) return "source_not_found";
        }
        return "telegram_rejected";
    }
}
