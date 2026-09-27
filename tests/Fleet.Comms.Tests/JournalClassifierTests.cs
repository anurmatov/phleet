using Fleet.Conversations.Contracts;

namespace Fleet.Comms.Tests;

/// <summary>
/// <see cref="JournalClassifier"/>: every rule, and their order, with the exact types the capture
/// slice will call (#375 AC16).
/// </summary>
public sealed class JournalClassifierTests
{
    private const long AllowedUser = 5001;
    private const long AllowedGroup = -1001111;
    private const long ExcludedGroup = -1002222;
    private const long UnknownChat = 9999;

    private static readonly JournalAllowlist Allowlist = new(
        new HashSet<long> { AllowedUser }, new HashSet<long> { AllowedGroup, ExcludedGroup });

    private static readonly IReadOnlySet<long> Excluded = new HashSet<long> { ExcludedGroup };

    public static TheoryData<JournalDirection, long, JournalChatKind, JournalTaskOrigin, bool, JournalExclusion?> Cases() => new()
    {
        // The issue's named cases.
        { JournalDirection.Outbound, AllowedGroup, JournalChatKind.Supergroup, JournalTaskOrigin.Relay, true, JournalExclusion.SystemOrigin },
        { JournalDirection.Outbound, AllowedGroup, JournalChatKind.Supergroup, JournalTaskOrigin.Bridge, true, JournalExclusion.SystemOrigin },
        { JournalDirection.Inbound, AllowedUser, JournalChatKind.Private, JournalTaskOrigin.Relay, true, null },
        { JournalDirection.Outbound, ExcludedGroup, JournalChatKind.Supergroup, JournalTaskOrigin.Human, true, JournalExclusion.OperationalChat },
        { JournalDirection.Inbound, UnknownChat, JournalChatKind.Private, JournalTaskOrigin.Human, false, null },
        { JournalDirection.Inbound, UnknownChat, JournalChatKind.Private, JournalTaskOrigin.Human, true, JournalExclusion.UnauthorizedChat },

        // Rule 1 comes first, whatever else is true.
        { JournalDirection.Outbound, 0, JournalChatKind.Private, JournalTaskOrigin.Relay, true, JournalExclusion.NoChat },
        { JournalDirection.Inbound, 0, JournalChatKind.Group, JournalTaskOrigin.Human, false, JournalExclusion.NoChat },

        // Rule 2 before rule 3: system work into an excluded chat is SystemOrigin.
        { JournalDirection.Outbound, ExcludedGroup, JournalChatKind.Supergroup, JournalTaskOrigin.Bridge, true, JournalExclusion.SystemOrigin },

        // Rule 3 before rule 4, and with no allowlist.
        { JournalDirection.Inbound, ExcludedGroup, JournalChatKind.Group, JournalTaskOrigin.Human, false, JournalExclusion.OperationalChat },

        // Rule 4 checks the set that matches the kind: a user id is not a group id.
        { JournalDirection.Inbound, AllowedUser, JournalChatKind.Group, JournalTaskOrigin.Human, true, JournalExclusion.UnauthorizedChat },
        { JournalDirection.Inbound, AllowedGroup, JournalChatKind.Private, JournalTaskOrigin.Human, true, JournalExclusion.UnauthorizedChat },
        { JournalDirection.Inbound, AllowedGroup, JournalChatKind.Group, JournalTaskOrigin.Human, true, null },
        { JournalDirection.Outbound, AllowedGroup, JournalChatKind.Supergroup, JournalTaskOrigin.Human, true, null },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Each_rule_applies_in_order(
        JournalDirection direction, long chatId, JournalChatKind kind, JournalTaskOrigin origin,
        bool withAllowlist, JournalExclusion? expected)
    {
        var decision = JournalClassifier.Classify(
            direction, chatId, kind, origin, withAllowlist ? Allowlist : null, Excluded);

        Assert.Equal(expected is null, decision.Include);
        Assert.Equal(expected, decision.Reason);
    }
}
