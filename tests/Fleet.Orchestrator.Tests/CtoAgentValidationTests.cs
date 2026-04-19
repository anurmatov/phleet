using Fleet.Orchestrator.Services;

namespace Fleet.Orchestrator.Tests;

// ── SetEnvVarAsync / ValidateCtoAgentName — shape rules ─────────────────────

public class CtoAgentValidationTests
{
    // ── bot-token shape rejected ─────────────────────────────────────────────

    [Theory]
    [InlineData("123456789:ABCDEFGHIJKLMNOPQRSTUVWXYZabcde")]  // typical bot token
    [InlineData("7777777777:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAaa")] // long secret
    public void BotToken_ThrowsWithHint(string value)
    {
        var ex = Assert.Throws<ArgumentException>(() => SetupService.ValidateCtoAgentName(value));
        Assert.Contains("looks like a Telegram bot token", ex.Message);
        Assert.Contains("TELEGRAM_CTO_BOT_TOKEN", ex.Message);
    }

    // ── non-short-name values rejected ───────────────────────────────────────

    [Theory]
    [InlineData("Agent With Space")]   // spaces
    [InlineData("UPPER")]              // uppercase only
    [InlineData("MixedCase")]          // mixed case
    [InlineData("1startsdigit")]       // starts with digit
    [InlineData("-startshyphen")]      // starts with hyphen
    [InlineData("has!special")]        // special char
    public void InvalidShortName_ThrowsArgumentException(string value)
    {
        var ex = Assert.Throws<ArgumentException>(() => SetupService.ValidateCtoAgentName(value));
        Assert.DoesNotContain("Telegram bot token", ex.Message); // distinct error path
    }

    // ── valid short names accepted ───────────────────────────────────────────

    [Theory]
    [InlineData("genius")]
    [InlineData("adev")]
    [InlineData("a-dev")]
    [InlineData("a_dev")]
    [InlineData("a1")]
    [InlineData("fleet-cto-2")]
    public void ValidShortName_DoesNotThrow(string value)
    {
        SetupService.ValidateCtoAgentName(value); // should not throw
    }

    // ── empty value accepted (clears key) ───────────────────────────────────

    [Fact]
    public void EmptyValue_DoesNotThrow()
    {
        SetupService.ValidateCtoAgentName(""); // should not throw
    }
}

// ── WriteCtoAgentAsync — returns failure result on invalid input ─────────────

public class WriteCtoAgentValidationTests
{
    private static SetupService MakeSvc()
    {
        var composeFile = Path.GetTempFileName();
        var svc = new SetupService(composeFile, null, Microsoft.Extensions.Logging.Abstractions.NullLogger<SetupService>.Instance);
        svc.ComposeDegradedReason = null;
        return svc;
    }

    [Fact]
    public async Task BotToken_ReturnsFailureResult_NoEnvWrite()
    {
        var svc = MakeSvc();
        var runnerCalled = false;
        svc.ComposeRunner = async (_, _) => { runnerCalled = true; await Task.CompletedTask; return (0, ""); };

        var (restarted, errors) = await svc.WriteCtoAgentAsync(
            "123456789:ABCDEFGHIJKLMNOPQRSTUVWXYZabcde", CancellationToken.None);

        Assert.Empty(restarted);
        Assert.True(errors.ContainsKey("_validation"));
        Assert.Contains("Telegram bot token", errors["_validation"]);
        Assert.False(runnerCalled, "ComposeRunner must not be called on validation failure");
    }

    [Fact]
    public async Task InvalidShortName_ReturnsFailureResult_NoEnvWrite()
    {
        var svc = MakeSvc();
        var runnerCalled = false;
        svc.ComposeRunner = async (_, _) => { runnerCalled = true; await Task.CompletedTask; return (0, ""); };

        var (restarted, errors) = await svc.WriteCtoAgentAsync("UPPER_CASE", CancellationToken.None);

        Assert.Empty(restarted);
        Assert.True(errors.ContainsKey("_validation"));
        Assert.False(runnerCalled, "ComposeRunner must not be called on validation failure");
    }
}
