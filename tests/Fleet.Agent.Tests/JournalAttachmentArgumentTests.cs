using Fleet.Conversations.Contracts;
namespace Fleet.Agent.Tests;
public sealed class JournalAttachmentArgumentTests
{
    [Theory]
    [InlineData(null, null, null, 0, "message_id")]
    [InlineData("bad", null, null, 0, "message_id")]
    [InlineData(null, 0L, null, 0, "telegram_message_id")]
    [InlineData(null, 5L, 1L, 0, "telegram_chat_id")]
    [InlineData(null, 5L, null, 256, "ordinal")]
    public void ArgumentsAreRejectedBeforeIO(string? id, long? message, long? chat, int ordinal, string field) =>
        Assert.Equal($"{{\"error\":\"invalid_argument\",\"field\":\"{field}\"}}", new JournalAttachmentRequest(id, message, chat, ordinal).Error());
    [Fact]
    public void TelegramFormAcceptsValidOrdinal() => Assert.Null(new JournalAttachmentRequest(null, 5L, null, 255).Error());
}
