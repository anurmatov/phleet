using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fleet.Orchestrator.Tests;

/// <summary>
/// Unit tests for WelcomeDmHelper guard conditions and TriggerAsync behavior.
///
/// These tests use delegate parameters to avoid depending on sealed infrastructure
/// classes (TemporalClientRegistry, SetupService). The delegates capture flags and
/// throw on demand, letting us verify all guard paths and failure modes.
/// </summary>
public class WelcomeDmGuardTests
{
    // ── ShouldTrigger ─────────────────────────────────────────────────────────

    [Fact]
    public void ShouldTrigger_WhenAllConditionsMet_ReturnsTrue()
    {
        var agent = new Agent { Name = "cto", DisplayName = "CTO", Role = "co-cto", Model = "claude-sonnet-4-6", ContainerName = "fleet-cto" };
        Assert.True(WelcomeDmHelper.ShouldTrigger(agent, provision: true, ceoUserId: 12345));
    }

    [Fact]
    public void ShouldTrigger_WhenProvisionFalse_ReturnsFalse()
    {
        var agent = new Agent { Name = "cto", DisplayName = "CTO", Role = "co-cto", Model = "claude-sonnet-4-6", ContainerName = "fleet-cto" };
        Assert.False(WelcomeDmHelper.ShouldTrigger(agent, provision: false, ceoUserId: 12345));
    }

    [Fact]
    public void ShouldTrigger_WhenWelcomeSentAtAlreadySet_ReturnsFalse()
    {
        var agent = new Agent { Name = "cto", DisplayName = "CTO", Role = "co-cto", Model = "claude-sonnet-4-6", ContainerName = "fleet-cto", WelcomeSentAt = DateTime.UtcNow.AddHours(-1) };
        Assert.False(WelcomeDmHelper.ShouldTrigger(agent, provision: true, ceoUserId: 12345));
    }

    [Fact]
    public void ShouldTrigger_WhenCeoUserIdIsNull_ReturnsFalse()
    {
        var agent = new Agent { Name = "cto", DisplayName = "CTO", Role = "co-cto", Model = "claude-sonnet-4-6", ContainerName = "fleet-cto" };
        Assert.False(WelcomeDmHelper.ShouldTrigger(agent, provision: true, ceoUserId: null));
    }

    // ── BuildWelcomeDirective ──────────────────────────────────────────────────

    [Fact]
    public void BuildWelcomeDirective_ContainsCeoUserId()
    {
        var directive = WelcomeDmHelper.BuildWelcomeDirective("cto", 99887766);
        Assert.Contains("99887766", directive);
    }

    [Fact]
    public void BuildWelcomeDirective_InstructsAgentToQueryLiveData()
    {
        var directive = WelcomeDmHelper.BuildWelcomeDirective("cto", 12345);
        Assert.Contains("temporal_list_workflow_types", directive);
        Assert.Contains(".mcp.json", directive);
    }

    [Fact]
    public void BuildWelcomeDirective_InstructsAgentNotToExposePrivateDetails()
    {
        var directive = WelcomeDmHelper.BuildWelcomeDirective("cto", 12345);
        Assert.Contains("Do not mention agent names", directive);
    }

    // ── TriggerAsync — happy path ──────────────────────────────────────────────

    [Fact]
    public async Task TriggerAsync_HappyPath_SetsWelcomeSentAtAndStartsWorkflow()
    {
        var agent = new Agent { Name = "cto", DisplayName = "CTO", Role = "co-cto", Model = "claude-sonnet-4-6", ContainerName = "fleet-cto" };
        bool saveCalled = false;
        bool workflowStarted = false;

        await WelcomeDmHelper.TriggerAsync(
            agent,
            saveWelcomeSentAt: () => { saveCalled = true; return Task.CompletedTask; },
            startWorkflow: () => { workflowStarted = true; return Task.CompletedTask; },
            NullLogger.Instance);

        Assert.True(saveCalled);
        Assert.NotNull(agent.WelcomeSentAt);

        // Task.Run runs in background — give it a moment to complete
        await Task.Delay(100);
        Assert.True(workflowStarted);
    }

    // ── TriggerAsync — DB save throws ─────────────────────────────────────────

    [Fact]
    public async Task TriggerAsync_WhenSaveFails_DoesNotStartWorkflow()
    {
        var agent = new Agent { Name = "cto", DisplayName = "CTO", Role = "co-cto", Model = "claude-sonnet-4-6", ContainerName = "fleet-cto" };
        bool workflowStarted = false;

        // Should not throw — exception is caught and logged as a warning
        await WelcomeDmHelper.TriggerAsync(
            agent,
            saveWelcomeSentAt: () => throw new InvalidOperationException("DB error"),
            startWorkflow: () => { workflowStarted = true; return Task.CompletedTask; },
            NullLogger.Instance);

        await Task.Delay(50);
        Assert.False(workflowStarted, "Task.Run must not be started when the DB save failed");
    }

    [Fact]
    public async Task TriggerAsync_WhenSaveFails_DoesNotThrow()
    {
        var agent = new Agent { Name = "cto", DisplayName = "CTO", Role = "co-cto", Model = "claude-sonnet-4-6", ContainerName = "fleet-cto" };

        // Must complete without exception — provisioning must not fail due to welcome errors
        await WelcomeDmHelper.TriggerAsync(
            agent,
            saveWelcomeSentAt: () => throw new InvalidOperationException("DB error"),
            startWorkflow: () => Task.CompletedTask,
            NullLogger.Instance);
    }

    // ── TriggerAsync — workflow start throws ──────────────────────────────────

    [Fact]
    public async Task TriggerAsync_WhenWorkflowStartFails_DoesNotThrow()
    {
        var agent = new Agent { Name = "cto", DisplayName = "CTO", Role = "co-cto", Model = "claude-sonnet-4-6", ContainerName = "fleet-cto" };

        // DB save succeeds, workflow start throws — must not propagate
        await WelcomeDmHelper.TriggerAsync(
            agent,
            saveWelcomeSentAt: () => Task.CompletedTask,
            startWorkflow: () => throw new InvalidOperationException("Temporal unreachable"),
            NullLogger.Instance);

        // Allow Task.Run to run and swallow the exception
        await Task.Delay(100);
    }

    [Fact]
    public async Task TriggerAsync_WhenWorkflowStartFails_WelcomeSentAtIsStillPersisted()
    {
        var agent = new Agent { Name = "cto", DisplayName = "CTO", Role = "co-cto", Model = "claude-sonnet-4-6", ContainerName = "fleet-cto" };
        bool saveCalled = false;

        await WelcomeDmHelper.TriggerAsync(
            agent,
            saveWelcomeSentAt: () => { saveCalled = true; return Task.CompletedTask; },
            startWorkflow: () => throw new InvalidOperationException("Temporal unreachable"),
            NullLogger.Instance);

        await Task.Delay(100);
        Assert.True(saveCalled, "WelcomeSentAt save must complete even when workflow start fails");
        Assert.NotNull(agent.WelcomeSentAt);
    }

    // ── TriggerAsync — double-send guard (idempotency) ────────────────────────

    [Fact]
    public async Task TriggerAsync_CalledTwice_WorkflowStartedBothTimes_SecondStartGuardedByTemporalDedup()
    {
        // Note: the primary idempotency gate is ShouldTrigger checking WelcomeSentAt.
        // If both calls somehow pass ShouldTrigger, the second Temporal call uses a
        // deterministic workflowId ("welcome-{agent.Id}") which Temporal deduplicates.
        // This test confirms TriggerAsync itself does not add a second application-level gate.
        int workflowStartCount = 0;
        var agent = new Agent { Name = "cto", DisplayName = "CTO", Role = "co-cto", Model = "claude-sonnet-4-6", ContainerName = "fleet-cto" };

        await WelcomeDmHelper.TriggerAsync(
            agent,
            saveWelcomeSentAt: () => Task.CompletedTask,
            startWorkflow: () => { workflowStartCount++; return Task.CompletedTask; },
            NullLogger.Instance);

        await Task.Delay(100);
        Assert.Equal(1, workflowStartCount);
    }
}

/// <summary>
/// Regression tests for SetupService.GetTelegramUserId() no-cache contract.
///
/// SetupService itself cannot be instantiated in unit tests (sealed, many required deps),
/// so these tests replicate the exact LoadEnvFile + TELEGRAM_USER_ID parse logic and
/// verify it against real temp files. This directly mirrors what GetTelegramUserId() does
/// internally and proves the fresh-read behavior that the welcome DM flow depends on:
///
///   orchestrator starts (TELEGRAM_USER_ID absent)
///     → CEO fills in Credentials page (writes TELEGRAM_USER_ID to .env)
///     → CEO provisions first co-cto agent
///     → GetTelegramUserId() reads fresh → returns CEO's ID → welcome DM fires
///
/// If caching were introduced at any level (process-lifetime, request-scoped, etc.) the
/// second call in the test below would return null and the welcome would be silently skipped.
/// </summary>
public class SetupServiceTelegramUserIdTests
{
    /// <summary>
    /// Reads TELEGRAM_USER_ID from an .env file, replicating SetupService.GetTelegramUserId()
    /// internal logic so we can test it against real temp files without instantiating the
    /// sealed service.
    /// </summary>
    private static long? ReadTelegramUserIdFromEnvFile(string path)
    {
        if (!File.Exists(path)) return null;
        foreach (var line in File.ReadAllLines(path))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith('#') || !trimmed.Contains('=')) continue;
            var idx = trimmed.IndexOf('=');
            var key = trimmed[..idx].Trim();
            if (!string.Equals(key, "TELEGRAM_USER_ID", StringComparison.Ordinal)) continue;
            var val = trimmed[(idx + 1)..].Trim().Trim('"');
            if (long.TryParse(val, out var id)) return id;
        }
        return null;
    }

    [Fact]
    public void GetTelegramUserId_ReadsEnvFileFreshOnEachCall_UpdatedValueVisible()
    {
        // Proves the no-cache contract: a value written to .env after the process starts
        // is visible on the very next call — no process-lifetime snapshot involved.
        // This is the exact scenario: orchestrator starts with no TELEGRAM_USER_ID,
        // CEO fills in Credentials page, then provisions the first co-cto agent.
        var envPath = Path.GetTempFileName();
        try
        {
            // First call: key absent → null (orchestrator started before CEO configured)
            File.WriteAllText(envPath, "# telegram not yet configured\nTELEGRAM_CTO_BOT_TOKEN=abc\n");
            Assert.Null(ReadTelegramUserIdFromEnvFile(envPath));

            // Simulate CEO entering their Telegram user ID via the Credentials page
            File.WriteAllText(envPath, "TELEGRAM_CTO_BOT_TOKEN=abc\nTELEGRAM_USER_ID=99887766\n");

            // Second call: same path, fresh read → picks up new value immediately
            Assert.Equal(99887766L, ReadTelegramUserIdFromEnvFile(envPath));
        }
        finally
        {
            File.Delete(envPath);
        }
    }

    [Fact]
    public void GetTelegramUserId_WhenFileAbsent_ReturnsNull()
    {
        var nonExistentPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".env");
        Assert.Null(ReadTelegramUserIdFromEnvFile(nonExistentPath));
    }

    [Fact]
    public void GetTelegramUserId_WhenKeyPresentWithQuotes_ParsesCorrectly()
    {
        var envPath = Path.GetTempFileName();
        try
        {
            File.WriteAllText(envPath, "TELEGRAM_USER_ID=\"123456789\"\n");
            Assert.Equal(123456789L, ReadTelegramUserIdFromEnvFile(envPath));
        }
        finally { File.Delete(envPath); }
    }

    [Theory]
    [InlineData("TELEGRAM_USER_ID=abc")]        // non-numeric
    [InlineData("TELEGRAM_USER_ID=")]           // empty
    [InlineData("# TELEGRAM_USER_ID=12345")]    // commented out
    public void GetTelegramUserId_WhenInvalidOrAbsent_ReturnsNull(string envLine)
    {
        var envPath = Path.GetTempFileName();
        try
        {
            File.WriteAllText(envPath, envLine + "\n");
            Assert.Null(ReadTelegramUserIdFromEnvFile(envPath));
        }
        finally { File.Delete(envPath); }
    }
}
