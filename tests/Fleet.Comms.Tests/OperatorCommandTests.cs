using Fleet.Comms.Operations;

namespace Fleet.Comms.Tests;

/// <summary>
/// What is left of the operator command line after sign-in moved to the orchestrator (#530): the
/// conversation, journal and media commands. The device and store commands are gone, and asking for
/// one is an unknown command rather than a command that quietly does nothing.
/// </summary>
public sealed class OperatorCommandTests
{
    [Theory]
    [InlineData("enroll", "issue")]
    [InlineData("devices", "list")]
    [InlineData("devices", "revoke")]
    [InlineData("store", "init")]
    [InlineData("store", "verify")]
    [InlineData("store", "backup")]
    public async Task The_removed_sign_in_commands_are_unknown(string verb, string noun)
    {
        var (exit, _, stderr) = await Run(verb, noun);

        Assert.Equal(2, exit);
        Assert.Contains("Unknown command", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unknown_command_exits_non_zero_and_prints_a_usage_without_sign_in()
    {
        var (exit, _, stderr) = await Run("wat", "nope");

        Assert.NotEqual(0, exit);
        Assert.Contains("conversations migrate", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("enroll", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("devices", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("store init", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missing_required_argument_exits_non_zero_with_a_message()
    {
        var (exit, _, stderr) = await Run("journal", "token");

        Assert.NotEqual(0, exit);
        Assert.Contains("--purpose is required", stderr, StringComparison.Ordinal);
    }

    private static async Task<(int Exit, string Stdout, string Stderr)> Run(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = await OperatorCommands.RunAsync(args, stdout, stderr);
        return (exit, stdout.ToString(), stderr.ToString());
    }
}
