using Fleet.Conversations.Contracts;
using Fleet.Conversations.Journal;

namespace Fleet.Conversations.Tests;

/// <summary>
/// Golden vectors for the journal fingerprint (#375 AC16c). The exact bytes are pinned, so a change
/// to the encoding — or to a library it must never have depended on — fails here instead of turning
/// every stored message's retry into a conflict.
/// </summary>
public sealed class JournalFingerprintTests
{
    private static readonly DateTimeOffset Sent = new(2026, 1, 2, 9, 4, 5, 678, TimeSpan.FromHours(6));

    private static JournalRecord Base(string? text) => new()
    {
        EventId = "01J00000000000000000000001",
        Telegram = new JournalTelegramRef { BotId = 7001, ChatId = -1001234567890, ChatKind = JournalChatKind.Supergroup, MessageId = 42 },
        Direction = JournalDirection.Inbound,
        Sender = new JournalSender { Kind = JournalSenderKind.Human, Id = "u_1" },
        SentAt = Sent,
        Text = text,
        TextFormat = text is null ? null : JournalTextFormat.Plain,
        Origin = JournalRecordOrigin.TelegramUpdate,
    };

    public static TheoryData<string, JournalRecord, string, string> Vectors() => new()
    {
        {
            "plain text",
            Base("say \"hi\"\\now\n\ttab\u0001"),
            """{"attachments":[],"conversationKey":"tg:group:-1001234567890","direction":"inbound","messageId":42,"senderId":"u_1","senderKind":"human","sentAt":"2026-01-02T03:04:05.678Z","text":"say \"hi\"\\now\u000a\u0009tab\u0001"}""",
            "ba5d79b6c0714c9bda8b0a8b286d2918dc1a0a82e85590298c456c47a164ead5"
        },
        {
            "cyrillic and an emoji",
            Base("Привет, мир 👋") with
            {
                Telegram = new JournalTelegramRef { BotId = 7001, ChatId = 5001, ChatKind = JournalChatKind.Private, MessageId = 9 },
                Direction = JournalDirection.Outbound,
                Sender = new JournalSender { Kind = JournalSenderKind.Agent, Id = "agent1" },
            },
            """{"attachments":[],"conversationKey":"tg:dm:7001:5001","direction":"outbound","messageId":9,"senderId":"agent1","senderKind":"agent","sentAt":"2026-01-02T03:04:05.678Z","text":"Привет, мир 👋"}""",
            "cf53da38b92ce8ebd2ee4cde8fa302163cb132d845c751e86922430728d7244e"
        },
        {
            "null text and two attachments out of order",
            Base(null) with
            {
                Attachments =
                [
                    new JournalAttachment { Ordinal = 1, Kind = JournalAttachmentKind.Document, MimeType = "application/pdf", ByteSize = 12345, FileName = "ignored.pdf", FileUniqueId = "AgADBQ", NotArchivedReason = JournalNotArchivedReason.OverSizeCap },
                    new JournalAttachment { Ordinal = 0, Kind = JournalAttachmentKind.Photo, MimeType = "image/jpeg", NotArchivedReason = JournalNotArchivedReason.MediaDisabled },
                ],
            },
            """{"attachments":[{"byteSize":null,"fileUniqueId":null,"kind":"photo","mimeType":"image/jpeg","ordinal":0},{"byteSize":12345,"fileUniqueId":"AgADBQ","kind":"document","mimeType":"application/pdf","ordinal":1}],"conversationKey":"tg:group:-1001234567890","direction":"inbound","messageId":42,"senderId":"u_1","senderKind":"human","sentAt":"2026-01-02T03:04:05.678Z","text":null}""",
            "cee613f328952b70c21bb0643e9cc0716114ec05aa1d0e3b227aa3c335ec900a"
        },
    };

    [Theory]
    [MemberData(nameof(Vectors))]
    public void Each_vector_encodes_and_hashes_to_its_pinned_value(string name, JournalRecord record, string canonical, string hex)
    {
        var key = JournalKeys.ConversationKey(record.Telegram);

        Assert.Equal(canonical, JournalFingerprint.Canonical(key, record));
        Assert.Equal(hex, JournalFingerprint.Compute(key, record));
        Assert.NotEmpty(name);
    }

    /// <summary>What two observers of one supergroup message may differ in, and still agree.</summary>
    [Fact]
    public void Excluded_fields_do_not_change_the_fingerprint()
    {
        var record = Base("hello") with { Attachments = [new JournalAttachment { Ordinal = 0, Kind = JournalAttachmentKind.Voice, MimeType = "audio/ogg", NotArchivedReason = JournalNotArchivedReason.MediaDisabled }] };
        var other = record with
        {
            EventId = "01J00000000000000000000002",
            Telegram = record.Telegram with { BotId = 7002, ChatTitle = "a title" },
            Sender = record.Sender with { Display = "Display Name" },
            TextFormat = JournalTextFormat.Html,
            Transcript = "transcribed",
            TranscriptTruncated = true,
            Origin = JournalRecordOrigin.AgentRuntime,
            Attachments = [record.Attachments[0] with { NotArchivedReason = JournalNotArchivedReason.DownloadFailed }],
        };

        var key = JournalKeys.ConversationKey(record.Telegram);
        Assert.Equal(key, JournalKeys.ConversationKey(other.Telegram));
        Assert.Equal(JournalFingerprint.Compute(key, record), JournalFingerprint.Compute(key, other));
    }

    [Fact]
    public void Included_fields_change_the_fingerprint()
    {
        var record = Base("hello");
        var key = JournalKeys.ConversationKey(record.Telegram);
        var original = JournalFingerprint.Compute(key, record);

        Assert.NotEqual(original, JournalFingerprint.Compute(key, record with { Text = "hello!" }));
        Assert.NotEqual(original, JournalFingerprint.Compute(key, record with { SentAt = Sent.AddMilliseconds(1) }));
        Assert.NotEqual(original, JournalFingerprint.Compute(key, record with { Direction = JournalDirection.Outbound }));
        Assert.NotEqual(original, JournalFingerprint.Compute("tg:group:-1", record));
    }

    [Theory]
    [InlineData(JournalChatKind.Private, "tg:dm:7001:-5")]
    [InlineData(JournalChatKind.Group, "tg:bgroup:7001:-5")]
    [InlineData(JournalChatKind.Supergroup, "tg:group:-5")]
    public void The_conversation_key_follows_the_chat_kind_not_the_sign(JournalChatKind kind, string expected)
    {
        Assert.Equal(expected, JournalKeys.ConversationKey(
            new JournalTelegramRef { BotId = 7001, ChatId = -5, ChatKind = kind, MessageId = 1 }));
    }
}
