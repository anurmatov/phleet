using Fleet.Shared.Journal;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Fleet.Telegram.Services;

/// <summary>
/// The one place in <c>fleet-telegram</c> that calls a Bot API send method (#394). A message the
/// agent's own bot got accepted is recorded into the tool call's <see cref="ToolSendCall"/>, which
/// hands one receipt to <see cref="ToolSendReceiptPublisher"/> when the call ends.
/// </summary>
/// <remarks>
/// <para>
/// <c>ToolSendReceiptTests</c> scans <c>src/Fleet.Telegram</c> and fails on a Bot API send
/// (<c>SendMessage</c>, <c>SendRichMessage</c>, <c>SendPhoto</c>, …) anywhere outside this file, so
/// a new send tool cannot skip its receipt by calling the client directly.
/// </para>
/// <para>
/// Recording only observes. Each method makes exactly the request the tools made before it
/// existed, and nothing recorded here can fail or delay a send.
/// </para>
/// </remarks>
public sealed class TelegramSender(ToolSendReceiptPublisher publisher, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>
    /// Opens the receipt for one tool call and takes its <c>requestedAt</c>, so call it before the
    /// first Bot API call. Disposing the result ends the call and publishes the receipt.
    /// </summary>
    /// <param name="isAgentBot">
    /// From <see cref="BotClientFactory.GetClient(string?, out bool)"/>. False means the notifier
    /// fallback, which records nothing: that bot is not in the agent's conversation.
    /// </param>
    public ToolSendCall BeginCall(string tool, string? agentName, ITelegramBotClient client, bool isAgentBot) =>
        new(publisher, tool, agentName, client,
            recording: isAgentBot && publisher.Enabled && !string.IsNullOrWhiteSpace(agentName),
            requestedAt: _time.GetUtcNow());

    /// <summary><c>sendMessage</c> from the call's own bot. The accepted message is recorded.</summary>
    /// <remarks>
    /// <c>parseMode ?? default</c> is <see cref="ParseMode.None"/>, the extension's own default, so
    /// the request is the one the tools built with and without a parse mode.
    /// </remarks>
    public async Task<Message> SendTextAsync(
        ToolSendCall call, long chatId, string text, ParseMode? parseMode,
        ReplyParameters? replyParameters, CancellationToken ct)
    {
        var sent = await call.Client.SendMessage(chatId, text, parseMode: parseMode ?? default,
            replyParameters: replyParameters, cancellationToken: ct);
        call.Record(chatId, sent, text,
            parseMode == ParseMode.Html ? ToolSendTextFormats.Html : ToolSendTextFormats.Plain);
        return sent;
    }

    /// <summary>
    /// <c>sendRichMessage</c> from the call's own bot. Recorded as <c>rich</c> with
    /// <paramref name="markdown"/>, the source the blocks were rendered from, as the agent's own
    /// journal does.
    /// </summary>
    public async Task<Message> SendRichAsync(
        ToolSendCall call, long chatId, InputRichMessage richMessage, string markdown,
        ReplyParameters? replyParameters, CancellationToken ct)
    {
        var sent = await call.Client.SendRichMessage(chatId, richMessage,
            replyParameters: replyParameters, cancellationToken: ct);
        call.Record(chatId, sent, markdown, ToolSendTextFormats.Rich);
        return sent;
    }

    /// <summary>
    /// <c>sendMessage</c> from the notifier fallback bot, after the agent's bot was refused.
    /// <b>Never recorded</b>: the message lands in a conversation the agent's bot is not in.
    /// </summary>
    public Task<Message> SendTextUnrecordedAsync(
        ITelegramBotClient fallbackClient, long chatId, string text, ParseMode? parseMode, CancellationToken ct) =>
        fallbackClient.SendMessage(chatId, text, parseMode: parseMode ?? default, cancellationToken: ct);
}

/// <summary>
/// One tool call's accepted messages, in send order. Disposing it hands the receipt to the
/// publisher: once, only when at least one message was recorded, and without waiting on the broker.
/// </summary>
public sealed class ToolSendCall : IDisposable
{
    private readonly ToolSendReceiptPublisher _publisher;
    private readonly string _tool;
    private readonly string? _agent;
    private readonly List<ToolSendMessage> _messages = [];
    private ToolSendChat? _chat;
    private int _ended;

    internal ToolSendCall(
        ToolSendReceiptPublisher publisher, string tool, string? agent, ITelegramBotClient client,
        bool recording, DateTimeOffset requestedAt)
    {
        _publisher = publisher;
        _tool = tool;
        _agent = agent;
        Client = client;
        Recording = recording;
        RequestedAt = requestedAt;
    }

    /// <summary>The bot this call sends from.</summary>
    public ITelegramBotClient Client { get; }

    /// <summary>False for the notifier fallback or with receipts off: nothing is recorded.</summary>
    public bool Recording { get; }

    public DateTimeOffset RequestedAt { get; }

    internal void Record(long chatId, Message? sent, string? text, string textFormat)
    {
        if (!Recording || sent is null) return;

        try
        {
            _chat ??= new ToolSendChat(
                sent.Chat?.Id is { } id && id != 0 ? id : chatId,
                ChatTypeName(sent.Chat?.Type),
                sent.Chat?.Title);
            _messages.Add(new ToolSendMessage(sent.Id, sent.Date, sent.ReplyToMessage?.Id, text, textFormat));
        }
        catch (Exception)
        {
            // A receipt problem must not turn a delivered message into a failed send.
            _publisher.Dropped("record");
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _ended, 1) != 0 || _messages.Count == 0 || _chat is null) return;

        try
        {
            _publisher.Enqueue(new ToolSendReceipt
            {
                Agent = ToolSendReceipts.RoutingKey(_agent!),
                Tool = _tool,
                RequestedAt = RequestedAt.ToUniversalTime(),
                BotId = Client.BotId,
                Chat = _chat,
                Messages = _messages.ToArray(),
            });
        }
        catch (Exception)
        {
            _publisher.Dropped("record");
        }
    }

    /// <summary>The Bot API spelling, as the agent's own journal writes it.</summary>
    private static string? ChatTypeName(ChatType? type) => type switch
    {
        ChatType.Private => "private",
        ChatType.Group => "group",
        ChatType.Supergroup => "supergroup",
        ChatType.Channel => "channel",
        _ => null,
    };
}
