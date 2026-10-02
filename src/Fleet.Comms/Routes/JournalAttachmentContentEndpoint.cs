using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Fleet.Conversations.Contracts;
using Fleet.Conversations.Journal;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Fleet.Comms.Routes;

/// <summary>Read-authorized, bound, streamed delivery. Never writes rows, files or URLs.</summary>
public sealed class JournalAttachmentContentEndpoint(IJournalAttachmentSource? source, IJournalObjectStore? objects,
    JournalReadGrants grants, JournalBindingScope scope, JournalRuntimeStats stats, TimeProvider? time = null)
{
    private readonly SemaphoreSlim _global = new(4, 4);
    private readonly ConcurrentDictionary<string, byte> _subjects = new(StringComparer.Ordinal);
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private static readonly JsonSerializerOptions Json = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    public static void Map(WebApplication app, IJournalObjectStore? objects, JournalRuntimeStats stats, TimeProvider? time = null)
    {
        var endpoint = new JournalAttachmentContentEndpoint(app.Services.GetRequiredService<IJournalReadStore>() as IJournalAttachmentSource,
            objects, app.Services.GetRequiredService<JournalReadGrants>(), app.Services.GetRequiredService<JournalBindingScope>(), stats, time);
        app.MapPost(JournalAttachmentRequest.ContentPath, endpoint.HandleAsync);
    }
    public async Task HandleAsync(HttpContext context)
    {
        // JournalAuth has already authenticated and reserved the subject's general request slot.
        var subject = (string)context.Items[JournalAuth.SubjectItem]!;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40), _time);
        using var whole = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, deadline.Token);
        var ct = whole.Token;
        string result = "internal";
        async Task Refuse(int status, string body, string code)
        { result = code; context.Response.StatusCode = status; context.Response.ContentType = "application/json"; await context.Response.WriteAsync(body, ct); }
        async Task Unavailable(string reason) => await Refuse(409, JournalAttachmentRequest.Unavailable(reason), reason);
        try
        {
            var bytes = new byte[1025]; var count = 0;
            while (count < bytes.Length)
            { var read = await context.Request.Body.ReadAsync(bytes.AsMemory(count), ct); if (read == 0) break; count += read; }
            JournalAttachmentRequest? request = null;
            if (count <= 1024)
                try { request = JsonSerializer.Deserialize<JournalAttachmentRequest>(bytes.AsSpan(0, count), Json); } catch (JsonException) { }
            if (request is null) { await Refuse(400, JournalAttachmentRequest.Invalid("body"), "invalid_argument"); return; }
            if (request.Error() is { } error) { await Refuse(400, error, "invalid_argument"); return; }
            if (objects is null) { await Unavailable("media_disabled"); return; }
            var binding = scope.Resolve(subject);
            if (binding.Reason is { } reason) { await Unavailable(reason); return; }
            if (source is null) { await Refuse(503, "{\"error\":\"store_unavailable\",\"retryable\":true}", "store_unavailable"); return; }
            var row = await source.FindAttachmentAsync(grants.ReaderFor(subject), binding.Key!, request.MessageId, request.TelegramMessageId, request.Ordinal, ct);
            if (row is null) { await Refuse(404, "{\"error\":\"not_found\"}", "not_found"); return; }
            if (row.AttachmentState == "not_archived")
            { await Refuse(422, JsonSerializer.Serialize(new { error = "not_archived", reason = row.NotArchivedReason }), "not_archived"); return; }
            if (row.AttachmentState == "lost") { await Unavailable("attachment_lost"); return; }
            if (row.ObjectState is not ("uploaded" or "committed") || row.ObjectKey is null)
            { await Unavailable("object_missing"); return; }
            if (!_subjects.TryAdd(subject, 0)) { await Refuse(429, "{\"error\":\"busy\",\"retryable\":true}", "busy"); return; }
            var global = false;
            try
            {
                global = _global.Wait(0);
                if (!global) { await Refuse(429, "{\"error\":\"busy\",\"retryable\":true}", "busy"); return; }
                stats.AttachmentFetchEntered();
                JournalObjectReadResult? opened;
                using (var openDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(30), _time))
                using (var open = CancellationTokenSource.CreateLinkedTokenSource(ct, openDeadline.Token))
                    opened = await objects.GetAsync(row.ObjectKey, open.Token);
                if (opened is null) { await Unavailable("object_missing"); return; }
                await using var stream = opened.Content;
                if (row.ByteSize is not { } length || length < 0 || length > JournalAttachmentRequest.MaxBytes
                    || opened.ByteSize != length || row.ObjectByteSize != length || row.Sha256 is not { Length: 64 }
                    || row.Sha256 != row.ObjectSha256)
                { await Unavailable("integrity_failed"); return; }
                context.Response.ContentType = row.MimeType;
                context.Response.ContentLength = length;
                context.Response.Headers["X-Journal-Message-Id"] = row.MessageId;
                context.Response.Headers["X-Journal-Ordinal"] = row.Ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture);
                context.Response.Headers["X-Journal-Kind"] = row.Kind;
                context.Response.Headers["X-Journal-Sha256"] = row.Sha256;
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[64 * 1024]; long sent = 0;
                // Keep the final chunk back until the digest is checked, so a bad stream never completes.
                var pending = Array.Empty<byte>();
                while (true)
                {
                    var read = await stream.ReadAsync(buffer, ct); if (read == 0) break;
                    sent += read; if (sent > length) { result = "integrity_failed"; context.Abort(); return; }
                    hash.AppendData(buffer.AsSpan(0, read));
                    if (pending.Length > 0) await context.Response.Body.WriteAsync(pending, ct);
                    pending = buffer.AsSpan(0, read).ToArray();
                }
                if (sent != length || !string.Equals(Convert.ToHexStringLower(hash.GetHashAndReset()), row.Sha256, StringComparison.Ordinal))
                { result = "integrity_failed"; context.Abort(); return; }
                if (pending.Length > 0) await context.Response.Body.WriteAsync(pending, ct);
                stats.AttachmentFetchBytes(sent); result = "ok";
            }
            finally
            {
                if (global) { stats.AttachmentFetchExited(); _global.Release(); }
                _subjects.TryRemove(subject, out _);
            }
        }
        catch (Exception e) when (e is JournalObjectStoreUnavailableException or JournalStoreUnavailableException or OperationCanceledException or IOException)
        {
            if (context.Response.HasStarted || deadline.IsCancellationRequested || context.RequestAborted.IsCancellationRequested)
            { result = "store_unavailable"; context.Abort(); }
            else
            {
                context.Response.Clear(); result = "store_unavailable"; context.Response.StatusCode = 503;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync("{\"error\":\"store_unavailable\",\"retryable\":true}", context.RequestAborted);
            }
        }
        finally { stats.RecordAttachmentFetch(result); }
    }
}
