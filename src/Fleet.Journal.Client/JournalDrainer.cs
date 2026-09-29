using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Fleet.Journal.Client;

/// <summary>Heartbeat view of the journal on this agent.</summary>
public sealed record JournalHeartbeatSnapshot(
    bool Enabled, int SpoolDepth, long OldestAgeSeconds, long Dropped, int Dead, bool AuthFailed);

/// <summary>
/// Delivers spooled journal records to the Comms journal listener, one request at a time.
/// </summary>
/// <remarks>
/// <para>
/// <b>Not FIFO-blocking.</b> Each pass sends the OLDEST record whose <c>nextAttemptAt</c> has come;
/// a record that keeps failing only delays itself. Journal order comes from Telegram's
/// <c>message_id</c> and date, not arrival, so out-of-order delivery is harmless.
/// </para>
/// <para>
/// Per record: backoff from 1 s doubling to 5 min, a 15 s request timeout, and <c>dead/</c> after
/// 20 attempts <b>and</b> 24 h of transport or 5xx failures. Across records: 5 consecutive transport
/// or 5xx failures pause the drainer 30 s, so an outage is not hammered.
/// </para>
/// <para>
/// A pass that fails for one record — the spool cannot record its outcome, or anything else
/// throws — delays only that record: its attempt and backoff are kept in memory, and the next pass
/// moves on to the next due record.
/// </para>
/// <para>
/// ⚠️ Logs carry record ids, statuses and reason codes only — never text, never the token.
/// </para>
/// </remarks>
public sealed class JournalDrainer : BackgroundService
{
    internal static readonly TimeSpan InitialBackoff = TimeSpan.FromSeconds(1);
    internal static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan OutagePause = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan EndpointMissingPause = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan AuthStall = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan PersistentAge = TimeSpan.FromHours(24);
    internal static readonly TimeSpan DeadRetention = TimeSpan.FromDays(30);
    internal static readonly TimeSpan PolicyWarnInterval = TimeSpan.FromHours(1);
    internal static readonly TimeSpan IdleWait = TimeSpan.FromSeconds(30);
    internal const int PersistentAttempts = 20;
    internal const int OutageThreshold = 5;

    private readonly JournalSpool _spool;
    private readonly JournalHttpClient _client;
    private readonly JournalMediaUploader? _media;
    private readonly JournalCounters _counters;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;

    private readonly Dictionary<string, DateTimeOffset> _schedule = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _policyWarnedAt = new(StringComparer.Ordinal);

    // Attempts the spool could not record. Applied over what is on disk, so a record whose file
    // cannot be written still backs off instead of looking new on every pass.
    private readonly Dictionary<string, (int Attempts, DateTimeOffset? FirstAttemptAt)> _unsaved = new(StringComparer.Ordinal);
    private DateTimeOffset _pausedUntil = DateTimeOffset.MinValue;
    private DateTimeOffset _nextDeadSweep = DateTimeOffset.MinValue;
    private int _consecutiveFailures;
    private volatile bool _authFailed;

    public JournalDrainer(
        JournalSpool spool, JournalHttpClient client, JournalCounters counters,
        ILogger<JournalDrainer> logger, TimeProvider? time = null,
        JournalMediaUploader? media = null)
    {
        _spool = spool;
        _client = client;
        _counters = counters;
        _media = media;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    public bool AuthFailed => _authFailed;

    public JournalHeartbeatSnapshot Snapshot() => new(
        Enabled: true,
        SpoolDepth: _spool.PendingCount,
        OldestAgeSeconds: (long)(_spool.OldestPendingAge()?.TotalSeconds ?? 0),
        Dropped: _counters.LocallyDropped,
        Dead: _spool.DeadCount,
        AuthFailed: _authFailed);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan wait;
            try
            {
                wait = await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                // The type only; the journal must never take the agent down.
                _logger.LogWarning("journal drainer pass failed: {Error}", e.GetType().Name);
                wait = InitialBackoff;
            }

            if (wait <= TimeSpan.Zero) continue;

            try
            {
                // A new record wakes the drainer at once, unless it is paused.
                if (_time.GetUtcNow() >= _pausedUntil)
                    await _spool.Written.WaitAsync(wait, stoppingToken);
                else
                    await Task.Delay(wait, _time, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>
    /// One pass: at most one request. Returns how long to wait before the next pass; zero means
    /// go again at once.
    /// </summary>
    internal async Task<TimeSpan> RunOnceAsync(CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        if (now < _pausedUntil) return _pausedUntil - now;

        if (now >= _nextDeadSweep)
        {
            var swept = _spool.SweepDead(DeadRetention);
            if (swept > 0) _logger.LogInformation("journal spool removed {Count} dead record(s) past retention", swept);
            _nextDeadSweep = now + TimeSpan.FromHours(1);
        }

        RefreshSchedule();

        string? due = null;
        DateTimeOffset? soonest = null;
        foreach (var (id, next) in _schedule.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (next <= now)
            {
                due = id;
                break;
            }

            if (soonest is null || next < soonest) soonest = next;
        }

        if (due is null) return soonest is { } s ? s - now : IdleWait;

        var entry = _spool.Load(due);
        if (entry is null)
        {
            _schedule.Remove(due);
            _unsaved.Remove(due);
            return TimeSpan.Zero;
        }

        if (_unsaved.TryGetValue(entry.Id, out var kept) && kept.Attempts > entry.Attempts)
        {
            entry.Attempts = kept.Attempts;
            entry.FirstAttemptAt ??= kept.FirstAttemptAt;
        }

        entry.FirstAttemptAt ??= now;
        entry.Attempts++;

        try
        {
            // Bytes first, message second. An attachment that has bytes in the spool and no
            // uploadId is a record that has not been uploaded yet, and posting it would journal a
            // message whose file is silently missing. With no uploader there is nothing to upload:
            // the capture already wrote `media_disabled` on every attachment, which is the answer
            // the listener expects from a deployment with no bucket.
            if (_media is not null) await UploadMediaAsync(entry, ct);

            var body = JsonSerializer.SerializeToUtf8Bytes(entry.Record, JournalRecordJson.StoredOptions);
            var result = await _client.PostAsync(body, ct);
            Handle(entry, result, _time.GetUtcNow());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            Defer(entry, e);
        }

        return TimeSpan.Zero;
    }

    /// <summary>
    /// This record's pass failed. Keep its attempt and backoff in memory so it waits its turn, and
    /// let the next pass try the next due record.
    /// </summary>
    private void Defer(SpoolEntry entry, Exception e)
    {
        var next = _time.GetUtcNow() + Backoff(entry.Attempts);
        _unsaved[entry.Id] = (entry.Attempts, entry.FirstAttemptAt);
        _schedule[entry.Id] = next;

        // The type only: an exception message could carry a path.
        _logger.LogWarning("journal record {Id} could not be processed ({Error}); retrying it after backoff, other records continue",
            entry.Id, e.GetType().Name);
    }

    /// <summary>When the drainer will next try <paramref name="id"/>, as it currently believes. For tests.</summary>
    internal DateTimeOffset? ScheduledFor(string id) => _schedule.TryGetValue(id, out var at) ? at : null;

    private void RefreshSchedule()
    {
        var ids = _spool.PendingIds();
        var present = new HashSet<string>(ids, StringComparer.Ordinal);

        foreach (var gone in _schedule.Keys.Where(id => !present.Contains(id)).ToList())
            _schedule.Remove(gone);
        foreach (var gone in _unsaved.Keys.Where(id => !present.Contains(id)).ToList())
            _unsaved.Remove(gone);

        foreach (var id in ids)
        {
            if (_schedule.ContainsKey(id)) continue;
            var entry = _spool.Load(id);
            if (entry is not null) _schedule[id] = entry.NextAttemptAt;
        }
    }

    private void Handle(SpoolEntry entry, JournalSendResult result, DateTimeOffset now)
    {
        var status = result.Status;

        if (status is not (0 or >= 500)) _consecutiveFailures = 0;
        if (status != 401) _authFailed = false;

        switch (status)
        {
            case 200 or 201:
                Remove(entry);
                _spool.Delete(entry);
                _counters.Delivered();
                return;

            case 409 when result.Error == "media_disabled":
                if (RewriteUploadsAsMediaDisabled(entry))
                {
                    _counters.MediaDisabled();
                    entry.NextAttemptAt = now;
                    Save(entry);
                }
                else
                {
                    // Nothing left to rewrite, so resending would loop.
                    Dead(entry, "media_disabled");
                }
                return;

            case 409:
                Dead(entry, result.Error ?? "conflict");
                return;

            case 413:
                Dead(entry, "too_large");
                return;

            case 422 when result.Error is "excluded_chat" or "unknown_conversation":
                // A policy outcome, not an error: drop, count, and warn at most once an hour. An
                // agent-side excluded_chat means the two exclusion lists have drifted.
                Remove(entry);
                _spool.Delete(entry);
                _counters.Dropped(result.Error!);
                if (!_policyWarnedAt.TryGetValue(result.Error!, out var warned) || now - warned >= PolicyWarnInterval)
                {
                    _policyWarnedAt[result.Error!] = now;
                    _logger.LogWarning("journal record dropped by Comms policy ({Reason}); counted as journal_dropped", result.Error);
                }
                return;

            case 422:
                Dead(entry, result.Error ?? "invalid_record", result.Reason);
                return;

            case 400:
                Dead(entry, "bad_request");
                return;

            case 401:
                // Stall everything and keep every record: a token problem is not the records' fault.
                entry.Attempts--;
                _authFailed = true;
                _pausedUntil = now + AuthStall;
                _logger.LogError("journal ingest token refused (401); drainer stalled, {Depth} record(s) kept", _spool.PendingCount);
                return;

            case 404:
                entry.Attempts--;
                _pausedUntil = now + EndpointMissingPause;
                _counters.EndpointMissing();
                _logger.LogWarning("journal listener route missing (404); drainer paused 5 min, records kept");
                return;

            case 429:
                entry.NextAttemptAt = now + (result.RetryAfter is { } retry && retry > TimeSpan.Zero ? retry : InitialBackoff);
                entry.LastError = "http_429";
                Save(entry);
                return;

            case 0 or >= 500:
                entry.LastError = status == 0 ? result.Error ?? "transport" : $"http_{status}";

                // Counted before the spool write, so a record whose file cannot be written still
                // counts toward the outage pause.
                if (++_consecutiveFailures >= OutageThreshold)
                {
                    _consecutiveFailures = 0;
                    _pausedUntil = now + OutagePause;
                    _logger.LogWarning("journal listener failing ({Error}); drainer paused 30 s", entry.LastError);
                }

                if (entry.Attempts >= PersistentAttempts && now - entry.FirstAttemptAt >= PersistentAge)
                {
                    Dead(entry, "persistent_5xx");
                }
                else
                {
                    entry.NextAttemptAt = now + Backoff(entry.Attempts);
                    Save(entry);
                }
                return;

            default:
                Dead(entry, $"http_{status}");
                return;
        }
    }

    /// <summary>1 s, doubling per attempt, capped at 5 min.</summary>
    internal static TimeSpan Backoff(int attempts)
    {
        var exponent = Math.Clamp(attempts - 1, 0, 20);
        var seconds = InitialBackoff.TotalSeconds * Math.Pow(2, exponent);
        return TimeSpan.FromSeconds(Math.Min(seconds, MaxBackoff.TotalSeconds));
    }

    /// <summary>
    /// Proves the bytes for every attachment that has them in the spool and no upload id yet.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Idempotent by the record's own content.</b> An attachment that already carries an
    /// <c>uploadId</c> is skipped, so a record that was posted, deferred, and retried does not
    /// declare twice. If the message post itself failed, the object is left <c>uploaded</c> and the
    /// next pass reuses the same upload id — which is what makes AC 3's "one committed object, one
    /// message, no duplicate" true after a 503.
    /// </para>
    /// <para>
    /// A declined upload writes a <c>not_archived(&lt;reason&gt;)</c> and drops the media file's
    /// reference: the message still goes, and the reason says what is missing. A retryable failure
    /// throws, so the record backs off and the attachment keeps its chance at the bytes.
    /// </para>
    /// </remarks>
    private async Task UploadMediaAsync(SpoolEntry entry, CancellationToken ct)
    {
        if (entry.Record["attachments"] is not JsonArray attachments) return;

        var changed = false;

        foreach (var node in attachments)
        {
            if (node is not JsonObject attachment) continue;

            // An `uploadId` means this attachment was already proven — a retry must not declare
            // twice. A reason that is NOT the capture's `media_disabled` placeholder means the
            // caller already knew the bytes were never coming (over the Bot API cap, a declined
            // kind), and that answer is more specific than anything the drain can produce.
            if (HasText(attachment, "uploadId")) continue;
            if (HasText(attachment, "notArchivedReason")
                && attachment["notArchivedReason"]!.GetValue<string>() != "media_disabled") continue;

            var ordinal = Int(attachment, "ordinal") ?? -1;

            var mimeType = HasText(attachment, "mimeType")
                ? attachment["mimeType"]!.GetValue<string>()
                : "application/octet-stream";

            var spoolPath = ordinal < 0 ? null : _spool.MediaPath(entry.Id, ordinal);

            // The capture spools what it could read. When it could not — a photo the agent skipped
            // on its own size limit, a hardlink whose source the attachment sweeper already pruned
            // — the file is still on disk where it was written, and this is the last moment where
            // hashing it before the message is still possible.
            if (spoolPath is not null && !File.Exists(spoolPath)
                && entry.MediaSources.TryGetValue(ordinal, out var source))
            {
                _spool.TryStageMedia(entry.Id, ordinal, source);
            }

            var upload = await _media!.UploadAsync(
                spoolPath: spoolPath,
                bytes: null,
                platformFileSize: Long(attachment, "byteSize"),
                mimeType: mimeType,
                ct: ct);

            if (upload.IsUploaded)
            {
                attachment["uploadId"] = upload.UploadId;
                attachment["uploadSha256"] = upload.Sha256;
            }
            else
            {
                attachment["notArchivedReason"] = ReasonCode(upload.Reason);
                changed = true;
                _counters.MediaReason(ReasonCode(upload.Reason));
                continue;
            }

            changed = true;
        }

        if (changed) Save(entry);
    }

    private static bool HasText(JsonObject attachment, string name) =>
        attachment[name] is { } value
        && value.GetValueKind() == JsonValueKind.String
        && value.GetValue<string>().Length > 0;

    /// <summary>
    /// A JSON number as an <see cref="int"/>, or null. A record whose field is absent, not a
    /// number, or too wide to be one is treated as absent — a malformed attachment declines its
    /// bytes rather than throwing the pass.
    /// </summary>
    private static int? Int(JsonObject attachment, string name) =>
        Number(attachment, name, out var value) && value is >= int.MinValue and <= int.MaxValue
            ? (int)value
            : null;

    private static long? Long(JsonObject attachment, string name) =>
        Number(attachment, name, out var value) ? value : null;

    private static bool Number(JsonObject attachment, string name, out long value)
    {
        value = 0;
        if (attachment[name] is not { } node || node.GetValueKind() != JsonValueKind.Number) return false;

        try
        {
            value = node.GetValue<JsonElement>().GetInt64();
            return true;
        }
        catch (Exception e) when (e is InvalidOperationException or OverflowException or FormatException)
        {
            return false;
        }
    }

    /// <summary>The wire spelling of a decline. Never a made-up code.</summary>
    internal static string ReasonCode(JournalMediaReason reason) => reason switch
    {
        JournalMediaReason.MediaDisabled => "media_disabled",
        JournalMediaReason.OverBotApiLimit => "over_bot_api_limit",
        JournalMediaReason.OverSizeCap => "over_size_cap",
        JournalMediaReason.UnsupportedKind => "unsupported_kind",
        JournalMediaReason.DownloadFailed => "download_failed",
        JournalMediaReason.SourceExpired => "source_expired",
        _ => "media_disabled",
    };

    /// <summary>
    /// Every attachment that references stored bytes becomes <c>not_archived(media_disabled)</c>.
    /// Returns false when there was nothing to rewrite.
    /// </summary>
    private static bool RewriteUploadsAsMediaDisabled(SpoolEntry entry)
    {
        if (entry.Record["attachments"] is not System.Text.Json.Nodes.JsonArray attachments) return false;

        var changed = false;
        foreach (var node in attachments)
        {
            if (node is not System.Text.Json.Nodes.JsonObject attachment) continue;

            var referenced = false;
            foreach (var reference in new[] { "uploadId", "objectId", "sha256" })
            {
                if (attachment.Remove(reference)) referenced = true;
            }

            if (!referenced) continue;
            attachment["notArchivedReason"] = "media_disabled";
            changed = true;
        }

        return changed;
    }

    private void Dead(SpoolEntry entry, string reason, string? field = null)
    {
        Remove(entry);
        _spool.MoveToDead(entry, field is null ? reason : $"{reason}:{field}");
        _counters.Dead(reason);
        _logger.LogError("journal record {Id} moved to dead/ ({Reason}{Field})",
            entry.Id, reason, field is null ? "" : $", field {field}");
    }

    private void Save(SpoolEntry entry)
    {
        _spool.Save(entry);
        _schedule[entry.Id] = entry.NextAttemptAt;
        _unsaved.Remove(entry.Id);
    }

    private void Remove(SpoolEntry entry)
    {
        _schedule.Remove(entry.Id);
        _unsaved.Remove(entry.Id);
    }
}
