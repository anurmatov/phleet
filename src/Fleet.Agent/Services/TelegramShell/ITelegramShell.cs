using Fleet.Agent.Models;
using Telegram.Bot;

namespace Fleet.Agent.Services.TelegramShell;

/// <summary>
/// What the inbound side of the Telegram transport needs from the transport itself. The bot
/// client, its username and the download helper are read each time they are used: the username is
/// only known once polling starts, and tests replace the client and the helper after construction.
/// </summary>
internal interface ITelegramShell
{
    ITelegramBotClient? Bot { get; }

    /// <summary>The bot's own username without the leading <c>@</c>; empty until polling has started.</summary>
    string BotUsername { get; }

    DocumentDownloadHelper DownloadHelper { get; }

    /// <summary>Hands a built message to the router (or to a test's hook).</summary>
    Task RouteAsync(IncomingMessage msg);

    Task SendTextAsync(long chatId, string text);
}
