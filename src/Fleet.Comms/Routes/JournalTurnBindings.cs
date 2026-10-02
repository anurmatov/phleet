using System.Diagnostics.Metrics;
using System.Text.Json;
using Fleet.Conversations.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Fleet.Comms.Routes;

/// <summary>Process-local turn scope on the authenticated journal listener only.</summary>
public sealed class JournalTurnBindings(TimeProvider time)
{
    public const string Path = "/journal/v1/turn-binding";
    public const int MaxSubjects = 4096;
    public const int MaxBodyBytes = 1024;
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(180);
    private static readonly Meter Meter = new("Fleet.Conversations", "1.0.0");
    private static readonly Counter<long> Writes =
        Meter.CreateCounter<long>("fleet_comms_journal_turn_binding_total", "bindings");
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    /// <summary>Accept a validated state, preserving the last four process epochs.</summary>
    public int Put(string subject, JournalTurnBinding binding)
    {
        lock (_gate)
        {
            var now = time.GetUtcNow();
            if (!_entries.TryGetValue(subject, out var entry))
            {
                if (_entries.Count >= MaxSubjects)
                    foreach (var expired in _entries.Where(pair => pair.Value.Expires <= now)
                                 .Select(pair => pair.Key).ToArray())
                        _entries.Remove(expired);
                if (_entries.Count >= MaxSubjects) return 503;
                entry = new Entry(binding, now + Ttl);
                _entries.Add(subject, entry);
                return 204;
            }

            if (binding.Epoch != entry.Binding.Epoch)
            {
                if (entry.Epochs.Contains(binding.Epoch, StringComparer.Ordinal)) return 409;
                entry.Epochs.Enqueue(binding.Epoch);
                while (entry.Epochs.Count > 4) entry.Epochs.Dequeue();
            }
            else if (binding.Seq < entry.Binding.Seq
                     || (binding.Seq == entry.Binding.Seq && binding != entry.Binding))
                return 409;

            entry.Binding = binding;
            entry.Expires = now + Ttl;
            return 204;
        }
    }

    /// <summary>Null for expired, unbound or unknown subjects; no stale scope can be read.</summary>
    public JournalTurnBinding? Get(string subject)
    {
        lock (_gate)
            return _entries.TryGetValue(subject, out var entry) && entry.Expires > time.GetUtcNow()
                && entry.Binding.State == "bound" ? entry.Binding : null;
    }

    public (int Active, int Expired) Counts()
    {
        lock (_gate)
        {
            var now = time.GetUtcNow();
            var bound = _entries.Values.Where(entry => entry.Binding.State == "bound").ToArray();
            var expired = bound.Count(entry => entry.Expires <= now);
            return (bound.Length - expired, expired);
        }
    }

    public static void Map(WebApplication app)
    {
        app.MapPut(Path, async Task<IResult> (HttpContext context, JournalTurnBindings bindings, CancellationToken ct) =>
        {
            // JournalAuth already verified the ingest subject before any body read.
            var subject = (string)context.Items[JournalAuth.SubjectItem]!;
            JournalTurnBinding? binding = null;
            if (context.Request.ContentLength is null or <= MaxBodyBytes)
            {
                var bytes = new byte[MaxBodyBytes + 1];
                var count = 0;
                while (count < bytes.Length)
                {
                    var read = await context.Request.Body.ReadAsync(bytes.AsMemory(count), ct);
                    if (read == 0) break;
                    count += read;
                }
                if (count <= MaxBodyBytes) binding = Parse(bytes.AsMemory(0, count));
            }
            var status = binding is null ? 400 : bindings.Put(subject, binding);
            var result = status switch { 204 => "accepted", 409 => "stale", 503 => "binding_capacity", _ => "invalid_argument" };
            Writes.Add(1, new KeyValuePair<string, object?>("result", result));
            return status == 204 ? Results.NoContent()
                : Results.Json(new { error = result }, statusCode: status);
        });
    }

    private static JournalTurnBinding? Parse(ReadOnlyMemory<byte> body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in root.EnumerateObject())
                if (property.Name is not ("epoch" or "seq" or "state" or "chatKind" or "botId" or "chatId")
                    || !names.Add(property.Name)) return null;
            if (!root.TryGetProperty("epoch", out var epoch) || epoch.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(epoch.GetString()) || epoch.GetString()!.Length > 128
                || !root.TryGetProperty("seq", out var seq) || !seq.TryGetInt64(out var sequence) || sequence < 1
                || !root.TryGetProperty("state", out var state) || state.ValueKind != JsonValueKind.String)
                return null;
            if (state.GetString() == "unbound")
                return names.Count == 3 ? new(epoch.GetString()!, sequence, "unbound") : null;
            if (state.GetString() != "bound" || names.Count != 6
                || !root.TryGetProperty("chatKind", out var kind) || kind.ValueKind != JsonValueKind.String
                || kind.GetString() is not ("private" or "group" or "supergroup")
                || !root.TryGetProperty("botId", out var bot) || !bot.TryGetInt64(out var botId) || botId <= 0
                || !root.TryGetProperty("chatId", out var chat) || !chat.TryGetInt64(out var chatId) || chatId == 0)
                return null;
            return new(epoch.GetString()!, sequence, "bound", kind.GetString(), botId, chatId);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException) { return null; }
    }

    private sealed class Entry(JournalTurnBinding binding, DateTimeOffset expires)
    {
        public JournalTurnBinding Binding = binding;
        public DateTimeOffset Expires = expires;
        public Queue<string> Epochs { get; } = new([binding.Epoch]);
    }
}
