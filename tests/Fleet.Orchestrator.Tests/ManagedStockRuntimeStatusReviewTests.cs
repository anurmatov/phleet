using Fleet.Orchestrator.Models;
using Fleet.Orchestrator.Services;

namespace Fleet.Orchestrator.Tests;

/// <summary>
/// #492 review (F7/F10/F16r, pending badge): observed workload/supervisor identities, stopped/removed
/// status and gateway backup age in the managed status view. Synthetic fakes only, not #493.
/// </summary>
public sealed class ManagedStockRuntimeStatusReviewTests : IDisposable
{
    private const string WorkloadId = "aaaaaaaaaaaa0000000000000000000000000000000000000000000000000001";
    private const string SupervisorId = "bbbbbbbbbbbb0000000000000000000000000000000000000000000000000002";
    private readonly ManagedStockRuntimeAdapterTests _t = new();
    private readonly string _backup = Directory.CreateTempSubdirectory("managed-backup-").FullName;

    public void Dispose()
    {
        _t.Dispose();
        Directory.Delete(_backup, recursive: true);
    }

    private static StockSandboxContainer[] Pair(string state) =>
        [new(SupervisorId, state, "supervisor"), new(WorkloadId, state, "workload")];

    [Fact]
    public async Task Verified_stop_persists_workload_and_supervisor_ids_with_stopped_status()
    {
        var binding = _t.Binding("fleet-agent1-51515151", Guid.NewGuid());
        _t._cli.Sandboxes[binding.SandboxName] = (binding.SandboxId, "Ready");
        _t._store.Add(_t.ManagedAgent(desired: "openshell", applied: "openshell", record: new ManagedRecord { State = "applied", Applied = binding }));
        _t._host.SandboxContainers = Pair("exited");

        var result = await _t.Runtime().StopAsync("agent1", default);

        Assert.True(result.Ok, result.Code);
        var json = _t._store.Get("agent1").AppliedRuntimeBinding;
        var containers = ManagedRecord.Parse(json).Applied!.Containers!;
        Assert.Equal((WorkloadId, SupervisorId, "stopped"), (containers.WorkloadId, containers.SupervisorId, containers.Status));
        var status = AgentRegistry.ManagedView(json).Status!;
        Assert.Equal((WorkloadId, SupervisorId, "stopped"), (status.WorkloadContainerId, status.SupervisorContainerId, status.ContainerStatus));
        Assert.NotNull(status.ContainersObservedAt);
        Assert.DoesNotContain("SYNTHETIC_API_KEY", System.Text.Json.JsonSerializer.Serialize(status));
    }

    [Fact]
    public async Task Revoke_with_no_labelled_container_left_reports_removed_and_keeps_last_known_ids()
    {
        var binding = _t.Binding("fleet-agent1-52525252", Guid.NewGuid()) with
        {
            Containers = new ManagedContainers(WorkloadId, SupervisorId, "running", DateTimeOffset.UtcNow.AddHours(-1)),
        };
        _t._cli.Sandboxes[binding.SandboxName] = (binding.SandboxId, "Ready");
        _t._store.Add(_t.ManagedAgent(desired: "openshell", applied: "openshell", record: new ManagedRecord { State = "applied", Applied = binding }));
        _t._host.SandboxContainers = [];

        var result = await _t.Runtime().RevokeAsync("agent1", default);

        Assert.True(result.Ok, result.Code);
        var status = AgentRegistry.ManagedView(_t._store.Get("agent1").AppliedRuntimeBinding).Status!;
        Assert.Equal(("revoked", WorkloadId, SupervisorId, "removed"), (status.State, status.WorkloadContainerId, status.SupervisorContainerId, status.ContainerStatus));
    }

    [Fact]
    public void Container_reads_fold_running_stopped_removed_and_unobservable_without_losing_ids()
    {
        var now = DateTimeOffset.UtcNow;
        var running = ManagedContainers.From([new(WorkloadId, "running", "workload"), new(SupervisorId, "running", "supervisor")], null, now);
        Assert.Equal((WorkloadId, SupervisorId, "running"), (running.WorkloadId, running.SupervisorId, running.Status));
        Assert.Equal("stopped", ManagedContainers.From(Pair("exited"), null, now).Status);
        var removed = ManagedContainers.From([], running, now);
        Assert.Equal((WorkloadId, SupervisorId, "removed"), (removed.WorkloadId, removed.SupervisorId, removed.Status));
        var unknown = ManagedContainers.From(null, running, now);
        Assert.Equal((WorkloadId, SupervisorId, "unobservable"), (unknown.WorkloadId, unknown.SupervisorId, unknown.Status));
        var never = ManagedContainers.From(null, null, now);
        Assert.Equal(((string?)null, (string?)null, "unobservable"), (never.WorkloadId, never.SupervisorId, never.Status));
    }

    [Fact]
    public void Records_written_before_identities_existed_still_parse_and_show_no_ids()
    {
        const string legacy = """{"v":1,"state":"applied","applied":{"sandboxName":"s","sandboxId":"sb-1","epoch":"11111111-2222-3333-4444-555555555555","image":"i","policyVersionId":1,"policySha256":"h","authMode":"api_key","keyRef":"K","provider":"p","gatewayGeneration":"g"}}""";
        var record = ManagedRecord.Parse(legacy);
        Assert.Null(record.Applied!.Containers);
        var status = AgentRegistry.ManagedStatus(record);
        Assert.Equal((null, null, null), (status.WorkloadContainerId, status.SupervisorContainerId, status.ContainerStatus));
        Assert.Null(ManagedRecord.Parse(record.Serialize()).Applied!.Containers);
        Assert.Equal("sb-1", ManagedRecord.Parse(record.Serialize()).Applied!.SandboxId);
    }

    [Fact]
    public void Pending_is_only_an_in_flight_operation_never_a_held_candidate()
    {
        var binding = _t.Binding("fleet-agent1-53535353", Guid.NewGuid());
        var held = AgentRegistry.ManagedStatus(new ManagedRecord { State = "needs_attention", Applied = binding, Candidate = binding, Errors = ["x"] });
        Assert.False(held.Pending);
        Assert.False(AgentRegistry.ManagedStatus(new ManagedRecord { State = "applying", Applied = binding }).Pending);
        var running = AgentRegistry.ManagedStatus(new ManagedRecord { State = "applying", Operation = "apply:instance-a", Candidate = binding });
        Assert.True(running.Pending);
        Assert.Equal("apply", running.Operation);
    }

    [Fact]
    public void Backup_age_comes_from_last_success_and_is_unknown_when_absent_or_unconfigured()
    {
        var record = new ManagedRecord { State = "applied", Applied = _t.Binding("fleet-agent1-54545454", Guid.NewGuid()) };
        Assert.Equal(("unknown", null, null), Backup(AgentRegistry.ManagedStatus(record)));
        Assert.Equal(("unknown", null, null), Backup(AgentRegistry.ManagedStatus(record, _backup)));
        File.WriteAllText(Path.Combine(_backup, "last-success"), "not-a-date\n");
        Assert.Equal(("unknown", null, null), Backup(AgentRegistry.ManagedStatus(record, _backup)));

        var at = DateTimeOffset.UtcNow.AddHours(-2);
        File.WriteAllText(Path.Combine(_backup, "last-success"), at.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'") + "\n");
        var recorded = AgentRegistry.ManagedStatus(record, _backup);
        Assert.Equal("recorded", recorded.BackupState);
        Assert.InRange(recorded.BackupAgeSeconds!.Value, 7190, 7300);
        var json = System.Text.Json.JsonSerializer.Serialize(recorded, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        Assert.Contains("\"backupAgeSeconds\":", json);
        Assert.DoesNotContain("fresh", json);

        File.WriteAllText(Path.Combine(_backup, "last-failure"), DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'") + "\n");
        Assert.Equal("failed", AgentRegistry.ManagedStatus(record, _backup).BackupState);
    }

    private static (string, DateTimeOffset?, long?) Backup(ManagedStatusView status) =>
        (status.BackupState, status.BackupLastSuccessAt, status.BackupAgeSeconds);
}
