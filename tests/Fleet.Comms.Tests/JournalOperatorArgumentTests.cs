using Fleet.Comms.Operations;

namespace Fleet.Comms.Tests;

/// <summary>
/// A negative number is a value only for <c>--telegram-chat</c>. Every other option keeps treating a
/// leading <c>-</c> as a missing value followed by the next flag.
/// </summary>
[Collection("auth-store-path")]
public sealed class JournalOperatorArgumentTests
{
    [Theory]
    [InlineData("enroll", "issue", "--principal", "-5")]
    [InlineData("devices", "revoke", "--device-id", "-5")]
    [InlineData("journal", "purge", "--message", "-5")]
    [InlineData("journal", "purge", "--conversation", "-5")]
    public async Task Other_options_still_refuse_a_value_starting_with_a_dash(params string[] args)
    {
        var (exit, stderr) = await RunAsync(args);

        Assert.Equal(1, exit);
        Assert.Contains("needs a value", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Telegram_chat_accepts_a_negative_chat_id()
    {
        var (_, stderr) = await RunAsync("journal", "purge", "--telegram-chat", "-1001234567890");

        // It gets past argument parsing; what stops it here is the missing database, not the value.
        Assert.DoesNotContain("needs a value", stderr, StringComparison.Ordinal);
    }

    private static async Task<(int Exit, string Stderr)> RunAsync(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = await OperatorCommands.RunAsync(args, stdout, stderr);
        return (exit, stderr.ToString());
    }
}
