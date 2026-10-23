using System.Collections.Frozen;
using Grpc.Core;
using Grpc.Net.Client;
using Openshell.Datamodel.V1;
using Openshell.V1;
using GatewayRpc = Openshell.V1.OpenShell.OpenShellClient;

namespace Fleet.Orchestrator.Services;

/// <summary>
/// Pinned upstream protobuf/gRPC reads with outside operator authorization. No retry, name/ID
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
            meta.Labels.ToFrozenDictionary(StringComparer.Ordinal));
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
