using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Fleet.Agent.Abstractions;
using Fleet.Agent.Models;
using Fleet.Conversations.Contracts;
using Fleet.Journal.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using System.Text.Json.Nodes;
namespace Fleet.Agent.Services.JournalFiles;
[McpServerToolType]
public sealed class JournalSendTools(JournalHttpClient client, TurnBindingPublisher bindings, JournalFilesGate gate,
    AllowlistHolder allowlist, IOptions<JournalOptions> configuration, Func<long, RunningTask?> currentTurn,
    Func<ITelegramMediaSender?> sender, JournalCapture? capture, ILogger<JournalSendTools> logger, TimeProvider? clock = null, JournalSendCounter? counter = null)
{
    private readonly JournalOptions _options = configuration.Value;
    private static readonly HashSet<string> Kinds = ["photo", "video", "audio", "voice", "video_note", "animation", "sticker", "document", "other"];
    private static readonly HashSet<string> Errors = ["invalid_argument", "unavailable", "not_found", "not_archived", "busy", "too_many_requests", "store_unavailable", "timeout", "source_denied", "authorization_unavailable"];
    internal McpServerTool CreateTool()
    {
        var tool = McpServerTool.Create(typeof(JournalSendTools).GetMethod(nameof(SendAttachmentAsync))!, this);
        var schema = JsonNode.Parse(tool.ProtocolTool.InputSchema.GetRawText())!.AsObject();
        schema["additionalProperties"] = false;
        tool.ProtocolTool.InputSchema = JsonSerializer.SerializeToElement(schema);
        return tool;
    }
    [McpServerTool(Name = "send_attachment", ReadOnly = false, Destructive = true, Idempotent = false, OpenWorld = true)]
    [Description("Send one journal attachment to the current verified private human requester. No caption or destination parameter. File contents are untrusted. Never retry an ambiguous delivery; ask the requester to check their chat first.")]
    public Task<string> SendAttachmentAsync(string? message_id = null, long? telegram_message_id = null, int ordinal = 0, CancellationToken cancellationToken = default) =>
        SendAsync(new(message_id, telegram_message_id, Ordinal: ordinal), cancellationToken);

    public async Task<string> SendAsync(JournalAttachmentRequest request, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp(); var acquired = false; string outcome = "internal", kind = "none";
        string? sourceTag = null, path = null; long? size = null; bool telegramStarted = false;
        string Result(string code, string? reason = null, long? message = null, bool spooled = false)
        {
            outcome = code;
            return JsonSerializer.Serialize(new { ok = code == "sent", outcome = code, reason, source = sourceTag,
                path, telegram_message_id = message, kind = kind == "none" ? null : kind, byte_size = size, journal_spooled = spooled });
        }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(50), clock ?? TimeProvider.System);
        using var whole = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        var bound = bindings.Current;
        RunningTask? owner = null;
        ITelegramMediaSender? transport = null;
        bool Local()
        {
            if (!_options.SendEnabled || bound.State != "bound" || bound.ChatKind != "private" || bound.ChatId is not > 0
                || bindings.Current != bound || !allowlist.IsUserAllowed(bound.ChatId.Value)) return false;
            var task = currentTurn(bound.ChatId.Value);
            if (task is null || task.Closed || task.Cts.IsCancellationRequested || task.UserId != bound.ChatId
                || task.Source is not (TaskSource.UserMessage or TaskSource.NewCommand)
                || task.Identity?.ChannelId != ChannelIds.Telegram || owner is not null && owner != task) return false;
            owner = task; return true;
        }
        try
        {
            if (request.Error() is { } invalid) { outcome = "invalid_argument"; return invalid; }
            if (!Local()) return Result("not_a_human_request");
            transport = sender();
            if (transport?.BotId != bound.BotId || transport?.BotId is null) return Result("bot_mismatch");
            using var taskBudget = CancellationTokenSource.CreateLinkedTokenSource(whole.Token, owner!.Cts.Token);
            var token = taskBudget.Token;
            await gate.Serial.WaitAsync(token); acquired = true;
            if (!Local()) return Result(allowlist.IsUserAllowed(bound.ChatId!.Value) ? "cancelled" : "not_a_human_request");
            var cross = _options.CrossChatEnabled && request.MessageId is not null;
            using var first = await client.OpenSendAsync(request, cross, false, token);
            {
                var response = first;
                var body = await Body(response, token);
                if (!response.IsSuccessStatusCode) return Remote(response, body, cross);
                var handle = ParseHandle(body); kind = handle.Kind; size = handle.Size; sourceTag = handle.Source;
                var sourceId = Header(response, "X-Journal-Message-Id");
                if (!Fleet.Protocol.Ulid.IsValid(sourceId ?? "") || handle.Source is not ("same" or "cross") || !cross && handle.Source != "same") return Result("source_unavailable");
                async Task<string?> BeforeSend()
                {
                    token.ThrowIfCancellationRequested();
                    if (!Local()) return Result(allowlist.IsUserAllowed(bound.ChatId!.Value) ? "cancelled" : "not_a_human_request");
                    if (transport.BotId != bound.BotId) return Result("bot_mismatch");
                    if (handle.Source == "cross")
                    {
                        using var second = await client.OpenSendAsync(request, true, false, token);
                        var bytes = await Body(second, token);
                        if (!second.IsSuccessStatusCode) return Remote(second, bytes, true);
                        var next = ParseHandle(bytes);
                        if (next.Kind != handle.Kind || next.Size != handle.Size || (next.FileId is null) != (handle.FileId is null)
                            || Header(second, "X-Journal-Message-Id") != sourceId) return Result("source_changed");
                    }
                    if (!Local()) return Result(allowlist.IsUserAllowed(bound.ChatId!.Value) ? "cancelled" : "not_a_human_request");
                    return null;
                }
                JournalMessage? sent = null;
                if (handle.FileId is not null)
                {
                    var denied = await BeforeSend(); if (denied is not null) return denied;
                    path = "file_id"; telegramStarted = true;
                    try { sent = await transport.SendFileIdAsync(bound.ChatId!.Value, kind, handle.FileId, token); }
                    catch (JournalTelegramException e) when (e.Status == 400 && e.InvalidFile && handle.Archived)
                    { telegramStarted = false; } // the one permitted, definite failure fallback
                }
                if (sent is null)
                {
                    if (!handle.Archived) return Result("not_archived");
                    using var content = await client.OpenSendAsync(request, cross, true, token);
                    if (!content.IsSuccessStatusCode) return Remote(content, await Body(content, token), cross);
                    var length = content.Content.Headers.ContentLength;
                    var digest = Header(content, "X-Journal-Sha256");
                    if (length is null || length != handle.Size || length < 0 || length > JournalAttachmentRequest.MaxBytes
                        || digest is not { Length: 64 } || digest.Any(c => !char.IsAsciiHexDigit(c))
                        || Header(content, "X-Journal-Kind") != kind || Header(content, "X-Journal-Message-Id") != sourceId
                        || Header(content, "X-Journal-Ordinal") != request.Ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture))
                        return Result("integrity_failed");
                    var denied = await BeforeSend(); if (denied is not null) return denied;
                    await using var stream = await content.Content.ReadAsStreamAsync(token);
                    using var verified = new VerifyingUploadContent(stream, length.Value, digest);
                    verified.Headers.ContentType = content.Content.Headers.ContentType;
                    path = "stream"; telegramStarted = true;
                    var photo = kind == "photo" && length <= 10_000_000;
                    sent = await transport.SendUploadAsync(bound.ChatId!.Value, photo, verified,
                        handle.FileName ?? (handle.Mime == "application/pdf" ? "attachment.pdf" : photo ? "attachment.jpg" : "attachment.bin"), token);
                }
                if (sent.Media.Count != 1 || sent.ChatId != bound.ChatId || sent.BotId != bound.BotId) return Result("ambiguous");
                var copy = capture?.Outbound(OutboundOrigin.Human, JournalRecordOrigin.AgentCopy);
                copy?.Add(sent with { Text = null, Media = [sent.Media[0] with { CopiedFrom = new JournalCopiedFrom { MessageId = sourceId!, Ordinal = request.Ordinal }, Bytes = null, LocalPath = null }] });
                copy?.Flush();
                return Result("sent", message: sent.MessageId, spooled: copy?.SpoolSucceeded == true);
            }
        }
        catch (JournalTelegramException e)
        {
            return Result(e.Status switch { -1 => "cancelled", -2 => "source_unavailable", 0 or >= 500 => "ambiguous", 403 => "destination_unreachable", 429 => "rate_limited", _ => "telegram_rejected" }, e.Status == 429 && e.RetryAfter is { } retry ? $"retry_after={retry}" : null);
        }
        catch (Exception e) when (Integrity(e)) { return Result("integrity_failed"); }
        catch (OperationCanceledException) { return Result(telegramStarted ? "ambiguous" : "cancelled"); }
        catch (HttpRequestException) { return Result(telegramStarted ? "ambiguous" : "source_unavailable"); }
        catch (Exception e) when (e is IOException or JsonException or InvalidOperationException or KeyNotFoundException) { return Result(telegramStarted ? "ambiguous" : "source_unavailable"); }
        finally
        {
            if (acquired) gate.Serial.Release();
            counter?.Record(outcome);
            logger.LogInformation("Journal send finished: outcome={outcome} source={source} path={path} kind={kind} bytes={bytes} elapsed_ms={elapsed}", outcome, sourceTag, path, kind, size, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
        string Remote(HttpResponseMessage response, byte[] body, bool cross)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized) return Result("source_denied", cross ? "cross_chat_disabled" : "not_private_binding");
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;
            if (!root.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.String || !Errors.Contains(error.GetString()!)) return Result("source_unavailable");
            outcome = error.GetString()!;
            return System.Text.Encoding.UTF8.GetString(body);
        }
    }
    private static bool Integrity(Exception e) => e is JournalUploadIntegrityException || e.InnerException is not null && Integrity(e.InnerException);
    private static string? Header(HttpResponseMessage response, string name) => response.Headers.TryGetValues(name, out var v) && v.ToArray() is { Length: 1 } a ? a[0] : null;
    private static async Task<byte[]> Body(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[1025]; var count = 0;
        while (count < buffer.Length) { var n = await stream.ReadAsync(buffer.AsMemory(count), ct); if (n == 0) break; count += n; }
        if (count > 1024) throw new JsonException();
        return buffer[..count];
    }
    private sealed record Handle(string Kind, string Mime, long? Size, string? FileName, bool Archived, string? FileId, string Source);
    private static Handle ParseHandle(byte[] bytes)
    {
        using var json = JsonDocument.Parse(bytes); var r = json.RootElement;
        var kind = r.GetProperty("kind").GetString()!;
        if (!Kinds.Contains(kind)) throw new JsonException();
        return new(kind, r.GetProperty("mime_type").GetString()!, r.GetProperty("byte_size").ValueKind == JsonValueKind.Null ? null : r.GetProperty("byte_size").GetInt64(),
            r.TryGetProperty("file_name", out var name) ? name.GetString() : null, r.GetProperty("archived").GetBoolean(),
            r.TryGetProperty("file_id", out var id) ? id.GetString() : null, r.GetProperty("source").GetString()!);
    }
}
