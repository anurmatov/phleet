using Fleet.Conversations.Contracts;
using Fleet.Protocol;
using MySqlConnector;

namespace Fleet.Conversations;

/// <summary>
/// The attachment half of the store (#308 D3). <b>Metadata only — not one statement here reads or
/// writes a byte.</b>
/// </summary>
/// <remarks>
/// <para>
/// The bytes live on a volume behind <see cref="AttachmentStore"/>, and the separation is the point:
/// the conversation database is dumped nightly and that dump is what protects the transcript.
/// </para>
/// <para>
/// <b>"Live bytes" means <c>reserved</c> + <c>sealed</c> + <c>bound</c>, excluding <c>failed</c></b>
/// — at the declared size for a reservation and the actual size thereafter. Expired-but-unswept
/// reservations still count; they are released by the sweep, which keeps the accounting a single
/// state query and stops a reserve storm from racing the sweeper.
/// </para>
/// </remarks>
public sealed partial class MySqlConversationStore
{
    /// <summary>
    /// The live-byte expression, written once. Two spellings of this sum is two answers to the
    /// question "is this conversation over its cap", and the cheaper one always wins a race.
    /// </summary>
    private const string LiveBytesExpression =
        "COALESCE(SUM(CASE WHEN state = 'reserved' THEN declared_byte_size ELSE byte_size END), 0)";

    /// <inheritdoc/>
    public async Task<ReserveAttachmentResult> ReserveAttachmentAsync(
        ReserveAttachmentRequest request, CancellationToken ct = default)
    {
        // Cheap refusals first, before a connection is opened. Each maps to exactly one status, and
        // each happens BEFORE a byte moves — which is what AC-14 is about: a HEIC is refused at
        // reserve, not after the client has spent 3 MiB of someone's mobile data uploading it.
        if (request.ByteSize <= 0 || request.ByteSize > ProtocolLimits.MaxAttachmentBytes)
            return new ReserveAttachmentResult { Outcome = ReserveOutcome.TooLarge };

        if (!ProtocolLimits.AcceptedAttachmentTypes.Contains(request.ContentType))
            return new ReserveAttachmentResult { Outcome = ReserveOutcome.UnsupportedType };

        await using var connection = await OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);

        // The SAME row lock the accept path takes, so concurrent reserves on one conversation
        // serialize and the committed per-conversation total never exceeds the cap. Taken before the
        // sum, not after: a sum read outside the lock is a number that was true a moment ago.
        var conversation = await LockConversationAsync(connection, transaction, request.ConversationId, ct)
            ?? throw new ConversationNotFoundException();

        if (!string.Equals(conversation.PrincipalId, request.PrincipalId, StringComparison.Ordinal))
            throw new ConversationNotFoundException();

        var conversationLive = await SumLiveBytesAsync(
            connection, transaction, request.ConversationId, ct);

        if (conversationLive + request.ByteSize > ProtocolLimits.MaxConversationAttachmentBytes)
        {
            await transaction.RollbackAsync(ct);
            return new ReserveAttachmentResult { Outcome = ReserveOutcome.QuotaExceeded };
        }

        // The deployment cap is a plain sum and may be exceeded by at most
        // (concurrent reserves × 8 MiB). Stated rather than claimed exact: making it exact needs a
        // global lock on the one route that accepts megabytes, which is a worse trade at this scale.
        // It is an operator backstop, and the caller logs it.
        var deploymentLive = await SumLiveBytesAsync(connection, transaction, conversationId: null, ct);

        if (deploymentLive + request.ByteSize > ProtocolLimits.MaxDeploymentAttachmentBytes)
        {
            await transaction.RollbackAsync(ct);
            return new ReserveAttachmentResult
            {
                Outcome = ReserveOutcome.QuotaExceeded,
                DeploymentCap = true,
            };
        }

        var id = Ulid.NewUlid();

        await using (var insert = Command(
            """
            INSERT INTO conversation_attachments
              (id, conversation_id, state, kind, content_type, declared_byte_size, sha256,
               file_name, upload_token_sha256)
            VALUES (@id, @conv, 'reserved', @kind, @type, @size, @sha, @name, @token)
            """, connection, transaction))
        {
            insert.Parameters.AddWithValue("@id", id);
            insert.Parameters.AddWithValue("@conv", request.ConversationId);
            insert.Parameters.AddWithValue("@kind", KindColumn(request.Kind));

            // The DECLARED type, stored only so the seal can compare against it. It is overwritten
            // with the sniffed one at seal and is never what a fetch serves.
            insert.Parameters.AddWithValue("@type", request.ContentType);
            insert.Parameters.AddWithValue("@size", request.ByteSize);
            insert.Parameters.AddWithValue("@sha", Digest(request.Sha256, nameof(request.Sha256)));
            insert.Parameters.AddWithValue("@name", (object?)Truncate(request.FileName) ?? DBNull.Value);
            insert.Parameters.AddWithValue(
                "@token", Digest(request.UploadTokenSha256, nameof(request.UploadTokenSha256)));
            await insert.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);

        ConversationMetrics.AttachmentReserve.Add(1, new KeyValuePair<string, object?>("result", "reserved"));

        return new ReserveAttachmentResult
        {
            Outcome = ReserveOutcome.Reserved,
            AttachmentId = id,
            ExpiresAt = DateTimeOffset.UtcNow + ProtocolLimits.AttachmentUploadWindow,
        };
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Conditional on <c>state = 'reserved'</c> AND the upload window, so a second PUT and a late one
    /// both affect zero rows and the caller answers without re-reading. The window is enforced here
    /// as well as at the capability check, because the row is the authority.
    /// </remarks>
    public async Task<bool> SealAttachmentAsync(
        SealAttachmentRequest request, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);

        await using var command = Command(
            """
            UPDATE conversation_attachments
               SET state = 'sealed',
                   byte_size = @size,
                   content_type = @type,
                   sealed_at = UTC_TIMESTAMP(6)
             WHERE id = @id
               AND state = 'reserved'
               AND created_at >= DATE_SUB(UTC_TIMESTAMP(6), INTERVAL @window SECOND)
            """, connection);

        command.Parameters.AddWithValue("@id", request.AttachmentId);
        command.Parameters.AddWithValue("@size", request.ByteSize);
        command.Parameters.AddWithValue("@type", request.SniffedContentType);
        command.Parameters.AddWithValue(
            "@window", (long)ProtocolLimits.AttachmentUploadWindow.TotalSeconds);

        var sealed_ = await command.ExecuteNonQueryAsync(ct) == 1;

        ConversationMetrics.AttachmentSeal.Add(
            1, new KeyValuePair<string, object?>("result", sealed_ ? "sealed" : "refused"));

        return sealed_;
    }

    /// <inheritdoc/>
    public async Task<bool> FailAttachmentAsync(string attachmentId, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);

        await using var command = Command(
            """
            UPDATE conversation_attachments
               SET state = 'failed'
             WHERE id = @id AND state = 'reserved'
            """, connection);

        command.Parameters.AddWithValue("@id", attachmentId);
        var failed = await command.ExecuteNonQueryAsync(ct) == 1;

        if (failed)
            ConversationMetrics.AttachmentSeal.Add(1, new KeyValuePair<string, object?>("result", "failed"));

        return failed;
    }

    /// <inheritdoc/>
    public async Task<AttachmentMetadata?> GetAttachmentAsync(
        string attachmentId, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = Command(
            AttachmentSelect + " WHERE a.id = @id", connection);
        command.Parameters.AddWithValue("@id", attachmentId);

        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadAttachment(reader) : null;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The principal check is a JOIN predicate rather than a second round trip, so there is no window
    /// in which the row is read and then authorized. A foreign id and a nonexistent one both return
    /// null, which is what makes the two indistinguishable at the route.
    /// </remarks>
    public async Task<AttachmentMetadata?> GetAttachmentForPrincipalAsync(
        string attachmentId, string principalId, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = Command(
            AttachmentSelect
            + """
               JOIN conversations c ON c.id = a.conversation_id
              WHERE a.id = @id AND c.principal_id = @principal
              """, connection);

        command.Parameters.AddWithValue("@id", attachmentId);
        command.Parameters.AddWithValue("@principal", principalId);

        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadAttachment(reader) : null;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<AttachmentMetadata>> GetSubmissionAttachmentsAsync(
        string submissionId, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var command = Command(
            AttachmentSelect + " WHERE a.submission_id = @sub ORDER BY a.id", connection);
        command.Parameters.AddWithValue("@sub", submissionId);

        var rows = new List<AttachmentMetadata>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) rows.Add(ReadAttachment(reader));
        return rows;
    }

    // ────────────────────────────────────────────────────────────── binding

    /// <summary>
    /// Bind attachments to a submission, INSIDE the accept transaction (#308 D3).
    /// </summary>
    /// <remarks>
    /// <para>
    /// One conditional UPDATE, so the check is atomic against a concurrent submission racing for the
    /// same id: the predicate <c>state='sealed' AND submission_id IS NULL</c> is what makes the
    /// affected-row count a decision rather than an observation.
    /// </para>
    /// <para>
    /// <c>event_seq</c> is the accept floor — the same seq the <c>submission.text</c> entry is about
    /// to take, known before the append, so one statement stamps both columns.
    /// </para>
    /// <para>
    /// A shortfall throws, and the caller rolls the whole transaction back. Binding what it can and
    /// accepting the rest is exactly the availability-shaped reading MUST NOT 3 forbids.
    /// </para>
    /// </remarks>
    private static async Task<IReadOnlyList<AttachmentMetadata>> BindAttachmentsAsync(
        MySqlConnection connection, MySqlTransaction transaction,
        string conversationId, string submissionId, ulong eventSeq,
        IReadOnlyList<string> attachmentIds, CancellationToken ct)
    {
        // DISTINCT, and the route has already refused a list that named one twice. Compared against
        // the distinct count as defence in depth: if the route's check is ever removed, a duplicate
        // would otherwise bind one row and be counted as two.
        var distinct = attachmentIds.Distinct(StringComparer.Ordinal).ToList();
        var (clause, parameters) = InClause(distinct);

        int affected;

        await using (var update = Command(
            $"""
             UPDATE conversation_attachments
                SET state = 'bound', submission_id = @sub, event_seq = @seq
              WHERE id IN ({clause})
                AND conversation_id = @conv
                AND state = 'sealed'
                AND submission_id IS NULL
                AND sealed_at >= DATE_SUB(UTC_TIMESTAMP(6), INTERVAL @window SECOND)
             """, connection, transaction))
        {
            foreach (var (name, value) in parameters) update.Parameters.AddWithValue(name, value);
            update.Parameters.AddWithValue("@sub", submissionId);
            update.Parameters.AddWithValue("@seq", eventSeq);
            update.Parameters.AddWithValue("@conv", conversationId);
            update.Parameters.AddWithValue(
                "@window", (long)ProtocolLimits.AttachmentSubmitWindow.TotalSeconds);

            affected = await update.ExecuteNonQueryAsync(ct);
        }

        if (affected != distinct.Count) throw new AttachmentNotBindableException();

        // Read back INSIDE the transaction, so the descriptors on the transcript entry are the rows
        // that were actually bound rather than whatever a later read finds.
        var bound = new List<AttachmentMetadata>();

        await using (var select = Command(
            AttachmentSelect + " WHERE a.submission_id = @sub ORDER BY a.id", connection, transaction))
        {
            select.Parameters.AddWithValue("@sub", submissionId);

            await using var reader = await select.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) bound.Add(ReadAttachment(reader));
        }

        return bound;
    }

    /// <summary>
    /// The wire descriptors for a set of bound rows.
    /// </summary>
    /// <remarks>
    /// The SNIFFED content type, never the declared one, and <c>fileName</c> only as a label. There
    /// is no path and no URL here — protocol Constraints 4 and 5 hold for a transcript payload
    /// exactly as they do for a progress event.
    /// </remarks>
    internal static IReadOnlyList<AttachmentDescriptor> Describe(
        IReadOnlyList<AttachmentMetadata> rows) =>
        rows.Select(row => new AttachmentDescriptor
        {
            AttachmentId = row.AttachmentId,
            Kind = row.Kind,
            ContentType = row.ContentType,
            ByteSize = row.ByteSize,
            FileName = row.FileName,
        }).ToList();

    // ────────────────────────────────────────────────────────────── helpers

    private const string AttachmentSelect =
        """
        SELECT a.id, a.conversation_id, a.state, a.kind, a.content_type,
               a.declared_byte_size, a.byte_size, a.file_name, a.sha256, a.upload_token_sha256,
               a.created_at, a.sealed_at
          FROM conversation_attachments a
        """;

    private static AttachmentMetadata ReadAttachment(MySqlDataReader reader)
    {
        var state = ParseState(reader.GetString(2));

        return new AttachmentMetadata
        {
            AttachmentId = reader.GetString(0),
            ConversationId = reader.GetString(1),
            State = state,
            Kind = ParseKind(reader.GetString(3)),
            ContentType = reader.GetString(4),

            // A reservation has no bytes yet, so its size is the declared one — which is also what
            // the caps counted for it. Reporting zero would make a refusal look like an empty file.
            ByteSize = state == AttachmentState.Reserved
                ? (long)reader.GetUInt64(5)
                : (long)reader.GetUInt64(6),
            FileName = reader.IsDBNull(7) ? null : reader.GetString(7),
            Sha256 = reader.GetString(8),
            UploadTokenSha256 = reader.GetString(9),
            CreatedAt = new DateTimeOffset(reader.GetDateTime(10), TimeSpan.Zero),
            SealedAt = reader.IsDBNull(11)
                ? null
                : new DateTimeOffset(reader.GetDateTime(11), TimeSpan.Zero),
        };
    }

    private async Task<long> SumLiveBytesAsync(
        MySqlConnection connection, MySqlTransaction transaction,
        string? conversationId, CancellationToken ct)
    {
        var scope = conversationId is null ? string.Empty : " WHERE conversation_id = @conv";

        await using var command = Command(
            $"SELECT {LiveBytesExpression} FROM conversation_attachments{scope}"
            + (conversationId is null ? " WHERE state <> 'failed'" : " AND state <> 'failed'"),
            connection, transaction);

        if (conversationId is not null) command.Parameters.AddWithValue("@conv", conversationId);

        var result = await command.ExecuteScalarAsync(ct);
        return result is null or DBNull ? 0 : Convert.ToInt64(result);
    }

    /// <summary>
    /// A parameterized <c>IN</c> list. Never string interpolation of the values — the ids arrive
    /// from a client body.
    /// </summary>
    private static (string Clause, IReadOnlyList<(string Name, object Value)> Parameters) InClause(
        IReadOnlyCollection<string> values)
    {
        var parameters = values
            .Select((value, index) => ($"@a{index.ToString(System.Globalization.CultureInfo.InvariantCulture)}", (object)value))
            .ToList();

        return (string.Join(", ", parameters.Select(p => p.Item1)), parameters);
    }

    private static string Digest(string value, string parameter) =>
        value.Length == 64 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f')
            ? value
            : throw new ArgumentException("expected 64 lowercase hex characters", parameter);

    private static string? Truncate(string? fileName) =>
        fileName is null ? null : fileName.Length <= 255 ? fileName : fileName[..255];

    internal static string KindColumn(AttachmentKind kind) => kind switch
    {
        AttachmentKind.Image => "image",
        AttachmentKind.Document => "document",
        AttachmentKind.Audio => "audio",
        AttachmentKind.Video => "video",
        _ => "other",
    };

    private static AttachmentKind ParseKind(string value) => value switch
    {
        "image" => AttachmentKind.Image,
        "document" => AttachmentKind.Document,
        "audio" => AttachmentKind.Audio,
        "video" => AttachmentKind.Video,
        _ => AttachmentKind.Other,
    };

    private static AttachmentState ParseState(string value) => value switch
    {
        "reserved" => AttachmentState.Reserved,
        "sealed" => AttachmentState.Sealed,
        "bound" => AttachmentState.Bound,
        "failed" => AttachmentState.Failed,
        _ => throw new InvalidOperationException($"unknown attachment state '{value}'"),
    };
}
