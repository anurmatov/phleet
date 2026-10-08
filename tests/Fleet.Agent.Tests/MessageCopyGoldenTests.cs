using System.Text.Json;
using Fleet.Agent.Abstractions;
using static Fleet.Agent.Tests.MessageCopyToolTests;
namespace Fleet.Agent.Tests;

public sealed class MessageCopyGoldenTests
{
    [Fact]
    public async Task Copy_ErrorFixtures_MapWithoutRetryOrFallback()
    {
        using var fixtures = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "message-copy", "errors.json")));
        foreach (var error in fixtures.RootElement.EnumerateArray())
        {
            using var rig = new Rig();
            rig.Bot.Before = (method, _) => method == "copyMessage"
                ? throw new TelegramCopyException(error.GetProperty("status").GetInt32(), error.GetProperty("description").GetString()!, error.TryGetProperty("retry_after", out var retry) ? retry.GetInt32() : null)
                : Task.CompletedTask;
            var call = rig.Copy(); await rig.Bot.Prompted.Task; await rig.Tap();
            using var result = JsonDocument.Parse(await call);
            Assert.Equal(error.GetProperty("outcome").GetString(), result.RootElement.GetProperty("outcome").GetString());
            if (error.TryGetProperty("retry_after", out var seconds)) Assert.Equal(seconds.GetInt32(), result.RootElement.GetProperty("retry_after").GetInt32());
            Assert.Equal(1, rig.Bot.Calls.Count(c => c == "copyMessage"));
            Assert.Equal(5, rig.Bot.Calls.Count);
        }
    }
    [Fact]
    public async Task Copy_NetworkFailure_IsAmbiguousAndNeverRetried()
    {
        using var rig = new Rig();
        rig.Bot.Before = (method, _) => method == "copyMessage" ? throw new HttpRequestException("synthetic network failure after send") : Task.CompletedTask;
        var call = rig.Copy(); await rig.Bot.Prompted.Task; await rig.Tap();
        var result = await call; Assert.Equal("ambiguous", Outcome(result)); Assert.Contains("check the destination", result);
        Assert.Equal(1, rig.Bot.Calls.Count(c => c == "copyMessage"));
    }
    [Fact]
    public async Task Copy_SyntheticZeroMessageId_IsScheduledWithNullId()
    {
        // Synthetic only: real Bot API acceptance must confirm this before merge.
        using var rig = new Rig(); rig.Bot.CopyId = 0;
        var call = rig.Copy(); await rig.Bot.Prompted.Task; await rig.Tap();
        using var result = JsonDocument.Parse(await call);
        Assert.Equal("scheduled", result.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(JsonValueKind.Null, result.RootElement.GetProperty("destination_message_id").ValueKind);
    }
    [Theory]
    [InlineData("getChat", "destination_unreachable")]
    [InlineData("sendMessage", "source_not_found")]
    public async Task Copy_PreflightFailure_StopsBeforeCopy(string method, string outcome)
    {
        using var rig = new Rig();
        rig.Bot.Before = (name, _) => name == method ? throw new TelegramCopyException(400, "Bad Request: message to be replied not found") : Task.CompletedTask;
        Assert.Equal(outcome, Outcome(await rig.Copy())); Assert.DoesNotContain("copyMessage", rig.Bot.Calls);
    }
}
