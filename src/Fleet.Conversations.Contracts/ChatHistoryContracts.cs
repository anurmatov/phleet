using Fleet.Protocol;

namespace Fleet.Conversations.Contracts;

/// <summary>
/// Reading back what was said in an agent's own first-party chat: the conversations people hold
/// with it from the dashboard and the phone app.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope is the agent, and it is applied inside every query.</b> The reader is named by the
/// caller's verified token, never by an argument, and sees only conversations on the client
/// channel whose agent it is. A message of another agent's conversation and one that does not
/// exist come back as the same nothing.
/// </para>
/// <para>
/// A message here is one of the four kinds that are something someone said: the person's
/// <c>submission.text</c>, and the agent's <c>turn.final</c>, <c>turn.message</c> and
/// <c>turn.recovered_answer</c> (the same set a reply may point at). Progress, notices and
/// acknowledgements are not messages and are never returned.
/// </para>
/// <para>
/// What can be read is what the event store still holds: durable events, for the retention the
/// deployment set (30 days by default). This is a read of the transcript, not a second copy of it.
/// </para>
/// </remarks>
public interface IChatHistoryReadStore
{
    /// <summary>
    /// Messages of <paramref name="agentName"/>'s chat, newest first, optionally filtered. One more
    /// row than asked for is never returned; <see cref="ChatHistoryPage.Next"/> says where the next
    /// page starts, or is null at the end.
    /// </summary>
    Task<ChatHistoryPage> SearchAsync(string agentName, ChatHistoryQuery query, CancellationToken ct = default);

    /// <summary>One message in full, or null when it is not a message of this agent's chat.</summary>
    Task<ChatHistoryMessage?> GetAsync(string agentName, string eventId, CancellationToken ct = default);
}

/// <summary>A search, already validated by the caller.</summary>
public sealed record ChatHistoryQuery
{
    /// <summary>Text the message must contain, compared without regard to case. Null for any.</summary>
    public string? Contains { get; init; }

    /// <summary>Only what the person wrote (true) or only what the agent wrote (false). Null for both.</summary>
    public bool? FromUser { get; init; }

    public DateTimeOffset? Since { get; init; }
    public DateTimeOffset? Until { get; init; }
    public required int Limit { get; init; }

    /// <summary>Only messages strictly older than this position: the previous page's <c>Next</c>.</summary>
    public ChatHistoryPosition? Before { get; init; }
}

/// <summary>A place in the newest-first order: when a message was written, then its id.</summary>
public sealed record ChatHistoryPosition(DateTimeOffset At, string EventId);

public sealed record ChatHistoryMessage
{
    public required string EventId { get; init; }
    public required string ConversationId { get; init; }
    public required string Kind { get; init; }
    public required DateTimeOffset At { get; init; }
    public required string Text { get; init; }

    public bool FromUser => string.Equals(Kind, ConversationEventKind.SubmissionText, StringComparison.Ordinal);
}

public sealed record ChatHistoryPage(IReadOnlyList<ChatHistoryMessage> Items, ChatHistoryPosition? Next);

/// <summary>Bounds of the chat history tools, in one place.</summary>
public static class ChatHistoryLimits
{
    public const int SearchDefault = 20;
    public const int SearchMax = 50;

    /// <summary>Longest <c>query</c>, in characters, and the shortest that is a search at all.</summary>
    public const int QueryMaxChars = 256;
    public const int QueryMinChars = 2;

    /// <summary>How much of a message a search result shows. The whole of it is one more call away.</summary>
    public const int PreviewChars = 600;
}
