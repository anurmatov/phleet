using Fleet.Conversations.Contracts;

namespace Fleet.Conversations.Journal;

/// <summary>
/// What an ingest does with the uploads its record names.
/// </summary>
/// <param name="Refusal">
/// A complete answer to return when the record cannot be written at all, or null to go ahead.
/// </param>
/// <param name="Resolved">
/// Per attachment ordinal, the object id the attachment row will point at. A dedup loser maps to
/// the <b>winner's</b> id, which is what makes two observers of one photo share one object.
/// </param>
/// <param name="Digests">
/// Per object id, the digest to store on the attachment and stamp on the object. For a dedup
/// loser that is the <b>winner's</b> digest — which is what makes the stored digest true of the
/// bytes the attachment actually resolves to.
/// </param>
/// <param name="Losers">
/// Objects this submission proved that lost dedup. Their rows become aborted inside the same
/// transaction as the message; the 24-hour sweeper removes their unreferenced bytes.
/// </param>
/// <param name="LoserKeys">
/// The same objects with the bucket keys that hold their bytes.
/// </param>
/// <param name="Proved">
/// The object ids belonging to THIS subject — every upload id in the record, winner or loser. These
/// are the only rows this ingest may write. <see cref="Resolved"/> can name a fifth object the
/// subject does not own (the dedup winner), and writing to that row would be writing someone
/// else's object.
/// </param>
internal readonly record struct MediaPlan(
    JournalIngestResult? Refusal,
    IReadOnlyDictionary<int, string> Resolved,
    IReadOnlyDictionary<string, string> Digests,
    IReadOnlyList<string> Losers,
    IReadOnlyList<(string Id, string Key)> LoserKeys,
    IReadOnlySet<string> Proved);

public partial class MySqlJournalStore
{
    /// <summary>
    /// Reads every object the record names and decides, for each, whether this subject may attach it
    /// and which object the attachment points at.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Three refusals, and all three are the same answer.</b> Unknown id, foreign-owned row, and
    /// a row in any state but <c>uploaded</c> or <c>committed</c> all produce
    /// <c>upload_incomplete</c> with the ordinal. A subject learning <i>which</i> of those it hit
    /// learns whether another runtime has already uploaded a given digest, which is exactly the
    /// oracle the lifecycle rule exists to deny.
    /// </para>
    /// <para>
    /// A row already <c>committed</c> is accepted only when its digest matches what this subject
    /// proved. That is the dedup case: subject B uploaded its own bytes, and B's digest is the one
    /// the committed object carries. Naming a digest without ever holding the bytes cannot produce a
    /// matching row, because the only rows this subject owns are the ones it declared.
    /// </para>
    /// </remarks>
    private MediaPlan ResolveUploads(JournalRecord record, string observer)
    {
        var resolved = new Dictionary<int, string>();
        var losers = new List<string>();

        var digests = new Dictionary<string, string>(StringComparer.Ordinal);

        var named = record.Attachments
            .Where(a => a.UploadId is not null)
            .ToArray();

        if (named.Length == 0)
            return new MediaPlan(null, resolved, digests, losers, [], EmptyIds);

        // The route refuses media_disabled before a record with an uploadId can reach the store;
        // this is the second gate, for a store built without media (a test host, or a deployment
        // that enabled the journal and did not wire the bucket).
        if (Objects is null)
            return new MediaPlan(RefuseUploads(named.Select(a => a.Ordinal)), resolved, digests, losers, [], EmptyIds);

        Dictionary<string, JournalObjectRow> rows = new(StringComparer.Ordinal);

        // One connection for the whole read: N attachments must not be N pools' worth of
        // connections, and one snapshot is the answer the whole record is judged against.
        //
        // ⚠️ Opened here and disposed here — never cached on the instance. MySqlJournalStore is a
        //    singleton and ingest is concurrent, so a field holding "the" read connection would
        //    hand one open MySqlConnection to two requests at once. Two commands on one connection
        //    interleave their result streams: the second ExecuteReader throws, or worse, a reader
        //    reads the other command's rows. `using` was already forcing per-call disposal on
        //    every path, so the cache bought nothing but the race. MySqlConnector pools the
        //    physical socket underneath, so opening per call is not per-call TCP.
        using var connection = OpenSync();
        foreach (var attachment in named)
        {
            if (!rows.TryGetValue(attachment.UploadId!, out var row))
            {
                row = ReadRow(connection, attachment.UploadId!);
                if (row is not null) rows[attachment.UploadId!] = row;
            }

            // Unknown, or owned by someone else — one answer, and it is not a 404-vs-409 tell.
            if (row is null || !string.Equals(row.Owner, observer, StringComparison.Ordinal))
                return new MediaPlan(RefuseUploads(named.Select(a => a.Ordinal)), resolved, digests, losers, [], EmptyIds);

            // Not yet uploaded, aborted, or swept. `deleting` is past its message's retention, so
            // attaching to it would point a new message at bytes on their way out.
            if (row.State is not (JournalObjectState.Uploaded or JournalObjectState.Committed))
                return new MediaPlan(RefuseUploads(named.Select(a => a.Ordinal)), resolved, digests, losers, [], EmptyIds);

            // ⚠️ The digest the subject DECLARED must equal the one its bytes produced at PUT time.
            //    This is the whole "bytes are always proven" rule seen from the commit side: the
            //    only way to attach an object is to have uploaded bytes that hashed to it, so
            //    naming a digest can never reach an object someone else uploaded.
            if (row.ByteSize != attachment.ByteSize
                || !string.Equals(row.Sha256, attachment.UploadSha256, StringComparison.OrdinalIgnoreCase))
                return new MediaPlan(RefuseUploads(named.Select(a => a.Ordinal)), resolved, digests, losers, [], EmptyIds);

            resolved[attachment.Ordinal] = row.Id;
            digests[row.Id] = row.Sha256;
        }

        // Every id in the plan is this subject's own row. The dedup loop below may point an
        // attachment at a committed object it does NOT own, so the commit needs the pre-dedup set
        // to know which rows it is allowed to write.
        var proved = new HashSet<string>(resolved.Values, StringComparer.Ordinal);
        var loserKeys = new List<(string, string)>();

        // ── dedup, after proof ────────────────────────────────────────────────────
        //
        // Two subjects proved the same bytes: exactly one object may be `committed` per digest, and
        // the attachment points at it. The other subject's object — the one THIS submission proved
        // and that is not the committed one — is aborted and the sweeper deletes its bytes.
        //
        // The committed row is found by digest rather than claimed here. `uq_committed_sha` makes
        // "at most one" a property of the schema, and the UPDATE in IngestOnceAsync does not write
        // committed_sha256 over an existing value, so a race between two first-commits is decided
        // by the unique key and the loser retries.
        var submissionWinners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (ordinal, digest) in digests.ToArray())
        {
            var winner = CommittedObjectWithDigest(connection, digest);
            if (winner is null && !submissionWinners.TryGetValue(digest, out winner))
            {
                submissionWinners[digest] = ordinal;
                continue;
            }
            if (string.Equals(winner, ordinal, StringComparison.Ordinal)) continue;

            foreach (var key in resolved.Keys
                     .Where(k => string.Equals(resolved[k], ordinal, StringComparison.Ordinal))
                     .ToArray())
            {
                resolved[key] = winner;
            }

            // The winner's digest is the one stored against every attachment that now points at it
            // — including the one this subject contributed, which is why the entry is ADDED rather
            // than moved. The commit re-asserts it on a row that already carries it; the UPDATE is
            // a no-op on the value and a guard on the state. The loser's own entry is what goes.
            digests[winner] = digest;
            digests.Remove(ordinal);
            losers.Add(ordinal);
            loserKeys.Add((ordinal, rows[ordinal].ObjectKey));
            proved.Remove(ordinal);
        }

        return new MediaPlan(null, resolved, digests, losers, loserKeys, proved);
    }

    private static readonly IReadOnlySet<string> EmptyIds = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>The one committed object holding this digest, or null when nothing is committed yet.</summary>
    private static string? CommittedObjectWithDigest(System.Data.IDbConnection connection, string digest)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT id FROM journal_objects WHERE committed_sha256 = @sha AND state = 'committed'";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@sha";
        parameter.Value = digest;
        command.Parameters.Add(parameter);

        return command.ExecuteScalar() as string;
    }

    /// <summary>
    /// The record with each attachment's <see cref="JournalAttachment.ObjectId"/> set to the object
    /// the media plan resolved it to, and the caller's upload fields cleared.
    /// </summary>
    /// <remarks>
    /// The same transformed record is what gets fingerprinted and what the attachment rows are
    /// written from, so there is one definition of "what this message is" and no chance of the
    /// fingerprint and the rows disagreeing.
    /// </remarks>
    internal static JournalRecord WithObjectIds(JournalRecord record, IReadOnlyDictionary<int, string> resolved)
    {
        if (resolved.Count == 0) return record;

        return record with
        {
            Attachments = record.Attachments
                .Select(a => a.UploadId is null
                    ? a
                    : a with
                    {
                        ObjectId = resolved.TryGetValue(a.Ordinal, out var id) ? id : a.ObjectId,
                        UploadId = null,
                        UploadSha256 = null,
                    })
                .ToArray(),
        };
    }

    private static JournalIngestResult RefuseUploads(IEnumerable<int> ordinals) => new()
    {
        Outcome = JournalIngestOutcome.UploadIncomplete,
        UploadOrdinals = ordinals.Distinct().Order().ToArray(),
    };

    /// <summary>
    /// A fresh, open connection for one synchronous read burst. The caller disposes it.
    /// </summary>
    private MySqlConnector.MySqlConnection OpenSync()
    {
        var connection = new MySqlConnector.MySqlConnection(ReadConnectionString());
        connection.Open();
        return connection;
    }

    private JournalObjectRow? ReadRow(System.Data.IDbConnection connection, string id)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, object_key, owner, sha256, byte_size, mime_type, state
              FROM journal_objects WHERE id = @id
            """;
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@id";
        parameter.Value = id;
        command.Parameters.Add(parameter);

        using var reader = (MySqlConnector.MySqlDataReader)command.ExecuteReader();
        return reader.Read() ? MySqlJournalObjectStore.Read(reader) : null;
    }

    private string ReadConnectionString()
    {
        var builder = new MySqlConnector.MySqlConnectionStringBuilder(_connectionString);
        builder.ConnectionTimeout = Math.Min(builder.ConnectionTimeout, 5);
        if (builder.DefaultCommandTimeout is 0 or > 10) builder.DefaultCommandTimeout = 10;
        return builder.ConnectionString;
    }
}
