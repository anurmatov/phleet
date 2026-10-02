using Fleet.Agent.Configuration;
using Fleet.Agent.Models;

namespace Fleet.Agent.Services;

public static class PrimaryHumanClassifier
{
    public static TaskPriority Classify(IncomingMessage message, TelegramOptions options, AllowlistHolder allowlist)
    {
        var allowed = allowlist.JournalSnapshot();
        return options.PrimaryHumanUserId > 0 && message.UserId == options.PrimaryHumanUserId
            && !message.FromIsBot && !message.HasSenderChat && allowed.UserIds.Contains(message.UserId)
            && (!message.IsGroupChat || allowed.GroupIds.Contains(message.ChatId))
            ? TaskPriority.PrimaryHuman : TaskPriority.Routine;
    }
}
