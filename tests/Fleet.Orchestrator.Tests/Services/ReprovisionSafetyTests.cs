using System.Text.Json;
using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Services;
using Microsoft.EntityFrameworkCore;

namespace Fleet.Orchestrator.Tests.Services;

/// <summary>
/// A reprovision decides and renders the new container before it touches the old one, creates it
/// from exactly that, and never runs beside another provisioning operation on the same agent.
/// </summary>
public class ReprovisionSafetyTests
{
    private static Agent Claude(string name = "agent1") => new()
    {
        Name = name, DisplayName = name, Role = "tester",
        Model = "sonnet", Provider = "claude",
        ContainerName = $"fleet-{name}", MemoryLimitMb = 512,
    };

    private static Agent CodexLocalWithACloudModel() => new()
    {
        Name = "agent1", DisplayName = "agent1", Role = "tester",
        Model = "gpt-5", Provider = "codex", LocalBaseUrl = "http://inference-host:11434",
        ContainerName = "fleet-agent1", MemoryLimitMb = 512,
    };

    private static void AssertOldContainerUntouched(ProvisioningHarness harness)
    {
        Assert.DoesNotContain(harness.DockerRequests, r => r.StartsWith("DELETE ", StringComparison.Ordinal));
        Assert.DoesNotContain(harness.DockerRequests, r => r.EndsWith("/stop", StringComparison.Ordinal));
        Assert.Empty(harness.CreateBodies);
    }

    // ── A refusal keeps the old container ────────────────────────────────────────────────────────

    [Fact]
    public async Task Reprovision_WithALocalModelFault_RefusesBeforeTheOldContainerIsRemoved()
    {
        await using var harness = ProvisioningHarness.Create();
        await harness.SeedAsync(db => db.Agents.Add(CodexLocalWithACloudModel()));
        harness.ContainerExists = true;

        var result = await harness.Service.ReprovisionAsync("agent1");

        Assert.False(result.Success);
        Assert.Contains("must be ollama/<tag> or lmstudio/<tag>", result.Message);
        Assert.DoesNotContain("deprovision succeeded", result.Message);
        AssertOldContainerUntouched(harness);
        Assert.False(Directory.Exists(harness.GeneratedDir("fleet-agent1")));
    }

    [Fact]
    public async Task Reprovision_NamingAStyleWithNoRow_RefusesBeforeTheOldContainerIsRemoved()
    {
        await using var harness = ProvisioningHarness.Create();
        var agent = Claude();
        agent.OutputStyle = "no-such-style";
        await harness.SeedAsync(db => db.Agents.Add(agent));
        harness.ContainerExists = true;

        var result = await harness.Service.ReprovisionAsync("agent1");

        Assert.False(result.Success);
        Assert.Contains("names output style 'no-such-style'", result.Message);
        AssertOldContainerUntouched(harness);
    }

    [Fact]
    public async Task Reprovision_WithBypassPermissions_RefusesBeforeTheOldContainerIsRemoved()
    {
        await using var harness = ProvisioningHarness.Create();
        var agent = Claude();
        agent.PermissionMode = "bypassPermissions";
        await harness.SeedAsync(db => db.Agents.Add(agent));
        harness.ContainerExists = true;

        var result = await harness.Service.ReprovisionAsync("agent1");

        Assert.False(result.Success);
        Assert.StartsWith("PermissionMode 'bypassPermissions' is not supported", result.Message);
        AssertOldContainerUntouched(harness);
    }

    [Fact]
    public async Task Provision_WithALocalModelFault_IsARefusal_NotAnException_AndWritesNothing()
    {
        await using var harness = ProvisioningHarness.Create();
        await harness.SeedAsync(db => db.Agents.Add(CodexLocalWithACloudModel()));

        var result = await harness.Service.ProvisionAsync("agent1");

        Assert.False(result.Success);
        Assert.Contains("must be ollama/<tag> or lmstudio/<tag>", result.Message);
        Assert.Empty(harness.CreateBodies);
        Assert.False(Directory.Exists(harness.GeneratedDir("fleet-agent1")));
    }

    // ── The typed outcome that replaces the "not found" substring ────────────────────────────────

    [Fact]
    public async Task Reprovision_WhenTheContainerIsAlreadyGone_ProvisionsIt()
    {
        await using var harness = ProvisioningHarness.Create();
        await harness.SeedAsync(db => db.Agents.Add(Claude()));

        var result = await harness.Service.ReprovisionAsync("agent1");

        Assert.True(result.Success, result.Message);
        Assert.Single(harness.CreateBodies);
        Assert.DoesNotContain(harness.DockerRequests, r => r.StartsWith("DELETE ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Deprovision_NamesWhyItFailed()
    {
        await using var harness = ProvisioningHarness.Create();
        await harness.SeedAsync(db => db.Agents.Add(Claude()));

        var noContainer = await harness.Service.DeprovisionAsync("agent1");
        var noAgent = await harness.Service.DeprovisionAsync("nobody");

        Assert.Equal((false, ProvisionFailure.ContainerNotFound), (noContainer.Success, noContainer.Failure));
        Assert.Equal((false, ProvisionFailure.AgentNotFound), (noAgent.Success, noAgent.Failure));
    }

    [Fact]
    public async Task Reprovision_OfAnAgentThatDoesNotExist_SaysSo_AndCallsDockerForNothing()
    {
        await using var harness = ProvisioningHarness.Create();

        var result = await harness.Service.ReprovisionAsync("nobody");

        Assert.Equal((false, "agent not found in DB"), (result.Success, result.Message));
        Assert.Empty(harness.DockerRequests);
    }

    [Fact]
    public async Task Reprovision_WhenTheOldContainerCannotBeRemoved_CreatesNothing()
    {
        await using var harness = ProvisioningHarness.Create();
        await harness.SeedAsync(db => db.Agents.Add(Claude()));
        harness.ContainerExists = true;
        // The failure message names a container that was "not found … " nowhere: it is a removal
        // that failed, which the old substring test happened to tell apart only by its wording.
        harness.OnDockerRequest = call => call.StartsWith("DELETE ", StringComparison.Ordinal)
            ? throw new HttpRequestException("docker is not answering")
            : Task.CompletedTask;

        var result = await harness.Service.ReprovisionAsync("agent1");

        Assert.False(result.Success);
        Assert.StartsWith("deprovision failed: failed to remove container", result.Message);
        Assert.Empty(harness.CreateBodies);
    }

    [Fact]
    public void TheFailureReason_IsNotPartOfTheSerialisedResult()
    {
        var failed = ProvisionResult.Fail("agent1", "nope") with { Failure = ProvisionFailure.ContainerNotFound };

        var json = JsonSerializer.Serialize(failed, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Equal("""{"agentName":"agent1","success":false,"message":"nope"}""", json);
    }

    // ── What was rendered is what is created ─────────────────────────────────────────────────────

    [Fact]
    public async Task Reprovision_CreatesFromWhatItRenderedBeforeRemoving_EvenIfTheRowChangesMeanwhile()
    {
        await using var harness = ProvisioningHarness.Create();
        var agent = Claude();
        agent.Image = "fleet:agent-before";
        await harness.SeedAsync(db => db.Agents.Add(agent));
        harness.ContainerExists = true;
        // The row changes while the old container is being removed: a second render after the
        // removal would pick up a model and an image that nothing checked.
        harness.OnDockerRequest = async call =>
        {
            if (!call.StartsWith("DELETE ", StringComparison.Ordinal)) return;
            await harness.MutateAsync(async db =>
            {
                var row = await db.Agents.SingleAsync();
                row.Model = "changed-during-removal";
                row.Image = "fleet:agent-after";
            });
        };

        var result = await harness.Service.ReprovisionAsync("agent1");

        Assert.True(result.Success, result.Message);
        var appsettings = JsonDocument.Parse(
            await File.ReadAllTextAsync(Path.Combine(harness.GeneratedDir("fleet-agent1"), "appsettings.json")));
        Assert.Equal("sonnet", appsettings.RootElement.GetProperty("Agent").GetProperty("Model").GetString());
        var created = JsonDocument.Parse(harness.CreateBodies.Single());
        Assert.Equal("fleet:agent-before", created.RootElement.GetProperty("Image").GetString());
    }

    // ── One operation per agent at a time ────────────────────────────────────────────────────────

    [Fact]
    public async Task TwoReprovisionsOfOneAgent_RunOneAfterTheOther()
    {
        await using var harness = ProvisioningHarness.Create();
        await harness.SeedAsync(db => db.Agents.Add(Claude()));
        harness.ContainerExists = true;

        var firstIsRemoving = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var letItFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = false;
        harness.OnDockerRequest = async call =>
        {
            if (held || !call.StartsWith("DELETE ", StringComparison.Ordinal)) return;
            held = true;
            firstIsRemoving.SetResult();
            await letItFinish.Task;
        };

        var first = Task.Run(() => harness.Service.ReprovisionAsync("agent1"));
        await firstIsRemoving.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var callsWhileHeld = harness.DockerRequests.Count;

        var second = Task.Run(() => harness.Service.ReprovisionAsync("agent1"));
        await Task.Delay(300);
        Assert.False(second.IsCompleted);
        Assert.Equal(callsWhileHeld, harness.DockerRequests.Count);

        letItFinish.SetResult();
        var firstResult = await first.WaitAsync(TimeSpan.FromSeconds(10));
        // The first one's container now exists, as far as the second can see.
        harness.ContainerExists = true;
        var secondResult = await second.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(firstResult.Success, firstResult.Message);
        Assert.True(secondResult.Success, secondResult.Message);
        // The first ran to its start call before the second made any call at all.
        var afterHold = harness.DockerRequests.Skip(callsWhileHeld).ToList();
        var firstStart = afterHold.FindIndex(r => r.EndsWith("/start", StringComparison.Ordinal));
        Assert.True(firstStart >= 0);
        Assert.DoesNotContain(afterHold.Take(firstStart), r => r.StartsWith("GET ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ADifferentAgent_DoesNotWait()
    {
        await using var harness = ProvisioningHarness.Create();
        await harness.SeedAsync(db =>
        {
            db.Agents.Add(Claude("agent1"));
            db.Agents.Add(Claude("agent2"));
        });

        var firstIsCreating = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var letItFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = false;
        harness.OnDockerRequest = async call =>
        {
            if (held || !call.EndsWith("/containers/create", StringComparison.Ordinal)) return;
            held = true;
            firstIsCreating.SetResult();
            await letItFinish.Task;
        };

        var first = Task.Run(() => harness.Service.ProvisionAsync("agent1"));
        await firstIsCreating.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var second = await harness.Service.ProvisionAsync("agent2").WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(second.Success, second.Message);
        Assert.False(first.IsCompleted);
        letItFinish.SetResult();
        Assert.True((await first.WaitAsync(TimeSpan.FromSeconds(10))).Success);
    }

    [Fact]
    public async Task AWaitingOperation_GivesUpWhenItsCallerDoes_AndTheAgentIsNotLeftLocked()
    {
        await using var harness = ProvisioningHarness.Create();
        await harness.SeedAsync(db => db.Agents.Add(Claude()));

        var firstIsCreating = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var letItFinish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = false;
        harness.OnDockerRequest = async call =>
        {
            if (held || !call.EndsWith("/containers/create", StringComparison.Ordinal)) return;
            held = true;
            firstIsCreating.SetResult();
            await letItFinish.Task;
        };

        var first = Task.Run(() => harness.Service.ProvisionAsync("agent1"));
        await firstIsCreating.Task.WaitAsync(TimeSpan.FromSeconds(10));

        using var gaveUp = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => harness.Service.DeprovisionAsync("agent1", gaveUp.Token));

        letItFinish.SetResult();
        Assert.True((await first.WaitAsync(TimeSpan.FromSeconds(10))).Success);
        harness.ContainerExists = true;
        var after = await harness.Service.DeprovisionAsync("agent1").WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(after.Success, after.Message);
    }
}

public class KeyedAsyncLockTests
{
    [Fact]
    public async Task OneHolderPerKey_WhateverTheCase_AndTheTableEmptiesAfterwards()
    {
        var locks = new KeyedAsyncLock();

        var first = await locks.AcquireAsync("agent1");
        var second = locks.AcquireAsync("Agent1");
        var other = await locks.AcquireAsync("agent2").WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(second.IsCompleted);
        first.Dispose();
        var secondHolder = await second.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, locks.ActiveKeyCount);

        secondHolder.Dispose();
        secondHolder.Dispose(); // a second dispose releases nothing more
        other.Dispose();
        Assert.Equal(0, locks.ActiveKeyCount);

        (await locks.AcquireAsync("agent1").WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
    }

    [Fact]
    public async Task ACancelledWait_HoldsNothing()
    {
        var locks = new KeyedAsyncLock();
        var holder = await locks.AcquireAsync("agent1");
        using var cancelled = new CancellationTokenSource();

        var waiting = locks.AcquireAsync("agent1", cancelled.Token);
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);

        holder.Dispose();
        Assert.Equal(0, locks.ActiveKeyCount);
        (await locks.AcquireAsync("agent1").WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
    }
}
