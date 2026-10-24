using System.Collections.Frozen;
using Grpc.Core;
using Grpc.Net.Client;
using Openshell.Datamodel.V1;
using Openshell.V1;
using GatewayRpc = Openshell.V1.OpenShell.OpenShellClient;

namespace Fleet.Orchestrator.Services;

/// <summary>
/// Pinned fork protobuf/gRPC calls with outside operator authorization. No retry, name/ID
/// guessing, partial inventory or Ready==admission shortcut. Not registered for workload admission.
/// </summary>
public sealed class OpenShellGatewayClient : IOpenShellGatewayClient, IDisposable
{
    private readonly GrpcChannel _channel;
    private readonly GatewayRpc _rpc;
    private readonly string _gatewayId;
    private readonly string _workspace;
    private readonly string _token;

    public OpenShellGatewayClient(Uri address, string gatewayId, string workspace, string operatorToken,
        HttpMessageHandler handler)
    {
        if (!address.IsAbsoluteUri || address.Scheme != "https" || !string.IsNullOrEmpty(address.UserInfo)
            || address.AbsolutePath != "/" || address.Query.Length != 0 || address.Fragment.Length != 0)
            throw new ArgumentException("An outside HTTPS gateway origin is required.", nameof(address));
        ValidateOpaque(gatewayId); ValidateOpaque(workspace);
        if (string.IsNullOrWhiteSpace(operatorToken) || operatorToken.Length > 8192
            || operatorToken.Any(c => c <= ' ' || c > '~'))
            throw new ArgumentException("Outside gateway authorization is required.", nameof(operatorToken));
        _gatewayId = gatewayId; _workspace = workspace; _token = operatorToken;
        _channel = GrpcChannel.ForAddress(address, new GrpcChannelOptions
        {
            HttpHandler = handler, DisposeHttpClient = true, MaxRetryAttempts = 1,
            MaxReceiveMessageSize = 4 * 1024 * 1024, ThrowOperationCanceledOnCancellation = true,
        });
        _rpc = new GatewayRpc(_channel);
    }

    public async Task<IReadOnlyList<OpenShellSandboxSnapshot>> InventoryAsync(CancellationToken ct = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        return await InventoryCoreAsync(deadline.Token).WaitAsync(deadline.Token);
    }

    private async Task<IReadOnlyList<OpenShellSandboxSnapshot>> InventoryCoreAsync(CancellationToken ct)
    {
        var records = new List<OpenShellSandboxSnapshot>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.Ordinal);
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        var token = "";
        for (var page = 0; page < 100; page++)
        {
            var response = await _rpc.ListSandboxesAsync(new ListSandboxesRequest
            {
                WorkspaceScope = new WorkspaceSelector { Workspace = _workspace },
                PageSize = 100, PageToken = token,
                // No label filter: ambiguous create ownership must not depend on a selector
                // that could omit objects. Return the full authenticated named-workspace view.
            }, Options(ct)).ResponseAsync.WaitAsync(ct);
            if (response.Sandboxes.Count > 100 || records.Count + response.Sandboxes.Count > 10000)
                throw new InvalidOperationException("Gateway inventory limit exceeded.");
            foreach (var sandbox in response.Sandboxes)
            {
                var record = Snapshot(sandbox);
                if (!ids.Add(record.Id) || !names.Add(record.Name))
                    throw new InvalidOperationException("Gateway inventory identity is inconsistent.");
                records.Add(record);
            }
            token = response.NextPageToken;
            if (token.Length == 0) return records.AsReadOnly();
            if (token.Length > 4096 || !tokens.Add(token))
                throw new InvalidOperationException("Gateway inventory pagination is inconsistent.");
        }
        throw new InvalidOperationException("Gateway inventory is incomplete.");
    }

    public async Task<OpenShellSandboxSnapshot?> ReadExactAsync(string sandboxId, CancellationToken ct = default)
    {
        ValidateOpaque(sandboxId);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        var inventory = await InventoryCoreAsync(deadline.Token).WaitAsync(deadline.Token);
        var found = inventory.SingleOrDefault(s => s.Id == sandboxId);
        if (found is null) return null; // Only a successfully exhausted authenticated view permits this.
        // Pinned GetSandbox accepts a NAME, not an ID. Resolve through the observed immutable ID,
        // then verify the refreshed response. This read does not authorize a named mutation.
        var response = await _rpc.GetSandboxAsync(new GetSandboxRequest
        {
            WorkspaceScope = new WorkspaceSelector { Workspace = _workspace }, Name = found.Name,
        }, Options(deadline.Token)).ResponseAsync.WaitAsync(deadline.Token);
        var current = Snapshot(response.Sandbox);
        if (current.Id != sandboxId || current.Name != found.Name)
            throw new InvalidOperationException("Gateway sandbox mapping changed during observation.");
        return current;
    }

    /// <summary>
    /// Stop only the recorded ID/version. The caller must persist requestId and reuse the same
    /// payload after an ambiguous outcome. This is not activation or intake/drain evidence.
    /// </summary>
    public async Task<OpenShellSandboxSnapshot> StopExactAsync(OpenShellSandboxSnapshot target,
        Guid requestId, CancellationToken ct = default)
    {
        ValidateMutationTarget(target, requestId);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        await RequireConditionalMutationsAsync(deadline.Token);
        var response = await _rpc.StopSandboxAsync(new StopSandboxRequest
        {
            WorkspaceScope = new WorkspaceSelector { Workspace = _workspace }, Name = target.Name,
            ExpectedSandboxId = target.Id, ExpectedResourceVersion = target.ResourceVersion,
            RequestId = requestId.ToString("D"),
        }, Options(deadline.Token)).ResponseAsync.WaitAsync(deadline.Token);
        var observed = Snapshot(response.Sandbox);
        if (observed.Id != target.Id || observed.Name != target.Name)
            throw new InvalidOperationException("Conditional stop outcome is unverified; reconcile the original request.");
        return observed;
    }

    public async Task<OpenShellDeleteAcknowledgement> DeleteExactAsync(OpenShellSandboxSnapshot target,
        Guid requestId, bool allowMissing, CancellationToken ct = default)
    {
        ValidateMutationTarget(target, requestId);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        await RequireConditionalMutationsAsync(deadline.Token);
        var response = await _rpc.DeleteSandboxAsync(new DeleteSandboxRequest
        {
            WorkspaceScope = new WorkspaceSelector { Workspace = _workspace }, Name = target.Name,
            ExpectedSandboxId = target.Id, ExpectedResourceVersion = target.ResourceVersion,
            RequestId = requestId.ToString("D"), AllowMissing = allowMissing,
        }, Options(deadline.Token)).ResponseAsync.WaitAsync(deadline.Token);
        var verified = response.Outcome switch
        {
            DeletionOutcome.Completed or DeletionOutcome.Accepted => response.SandboxId == target.Id,
            DeletionOutcome.AlreadyAbsent => allowMissing && response.SandboxId.Length == 0,
            _ => false,
        };
        if (!verified)
            throw new InvalidOperationException("Conditional delete outcome is unverified; reconcile the original request.");
        return new(response.SandboxId, response.Outcome);
    }

    public async Task<OpenShellSandboxSnapshot> CreateAsync(OpenShellLaunchPlan plan, Guid launchId,
        Guid epoch, Guid requestId, CancellationToken ct = default)
    {
        if (plan.GatewayId != _gatewayId || plan.Workspace != _workspace || launchId == Guid.Empty
            || epoch == Guid.Empty || requestId == Guid.Empty) throw new ArgumentException("Exact create ownership required.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        await RequireConditionalMutationsAsync(deadline.Token);
        var request = new CreateSandboxRequest
        {
            WorkspaceScope = new WorkspaceSelector { Workspace = _workspace }, Spec = plan.ReadSpec(),
            Name = "launch-" + launchId.ToString("N"), RequestId = requestId.ToString("D"),
        };
        // Metadata is correlation, NOT complete ownership evidence after an ambiguous Create.
        request.Labels.Add("phleet-launch", launchId.ToString("D"));
        request.Labels.Add("phleet-epoch", epoch.ToString("D"));
        var response = await _rpc.CreateSandboxAsync(request, Options(deadline.Token)).ResponseAsync.WaitAsync(deadline.Token);
        var result = Snapshot(response.Sandbox);
        if (result.Name != request.Name) throw new InvalidOperationException("create_identity_unconfirmed");
        return result;
    }

    public async Task<OpenShellSupervisorProof> ReadCurrentSupervisorAsync(OpenShellSandboxSnapshot target,
        string provider, CancellationToken ct = default)
    {
        ValidateMutationTarget(target, Guid.NewGuid());
        ValidateOpaque(provider);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        var before = await ReadExactAsync(target.Id, deadline.Token);
        RequireSame(before, target);
        var response = await _rpc.GetSandboxProviderStatusAsync(new GetSandboxProviderStatusRequest
        {
            WorkspaceScope = new WorkspaceSelector { Workspace = _workspace }, Sandbox = target.Name, Provider = provider,
            // Empty receipt requests CURRENT desired state, never persisted-operation-only readiness.
        }, Options(deadline.Token)).ResponseAsync.WaitAsync(deadline.Token);
        var after = await ReadExactAsync(target.Id, deadline.Token);
        RequireSame(after, before!);
        return OpenShellSupervisorProof.Verify(after!, provider, response.Status, DateTime.UtcNow);
    }

    private static void RequireSame(OpenShellSandboxSnapshot? actual, OpenShellSandboxSnapshot expected)
    {
        if (actual is null || actual.Id != expected.Id || actual.Name != expected.Name
            || actual.GatewayId != expected.GatewayId || actual.Workspace != expected.Workspace
            || actual.ResourceVersion != expected.ResourceVersion || actual.PolicyVersion != expected.PolicyVersion
            || actual.SupervisorInstanceId != expected.SupervisorInstanceId || actual.Phase != expected.Phase)
            throw new OpenShellStateConflictException("sandbox_changed_during_readiness");
    }

    private async Task RequireConditionalMutationsAsync(CancellationToken ct)
    {
        // Fetch per request, not from cache or semver. Health and mutation are not atomic:
        // endpoints containing mixed or downgraded replicas remain unqualified.
        var response = await _rpc.HealthAsync(new HealthRequest(), Options(ct)).ResponseAsync.WaitAsync(ct);
        if (!response.Capabilities.Contains("sandbox-mutation-preconditions-v1"))
            throw new InvalidOperationException("Gateway conditional-mutation capability is unverified.");
    }

    private void ValidateMutationTarget(OpenShellSandboxSnapshot target, Guid requestId)
    {
        ArgumentNullException.ThrowIfNull(target);
        ValidateOpaque(target.Id);
        if (target.GatewayId != _gatewayId || target.Workspace != _workspace || target.ResourceVersion == 0
            || requestId == Guid.Empty || string.IsNullOrWhiteSpace(target.Name) || target.Name.Length > 253
            || target.Name.Any(char.IsControl))
            throw new ArgumentException("An exact bound gateway/workspace/identity/version and request ID are required.");
    }

    private OpenShellSandboxSnapshot Snapshot(Sandbox? sandbox)
    {
        if (sandbox?.Metadata is not { } meta
            || string.IsNullOrWhiteSpace(meta.Name) || meta.Name.Length > 253)
            throw new InvalidOperationException("Gateway sandbox metadata is unverified.");
        var workspace = meta.Workspace.Length == 0 ? "default" : meta.Workspace;
        if (workspace != _workspace)
            throw new InvalidOperationException("Gateway sandbox workspace is unverified.");
        ValidateOpaque(meta.Id);
        return new(_gatewayId, workspace, meta.Id, meta.Name,
            sandbox.Status?.Phase ?? SandboxPhase.Unspecified,
            sandbox.Status?.MainProcessInstanceId ?? "", sandbox.Status?.CurrentPolicyVersion ?? 0,
            meta.Labels.ToFrozenDictionary(StringComparer.Ordinal)) { ResourceVersion = meta.ResourceVersion };
    }

    private CallOptions Options(CancellationToken ct) => new(
        headers: new Metadata { { "authorization", "Bearer " + _token } },
        deadline: DateTime.UtcNow.AddSeconds(5), cancellationToken: ct);

    private static void ValidateOpaque(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128
            || value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_'))
            throw new ArgumentException("A canonical gateway, workspace or sandbox identifier is required.");
    }

    public void Dispose() => _channel.Dispose();
}
