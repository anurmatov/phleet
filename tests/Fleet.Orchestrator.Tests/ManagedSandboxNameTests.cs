using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Services;

namespace Fleet.Orchestrator.Tests;

/// <summary>
/// #503 W3: generated sandbox names fit the pinned stock contract (openshell-server validate_sandbox_name: 1–19 chars, a
/// DNS-1123 label without consecutive hyphens) for every supported agent name, without truncating identity away.
/// </summary>
public sealed class ManagedSandboxNameTests : IDisposable
{
    private readonly ManagedStockRuntimeAdapterTests _t = new();

    public void Dispose() => _t.Dispose();

    private const string LongestAgent = "a1234567890123456789012345678901234567890"; // 41 chars, the supported maximum

    private static readonly string[] AgentNames =
    [
        "a", "agent1", "x-y-z", LongestAgent, new string('a', 40) + "1", new string('a', 40) + "2", "0agent", "agent-with-hyphens",
    ];

    [Fact]
    public void Names_are_stock_valid_deterministic_and_distinct_for_every_agent_and_epoch()
    {
        var epochs = Enumerable.Range(0, 6).Select(_ => Guid.NewGuid()).ToList();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var agent in AgentNames)
            foreach (var epoch in epochs)
            {
                var name = StockOpenShellRuntime.SandboxNameFor(agent, epoch);
                Assert.True(ManagedStockRuntimeAdapterTests.FakeCli.StockSandboxName(name), name);
                Assert.Equal(name, StockOpenShellRuntime.SandboxNameFor(agent, epoch));
                Assert.True(names.Add(name), $"collision for {agent}/{epoch}");
            }
        // The old scheme already broke the limit for any agent name of five or more characters.
        Assert.False(ManagedStockRuntimeAdapterTests.FakeCli.StockSandboxName($"fleet-agent1-{epochs[0].ToString("N")[..8]}"));
    }

    [Fact]
    public void Same_epoch_prefix_or_shared_agent_prefix_never_share_a_name()
    {
        var a = Guid.Parse("11111111-0000-4000-8000-000000000001");
        var b = Guid.Parse("11111111-0000-4000-8000-000000000002"); // same first 8 hex as a
        Assert.NotEqual(StockOpenShellRuntime.SandboxNameFor("agent1", a), StockOpenShellRuntime.SandboxNameFor("agent1", b));
        Assert.NotEqual(StockOpenShellRuntime.SandboxNameFor(new string('a', 40) + "1", a), StockOpenShellRuntime.SandboxNameFor(new string('a', 40) + "2", a));
    }

    private Agent LongAgent()
    {
        var agent = _t.ManagedAgent(desired: "openshell", applied: "docker");
        agent.Name = agent.ShortName = agent.ContainerName = LongestAgent;
        return _t._store.Add(agent);
    }

    [Fact]
    public async Task A_long_agent_name_applies_with_a_stock_valid_name_and_full_identity_labels()
    {
        LongAgent();
        _t._host.DockerStates = ["exited"];

        var result = await _t.Runtime().ApplyAsync(LongestAgent, default);

        Assert.True(result.Ok, result.Message);
        var applied = ManagedRecord.Parse(_t._store.Get(LongestAgent).AppliedRuntimeBinding).Applied!;
        Assert.Equal(StockOpenShellRuntime.SandboxNameFor(LongestAgent, applied.Epoch), applied.SandboxName);
        var create = Assert.Single(_t._cli.Calls, c => c.Args.Take(2).SequenceEqual(["sandbox", "create"])).Args;
        Assert.Equal(applied.SandboxName, create[create.IndexOf("--name") + 1]);
        Assert.Contains("fleet.agent=" + LongestAgent, create);
        Assert.Contains("fleet.epoch=" + applied.Epoch.ToString("N"), create);
        Assert.Equal(_t._cli.Sandboxes[applied.SandboxName].Id, applied.SandboxId);
    }

    [Theory]
    [InlineData("taken")]
    [InlineData("unobservable")]
    public async Task A_name_not_proven_absent_refuses_before_any_effect(string state)
    {
        _t._store.Add(_t.ManagedAgent(desired: "openshell", applied: "docker"));
        // Taken: the derived name resolves to a foreign sandbox. Unobservable: the listing never ends within the page bound.
        if (state == "taken") _t._cli.NameTaken = true;
        else _t._cli.ListPages = [.. Enumerable.Range(0, StockOpenShellRuntime.MaxListPages + 1).Select(_ => Array.Empty<string>())];
        var stops = 0;
        _t._host.DockerStates = ["running"];

        var result = await _t.Runtime(dockerStop: _ => { stops++; return Task.FromResult(true); }).ApplyAsync("agent1", default);

        Assert.Equal((409, "sandbox_name_not_proven_absent"), (result.Status, result.Code));
        Assert.Equal(0, stops);
        Assert.DoesNotContain(_t._cli.Calls, c => c.Args.Take(2).SequenceEqual(["sandbox", "create"]) || c.Args.Take(2).SequenceEqual(["sandbox", "stop"]));
        Assert.Empty(_t._broker.Puts);
        var record = ManagedRecord.Parse(_t._store.Get("agent1").AppliedRuntimeBinding);
        Assert.Equal(("none", null), (record.State, record.Candidate)); // a foreign sandbox is never adopted
    }

    [Fact]
    public async Task A_rejected_create_holds_without_a_uuid_and_the_supported_revoke_clears_it()
    {
        _t._store.Add(_t.ManagedAgent(desired: "openshell", applied: "docker"));
        _t._host.DockerStates = ["exited"];
        _t._cli.CreateRejects = true;
        var runtime = _t.Runtime();

        var apply = await runtime.ApplyAsync("agent1", default);

        Assert.Equal("sandbox_create_unknown", apply.Code);
        var held = ManagedRecord.Parse(_t._store.Get("agent1").AppliedRuntimeBinding);
        Assert.Equal("needs_attention", held.State);
        Assert.True(string.IsNullOrEmpty(held.Candidate!.SandboxId));
        Assert.DoesNotContain(_t._cli.Calls, c => c.Args.Take(2).SequenceEqual(["sandbox", "stop"]) || c.Args.Contains("detach"));
        _t._cli.Calls.Clear();

        var revoke = await runtime.RevokeAsync("agent1", default);

        Assert.True(revoke.Ok, revoke.Message);
        var record = ManagedRecord.Parse(_t._store.Get("agent1").AppliedRuntimeBinding);
        Assert.Equal(("revoked", 0, null), (record.State, record.Errors.Count, record.Candidate));
        Assert.Empty(record.Superseded); // an API-key candidate carries no subscription custody
        Assert.Contains(_t._cli.Calls, c => c.Args.Take(2).SequenceEqual(["sandbox", "list"])); // absence proven, not assumed
        Assert.DoesNotContain(_t._cli.Calls, c => c.Args.Take(2).SequenceEqual(["sandbox", "stop"]) || c.Args.Contains("delete"));
    }

    // ── #503 W4: post-create adoption needs the candidate's own agent and epoch labels ──

    private bool Touches(ManagedStockRuntimeAdapterTests.Call call, string name) =>
        call.Args.Contains(name) && (call.Args.Take(2).SequenceEqual(["sandbox", "stop"]) || call.Args.Take(2).SequenceEqual(["sandbox", "delete"])
            || call.Args.Take(2).SequenceEqual(["sandbox", "start"]) || call.Args.Take(2).SequenceEqual(["sandbox", "exec"])
            || call.Args.Take(2).SequenceEqual(["sandbox", "upload"]) || call.Args.Contains("detach") || call.Args.Contains("attach"));

    [Theory]
    [InlineData("foreign_labels")]
    [InlineData("no_labels")]
    public async Task A_foreign_sandbox_appearing_between_the_absence_proof_and_create_is_never_adopted_or_cleaned(string kind)
    {
        _t._store.Add(_t.ManagedAgent(desired: "openshell", applied: "docker"));
        _t._host.DockerStates = ["exited"];
        var foreignId = Guid.NewGuid().ToString();
        string? raced = null;
        _t._cli.BeforeCreate = name =>
        {
            // Absent at the proof, then another owner takes the name; the pinned server refuses our duplicate create.
            raced = name;
            _t._cli.Sandboxes[name] = (foreignId, "Ready");
            if (kind == "foreign_labels") _t._cli.Labels[name] = new() { ["fleet.agent"] = "intruder", ["fleet.epoch"] = new string('0', 32) };
        };
        var runtime = _t.Runtime();

        var apply = await runtime.ApplyAsync("agent1", default);

        Assert.Equal("sandbox_owner_unverified", apply.Code);
        var held = ManagedRecord.Parse(_t._store.Get("agent1").AppliedRuntimeBinding);
        Assert.Equal("needs_attention", held.State);
        Assert.Equal(raced, held.Candidate!.SandboxName);
        Assert.True(string.IsNullOrEmpty(held.Candidate.SandboxId)); // the foreign UUID is never recorded
        Assert.Contains("sandbox_create_unknown", held.Errors);
        Assert.Empty(_t._broker.Puts);

        // The supported revoke cannot prove the name absent and stays held; it still never touches the foreign sandbox.
        var revoke = await runtime.RevokeAsync("agent1", default);

        Assert.False(revoke.Ok);
        Assert.Equal("needs_attention", ManagedRecord.Parse(_t._store.Get("agent1").AppliedRuntimeBinding).State);
        Assert.DoesNotContain(_t._cli.Calls, c => Touches(c, raced!));
        Assert.Equal((foreignId, "Ready"), _t._cli.Sandboxes[raced!]);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("other_agent")]
    [InlineData("other_epoch")]
    [InlineData("agent_only")]
    public async Task A_created_sandbox_without_this_epochs_exact_identity_is_held_untouched(string labels)
    {
        _t._store.Add(_t.ManagedAgent(desired: "openshell", applied: "docker"));
        _t._host.DockerStates = ["exited"];
        _t._cli.LabelsOverride = labels switch
        {
            "missing" => new(),
            "other_agent" => new() { ["fleet.agent"] = "agent2", ["fleet.epoch"] = new string('1', 32) },
            "other_epoch" => new() { ["fleet.agent"] = "agent1", ["fleet.epoch"] = new string('0', 32) },
            _ => new() { ["fleet.agent"] = "agent1" },
        };

        var apply = await _t.Runtime().ApplyAsync("agent1", default);

        Assert.Equal((502, "sandbox_owner_unverified"), (apply.Status, apply.Code));
        var held = ManagedRecord.Parse(_t._store.Get("agent1").AppliedRuntimeBinding);
        Assert.True(string.IsNullOrEmpty(held.Candidate!.SandboxId));
        Assert.DoesNotContain(_t._cli.Calls, c => Touches(c, held.Candidate.SandboxName));
        Assert.Equal("Ready", _t._cli.Sandboxes[held.Candidate.SandboxName].Phase);
    }

    [Fact]
    public async Task A_genuinely_owned_sandbox_after_a_failed_create_is_recorded_stopped_and_revocable()
    {
        _t._store.Add(_t.ManagedAgent(desired: "openshell", applied: "docker"));
        _t._host.DockerStates = ["exited"];
        _t._cli.CreateFailsAfterAllocating = true;
        var runtime = _t.Runtime();

        var apply = await runtime.ApplyAsync("agent1", default);

        Assert.Equal("sandbox_create_unknown", apply.Code);
        var held = ManagedRecord.Parse(_t._store.Get("agent1").AppliedRuntimeBinding);
        var name = held.Candidate!.SandboxName;
        Assert.Equal(_t._cli.Sandboxes[name].Id, held.Candidate.SandboxId); // its own labels: recorded for cleanup
        Assert.Contains(_t._cli.Calls, c => c.Args.SequenceEqual(["sandbox", "stop", name]));
        Assert.Equal("Stopped", _t._cli.Sandboxes[name].Phase);

        var revoke = await runtime.RevokeAsync("agent1", default);

        Assert.True(revoke.Ok, revoke.Message);
        Assert.Equal("revoked", ManagedRecord.Parse(_t._store.Get("agent1").AppliedRuntimeBinding).State);
    }
}
