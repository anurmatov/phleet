namespace Fleet.Conversations.Contracts;

// The conversation journal's media contract (slice 4, #388). One object is one set of bytes that a
// subject PROVED by uploading them; one attachment points at an object.
//
// BCL-only, like the rest of this project: the agent half references nothing that reaches a
// database, and the Comms half is the only thing that reaches an object store.

/// <summary>Lifecycle of one journal object.</summary>
public enum JournalObjectState
{
    /// <summary>A subject declared bytes. No bytes have arrived.</summary>
    Uploading,

    /// <summary>Bytes arrived, were hashed, and matched the declaration. Not yet attached to anything.</summary>
    Uploaded,

    /// <summary>Referenced by a stored message. Readable by that message's observers.</summary>
    Committed,

    /// <summary>Dead: a mismatch, a loser of dedup, or a subject that never committed. The sweeper deletes it.</summary>
    Aborted,

    /// <summary>Retention removed it; the bytes go when <c>delete_after</c> passes.</summary>
    Deleting,
}

/// <summary>What <c>POST /journal/v1/uploads</c> declares. Bytes are NOT part of this request.</summary>
public sealed record JournalUploadDeclaration
{
    /// <summary>Lowercase hex SHA-256 of the bytes the subject is about to send.</summary>
    public required string Sha256 { get; init; }

    /// <summary>Exact byte count the PUT will carry.</summary>
    public required long ByteSize { get; init; }

    public required string MimeType { get; init; }
}

/// <summary>Why an upload could not be declared.</summary>
public enum JournalUploadRefusal
{
    /// <summary>Media is off on this deployment. The caller should record <c>media_disabled</c>.</summary>
    MediaDisabled,

    /// <summary>The declared size exceeds the object cap. Never a clamp.</summary>
    TooLarge,

    /// <summary>The object store is configured and not answering.</summary>
    Unavailable,
}

/// <summary>The object store behind Comms. Keys are opaque to callers outside Comms.</summary>
public interface IJournalObjectStore
{
    /// <summary>Streams at most <paramref name="byteSize"/> bytes into the store, hashing as it goes.</summary>
    /// <param name="contentLength">
    /// The exact length the store must send, when the caller knows it and <paramref name="body"/>
    /// cannot state it itself. An HTTP request body is the case this exists for: a chunked body has
    /// no length a stream can report, and an S3 <c>PutObject</c> has to have one. Null means "the
    /// stream speaks for itself".
    /// </param>
    Task<JournalObjectWriteResult> PutAsync(
        string objectKey, Stream body, long byteSize, string contentType,
        CancellationToken ct = default, long? contentLength = null);

    Task<JournalObjectReadResult?> GetAsync(string objectKey, CancellationToken ct = default);

    /// <summary>True when the object exists. A store error is a throw, not a false.</summary>
    Task<bool> ExistsAsync(string objectKey, CancellationToken ct = default);

    Task DeleteAsync(string objectKey, CancellationToken ct = default);

    /// <summary>Keys under the journal prefix, for the orphan sweep. Not paginated away.</summary>
    Task<IReadOnlyList<JournalObjectListing>> ListAsync(string prefix, CancellationToken ct = default);

    /// <summary>
    /// The bucket is reachable and the credentials work. False means the store is degraded, not
    /// that the configuration is wrong — a wrong configuration is refused at startup.
    /// </summary>
    Task<bool> ProbeAsync(CancellationToken ct = default);
}

/// <summary>Outcome of streaming one object's bytes.</summary>
public sealed record JournalObjectWriteResult
{
    public required bool Succeeded { get; init; }

    /// <summary>Lowercase hex SHA-256 of what was actually read.</summary>
    public string Sha256 { get; init; } = "";

    public long ByteSize { get; init; }

    /// <summary>The body was longer than the declared size.</summary>
    public bool Overflow { get; init; }

    /// <summary>The store refused or could not be reached. The row stays <c>uploading</c>.</summary>
    public bool StoreFailure { get; init; }

    public static JournalObjectWriteResult Written(string sha256, long byteSize) =>
        new() { Succeeded = true, Sha256 = sha256, ByteSize = byteSize };

    public static JournalObjectWriteResult Overflowed() =>
        new() { Succeeded = false, Overflow = true };

    public static JournalObjectWriteResult Failed() =>
        new() { Succeeded = false, StoreFailure = true };
}

/// <summary>One object's bytes, opened for reading or backup.</summary>
public sealed record JournalObjectReadResult
{
    public required Stream Content { get; init; }
    public required long ByteSize { get; init; }
}

/// <summary>One key in a listing, with the time it was written.</summary>
public sealed record JournalObjectListing
{
    public required string Key { get; init; }
    public required DateTimeOffset LastModified { get; init; }
}

/// <summary>Wire vocabulary for <see cref="JournalObjectState"/>.</summary>
public static class JournalMediaWire
{
    public static string Of(JournalObjectState value) => value switch
    {
        JournalObjectState.Uploading => "uploading",
        JournalObjectState.Uploaded => "uploaded",
        JournalObjectState.Committed => "committed",
        JournalObjectState.Aborted => "aborted",
        JournalObjectState.Deleting => "deleting",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    public static bool TryParse(string? wire, out JournalObjectState value)
    {
        foreach (var candidate in Enum.GetValues<JournalObjectState>())
        {
            if (string.Equals(Of(candidate), wire, StringComparison.Ordinal))
            {
                value = candidate;
                return true;
            }
        }

        value = default;
        return false;
    }
}
