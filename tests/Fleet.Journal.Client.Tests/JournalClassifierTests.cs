using Fleet.Conversations.Contracts;

namespace Fleet.Journal.Client.Tests;

/// <summary>AC1: every classifier rule gives the expected decision and reason.</summary>
public sealed class JournalClassifierTests
{
    private const long User = 111;
    private const long OtherUser = 222;
    private const long Group = -1001000000001;
    private const long OtherGroup = -1001000000002;
    private const long Operational = -1001000000003;

    private static readonly JournalAllowlist Allowlist = new(
        new HashSet<long> { User }, new HashSet<long> { Group, Operational });

    private static readonly IReadOnlySet<long> Excluded = new HashSet<long> { Operational };

    public static TheoryData<JournalDirection, long, string?, JournalTaskOrigin, bool, string> Table() => new()
    {
        // A human's DM and its answer, a check-in answer included (a check-in is Human origin).
        { JournalDirection.Inbound, User, "private", JournalTaskOrigin.Human, true, "" },
        { JournalDirection.Outbound, User, "private", JournalTaskOrigin.Human, true, "" },
        { JournalDirection.Inbound, Group, "supergroup", JournalTaskOrigin.Human, true, "" },
        { JournalDirection.Outbound, Group, "group", JournalTaskOrigin.Human, true, "" },

        // Rule 1: no chat.
        { JournalDirection.Outbound, 0, "private", JournalTaskOrigin.Human, false, "no_chat" },

        // Rule 2: relay and bridge output is operational, even to an allowed group.
        { JournalDirection.Outbound, Group, "supergroup", JournalTaskOrigin.Relay, false, "system_origin" },
        { JournalDirection.Outbound, Group, "supergroup", JournalTaskOrigin.Bridge, false, "system_origin" },
        { JournalDirection.Outbound, User, "private", JournalTaskOrigin.Relay, false, "system_origin" },

        // Rule 3: an operational chat is never journaled, whoever speaks.
        { JournalDirection.Inbound, Operational, "supergroup", JournalTaskOrigin.Human, false, "operational_chat" },
        { JournalDirection.Outbound, Operational, "supergroup", JournalTaskOrigin.Human, false, "operational_chat" },

        // Rule 4: only allowed users and groups.
        { JournalDirection.Inbound, OtherUser, "private", JournalTaskOrigin.Human, false, "unauthorized_chat" },
        { JournalDirection.Inbound, OtherGroup, "supergroup", JournalTaskOrigin.Human, false, "unauthorized_chat" },
        { JournalDirection.Outbound, OtherGroup, "group", JournalTaskOrigin.Human, false, "unauthorized_chat" },

        // A chat type the journal has no conversation key for.
        { JournalDirection.Inbound, Group, "channel", JournalTaskOrigin.Human, false, "unsupported_chat" },
        { JournalDirection.Inbound, Group, null, JournalTaskOrigin.Human, false, "unsupported_chat" },
    };

    [Theory]
    [MemberData(nameof(Table))]
    public void Each_rule_gives_the_expected_decision(
        JournalDirection direction, long chatId, string? chatType, JournalTaskOrigin origin, bool include, string reason)
    {
        var decision = JournalClassifier.ShouldCapture(
            direction, chatId, JournalClassifier.ChatKind(chatType), origin, Allowlist, Excluded, out var actual);

        Assert.Equal(include, decision);
        Assert.Equal(reason, actual);
    }

    [Theory]
    [InlineData("private", JournalChatKind.Private)]
    [InlineData("group", JournalChatKind.Group)]
    [InlineData("supergroup", JournalChatKind.Supergroup)]
    [InlineData("Supergroup", JournalChatKind.Supergroup)]
    public void The_chat_kind_comes_from_the_platform_type(string type, JournalChatKind kind) =>
        Assert.Equal(kind, JournalClassifier.ChatKind(type));
}
