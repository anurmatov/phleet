using Fleet.Protocol;

namespace Fleet.Conversations.Contracts;

/// <summary>
/// Lifecycle state of one attachment row (#308 D3).
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="Bound"/> is what makes the retention argument true.</b> D3 claims an attachment
/// cannot be pruned while still referenced, and that holds only because exactly one event can
/// reference a row: binding is a conditional transition out of <see cref="Sealed"/>, so submitting A
/// then B both naming X cannot happen. Without it two live transcript entries would share one row
/// and one retention clock, and pruning A would delete the bytes under B.
/// </para>
/// </remarks>
public enum AttachmentState
{
    /// <summary>Reserved; no bytes yet. Counts against the caps at its DECLARED size.</summary>
    Reserved,

    /// <summary>Bytes verified and on disk. Fetchable, and bindable exactly once.</summary>
    Sealed,

    /// <summary>Referenced by a durable <c>submission.text</c>. Fetchable; no longer bindable.</summary>
    Bound,

    /// <summary>Verification failed. Bytes discarded, never fetchable, never counted.</summary>
    Failed,
}

// ───────────────────────────────────────────────────────────────── reserve

/// <summary>
/// Reserve a slot before any byte moves (#308 D2).
/// </summary>
/// <remarks>
/// The declared size and digest are a CLAIM. They are recorded so the seal can check the bytes
/// against them; nothing downstream trusts them on their own.
/// </remarks>
public sealed record ReserveAttachmentRequest
{
    public required string ConversationId { get; init; }

    /// <summary>The caller's principal. A foreign conversation is not-found, never a conflict.</summary>
    public required string PrincipalId { get; init; }

    public required AttachmentKind Kind { get; init; }

    /// <summary>Declared type, checked against the accepted set here and re-decided at seal.</summary>
    public required string ContentType { get; init; }

    public required long ByteSize { get; init; }

    /// <summary>Lowercase hex SHA-256 the uploaded bytes must hash to.</summary>
    public required string Sha256 { get; init; }

    /// <summary>Client-supplied label. Stored, echoed, and NEVER used as a path.</summary>
    public string? FileName { get; init; }

    /// <summary>
    /// SHA-256 of the upload capability. The plaintext never reaches the store.
    /// </summary>
    /// <remarks>
    /// Hashed by the caller so this assembly never holds the capability at all — the one place the
    /// plaintext exists is the reserve response.
    /// </remarks>
    public required string UploadTokenSha256 { get; init; }
}

/// <summary>Why a reservation was refused. Each maps to exactly one documented status.</summary>
public enum ReserveOutcome
{
    Reserved,

    /// <summary>Over <see cref="ProtocolLimits.MaxAttachmentBytes"/> — <c>413</c>.</summary>
    TooLarge,

    /// <summary>Not one of the four accepted types — <c>415</c>.</summary>
    UnsupportedType,

    /// <summary>Per-conversation or per-deployment live bytes exceeded — <c>409</c>.</summary>
    QuotaExceeded,
}

public sealed record ReserveAttachmentResult
{
    public required ReserveOutcome Outcome { get; init; }

    /// <summary>Null unless <see cref="ReserveOutcome.Reserved"/>.</summary>
    public string? AttachmentId { get; init; }

    /// <summary>When the upload capability dies. Null unless reserved.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>
    /// True when the DEPLOYMENT cap was the one exceeded, so the caller can log the operator
    /// condition rather than the ordinary per-conversation refusal. Both are the same status to the
    /// client.
    /// </summary>
    public bool DeploymentCap { get; init; }
}

// ───────────────────────────────────────────────────────────────── seal

/// <summary>
/// Commit verified bytes (#308 D2). Sealing is verification, not acceptance of a claim.
/// </summary>
public sealed record SealAttachmentRequest
{
    public required string AttachmentId { get; init; }

    /// <summary>Actual bytes written, counted while streaming.</summary>
    public required long ByteSize { get; init; }

    /// <summary>Lowercase hex SHA-256 computed over what actually arrived.</summary>
    public required string Sha256 { get; init; }

    /// <summary>The type SNIFFED from container magic bytes. The declared one is never stored.</summary>
    public required string SniffedContentType { get; init; }
}

// ───────────────────────────────────────────────────────────────── resolve

/// <summary>
/// The row as a reader sees it. Carries no bytes and no path — the path is derived from the id by
/// the one type allowed to touch the filesystem.
/// </summary>
public sealed record AttachmentMetadata
{
    public required string AttachmentId { get; init; }
    public required string ConversationId { get; init; }
    public required AttachmentState State { get; init; }
    public required AttachmentKind Kind { get; init; }

    /// <summary>The sniffed type once sealed; the declared one while still reserved.</summary>
    public required string ContentType { get; init; }

    public required long ByteSize { get; init; }
    public string? FileName { get; init; }

    /// <summary>
    /// The digest the bytes must produce, as reserved. Re-verified against what actually arrives.
    /// </summary>
    public required string Sha256 { get; init; }

    /// <summary>The stored digest of the upload capability. Never the capability.</summary>
    public required string UploadTokenSha256 { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? SealedAt { get; init; }
}
