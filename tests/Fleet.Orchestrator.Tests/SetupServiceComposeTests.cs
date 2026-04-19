using Fleet.Orchestrator.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fleet.Orchestrator.Tests;

// ── BuildComposeArgs ─────────────────────────────────────────────────────────

public class BuildComposeArgsTests
{
    [Fact]
    public void BuildComposeArgs_NoProject_ProducesFileAndSubcommand()
    {
        var args = SetupService.BuildComposeArgs(
            "/compose/docker-compose.yml", null,
            "up", "-d", "--force-recreate", "--no-deps", "fleet-telegram");

        Assert.Equal([
            "-f", "/compose/docker-compose.yml",
            "up", "-d", "--force-recreate", "--no-deps", "fleet-telegram"
        ], args);
    }

    [Fact]
    public void BuildComposeArgs_WithProject_InjectsProjectFlag()
    {
        var args = SetupService.BuildComposeArgs(
            "/compose/docker-compose.yml", "myproject",
            "up", "-d", "--force-recreate", "--no-deps", "fleet-bridge");

        Assert.Equal([
            "-f", "/compose/docker-compose.yml",
            "-p", "myproject",
            "up", "-d", "--force-recreate", "--no-deps", "fleet-bridge"
        ], args);
    }

    [Fact]
    public void BuildComposeArgs_EmptyProjectName_NotInjected()
    {
        var args = SetupService.BuildComposeArgs(
            "/compose/docker-compose.yml", "",
            "up", "-d");

        Assert.DoesNotContain("-p", args);
    }
}

// ── InfraContainerRecreateAsync ──────────────────────────────────────────────

public class InfraContainerRecreateAsyncTests
{
    // Creates a minimal SetupService for testing — no Docker/DB/DI wiring needed.
    private static SetupService MakeSvc(
        string composeFilePath,
        Func<string[], CancellationToken, Task<(int, string)>>? runner = null)
    {
        var svc = new SetupService(
            composeFilePath, null,
            NullLogger<SetupService>.Instance);
        if (runner is not null)
            svc.ComposeRunner = runner;
        return svc;
    }

    [Fact]
    public async Task SuccessfulCompose_DoesNotThrow_AndPassesCorrectArgs()
    {
        var composeFile = Path.GetTempFileName();
        try
        {
            string[]? capturedArgs = null;
            var svc = MakeSvc(composeFile, async (args, _) =>
            {
                capturedArgs = args;
                await Task.CompletedTask;
                return (0, "");
            });
            svc.ComposeDegradedReason = null; // clear in case CLI not on test PATH

            await svc.InfraContainerRecreateAsync("fleet-telegram", CancellationToken.None);

            Assert.NotNull(capturedArgs);
            Assert.Equal("-f", capturedArgs[0]);
            Assert.Equal(composeFile, capturedArgs[1]);
            Assert.Contains("up", capturedArgs);
            Assert.Contains("--force-recreate", capturedArgs);
            Assert.Contains("--no-deps", capturedArgs);
            Assert.Contains("fleet-telegram", capturedArgs);
        }
        finally { File.Delete(composeFile); }
    }

    [Fact]
    public async Task NonZeroExit_ThrowsInvalidOperationWithStderr()
    {
        var composeFile = Path.GetTempFileName();
        try
        {
            var svc = MakeSvc(composeFile, async (_, _) =>
            {
                await Task.CompletedTask;
                return (1, "no such service: fleet-bogus");
            });
            svc.ComposeDegradedReason = null; // clear in case CLI not on test PATH

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => svc.InfraContainerRecreateAsync("fleet-bogus", CancellationToken.None));

            Assert.Contains("exit 1", ex.Message);
            Assert.Contains("no such service", ex.Message);
        }
        finally { File.Delete(composeFile); }
    }

    [Fact]
    public async Task ComposeDegraded_ThrowsWithoutCallingRunner()
    {
        // Use a path that definitely doesn't exist → ComposeDegradedReason will be set
        var svc = MakeSvc("/nonexistent/docker-compose.yml");
        var runnerCalled = false;
        svc.ComposeRunner = async (_, _) =>
        {
            runnerCalled = true;
            await Task.CompletedTask;
            return (0, "");
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.InfraContainerRecreateAsync("fleet-telegram", CancellationToken.None));

        Assert.Contains("docker-compose unavailable", ex.Message);
        Assert.False(runnerCalled, "ComposeRunner must not be called when compose is unavailable");
    }
}

// ── IsDockerComposeOnPath ────────────────────────────────────────────────────

public class IsDockerComposeOnPathTests
{
    [Fact]
    public void BinaryExists_ReturnsTrue()
    {
        var tmpDir = Path.GetTempPath();
        var fakeCompose = Path.Combine(tmpDir, "docker-compose");
        try
        {
            File.WriteAllText(fakeCompose, "#!/bin/sh\necho ok");
            var original = Environment.GetEnvironmentVariable("PATH") ?? "";
            Environment.SetEnvironmentVariable("PATH", tmpDir + ":" + original);
            try
            {
                Assert.True(SetupService.IsDockerComposeOnPath());
            }
            finally { Environment.SetEnvironmentVariable("PATH", original); }
        }
        finally { if (File.Exists(fakeCompose)) File.Delete(fakeCompose); }
    }

    [Fact]
    public void BinaryAbsent_ReturnsFalse()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tmpDir);
        try
        {
            var original = Environment.GetEnvironmentVariable("PATH") ?? "";
            Environment.SetEnvironmentVariable("PATH", tmpDir);
            try
            {
                Assert.False(SetupService.IsDockerComposeOnPath());
            }
            finally { Environment.SetEnvironmentVariable("PATH", original); }
        }
        finally { Directory.Delete(tmpDir); }
    }
}
