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
}
