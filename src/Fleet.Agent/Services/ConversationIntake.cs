using System.Text;
using Fleet.Agent.Abstractions;
using Fleet.Agent.Models;
using Fleet.Protocol;
using Microsoft.Extensions.Logging;

namespace Fleet.Agent.Services;

/// <summary>
/// The inbound seam: turns client events into runtime dispatch decisions (D8, D8.1, D9, D14).
///
/// This class deliberately does NOT go through <c>MessageRouter</c> or <c>CommandDispatcher</c>.
/// Phase 0 has no authentication, and the command surface includes a global kill switch and a raw
/// executor passthrough; neither may become reachable from a channel that cannot yet authenticate
/// its caller. Slash-prefixed text is conversation content, verbatim.
/// </summary>
public sealed class ConversationIntake
{
    private readonly ConversationRegistry _registry;
    private readonly PrincipalBinder _binder;
    private readonly TaskManager _taskManager;
    private readonly GroupBehavior _groupBehavior;
    private readonly IConversationEventPublisher _events;
    private readonly ILogger<ConversationIntake> _logger;

    public ConversationIntake(
        ConversationRegistry registry,
        PrincipalBinder binder,
        TaskManager taskManager,
        GroupBehavior groupBehavior,
        IConversationEventPublisher events,
        ILogger<ConversationIntake> logger)
    {
        _registry = registry;
        _binder = binder;
        _taskManager = taskManager;
        _groupBehavior = groupBehavior;
        _events = events;
        _logger = logger;
    }

    /// <summary>
    /// Open (or re-resolve) a conversation. Any binding failure creates NO registry entry —
    /// a rejected open must not leave a resolvable key behind.
    /// </summary>
    public ConversationOpenResult Open(ConversationOpenPayload payload, string? externalRef = null)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var channelId = payload.ChannelId;
        if (string.IsNullOrWhiteSpace(channelId) || ChannelIds.IsRuntimeOwned(channelId))
        {
            // 'telegram' and 'relay' are owned by the existing runtime paths; a client claiming
            // one of them would be asking to be routed through the bot client.
            return Reject(channelId ?? "", ProtocolErrorCode.Unauthorized);
        }

        var binding = _binder.Bind(payload.PrincipalBinding, payload.Role ?? PrincipalRole.Owner);
        if (!binding.Success)
            return Reject(channelId, binding.Error ?? ProtocolErrorCode.Unauthorized);

        var conversationId = string.IsNullOrWhiteSpace(externalRef ?? payload.ExternalConversationRef)
            ? Guid.NewGuid().ToString("N")
            : (externalRef ?? payload.ExternalConversationRef)!;

        var reference = new ConversationRef(channelId, conversationId, binding.PrincipalId);
        var runtimeKey = _registry.Resolve(reference);

        // The channel anchor is set ONCE, here. A reserved key is positive and would otherwise
        // render as a Telegram DM anchor in the prompt.
        _groupBehavior.GetGroupBuffer(runtimeKey).ChannelAnchorOverride =
            $"[channel: {channelId} conversation={conversationId}]";

        _logger.LogInformation(
            "Client conversation opened: channelId={ChannelId} runtimeKey={RuntimeKey}", channelId, runtimeKey);

        return new ConversationOpenResult(true, runtimeKey, conversationId, binding.PrincipalId, null);
    }

    /// <summary>
    /// Open (or re-resolve) a conversation whose caller the SERVICE already authenticated
    /// (#303 D8, D9a).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="PrincipalBinder"/> is deliberately not on this path. It derives a principal id
    /// from the operator token plus the agent name, which is a <b>different value</b> from the
    /// server-derived <c>principalId</c> the command envelope carries — re-binding would attach one
    /// principal to the events and another to the stored conversation, for the same human.
    /// </para>
    /// <para>
    /// The south path is authenticated by the service before the message is published, by a
    /// credential this process never sees. <c>ClientChannelOptions.OwnerPrincipalToken</c> is
    /// therefore not required here, and its absence must not disable this path (MUST NOT 13).
    /// </para>
    /// <para>
    /// The runtime key comes from the existing <see cref="ConversationRegistry"/> and no second
    /// cache is introduced. <see cref="ConversationRegistry.Resolve"/> is idempotent, so a
    /// redelivery, a resume and a second submission in the same conversation land on the same key,
    /// the same buffer and the same model context. Nothing on this path ever calls
    /// <c>Unregister</c> (MUST NOT 15): the key also anchors the conversation's buffered context,
    /// and evicting a conversation a client may return to would silently drop its history.
    /// </para>
    /// </remarks>
    /// <returns>The resolved runtime key.</returns>
    public ConversationOpenResult OpenAuthorized(string channelId, string conversationId, string principalId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(principalId);

        if (ChannelIds.IsRuntimeOwned(channelId))
        {
            // Same refusal as the client-facing open, for the same reason: 'telegram' and 'relay'
            // are owned by the existing runtime paths.
            return Reject(channelId, ProtocolErrorCode.Unauthorized);
        }

        var reference = new ConversationRef(channelId, conversationId, principalId);
        var runtimeKey = _registry.Resolve(reference);

        _groupBehavior.GetGroupBuffer(runtimeKey).ChannelAnchorOverride =
            $"[channel: {channelId} conversation={conversationId}]";

        _logger.LogInformation(
            "South conversation resolved: channelId={ChannelId} runtimeKey={RuntimeKey}", channelId, runtimeKey);

        return new ConversationOpenResult(true, runtimeKey, conversationId, principalId, null);
    }

    /// <summary>
    /// Submit conversation content. <c>submission.create</c> and <c>submission.steer</c> take the
    /// SAME path: steering is not a separate code path, and inject-vs-queue is decided by the
    /// task manager's dispatch exactly as it is for Telegram.
    /// </summary>
    /// <param name="submissionId">
    /// The store's submission id, when the caller has one (#303 D9). It must appear on every event
    /// so each one routes to the right submission and attempt in <c>StagedEvent</c>. Null keeps the
    /// pre-existing behaviour of minting one locally, which is correct for a caller that is itself
    /// the origin of the submission.
    /// </param>
    public async Task<TaskDispatchOutcome?> SubmitAsync(
        long runtimeKey, string text, IReadOnlyList<AttachmentDescriptor>? attachments = null,
        string? replyToEventId = null, string? submissionId = null)
    {
        var reference = _registry.Lookup(runtimeKey);
        if (reference is null)
        {
            PublishRejection(ConversationIdentity.ForChannel(""), ProtocolErrorCode.ConversationNotFound, runtimeKey);
            return null;
        }

        var identity = NewSubmissionIdentity(reference, replyToEventId, submissionId);

        // Attachments are rejected OUTRIGHT. The submission must not silently proceed as
        // text-only — a client that attached a file and got a text-only answer has been lied to.
        if (attachments is { Count: > 0 })
        {
            PublishRejection(identity, ProtocolErrorCode.UnsupportedAttachments, runtimeKey);
            return null;
        }

        if (text is null || Encoding.UTF8.GetByteCount(text) > ProtocolLimits.MaxInboundTextBytes)
        {
            PublishRejection(identity, ProtocolErrorCode.PayloadTooLarge, runtimeKey);
            return null;
        }

        // Buffer in memory exactly as the DM path does, so a cold executor still gets context.
        // SaveBuffers skips reserved keys, so this never reaches disk.
        _groupBehavior.AddAndPersist(runtimeKey, "user", text, replyTo: null);

        // Reuse the existing DM prompt shape rather than inventing one:
        //   telegramMessageId: 0  -> no [telegram_message_id:] tag (ForDm gates on > 0)
        //   chatUsername/FirstName null -> ChatLabel stays null, so the anchor comes from
        //                                  ChannelAnchorOverride, never a Telegram DM anchor
        //   replyToText: null     -> replyToEventId is an EVENT id, not text; resolving it to
        //                            quoted text is a later issue
        //   isVoiceTranscription  -> there is no client voice path in Phase 0
        var prompt = _groupBehavior.BuildDmTask(
            chatId: runtimeKey,
            taskText: text,
            replyToText: null,
            telegramMessageId: 0,
            chatUsername: null,
            chatFirstName: null,
            isVoiceTranscription: false);

        // userId: 0 is a SAFETY BOUNDARY, not a default. StartTaskCore indexes a task into the
        // cross-chat user index only when userId != 0, so a client turn never enters that index
        // and a Telegram /cancel can never reach it. See CancelAsync for the other direction.
        return await _taskManager.StartTask(
            runtimeKey, prompt, Display(text), isSessionTask: true,
            source: TaskSource.UserMessage, userId: 0, identity: identity);
    }

    /// <summary>
    /// Request cancellation. The acknowledgement is owned HERE rather than by the task manager,
    /// whose human-readable strings go to the sink and no-op for a reserved key.
    /// </summary>
    public async Task<bool> CancelAsync(long runtimeKey, CancelScope scope)
    {
        var reference = _registry.Lookup(runtimeKey);
        if (reference is null)
        {
            PublishRejection(ConversationIdentity.ForChannel(""), ProtocolErrorCode.ConversationNotFound, runtimeKey);
            return false;
        }

        var hadRunningTask = _taskManager.HasRunningTasks(runtimeKey);

        // userId: 0 again, and for a sharper reason than above. HandleCancel's cross-chat fallback
        // triggers when the target chat has no running task AND userId != 0: it looks the user up
        // in the cross-chat index, cancels their turns in OTHER chats, and writes "Task cancelled
        // by user from another chat." into those chats. Since the Phase-0 principal binds to the
        // same owner who uses Telegram, passing a real userId would let an unauthenticated client
        // kill an in-flight Telegram turn and post into a Telegram chat.
        await _taskManager.HandleCancel(
            runtimeKey,
            scope == CancelScope.All ? "all" : "",
            userId: 0,
            reason: TurnCancelReason.User);

        var identity = NewSubmissionIdentity(reference, null);
        _events.Publish(runtimeKey, ConversationEventKind.ControlAck, identity,
            new ControlAckPayload
            {
                Target = ControlTarget.Cancel,
                // `accepted` means the REQUEST was accepted, not that the turn stopped. The
                // authoritative outcome is the turn.canceled that follows — and when there was no
                // running task, none follows and this ack is the whole story.
                Accepted = true,
                HadRunningTask = hadRunningTask,
            });

        return hadRunningTask;
    }

    /// <summary>An inbound kind the runtime does not recognise is rejected, never a connection drop.</summary>
    public void RejectUnknownKind(long runtimeKey, string channelId) =>
        PublishRejection(ConversationIdentity.ForChannel(channelId), ProtocolErrorCode.UnsupportedKind, runtimeKey);

    /// <summary>An unsupported protocol string is rejected with a stable code.</summary>
    public void RejectUnsupportedProtocol(long runtimeKey, string channelId) =>
        PublishRejection(ConversationIdentity.ForChannel(channelId), ProtocolErrorCode.UnsupportedProtocol, runtimeKey);

    private ConversationIdentity NewSubmissionIdentity(
        ConversationRef reference, string? replyToEventId, string? submissionId = null) => new()
    {
        PrincipalId = reference.PrincipalId,
        Role = PrincipalRole.Owner,
        ChannelId = reference.ChannelId,
        ConversationId = reference.ConversationId,
        // An externally-supplied id wins. The store already has a row under it, and an event
        // carrying a locally-minted id would route to a submission that does not exist.
        SubmissionId = string.IsNullOrWhiteSpace(submissionId) ? Guid.NewGuid().ToString("N") : submissionId,
        Attempt = 1,
        ReplyToEventId = replyToEventId,
    };

    private ConversationOpenResult Reject(string channelId, ProtocolErrorCode code)
    {
        _logger.LogInformation("Client conversation.open rejected: channelId={ChannelId} code={Code}", channelId, code);
        return new ConversationOpenResult(false, 0, null, null, code);
    }

    private void PublishRejection(ConversationIdentity identity, ProtocolErrorCode code, long runtimeKey) =>
        _events.Publish(runtimeKey, ConversationEventKind.ProtocolRejected, identity,
            new ProtocolRejectedPayload { Code = code, Message = ProtocolErrors.MessageFor(code) });

    private static string Display(string text) =>
        text.Length <= 60 ? text : text[..Fleet.Shared.TextTruncation.SafeCutIndex(text, 60)] + "…";
}

/// <summary>Outcome of <see cref="ConversationIntake.Open"/>.</summary>
public readonly record struct ConversationOpenResult(
    bool Success,
    long RuntimeKey,
    string? ConversationId,
    string? PrincipalId,
    ProtocolErrorCode? Error);
