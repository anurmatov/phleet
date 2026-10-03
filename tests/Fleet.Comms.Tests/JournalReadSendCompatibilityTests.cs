using System.Text.RegularExpressions;
using Fleet.Conversations.Journal;
namespace Fleet.Comms.Tests;
public sealed partial class JournalReadMcpTests
{
    [Theory]
    [InlineData("search_messages")]
    [InlineData("get_message")]
    [InlineData("get_conversation")]
    public async Task SendFeature_LeavesReadToolPayloadGoldensUnchanged(string tool)
    {
        await using var host = await McpHost.StartAsync(); var world = World.Seed(host.Reads);
        object arguments = tool switch { "get_message" => new { message_id = world.A3 }, "get_conversation" => new { conversation_id = world.ConversationA, limit = 100 }, _ => new { limit = 100 } };
        var response = await host.CallAsync(Token(JournalTokens.PurposeRead, AgentA), tool, arguments);
        Assert.False(response.IsError);
        // Fixture world creates random ULID entropy. Only those IDs are normalized, not fields.
        var normalized = Regex.Replace(response.Text, "[0-9A-HJKMNP-TV-Z]{26}", "@ulid");
        Assert.Equal(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "journal-read", "413-read-" + tool + ".json")), normalized);
        foreach (var field in new[] { "file_id", "fileId", "copiedFrom", "copied_from_message_id", "object_key", "sha256" }) Assert.DoesNotContain(field, response.Text);
    }
}
