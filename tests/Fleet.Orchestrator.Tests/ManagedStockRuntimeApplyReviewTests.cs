using System.Text.Json.Nodes;
using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Fleet.Orchestrator.Tests;

/// <summary>
/// #492 apply/revoke review regressions on the synthetic stock CLI, store, host and broker of
/// <see cref="ManagedStockRuntimeAdapterTests"/>. Source checks only, not runtime qualification.
/// </summary>
public sealed class ManagedStockRuntimeApplyReviewTests : IDisposable
{
    private readonly ManagedStockRuntimeAdapterTests _t = new();

    public void Dispose() => _t.Dispose();

    [Fact]
    public async Task Hold_appends_to_earlier_errors_and_partial_names()
    {
        var binding = _t.Binding("fleet-agent1-a1a1a1a1", Guid.NewGuid());
        _t._cli.Sandboxes[binding.SandboxName] = (binding.SandboxId, "Ready");
        _t._store.Add(_t.ManagedAgent(desired: "openshell", applied: "openshell",
            record: new ManagedRecord { State = "needs_attention", Applied = binding, Errors = ["earlier_hold", "partial:user-x"] }));
        _t._broker.FailDeletes = true;

        var revoke = await _t.Runtime().RevokeAsync("agent1", default);

        Assert.Equal("revoke_unresolved", revoke.Code);
        var errors = ManagedRecord.Parse(_t._store.Get("agent1").AppliedRuntimeBinding).Errors;
        Assert.Equal(["earlier_hold", "partial:user-x", "revoke_unresolved"], errors.Take(3));
        Assert.Contains(errors, e => e.StartsWith("broker:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Revoke_detaches_the_union_of_applied_and_candidate_providers()
    {
        var binding = _t.Binding("fleet-agent1-b2b2b2b2", Guid.NewGuid());
        var candidate = binding with { Epoch = Guid.NewGuid(), ServiceProviders = ["fleet-agent1-telegram"] };
        _t._cli.Sandboxes[binding.SandboxName] = (binding.SandboxId, "Ready");
        _t._store.Add(_t.ManagedAgent(desired: "openshell", applied: "openshell",
            record: new ManagedRecord { State = "needs_attention", Applied = binding, Candidate = candidate }));

        var revoke = await _t.Runtime().RevokeAsync("agent1", default);

        Assert.True(revoke.Ok, revoke.Message);
        Assert.Contains(_t._cli.Calls, c => c.Args.SequenceEqual(["sandbox", "provider", "detach", binding.SandboxName, "fleet-agent1-telegram"]));
        Assert.Contains(_t._cli.Calls, c => c.Args.SequenceEqual(["sandbox", "provider", "detach", binding.SandboxName, "fleet-agent1-openai"]));
        Assert.Null(ManagedRecord.Parse(_t._store.Get("agent1").AppliedRuntimeBinding).Candidate);
    }

    [Fact]
    public async Task Reuse_apply_keeps_live_control_credentials_until_the_cut_succeeds()
    {
        var binding = _t.Binding("fleet-agent1-c3c3c3c3", Guid.NewGuid());
        _t._cli.Sandboxes[binding.SandboxName] = (binding.SandboxId, "Ready");
        _t._store.Add(_t.ManagedAgent(desired: "openshell", applied: "openshell", record: new ManagedRecord { State = "applied", Applied = binding }));
        var runtime = _t.Runtime();
        var control = Path.Combine(_t._dir, "managed", "authority", "agent1.control.password");
        Directory.CreateDirectory(Path.GetDirectoryName(control)!);
        File.WriteAllText(control, "live-control");
        _t.WriteAuthority("active");
        _t._broker.FailDeletes = true; // the cut of the live epoch fails

        var result = await runtime.ApplyAsync("agent1", default);

        Assert.Equal("revoke_unresolved", result.Code);
        Assert.Equal("live-control", File.ReadAllText(control));
        Assert.Null(JsonNode.Parse(File.ReadAllText(_t.AuthorityPath()))!["Epoch"]); // the candidate epoch was never published
        Assert.Contains(_t._cli.Calls, c => c.Args.SequenceEqual(["sandbox", "stop", binding.SandboxName]));
        Assert.Empty(_t._broker.Puts);
    }

    [Fact]
    public async Task Every_selected_provider_profile_is_pinned_and_verified_before_the_old_runtime_stops()
    {
        var agent = _t._store.Add(_t.ManagedAgent(desired: "openshell", applied: "docker"));
        agent.EnvRefs.Add(new AgentEnvRef { EnvKeyName = "TELEGRAM_AGENT1_BOT_TOKEN" });
        _t._broker.Credentials.Extra["TELEGRAM_AGENT1_BOT_TOKEN"] = "424242:synthetic-bot-secret-never-in-argv";
        var pins = JsonNode.Parse(ManagedStockRuntimeAdapterTests.FakeStore.PinnedProfiles)!.AsObject();
        pins.Remove(StockOpenShellRuntime.TelegramProfile);
        _t._store.ProfileHashes = pins.ToJsonString();
        var stops = 0;
        _t._host.DockerStates = ["running"];

        var result = await _t.Runtime(dockerStop: _ => { stops++; return Task.FromResult(true); }).ApplyAsync("agent1", default);

        Assert.Equal("provider_profile_unpinned:" + StockOpenShellRuntime.TelegramProfile, result.Code);
        Assert.Equal(0, stops);
        Assert.Empty(_t._broker.Puts);
    }

    [Fact]
    public async Task Config_dir_with_a_linked_ancestor_or_foreign_owner_refuses_before_any_write()
    {
        _t._store.Add(_t.ManagedAgent(desired: "openshell", applied: "docker"));
        var runtime = _t.Runtime(); // writes the fixture's private ConfigDir
        var link = Path.Combine(Path.GetDirectoryName(_t._dir)!, "linked-" + Guid.NewGuid().ToString("N"));
        Directory.CreateSymbolicLink(link, Path.GetDirectoryName(_t._dir)!);
        try
        {
            var linked = new StockOpenShellRuntime(new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(
                    new Dictionary<string, string?> { ["StockOpenShell:ManagedEnabled"] = "true", ["StockOpenShell:ConfigDir"] = Path.Combine(link, Path.GetFileName(_t._dir)) }).Build(),
                _t._cli, null!, _t._store, _t._host, _t._broker.Credentials,
                new Microsoft.Extensions.DependencyInjection.ServiceCollection().BuildServiceProvider(),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<StockOpenShellRuntime>.Instance);
            Assert.Equal("managed_config_dir_not_private", await DirFault(linked));
        }
        finally { Directory.Delete(link); }

        Assert.Null(await DirFault(runtime));
        if (StockOpenShellRuntime.OwnerUid(_t._dir) == 0)
        {
            File.SetUnixFileMode(_t._dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Assert.Equal(0, Chown(_t._dir, 65534, 65534));
            try { Assert.Equal("managed_config_dir_not_owned", await DirFault(runtime)); }
            finally { Chown(_t._dir, 0, 0); }
        }
    }

    [Fact]
    public async Task Docker_handoff_mirrors_sandbox_deletions_but_never_touches_control_state()
    {
        var ws = _t.DockerWorkspaceDir;
        Directory.CreateDirectory(Path.Combine(ws, "gone-dir", "nested"));
        File.WriteAllText(Path.Combine(ws, "gone-dir", "nested", "old.txt"), "deleted in sandbox");
        File.WriteAllText(Path.Combine(ws, "deleted.txt"), "deleted in sandbox");
        File.WriteAllText(Path.Combine(ws, "only-dir"), "became a directory in sandbox");
        Directory.CreateDirectory(Path.Combine(ws, ".ssh"));
        File.WriteAllText(Path.Combine(ws, ".ssh", "id"), "control");
        File.WriteAllText(Path.Combine(ws, ".mcp.json"), "{\"docker\":true}");
        _t._store.Add(_t.ManagedAgent(desired: "openshell", applied: "docker"));
        var runtime = _t.Runtime();
        Assert.True((await runtime.ApplyAsync("agent1", default)).Ok);
        // #507 A5: user edits in W inside the sandbox: a new file, deletions and a file that became a directory.
        var w = _t._cli.Fs(ManagedRecord.Parse(_t._store.Get("agent1").AppliedRuntimeBinding).Applied!.SandboxName, "/home/agent/workspace");
        File.WriteAllText(Path.Combine(w, "marker-back.txt"), "from sandbox");
        File.Delete(Path.Combine(w, "deleted.txt"));
        Directory.Delete(Path.Combine(w, "gone-dir"), recursive: true);
        File.Delete(Path.Combine(w, "only-dir"));
        Directory.CreateDirectory(Path.Combine(w, "only-dir"));

        _t._store.Get("agent1").RuntimeKind = "docker";
        Assert.True((await runtime.ApplyAsync("agent1", default)).Ok);

        // The sandbox's W now holds only marker-back.txt and only-dir/ (and its routes file).
        Assert.Equal("from sandbox", File.ReadAllText(Path.Combine(ws, "marker-back.txt")));
        Assert.True(Directory.Exists(Path.Combine(ws, "only-dir")));
        Assert.False(File.Exists(Path.Combine(ws, "deleted.txt")));
        Assert.False(Directory.Exists(Path.Combine(ws, "gone-dir")));
        Assert.Equal("control", File.ReadAllText(Path.Combine(ws, ".ssh", "id")));
        Assert.Equal("{\"docker\":true}", File.ReadAllText(Path.Combine(ws, ".mcp.json")));
    }

    [Fact]
    public async Task Pinned_uploads_land_under_the_non_root_home_where_the_agent_reads_them()
    {
        Directory.CreateDirectory(_t.DockerWorkspaceDir);
        File.WriteAllText(Path.Combine(_t.DockerWorkspaceDir, "marker.txt"), "keep");
        _t._store.Add(_t.ManagedAgent(desired: "openshell", applied: "docker"));

        Assert.True((await _t.Runtime().ApplyAsync("agent1", default)).Ok);

        var remote = _t._cli.Uploads.Select(u => u.Remote).ToHashSet();
        // Nothing nests (/app/app, /workspace/workspace) or targets the image-owned /app or root-owned /trust.
        Assert.DoesNotContain(remote, r => r.StartsWith("/app/", StringComparison.Ordinal) || r.StartsWith("/trust/", StringComparison.Ordinal)
            || r.StartsWith("/home/agent/app/app/", StringComparison.Ordinal) || r.StartsWith("/workspace", StringComparison.Ordinal)
            || r.StartsWith("/home/agent/workspace/workspace/", StringComparison.Ordinal));
        // #507: the user tree, its fresh routes and the generated WorkDir are all W, inside the stock CLI's copy root.
        Assert.Contains("/home/agent/workspace/marker.txt", remote);
        Assert.Contains("/home/agent/workspace/.mcp.json", remote);
        Assert.Equal("/home/agent/workspace", JsonNode.Parse(_t._cli.Uploads.Last(u => u.Remote == "/home/agent/app/appsettings.json").Content!)!["Agent"]!["WorkDir"]!.GetValue<string>());
        Assert.Equal("/home/agent/workspace", ManagedRecord.Parse(_t._store.Get("agent1").AppliedRuntimeBinding).Applied!.WorkloadWorkspacePath);
        Assert.Contains("/home/agent/app/" + Fleet.Shared.HarnessDefaults.CodexRootTomlFileName, remote);
        var settings = JsonNode.Parse(_t._cli.Uploads.Last(u => u.Remote == "/home/agent/app/appsettings.json").Content!)!["StockOpenShell"]!;
        // Every trust path the agent is configured with is exactly where an upload placed it.
        foreach (var key in new[] { "EnrollmentFile", "AuthorityFile", "BrokerPasswordFile" })
            Assert.Contains(settings[key]!.GetValue<string>(), remote);
        Assert.Equal("/home/agent/app", settings["AppRoot"]!.GetValue<string>());
        Assert.Equal("/home/agent/trust/enrollment.json", _t._cli.Uploads.Last().Remote); // activated enrollment, same path
    }

    [Fact]
    public async Task Workspace_upload_gets_the_same_bound_as_the_workspace_download()
    {
        _t._store.Add(_t.ManagedAgent(desired: "openshell", applied: "docker"));
        var runtime = _t.Runtime();
        Assert.True((await runtime.ApplyAsync("agent1", default)).Ok);
        _t._store.Get("agent1").RuntimeKind = "docker";
        Assert.True((await runtime.ApplyAsync("agent1", default)).Ok);

        var upload = Assert.Single(_t._cli.Timeouts, c => c.Args.Take(2).SequenceEqual(["sandbox", "upload"]) && Path.GetFileName(c.Args[3]) == "workspace");
        var download = Assert.Single(_t._cli.Timeouts, c => c.Args.Take(2).SequenceEqual(["sandbox", "download"]));
        Assert.Equal(TimeSpan.FromMinutes(5), runtime.TransferTimeout);
        Assert.Equal(runtime.TransferTimeout, upload.Timeout);
        Assert.Equal(download.Timeout, upload.Timeout);
        Assert.NotEqual(runtime.LegTimeout, upload.Timeout);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"not\":\"an array\"}")]
    [InlineData("[{}]")]
    public async Task Broker_revoke_records_a_connections_parse_error_and_still_runs_every_other_step(string listing)
    {
        var epoch = Guid.NewGuid();
        var provisioner = _t.Provisioner();
        _t._broker.ConnectionsBody = listing;

        var revoke = await provisioner.RevokeEpochAsync(epoch, default);

        Assert.False(revoke.Ok);
        Assert.Equal("broker_revoke_unresolved", revoke.Code);
        Assert.DoesNotContain("broker_management_unavailable", revoke.OwnedPartials);
        Assert.Equal(StockBrokerProvisioner.Roles.Select(r => "connections:" + StockBrokerProvisioner.Principal(epoch, r)), revoke.OwnedPartials);
        foreach (var role in StockBrokerProvisioner.Roles)
        {
            var user = StockBrokerProvisioner.Principal(epoch, role);
            Assert.Contains(_t._broker.Deletes, d => d.StartsWith("/api/permissions/", StringComparison.Ordinal) && d.EndsWith("/" + user, StringComparison.Ordinal));
            Assert.Contains(_t._broker.Deletes, d => d == "/api/users/" + user);
        }
        // The management user's own grant on the epoch vhost is still removed after the failed listings.
        Assert.Equal(StockBrokerProvisioner.Roles.Length + 1, _t._broker.Deletes.Count(d => d.StartsWith("/api/permissions/", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Stop_ends_within_its_own_overall_bound_when_every_leg_hangs()
    {
        var binding = _t.Binding("fleet-agent1-d4d4d4d4", Guid.NewGuid());
        _t._cli.Sandboxes[binding.SandboxName] = (binding.SandboxId, "Ready");
        _t._store.Add(_t.ManagedAgent(desired: "openshell", applied: "openshell", record: new ManagedRecord { State = "applied", Applied = binding }));
        _t._cli.Hang.Add(binding.SandboxName); // every leg on the sandbox ignores its own leg bound

        var stop = _t.Runtime().StopAsync("agent1", default);
        var done = await Task.WhenAny(stop, Task.Delay(TimeSpan.FromSeconds(30)));

        Assert.Same(stop, done); // the fixture's 5 s FanoutTimeout, not an unbounded wait
        Assert.Equal("stop_unknown", (await stop).Code);
        var record = ManagedRecord.Parse(_t._store.Get("agent1").AppliedRuntimeBinding);
        Assert.Equal("needs_attention", record.State);
        Assert.Null(record.Operation);
    }

    [Fact]
    public async Task Start_legs_ignore_a_client_disconnect()
    {
        var binding = _t.Binding("fleet-agent1-e5e5e5e5", Guid.NewGuid());
        _t._cli.Sandboxes[binding.SandboxName] = (binding.SandboxId, "Stopped");
        _t._store.Add(_t.ManagedAgent(desired: "openshell", applied: "openshell", record: new ManagedRecord { State = "stopped", Applied = binding }));
        _t.Enroll(binding);
        using var client = new CancellationTokenSource();
        var probes = 0;
        // The client disconnects (RequestAborted) during staging; the next poll still runs on Start's own deadline.
        _t._cli.OnExec = script => { if (script.Contains(".fleet-staged") && probes++ == 0) { client.Cancel(); return 1; } return 0; };

        var start = await _t.Runtime().StartAsync("agent1", client.Token);

        Assert.True(start.Ok, start.Message);
        Assert.Equal("started", start.Code);
        Assert.Equal("applied", ManagedRecord.Parse(_t._store.Get("agent1").AppliedRuntimeBinding).State);
    }

    [Fact]
    public async Task A_claim_left_by_a_returned_call_is_shown_interrupted_not_busy()
    {
        var binding = _t.Binding("fleet-agent1-f6f6f6f6", Guid.NewGuid());
        _t._cli.Sandboxes[binding.SandboxName] = (binding.SandboxId, "Ready");
        var agent = _t.ManagedAgent(desired: "openshell", applied: "openshell", record: new ManagedRecord { State = "applied", Applied = binding });
        agent.Name = "agent-a8c"; // the process-wide live-call set is shared with concurrently running "agent1" tests
        _t._store.Add(agent);
        var registry = new AgentRegistry(new TaskHistoryStore(), Microsoft.Extensions.Logging.Abstractions.NullLogger<AgentRegistry>.Instance);
        registry.PreloadFromDbConfig(agent);
        // The final (claim-clearing) commit fails without committing: the call returns and its claim stays stored.
        _t._store.OnSwap = (_, next) => ManagedRecord.Parse(next).Operation is null
            ? throw new InvalidOperationException("synthetic_db_failure") : Task.CompletedTask;

        var stop = await _t.Runtime(registry: registry).StopAsync("agent-a8c", default);

        Assert.Equal("stop_state_unknown", stop.Code);
        var stored = _t._store.Get("agent-a8c").AppliedRuntimeBinding;
        Assert.StartsWith("stop:", ManagedRecord.Parse(stored).Operation);
        var shown = registry.Get("agent-a8c")!;
        Assert.Equal("needs_attention", shown.ManagedState);
        Assert.Contains("interrupted_stop", shown.ManagedErrors!);
        Assert.False(shown.Managed!.Pending);
        // A later reload of the same stored record (fresh registry) agrees.
        var (state, errors, status) = AgentRegistry.ManagedView(stored, agentName: "agent-a8c");
        Assert.Equal(("needs_attention", false), (state, status!.Pending));
        Assert.Contains("interrupted_stop", errors!);
    }

    [Fact]
    public async Task Config_dir_owned_by_the_recorded_operator_uid_is_accepted_and_any_other_owner_refused()
    {
        var runtime = _t.Runtime(); // creates the fixture's private ConfigDir
        if (StockOpenShellRuntime.OwnerUid(_t._dir) != 0) return; // chown needs root
        StockOpenShellRuntime WithOwner(string? uid) => new(new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?> { ["StockOpenShell:ManagedEnabled"] = "true", ["StockOpenShell:ConfigDir"] = _t._dir, ["StockOpenShell:ConfigDirOwnerUid"] = uid }).Build(),
            _t._cli, null!, _t._store, _t._host, _t._broker.Credentials, new ServiceCollection().BuildServiceProvider(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<StockOpenShellRuntime>.Instance);
        // setup.sh/upgrade.sh create openshell-client as the host operator (here 1000) and record FLEET_STOCK_CONFIG_DIR_UID.
        Assert.Equal(0, Chown(_t._dir, 1000, 1000));
        try
        {
            Assert.Null(await DirFault(WithOwner("1000")));
            Assert.Equal("managed_config_dir_not_owned", await DirFault(WithOwner(null)));
            Assert.Equal("managed_config_dir_not_owned", await DirFault(WithOwner("1001")));
            Assert.Equal("managed_config_dir_not_owned", await DirFault(WithOwner("-1")));
            Assert.Equal(0, Chown(_t._dir, 65534, 65534));
            Assert.Equal("managed_config_dir_not_owned", await DirFault(WithOwner("1000")));
        }
        finally { Chown(_t._dir, 0, 0); }
        Assert.Null(await DirFault(WithOwner("1000"))); // the container's own (root) uid stays accepted
        Assert.Null(await DirFault(runtime));
    }

    [Fact]
    public async Task Image_change_is_refused_until_revoke_then_reapply_creates_a_fresh_pinned_sandbox_with_the_workspace()
    {
        var old = _t.Binding("fleet-agent1-a7a7a7a7", Guid.NewGuid()) with { Image = "registry.example.test/agent@sha256:old" };
        _t._cli.Sandboxes[old.SandboxName] = (old.SandboxId, "Ready");
        _t._store.Add(_t.ManagedAgent(desired: "openshell", applied: "openshell", record: new ManagedRecord { State = "applied", Applied = old }));
        var runtime = _t.Runtime();
        _t.WriteAuthority("active");

        Assert.Equal("managed_image_change_requires_revoke", (await runtime.ApplyAsync("agent1", default)).Code);
        Assert.DoesNotContain(_t._cli.Calls, c => c.Args.Take(2).SequenceEqual(["sandbox", "create"]));

        Assert.True((await runtime.RevokeAsync("agent1", default)).Ok);
        var apply = await runtime.ApplyAsync("agent1", default);

        Assert.True(apply.Ok, apply.Message);
        Assert.Equal("applied_openshell", apply.Code);
        var create = Assert.Single(_t._cli.Calls, c => c.Args.Take(2).SequenceEqual(["sandbox", "create"])).Args;
        var image = create[create.IndexOf("--from") + 1];
        Assert.NotEqual(old.Image, image); // the configured (pinned) image, not the old one
        var record = ManagedRecord.Parse(_t._store.Get("agent1").AppliedRuntimeBinding);
        Assert.Equal(("applied", image), (record.State, record.Applied!.Image));
        Assert.NotEqual(old.SandboxId, record.Applied.SandboxId);
        Assert.NotEqual(old.SandboxName, record.Applied.SandboxName);
        // The old sandbox's W was exported through its filtered snapshot inside the stock root and the old sandbox is left stopped.
        Assert.Contains(_t._cli.Calls, c => c.Args.Take(3).SequenceEqual(["sandbox", "download", old.SandboxName])
            && c.Args[3].StartsWith("/home/agent/.phleet-workspace-transfer/", StringComparison.Ordinal) && c.Args[3].EndsWith("/data", StringComparison.Ordinal));
        Assert.Contains("/home/agent/workspace/marker-back.txt", _t._cli.Uploads.Select(u => u.Remote));
        Assert.Equal("from sandbox", File.ReadAllText(_t._cli.Fs(old.SandboxName, "/home/agent/workspace/marker-back.txt")));
        Assert.Equal("Stopped", _t._cli.Sandboxes[old.SandboxName].Phase);
        // Ownership tracking survives the replacement: the old sandbox stays recorded with its UUID.
        var superseded = Assert.Single(record.Superseded);
        Assert.Equal((old.SandboxName, old.SandboxId), (superseded.SandboxName, superseded.SandboxId));
    }

    /// <summary>
    /// #503 W12: the live layout. ConfigDir and the mounted <c>managed/authority</c> parents belong to the recorded host
    /// operator (here 1000, 0700), the orchestrator writes as root and the outside signer reads as the operator.
    /// </summary>
    [Fact]
    public async Task Root_written_authority_records_are_published_for_the_operator_reader_and_secrets_stay_writer_owned()
    {
        var runtime = _t.Runtime(); // creates the fixture's private ConfigDir
        if (StockOpenShellRuntime.OwnerUid(_t._dir) != 0) return; // a cross-uid publication needs a root writer
        var authority = Path.Combine(_t._dir, "managed", "authority");
        Directory.CreateDirectory(authority, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        foreach (var dir in new[] { _t._dir, Path.GetDirectoryName(authority)!, authority }) Assert.Equal(0, Chown(dir, 1000, 1000));
        _t.ConfigDirOwnerUid = "1000";
        _t._store.Add(_t.ManagedAgent(desired: "openshell", applied: "docker"));
        var record = Path.Combine(authority, "agent1.json");

        var apply = await _t.Runtime().ApplyAsync("agent1", default);

        Assert.True(apply.Ok, apply.Message);
        Assert.Equal((1000u, UnixFileMode.UserRead | UnixFileMode.UserWrite), (StockOpenShellRuntime.OwnerUid(record), File.GetUnixFileMode(record)));
        Assert.Equal(File.ReadAllText(record), ReadAs(1000, record)); // what `test -r` as the signer uid refused at 64efc522
        var applied = ManagedRecord.Parse(_t._store.Get("agent1").AppliedRuntimeBinding).Applied!;
        Assert.Equal(applied.SandboxId, Fleet.Shared.Security.StockEnrollmentAuthority.Load(record).SandboxId);
        // Only the record changes hands: control and observer passwords stay root-owned 0600 and unreadable to the reader.
        foreach (var secret in new[] { Path.Combine(authority, "agent1.control.password"), Path.Combine(_t._dir, "managed", "observer", "agent1.observer.password") })
        {
            Assert.Equal((0u, UnixFileMode.UserRead | UnixFileMode.UserWrite), (StockOpenShellRuntime.OwnerUid(secret), File.GetUnixFileMode(secret)));
            Assert.Null(ReadAs(1000, secret));
        }

        // Atomic replacement keeps the reader: the revoked record is still the operator's and reads as revoked.
        Assert.True((await _t.Runtime().RevokeAsync("agent1", default)).Ok);
        Assert.Equal(1000u, StockOpenShellRuntime.OwnerUid(record));
        Assert.Equal("revoked", JsonNode.Parse(ReadAs(1000, record)!)!["State"]!.GetValue<string>());
        Assert.Empty(Directory.GetFiles(authority, "*.tmp"));
    }

    /// <summary>Review W12-DIR: setup prepared only ConfigDir, so the runtime itself creates <c>managed/authority</c> as root.</summary>
    [Fact]
    public async Task A_fresh_root_created_authority_directory_refuses_active_publication()
    {
        var runtime = _t.Runtime();
        if (StockOpenShellRuntime.OwnerUid(_t._dir) != 0) return; // a cross-uid publication needs a root writer
        Assert.Equal(0, Chown(_t._dir, 1000, 1000)); // setup.sh creates ConfigDir as the recorded operator
        _t.ConfigDirOwnerUid = "1000";
        _t._store.Add(_t.ManagedAgent(desired: "openshell", applied: "docker"));
        var authority = Path.Combine(_t._dir, "managed", "authority");
        var record = Path.Combine(authority, "agent1.json");

        var apply = await _t.Runtime().ApplyAsync("agent1", default);

        Assert.Equal("managed_files_failed", apply.Code);
        Assert.Equal((0u, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute),
            (StockOpenShellRuntime.OwnerUid(authority), File.GetUnixFileMode(authority)));
        Assert.Null(ReadAs(1000, record)); // the fresh directory blocks the reader, whoever owns the file
        Assert.Equal("revoked", JsonNode.Parse(File.ReadAllText(record))!["State"]!.GetValue<string>());
        Assert.DoesNotContain(_t._cli.Calls, c => c.Args.Any(a => a.Contains(".fleet-activate")));
    }

    [Fact]
    public async Task An_active_record_the_operator_cannot_be_given_is_refused_while_revocation_still_publishes()
    {
        var runtime = _t.Runtime();
        if (StockOpenShellRuntime.OwnerUid(_t._dir) is not { } writer || writer == 0) return; // root can always hand a file over
        _t.ConfigDirOwnerUid = (writer + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        _t._store.Add(_t.ManagedAgent(desired: "openshell", applied: "docker"));
        var authority = Path.Combine(_t._dir, "managed", "authority");

        var apply = await _t.Runtime().ApplyAsync("agent1", default);

        Assert.False(apply.Ok);
        Assert.Equal("managed_files_failed", apply.Code);
        Assert.Equal("revoked", JsonNode.Parse(File.ReadAllText(Path.Combine(authority, "agent1.json")))!["State"]!.GetValue<string>());
        Assert.Empty(Directory.GetFiles(authority, "*.tmp"));
        Assert.DoesNotContain(_t._cli.Calls, c => c.Args.Any(a => a.Contains(".fleet-activate")));
        // Revocation's own writes are never held by ownership (still applied Docker, so revoke ends retired).
        Assert.DoesNotContain((await _t.Runtime().RevokeAsync("agent1", default)).Errors, e => e.StartsWith("admission", StringComparison.Ordinal));
        Assert.Equal(Fleet.Shared.Security.StockAuthoritySource.RetiredState,
            JsonNode.Parse(File.ReadAllText(Path.Combine(authority, "agent1.json")))!["State"]!.GetValue<string>());
    }

    // ── #507: one managed user tree W under the stock root, legacy preservation and transfer holds ──

    private const string W = "/home/agent/workspace";
    private const string Old = "registry.example.test/agent@sha256:old";

    /// <summary>A binding recorded before #507: no persisted path; its epoch's private bundle generated the given WorkDir.</summary>
    private ManagedBinding Legacy(string name, string image = "stock-agent:synthetic", string? workDir = "/workspace", string appRoot = "/home/agent/app")
    {
        var binding = _t.Binding(name, Guid.NewGuid()) with { WorkloadWorkspacePath = null, Image = image };
        if (workDir is null) return binding;
        var settings = Path.Combine(_t._dir, "managed", "agent1", binding.Epoch.ToString("N"), "bundle", "app", "appsettings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(settings)!);
        File.WriteAllText(settings, new JsonObject
        {
            ["Agent"] = new JsonObject { ["WorkDir"] = workDir }, ["StockOpenShell"] = new JsonObject { ["AppRoot"] = appRoot },
        }.ToJsonString());
        return binding;
    }

    /// <summary>The old image's filesystem: a legacy /workspace with user data and a sentinel per exclusion, and no W.</summary>
    private string LegacyTree(string sandbox)
    {
        Directory.Delete(_t._cli.Fs(sandbox, W), recursive: true);
        var legacy = _t._cli.Fs(sandbox, "/workspace");
        Directory.CreateDirectory(Path.Combine(legacy, "src"));
        File.WriteAllText(Path.Combine(legacy, "src", "main.py"), "print('user')\n");
        File.WriteAllText(Path.Combine(legacy, "run.sh"), "#!/bin/sh\n");
        File.SetUnixFileMode(Path.Combine(legacy, "run.sh"), (UnixFileMode)0b111_101_101);
        Directory.CreateDirectory(Path.Combine(legacy, "empty"));
        Directory.CreateDirectory(Path.Combine(legacy, "app"));
        File.WriteAllText(Path.Combine(legacy, "app", "readme"), "an ordinary project directory named app");
        Directory.CreateDirectory(Path.Combine(legacy, "nested", ".ssh"));
        File.WriteAllText(Path.Combine(legacy, "nested", ".ssh", "known"), "nested names are user data");
        foreach (var name in StockOpenShellRuntime.PrivateWorkspaceEntries)
            if (name.StartsWith(".fleet-", StringComparison.Ordinal) || name == ".mcp.json") File.WriteAllText(Path.Combine(legacy, name), "control");
            else
            {
                Directory.CreateDirectory(Path.Combine(legacy, name));
                File.WriteAllText(Path.Combine(legacy, name, "auth.json"), "{\"synthetic\":\"control\"}");
            }
        return legacy;
    }

    /// <summary>Relative path → content (or a directory marker and the owner-execute bit) of a host tree.</summary>
    private static SortedDictionary<string, string> Tree(string root) => new(
        Directory.EnumerateFileSystemEntries(root, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 })
            .ToDictionary(p => Path.GetRelativePath(root, p), p => Directory.Exists(p) ? "<dir>"
                : File.ReadAllText(p) + ((File.GetUnixFileMode(p) & UnixFileMode.UserExecute) != 0 ? "+x" : "")), StringComparer.Ordinal);

    /// <summary>The portable part of a legacy tree: every exclusion and its contents removed.</summary>
    private static SortedDictionary<string, string> Portable(SortedDictionary<string, string> tree) => new(
        tree.Where(p => !StockOpenShellRuntime.PrivateWorkspaceEntries.Contains(p.Key.Split('/')[0])).ToDictionary(), StringComparer.Ordinal);

    /// <summary>The activation write itself (the copy helper's exclusion arguments also name the marker).</summary>
    private static bool Activation(ManagedStockRuntimeAdapterTests.Call c) => c.Args.Any(a => a.StartsWith("printf 1 > ", StringComparison.Ordinal));

    private ManagedRecord Stored() => ManagedRecord.Parse(_t._store.Get("agent1").AppliedRuntimeBinding);

    /// <summary>A stored record without its write timestamp (every final commit re-stamps it).</summary>
    private static string Shape(string? json)
    {
        var node = JsonNode.Parse(json!)!.AsObject();
        node.Remove("updatedAt");
        return node.ToJsonString();
    }

    private bool Mutating(ManagedStockRuntimeAdapterTests.Call c) => c.Args.Count > 1 && c.Args[0] == "sandbox"
        && c.Args[1] is "create" or "start" or "stop" or "upload" or "download" or "exec" or "provider";

    [Fact]
    public async Task Legacy_same_image_reprovision_moves_the_tree_into_W_on_the_same_sandbox_and_keeps_the_original()
    {
        var old = Legacy("fl-1e9ac7000000a401");
        _t._cli.Sandboxes[old.SandboxName] = (old.SandboxId, "Stopped");
        _t._store.Add(_t.ManagedAgent(desired: "openshell", applied: "openshell", record: new ManagedRecord { State = "stopped", Applied = old }));
        var legacy = LegacyTree(old.SandboxName);
        var before = Tree(legacy);

        var apply = await _t.Runtime().ApplyAsync("agent1", default);

        Assert.True(apply.Ok, apply.Message);
        var applied = Stored().Applied!;
        Assert.Equal((old.SandboxName, old.SandboxId, W), (applied.SandboxName, applied.SandboxId, applied.WorkloadWorkspacePath));
        Assert.DoesNotContain(_t._cli.Calls, c => c.Args.Take(2).SequenceEqual(["sandbox", "create"]));
        var w = _t._cli.Fs(old.SandboxName, W);
        var carried = Tree(w);
        Assert.Equal("{\"mcpServers\":{\"memory\":{\"url\":\"https://memory.example.test/mcp\"}}}", carried[".mcp.json"]); // fresh routes
        carried.Remove(".mcp.json");
        Assert.Equal(Portable(before), carried); // bytes, empty directories, exec bits, W/app and nested names
        Assert.Equal(before, Tree(legacy)); // the original is neither moved nor changed
        Assert.Equal(W, JsonNode.Parse(_t._cli.Uploads.Last(u => u.Remote == "/home/agent/app/appsettings.json").Content!)!["Agent"]!["WorkDir"]!.GetValue<string>());
        // Only the snapshot stays in the owned operation directory; the helper is gone; nothing was read outside the root.
        var operation = Assert.Single(Directory.GetDirectories(_t._cli.Fs(old.SandboxName, "/home/agent/.phleet-workspace-transfer")));
        Assert.Equal(["data"], Directory.GetFileSystemEntries(operation).Select(Path.GetFileName));
        Assert.All(_t._cli.Downloads, d => Assert.StartsWith("/home/agent/.phleet-workspace-transfer/", d));
        var snapshot = Assert.Single(Directory.GetDirectories(Path.Combine(_t._dir, "managed", "agent1", applied.Epoch.ToString("N"), "workspace-out")));
        Assert.Equal(Portable(before).Keys, Tree(snapshot).Keys);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(snapshot));
    }

    [Fact]
    public async Task Current_layout_reprovision_keeps_W_edits_and_never_unions_a_stale_legacy_tree()
    {
        var binding = _t.Binding("fl-1e9ac7000000a402", Guid.NewGuid());
        _t._cli.Sandboxes[binding.SandboxName] = (binding.SandboxId, "Stopped");
        _t._store.Add(_t.ManagedAgent(desired: "openshell", applied: "openshell", record: new ManagedRecord { State = "stopped", Applied = binding }));
        var w = _t._cli.Fs(binding.SandboxName, W);
        File.WriteAllText(Path.Combine(w, "latest.txt"), "edit 2");
        var stale = _t._cli.Fs(binding.SandboxName, "/workspace");
        Directory.CreateDirectory(stale);
        File.WriteAllText(Path.Combine(stale, "latest.txt"), "stale");
        File.WriteAllText(Path.Combine(stale, "old-only.txt"), "stale");

        var apply = await _t.Runtime().ApplyAsync("agent1", default);

        Assert.True(apply.Ok, apply.Message);
        Assert.Equal((binding.SandboxId, W), (Stored().Applied!.SandboxId, Stored().Applied!.WorkloadWorkspacePath));
        Assert.Equal("edit 2", File.ReadAllText(Path.Combine(w, "latest.txt")));
        Assert.False(File.Exists(Path.Combine(w, "old-only.txt")));
        Assert.Equal(["stale", "stale"], Directory.GetFiles(stale).Select(File.ReadAllText));
        Assert.Empty(_t._cli.Downloads);
        Assert.DoesNotContain(_t._cli.Uploads, u => u.Remote.StartsWith(W + "/", StringComparison.Ordinal) && u.Remote != W + "/.mcp.json");
        Assert.Contains(_t._cli.Uploads, u => u.Remote == W + "/.mcp.json");
    }

    [Theory]
    [InlineData("stray file")]
    [InlineData("link")]
    public async Task Legacy_reprovision_with_unexpected_W_content_holds_without_merging(string kind)
    {
        var old = Legacy("fl-1e9ac7000000a403");
        _t._cli.Sandboxes[old.SandboxName] = (old.SandboxId, "Stopped");
        _t._store.Add(_t.ManagedAgent(desired: "openshell", applied: "openshell", record: new ManagedRecord { State = "stopped", Applied = old }));
        var legacy = LegacyTree(old.SandboxName);
        var before = Tree(legacy);
        var w = _t._cli.Fs(old.SandboxName, W);
        if (kind == "link") File.CreateSymbolicLink(w, legacy);
        else { Directory.CreateDirectory(w); File.WriteAllText(Path.Combine(w, "stray"), "unexpected"); }

        var apply = await _t.Runtime().ApplyAsync("agent1", default);

        Assert.Equal("workspace_layout_conflict", apply.Code);
        var record = Stored();
        Assert.Equal("needs_attention", record.State);
        Assert.Contains("layout:workspace_layout_conflict", record.Errors);
        Assert.Null(record.Applied!.WorkloadWorkspacePath); // the legacy layout is never silently promoted
        Assert.DoesNotContain(_t._cli.Uploads, u => u.Remote.StartsWith(W + "/", StringComparison.Ordinal));
        Assert.Empty(_t._cli.Downloads);
        Assert.Equal(before, Tree(legacy));
        if (kind != "link") Assert.Equal("unexpected", File.ReadAllText(Path.Combine(w, "stray")));
        Assert.Equal("Stopped", _t._cli.Sandboxes[old.SandboxName].Phase);
        Assert.DoesNotContain(_t._cli.Calls, Activation);
    }

    [Theory]
    [InlineData("openshell", "no_generated_settings")]
    [InlineData("openshell", "other_workdir")]
    [InlineData("openshell", "other_home_in_settings")]
    [InlineData("openshell", "unknown_persisted_path")]
    [InlineData("openshell", "changed_home_config")]
    [InlineData("openshell", "image_change")]
    [InlineData("docker", "no_generated_settings")]
    [InlineData("docker", "unknown_persisted_path")]
    [InlineData("docker", "missing_helper")]
    public async Task Unprovable_layout_or_missing_helper_refuses_before_any_effect(string desired, string fault)
    {
        var binding = fault switch
        {
            "no_generated_settings" => Legacy("fl-1e9ac7000000a404", workDir: null),
            "other_workdir" => Legacy("fl-1e9ac7000000a404", workDir: W),
            "other_home_in_settings" => Legacy("fl-1e9ac7000000a404", appRoot: "/home/other/app"),
            "unknown_persisted_path" => _t.Binding("fl-1e9ac7000000a404", Guid.NewGuid()) with { WorkloadWorkspacePath = "/srv/workspace" },
            "image_change" => Legacy("fl-1e9ac7000000a404", image: Old, workDir: null),
            _ => _t.Binding("fl-1e9ac7000000a404", Guid.NewGuid()),
        };
        if (fault == "changed_home_config") _t.ConfigOverrides = new() { ["StockOpenShell:WorkloadHome"] = "/home/other" };
        _t._cli.Sandboxes[binding.SandboxName] = (binding.SandboxId, "Stopped");
        var state = fault == "image_change" ? "revoked" : "stopped";
        _t._store.Add(_t.ManagedAgent(desired: desired, applied: "openshell", record: new ManagedRecord { State = state, Applied = binding }));
        if (fault == "missing_helper") _t.HelperFile = Path.Combine(_t._dir, "absent.py");
        var before = Shape(_t._store.Get("agent1").AppliedRuntimeBinding);

        var apply = await _t.Runtime().ApplyAsync("agent1", default);

        Assert.Equal((409, fault == "missing_helper" ? "workspace_export_failed" : "workspace_layout_unverified"), (apply.Status, apply.Code));
        Assert.DoesNotContain(_t._cli.Calls, Mutating);
        Assert.Empty(_t._broker.Puts);
        Assert.Empty(_t._broker.Deletes);
        Assert.Equal(before, Shape(_t._store.Get("agent1").AppliedRuntimeBinding));
        Assert.Empty(_t._dockerProvisions);
    }

    [Fact]
    public async Task Legacy_image_change_exports_the_old_tree_into_a_fresh_W_and_retains_the_stopped_original()
    {
        var old = Legacy("fl-1e9ac7000000a405", image: Old);
        _t._cli.Sandboxes[old.SandboxName] = (old.SandboxId, "Ready");
        _t._store.Add(_t.ManagedAgent(desired: "openshell", applied: "openshell", record: new ManagedRecord { State = "applied", Applied = old }));
        _t.WriteAuthority("active");
        var legacy = LegacyTree(old.SandboxName);
        var before = Tree(legacy);
        var runtime = _t.Runtime();

        Assert.Equal("managed_image_change_requires_revoke", (await runtime.ApplyAsync("agent1", default)).Code);
        Assert.True((await runtime.RevokeAsync("agent1", default)).Ok);
        var apply = await runtime.ApplyAsync("agent1", default);

        Assert.True(apply.Ok, apply.Message);
        var record = Stored();
        Assert.NotEqual(old.SandboxId, record.Applied!.SandboxId);
        Assert.Equal(("stock-agent:synthetic", W), (record.Applied.Image, record.Applied.WorkloadWorkspacePath));
        var carried = Tree(_t._cli.Fs(record.Applied.SandboxName, W));
        Assert.StartsWith("{\"mcpServers\":", carried[".mcp.json"]);
        carried.Remove(".mcp.json");
        Assert.Equal(Portable(before), carried);
        // The old identity keeps its data and custody: stopped, its legacy tree byte-identical, still recorded without a path.
        Assert.Equal("Stopped", _t._cli.Sandboxes[old.SandboxName].Phase);
        Assert.Equal(before, Tree(legacy));
        var superseded = Assert.Single(record.Superseded);
        Assert.Equal((old.SandboxId, (string?)null), (superseded.SandboxId, superseded.WorkloadWorkspacePath));
        Assert.DoesNotContain("/workspace", _t._cli.Downloads);
        Assert.All(_t._cli.Downloads, d => Assert.StartsWith("/home/agent/.phleet-workspace-transfer/", d));
    }

    /// <summary>A pinned CLI that fails one verb (download) like a nonzero or timed-out transfer.</summary>
    private sealed class FailingCli(IStockCli inner, string verb, bool timeout) : IStockCli
    {
        public Task<StockCliResult> RunAsync(IReadOnlyList<string> args, IReadOnlyDictionary<string, string>? secretEnv, TimeSpan bound, CancellationToken ct) =>
            args.Count > 1 && args[0] == "sandbox" && args[1] == verb ? Task.FromResult(new StockCliResult(-1, "", TimedOut: timeout))
                : inner.RunAsync(args, secretEnv, bound, ct);
    }

    [Theory]
    [InlineData("included_link", "export:workspace_export_failed")]
    [InlineData("absent_source", "export:workspace_layout_unverified")]
    [InlineData("python_missing", "export:workspace_export_failed")]
    [InlineData("policy_denied", "export:workspace_export_failed")]
    [InlineData("root_mismatch", "export:workspace_layout_unverified")]
    [InlineData("operation_collision", "export:workspace_layout_conflict")]
    [InlineData("altered_download", "download:workspace_download_unknown")]
    [InlineData("download_nonzero", "download:workspace_download_unknown")]
    [InlineData("download_timeout", "download:workspace_download_unknown")]
    [InlineData("identity_change", "download:sandbox_identity_changed")]
    [InlineData("unwritable_staging", "download:workspace_stage_failed")]
    [InlineData("cleanup_failed", "export:workspace_cleanup_failed")]
    public async Task Image_change_transfer_faults_hold_before_any_new_sandbox_and_keep_the_source(string fault, string detail)
    {
        var old = Legacy("fl-1e9ac7000000a406", image: Old);
        _t._cli.Sandboxes[old.SandboxName] = (old.SandboxId, "Stopped");
        _t._store.Add(_t.ManagedAgent(desired: "openshell", applied: "openshell", record: new ManagedRecord { State = "revoked", Applied = old }));
        var legacy = LegacyTree(old.SandboxName);
        var cli = (IStockCli)_t._cli;
        switch (fault)
        {
            case "included_link": File.CreateSymbolicLink(Path.Combine(legacy, "src", "link"), "/etc/hostname"); break;
            case "absent_source": Directory.Delete(legacy, recursive: true); break;
            case "python_missing": _t._cli.OnWorkspaceExec = label => label == "copy" ? 127 : null; break;
            case "policy_denied": _t._cli.OnWorkspaceExec = label => label == "copy" ? 5 : null; break;
            case "root_mismatch": _t._cli.OnWorkspaceExec = label => label == "copy" ? 3 : null; break;
            case "operation_collision": _t._cli.OnWorkspaceExec = label => label == "transfer" ? 4 : null; break;
            case "altered_download": _t._cli.BeforeDownload = data => File.WriteAllText(Path.Combine(data, "late.txt"), "appeared after the copy"); break;
            case "download_nonzero": cli = new FailingCli(_t._cli, "download", timeout: false); break;
            case "download_timeout": cli = new FailingCli(_t._cli, "download", timeout: true); break;
            case "identity_change": _t._cli.BeforeDownload = _ => _t._cli.Sandboxes[old.SandboxName] = (Guid.NewGuid().ToString(), "Ready"); break;
            case "unwritable_staging":
                Directory.CreateDirectory(Path.Combine(_t._dir, "managed", "agent1", old.Epoch.ToString("N")));
                File.WriteAllText(Path.Combine(_t._dir, "managed", "agent1", old.Epoch.ToString("N"), "workspace-out"), "not a directory");
                break;
            case "cleanup_failed": _t._cli.OnWorkspaceExec = label => label == "cleanup" ? 1 : null; break;
        }
        var before = Directory.Exists(legacy) ? Tree(legacy) : null;

        var apply = await _t.Runtime(cli: cli).ApplyAsync("agent1", default);

        Assert.Equal("image_change_workspace_unresolved", apply.Code);
        Assert.Contains(detail, apply.Errors);
        var record = Stored();
        Assert.Equal(("needs_attention", old.SandboxId, (ManagedBinding?)null), (record.State, record.Applied!.SandboxId, record.Candidate));
        Assert.DoesNotContain(_t._cli.Calls, c => c.Args.Take(2).SequenceEqual(["sandbox", "create"]));
        Assert.DoesNotContain(_t._cli.Calls, Activation);
        if (fault != "identity_change") Assert.Equal("Stopped", _t._cli.Sandboxes[old.SandboxName].Phase); // the bounded, verified Stop ran
        if (before is not null) Assert.Equal(before, Tree(legacy));
        // No partial snapshot ever reached a destination: the bundle workspace has only its generated routes.
        Assert.DoesNotContain(_t._cli.Uploads, u => u.Remote.StartsWith(W + "/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_cancelled_handoff_stops_on_its_own_deadline_and_never_touches_docker()
    {
        var binding = _t.Binding("fl-1e9ac7000000a407", Guid.NewGuid());
        _t._cli.Sandboxes[binding.SandboxName] = (binding.SandboxId, "Ready");
        _t._store.Add(_t.ManagedAgent(desired: "docker", applied: "openshell", record: new ManagedRecord { State = "applied", Applied = binding }));
        Directory.CreateDirectory(_t.DockerWorkspaceDir);
        File.WriteAllText(Path.Combine(_t.DockerWorkspaceDir, "prior.txt"), "docker");
        using var client = new CancellationTokenSource();
        _t._cli.OnWorkspaceExec = label => { if (label == "copy") client.Cancel(); return null; };

        var apply = await _t.Runtime().ApplyAsync("agent1", client.Token);

        Assert.Equal("openshell_to_docker_unresolved", apply.Code);
        Assert.Contains("export:workspace_transfer_cancelled", apply.Errors);
        Assert.Equal("Stopped", _t._cli.Sandboxes[binding.SandboxName].Phase);
        Assert.Equal(["prior.txt"], Directory.GetFileSystemEntries(_t.DockerWorkspaceDir).Select(Path.GetFileName));
        Assert.Empty(_t._dockerProvisions);
        Assert.Equal("openshell", _t._store.Get("agent1").AppliedRuntimeKind);
    }

    [Fact]
    public async Task Docker_handoff_snapshots_the_prior_portable_tree_and_leaves_excluded_state_untouched()
    {
        var binding = _t.Binding("fl-1e9ac7000000a408", Guid.NewGuid());
        _t._cli.Sandboxes[binding.SandboxName] = (binding.SandboxId, "Ready");
        _t._store.Add(_t.ManagedAgent(desired: "docker", applied: "openshell", record: new ManagedRecord { State = "applied", Applied = binding }));
        var w = _t._cli.Fs(binding.SandboxName, W);
        Directory.CreateDirectory(Path.Combine(w, "app"));
        File.WriteAllText(Path.Combine(w, "app", "main.py"), "user app");
        File.WriteAllText(Path.Combine(w, ".ssh"), "sandbox-side control never transferred");
        var docker = _t.DockerWorkspaceDir;
        Directory.CreateDirectory(Path.Combine(docker, ".ssh"));
        File.WriteAllText(Path.Combine(docker, ".ssh", "id"), "docker control");
        Directory.CreateDirectory(Path.Combine(docker, "codex"));
        File.CreateSymbolicLink(Path.Combine(docker, "codex", "current"), "/etc"); // inside an excluded entry: never read
        File.WriteAllText(Path.Combine(docker, "prior.txt"), "replaced by the sandbox tree");

        var apply = await _t.Runtime().ApplyAsync("agent1", default);

        Assert.True(apply.Ok, apply.Message);
        Assert.Equal("user app", File.ReadAllText(Path.Combine(docker, "app", "main.py")));
        Assert.Equal("from sandbox", File.ReadAllText(Path.Combine(docker, "marker-back.txt")));
        Assert.False(File.Exists(Path.Combine(docker, "prior.txt")));
        Assert.Equal("docker control", File.ReadAllText(Path.Combine(docker, ".ssh", "id")));
        Assert.Equal("/etc", new FileInfo(Path.Combine(docker, "codex", "current")).LinkTarget);
        var snapshot = Assert.Single(Directory.GetDirectories(Path.Combine(_t._dir, "managed", "agent1", binding.Epoch.ToString("N"), "docker-before")));
        Assert.Equal(["prior.txt"], Directory.GetFileSystemEntries(snapshot).Select(Path.GetFileName));
        Assert.Equal("replaced by the sandbox tree", File.ReadAllText(Path.Combine(snapshot, "prior.txt")));
        Assert.Single(Directory.GetDirectories(Path.Combine(_t._dir, "managed", "agent1", binding.Epoch.ToString("N"), "workspace-out")));
    }

    [Theory]
    [InlineData(3, "workspace_layout_unverified")]
    [InlineData(4, "workspace_layout_conflict")]
    [InlineData(127, "workspace_layout_unverified")]
    public async Task Fresh_apply_verifies_the_stock_root_and_an_empty_W_before_any_input_lands(int exit, string code)
    {
        _t._store.Add(_t.ManagedAgent(desired: "openshell", applied: "docker"));
        _t._cli.OnWorkspaceExec = label => label == "layout" ? exit : null;

        var apply = await _t.Runtime().ApplyAsync("agent1", default);

        Assert.Equal(code, apply.Code);
        var record = Stored();
        Assert.Equal(("needs_attention", W), (record.State, record.Candidate!.WorkloadWorkspacePath)); // persisted before any effect
        Assert.Empty(_t._cli.Uploads);
        Assert.Equal("Stopped", _t._cli.Sandboxes[record.Candidate.SandboxName].Phase);
    }

    [Fact]
    public async Task Fresh_image_with_content_in_W_is_a_conflict_not_a_merge()
    {
        _t._store.Add(_t.ManagedAgent(desired: "openshell", applied: "docker"));
        _t._cli.OnWorkspaceExec = label =>
        {
            if (label == "layout") File.WriteAllText(_t._cli.Fs(_t._cli.Sandboxes.Keys.Single(), W + "/from-image"), "image content");
            return null;
        };

        Assert.Equal("workspace_layout_conflict", (await _t.Runtime().ApplyAsync("agent1", default)).Code);
        Assert.Empty(_t._cli.Uploads);
    }

    [Fact]
    public async Task Workspace_operations_use_the_transfer_bound_only_for_the_copy_and_download()
    {
        var binding = _t.Binding("fl-1e9ac7000000a409", Guid.NewGuid());
        _t._cli.Sandboxes[binding.SandboxName] = (binding.SandboxId, "Ready");
        _t._store.Add(_t.ManagedAgent(desired: "docker", applied: "openshell", record: new ManagedRecord { State = "applied", Applied = binding }));
        var runtime = _t.Runtime();

        Assert.True((await runtime.ApplyAsync("agent1", default)).Ok);

        var execs = _t._cli.Timeouts.Where(c => c.Args.Take(2).SequenceEqual(["sandbox", "exec"])).ToList();
        Assert.Equal(runtime.TransferTimeout, Assert.Single(execs, c => c.Args.Contains("python3")).Timeout);
        Assert.All(execs.Where(c => !c.Args.Contains("python3")), c => Assert.Equal(runtime.LegTimeout, c.Timeout));
        Assert.Equal(runtime.TransferTimeout, Assert.Single(_t._cli.Timeouts, c => c.Args.Take(2).SequenceEqual(["sandbox", "download"])).Timeout);
        // The helper receives the adapter's one exclusion list.
        var copy = Assert.Single(_t._cli.Calls, c => c.Args.Contains("python3")).Args;
        Assert.Equal(StockOpenShellRuntime.PrivateWorkspaceEntries, copy.Select((a, i) => (a, i)).Where(p => p.a == "--exclude").Select(p => copy[p.i + 1]));
        Assert.Equal(W, copy[copy.IndexOf("--source") + 1]);
    }

    [Fact]
    public async Task Git_filtered_paths_are_reported_privately_and_kept_in_the_source()
    {
        var docker = _t.DockerWorkspaceDir;
        Directory.CreateDirectory(docker);
        foreach (var args in new[] { new[] { "init", "-q", docker } })
            System.Diagnostics.Process.Start("git", args)!.WaitForExit();
        File.WriteAllText(Path.Combine(docker, ".gitignore"), "ignored.log\n");
        File.WriteAllText(Path.Combine(docker, "tracked.txt"), "kept");
        File.WriteAllText(Path.Combine(docker, "ignored.log"), "filtered by the CLI's default Git filtering");
        _t._store.Add(_t.ManagedAgent(desired: "openshell", applied: "docker"));

        Assert.True((await _t.Runtime().ApplyAsync("agent1", default)).Ok);

        var epoch = Stored().Applied!.Epoch.ToString("N");
        var report = Path.Combine(_t._dir, "managed", "agent1", epoch, "workspace-not-transferred.txt");
        var omitted = File.ReadAllLines(report);
        Assert.Contains("ignored.log", omitted);
        Assert.Contains(omitted, line => line.StartsWith(".git/", StringComparison.Ordinal));
        Assert.DoesNotContain("tracked.txt", omitted);
        Assert.DoesNotContain(".gitignore", omitted);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(report));
        Assert.True(File.Exists(Path.Combine(docker, "ignored.log"))); // the original stays
    }

    /// <summary>
    /// #507 review F1: a user repository whose own config runs a canary through <c>core.fsmonitor</c> and an index hook,
    /// with a kept file and an ignored one. Returns the canary path after proving plain Git really runs it.
    /// </summary>
    internal static string CanaryRepository(string repository, string scratch)
    {
        var canary = Path.Combine(scratch, "git-command-ran");
        var hooks = Path.Combine(scratch, "hooks");
        Directory.CreateDirectory(hooks);
        var script = $"#!/bin/sh\ntouch '{canary}'\n";
        foreach (var path in new[] { Path.Combine(scratch, "fsmonitor.sh"), Path.Combine(hooks, "post-index-change") })
        {
            File.WriteAllText(path, script);
            File.SetUnixFileMode(path, (UnixFileMode)0b111_101_101);
        }
        Git("init", "-q", repository);
        Git("-C", repository, "config", "core.fsmonitor", Path.Combine(scratch, "fsmonitor.sh"));
        Git("-C", repository, "config", "core.hooksPath", hooks);
        File.WriteAllText(Path.Combine(repository, ".gitignore"), "ignored.log\n");
        File.WriteAllText(Path.Combine(repository, "kept.txt"), "kept");
        File.WriteAllText(Path.Combine(repository, "ignored.log"), "ignored");
        Git("-C", repository, "ls-files", "-co", "--exclude-standard"); // positive control: plain Git runs it
        Assert.True(File.Exists(canary), "the canary repository does not exercise core.fsmonitor");
        File.Delete(canary);
        return canary;
    }

    private static void Git(params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var process = System.Diagnostics.Process.Start(psi)!;
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }

    [Fact]
    public async Task A_copied_repository_config_never_runs_a_command_and_filtering_still_applies()
    {
        Directory.CreateDirectory(_t.DockerWorkspaceDir);
        var canary = CanaryRepository(_t.DockerWorkspaceDir, Path.Combine(_t._dir, "canary"));
        _t._store.Add(_t.ManagedAgent(desired: "openshell", applied: "docker"));

        Assert.True((await _t.Runtime().ApplyAsync("agent1", default)).Ok);

        Assert.False(File.Exists(canary));
        var report = File.ReadAllLines(Path.Combine(_t._dir, "managed", "agent1", Stored().Applied!.Epoch.ToString("N"), "workspace-not-transferred.txt"));
        Assert.Contains("ignored.log", report);
        Assert.DoesNotContain("kept.txt", report);
    }

    [Theory]
    [InlineData("running", "restore:docker_quiescence_pending")]
    [InlineData("restarting", "restore:docker_quiescence_pending")]
    [InlineData("unobservable", "restore:docker_observation_failed")]
    public async Task Docker_destination_is_never_snapshotted_or_written_unless_its_container_is_stopped(string docker, string detail)
    {
        var binding = _t.Binding("fl-1e9ac7000000a40a", Guid.NewGuid());
        _t._cli.Sandboxes[binding.SandboxName] = (binding.SandboxId, "Ready");
        _t._store.Add(_t.ManagedAgent(desired: "docker", applied: "openshell", record: new ManagedRecord { State = "applied", Applied = binding }));
        Directory.CreateDirectory(Path.Combine(_t.DockerWorkspaceDir, "nested"));
        File.WriteAllText(Path.Combine(_t.DockerWorkspaceDir, "prior.txt"), "docker bytes");
        File.WriteAllText(Path.Combine(_t.DockerWorkspaceDir, "nested", "file"), "docker bytes");
        var before = Tree(_t.DockerWorkspaceDir);
        _t._host.DockerStates = docker == "unobservable" ? null : [docker];

        var apply = await _t.Runtime().ApplyAsync("agent1", default);

        Assert.Equal("openshell_to_docker_unresolved", apply.Code);
        Assert.Contains(detail, apply.Errors);
        Assert.Equal(before, Tree(_t.DockerWorkspaceDir));
        var epochDir = Path.Combine(_t._dir, "managed", "agent1", binding.Epoch.ToString("N"));
        Assert.False(Directory.Exists(Path.Combine(epochDir, "docker-before")));
        Assert.Single(Directory.GetDirectories(Path.Combine(epochDir, "workspace-out"))); // the verified export is retained
        Assert.Equal("Stopped", _t._cli.Sandboxes[binding.SandboxName].Phase);
        Assert.Empty(_t._dockerProvisions);
        Assert.Equal("openshell", _t._store.Get("agent1").AppliedRuntimeKind);
    }

    /// <summary>The file's bytes as read by <paramref name="uid"/> (no supplementary groups), or null when that uid is refused.</summary>
    private static string? ReadAs(uint uid, string path)
    {
        using var reader = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("setpriv",
            [$"--reuid={uid}", $"--regid={uid}", "--clear-groups", "cat", path]) { RedirectStandardOutput = true, RedirectStandardError = true })!;
        var text = reader.StandardOutput.ReadToEnd();
        reader.WaitForExit();
        return reader.ExitCode == 0 ? text : null;
    }

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "chown")]
    private static extern int Chown([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPUTF8Str)] string path, uint owner, uint group);

    private Task<string?> DirFault(StockOpenShellRuntime runtime) =>
        Task.FromResult((string?)typeof(StockOpenShellRuntime).GetMethod("DescribeConfigDirFault",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(runtime, null));
}
