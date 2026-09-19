namespace Fleet.Protocol;

/// <summary>
/// The fixed message table (D12). <c>turn.error.message</c> and <c>protocol.rejected.message</c>
/// are constants selected by code — never runtime text.
///
/// This is deliberate, not laziness: the runtime's own error string carries provider text,
/// executor stdout and exception messages, any of which can contain filesystem paths, session
/// ids or credentials. Those are logged server-side. The Telegram path still shows the raw
/// string because that is the owner's own channel and is unchanged (Constraint 4).
/// </summary>
public static class ProtocolErrors
{
    public const string Unauthorized = "The principal could not be authorized for this channel.";
    public const string UnsupportedProtocol = "The requested protocol version is not supported.";
    public const string UnsupportedKind = "The event kind is not recognized.";
    public const string UnsupportedRole = "The requested principal role is not supported.";
    public const string UnsupportedAttachments = "Attachments are not accepted on this channel.";
    public const string PayloadTooLarge = "The submission exceeds the maximum accepted size.";
    public const string ConversationNotFound = "The conversation is not open.";
    public const string RateLimited = "Too many requests. Try again shortly.";
    public const string RuntimeBusy = "The runtime cannot accept this request right now.";
    public const string ExecutorError = "The turn failed.";
    public const string Canceled = "The turn was canceled.";
    public const string Internal = "An internal error occurred.";
    public const string DeviceLimit = "A device is already registered for this principal.";
    public const string IdempotencyConflict = "The idempotency key was reused with a different payload.";
    public const string InvalidCursor = "The cursor is not a valid position in this conversation.";
    public const string AttachmentNotFound = "The attachment is not available.";
    public const string AttachmentLimit = "The attachment exceeds an accepted limit.";
    public const string UnsupportedMediaType = "The media type is not accepted.";
    public const string AttachmentGone = "The attachment is no longer available.";

    /// <summary>Look up the fixed message for a code. Never returns runtime-derived text.</summary>
    public static string MessageFor(ProtocolErrorCode code) => code switch
    {
        ProtocolErrorCode.Unauthorized => Unauthorized,
        ProtocolErrorCode.UnsupportedProtocol => UnsupportedProtocol,
        ProtocolErrorCode.UnsupportedKind => UnsupportedKind,
        ProtocolErrorCode.UnsupportedRole => UnsupportedRole,
        ProtocolErrorCode.UnsupportedAttachments => UnsupportedAttachments,
        ProtocolErrorCode.PayloadTooLarge => PayloadTooLarge,
        ProtocolErrorCode.ConversationNotFound => ConversationNotFound,
        ProtocolErrorCode.RateLimited => RateLimited,
        ProtocolErrorCode.RuntimeBusy => RuntimeBusy,
        ProtocolErrorCode.ExecutorError => ExecutorError,
        ProtocolErrorCode.Canceled => Canceled,
        ProtocolErrorCode.DeviceLimit => DeviceLimit,
        ProtocolErrorCode.IdempotencyConflict => IdempotencyConflict,
        ProtocolErrorCode.InvalidCursor => InvalidCursor,
        ProtocolErrorCode.AttachmentNotFound => AttachmentNotFound,
        ProtocolErrorCode.AttachmentLimit => AttachmentLimit,
        ProtocolErrorCode.UnsupportedMediaType => UnsupportedMediaType,
        ProtocolErrorCode.AttachmentGone => AttachmentGone,
        _ => Internal,
    };
}

/// <summary>Payload bounds (D18). Exceeding a bound has a defined outcome, never a silent one.</summary>
public static class ProtocolLimits
{
    /// <summary>Inbound submission text. Exceeding it yields <c>payload_too_large</c>.</summary>
    public const int MaxInboundTextBytes = 32 * 1024;

    /// <summary>Outbound final text. Exceeding it truncates and sets <c>truncated: true</c>.</summary>
    public const int MaxFinalTextChars = 64 * 1024;

    /// <summary>Outbound notice / recovered-answer text. Exceeding it truncates.</summary>
    public const int MaxNoticeTextChars = 4 * 1024;

    /// <summary>Outbound tool name. Exceeding it truncates silently.</summary>
    public const int MaxToolNameChars = 64;

    /// <summary>
    /// Hard cap on any serialized event. Exceeding it drops the event; if it was terminal, a
    /// <c>turn.outcome_unknown{terminal_event_oversize}</c> replaces it (D10 case 3).
    /// </summary>
    public const int MaxSerializedEventBytes = 128 * 1024;

    // ── Attachments (#308 D4) ────────────────────────────────────────────────────────
    //
    // Every number here is a wire bound both sides of the seam agree on, so it lives in the one
    // assembly the service, the store and the agent all reference. A literal restated per project is
    // a literal that drifts, and the symptom of the drift is an upload the service accepted and the
    // agent refuses to fetch.

    /// <summary>
    /// Largest single attachment: <b>8 MiB</b>. A reservation above it is <c>413</c>; a PUT that
    /// exceeds its own reservation is aborted mid-stream and the row is marked failed.
    /// </summary>
    /// <remarks>
    /// Sized against the artifact rather than against a round number: an iPhone HEIC is 1.5–3 MiB, a
    /// 12 MP JPEG at full quality about 5 MiB, a phone-screen PNG 2–4 MiB.
    /// </remarks>
    public const long MaxAttachmentBytes = 8L * 1024 * 1024;

    /// <summary>
    /// Attachments per submission: <b>4</b>. Covers "here are the two sides of the receipt" without
    /// inviting an album.
    /// </summary>
    public const int MaxAttachmentsPerSubmission = 4;

    /// <summary>Live attachment bytes per conversation: <b>256 MiB</b>. Exact, under the row lock.</summary>
    public const long MaxConversationAttachmentBytes = 256L * 1024 * 1024;

    /// <summary>
    /// Live attachment bytes per deployment: <b>2 GiB</b>. An operator backstop, not an exact bound.
    /// </summary>
    /// <remarks>
    /// May be exceeded by at most <i>(concurrent reserves × 8 MiB)</i>. Making it exact needs a global
    /// lock on the one route that accepts megabytes, which is a worse trade at this scale — so it is
    /// stated rather than claimed, and it logs.
    /// </remarks>
    public const long MaxDeploymentAttachmentBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>Header-declared pixel ceiling: <b>50 megapixels</b>. Checked at seal.</summary>
    public const long MaxAttachmentPixels = 50_000_000;

    /// <summary>
    /// How long a reservation's upload capability lives, from <c>created_at</c>: <b>15 minutes</b>.
    /// </summary>
    public static readonly TimeSpan AttachmentUploadWindow = TimeSpan.FromMinutes(15);

    /// <summary>
    /// How long a sealed attachment may wait to be submitted, from <c>sealed_at</c>:
    /// <b>60 minutes</b>.
    /// </summary>
    /// <remarks>
    /// A DIFFERENT anchor from the upload window, deliberately. Sharing the 15-minute anchor would
    /// leave a client that sealed at minute 14 exactly one minute to send its message.
    /// </remarks>
    public static readonly TimeSpan AttachmentSubmitWindow = TimeSpan.FromMinutes(60);

    /// <summary>
    /// The four accepted container types — exactly the four every provider vision API accepts.
    /// </summary>
    /// <remarks>
    /// HEIC and HEIF are absent on purpose: accepting them would put an image decoder and a new
    /// parser attack surface into a service that today parses no media at all. The client converts to
    /// JPEG before upload, which iOS does natively and cheaply.
    /// </remarks>
    public static readonly IReadOnlySet<string> AcceptedAttachmentTypes =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "image/jpeg",
            "image/png",
            "image/webp",
            "image/gif",
        };
}
