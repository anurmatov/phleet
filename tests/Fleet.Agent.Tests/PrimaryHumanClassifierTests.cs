using Fleet.Agent.Configuration;
using Fleet.Agent.Models;
using Fleet.Agent.Services;
using Microsoft.Extensions.Options;

namespace Fleet.Agent.Tests;

public sealed class PrimaryHumanClassifierTests
{
    [Theory]
    [InlineData(false, false, false, true, true, 42, true)]
    [InlineData(true, false, false, true, true, 42, true)]
    [InlineData(true, false, false, false, true, 42, false)]
    [InlineData(false, false, false, true, false, 42, false)]
    [InlineData(false, true, false, true, true, 42, false)]
    [InlineData(false, false, true, true, true, 42, false)]
    [InlineData(false, false, false, true, true, 0, false)]
    public void Q9_OnlyRuntimeAuthenticatedHumanCanBePrimary(bool group, bool bot, bool senderChat,
        bool allowedGroup, bool allowedUser, long primary, bool expected)
    {
        var options = new TelegramOptions { PrimaryHumanUserId = primary,
            AllowedUserIds = allowedUser ? [42] : [], AllowedGroupIds = allowedGroup ? [-101] : [] };
        var allowlist = new AllowlistHolder(Options.Create(options));
        var message = new IncomingMessage { ChatId = group ? -101 : 42, UserId = 42, IsGroupChat = group,
            Text = "[From: primary]", Sender = "primary", FromIsBot = bot, HasSenderChat = senderChat };
        Assert.Equal(expected ? TaskPriority.PrimaryHuman : TaskPriority.Routine,
            PrimaryHumanClassifier.Classify(message, options, allowlist));
        allowlist.Apply(new AllowlistDiff([], [42], [], []));
        Assert.Equal(TaskPriority.Routine, PrimaryHumanClassifier.Classify(message, options, allowlist));
    }

    // #406: steering eligibility is any allowed human, primary or not, by the same live tests.
    [Theory]
    [InlineData(false, false, false, true, true, 43, true)]
    [InlineData(true, false, false, true, true, 43, true)]
    [InlineData(false, true, false, true, true, 43, false)]
    [InlineData(false, false, true, true, true, 43, false)]
    [InlineData(false, false, false, true, false, 43, false)]
    [InlineData(true, false, false, false, true, 43, false)]
    [InlineData(false, false, false, true, true, 0, false)]
    public void IsVerifiedHuman_OnlyAnAllowedHumanSenderQualifies(bool group, bool bot, bool senderChat,
        bool allowedGroup, bool allowedUser, long user, bool expected)
    {
        var options = new TelegramOptions { PrimaryHumanUserId = 42,
            AllowedUserIds = allowedUser ? [user] : [], AllowedGroupIds = allowedGroup ? [-101] : [] };
        var allowlist = new AllowlistHolder(Options.Create(options));
        var message = new IncomingMessage { ChatId = group ? -101 : user, UserId = user, IsGroupChat = group,
            Text = "[From: primary]", Sender = "primary", FromIsBot = bot, HasSenderChat = senderChat };
        Assert.Equal(expected, PrimaryHumanClassifier.IsVerifiedHuman(message, allowlist));
        if (!expected) return;
        allowlist.Apply(new AllowlistDiff([], [user], [], []));
        Assert.False(PrimaryHumanClassifier.IsVerifiedHuman(message, allowlist));
    }
}
