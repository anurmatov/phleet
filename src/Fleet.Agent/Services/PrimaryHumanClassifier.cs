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

    /// <summary>
    /// An allowed human, primary or not: the same tests as <see cref="Classify"/> minus the
    /// primary-id match. Only this decides human-steering eligibility (#406).
    /// </summary>
    public static bool IsVerifiedHuman(IncomingMessage message, AllowlistHolder allowlist)
    {
        var allowed = allowlist.JournalSnapshot();
        return message.UserId != 0 && !message.FromIsBot && !message.HasSenderChat && allowed.UserIds.Contains(message.UserId)
            && (!message.IsGroupChat || allowed.GroupIds.Contains(message.ChatId));
    }
}
