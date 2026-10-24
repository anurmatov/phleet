using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Fleet.Orchestrator.Data;
using Microsoft.EntityFrameworkCore;

namespace Fleet.Orchestrator.Services;

/// <summary>
/// Outside control client. Generated gateway service observation must prove the exact sandbox and
/// qualified TLS routing origin before the per-launch capability is read/sent. No activation or retry.
/// Missing concrete preflight/runtime peers still block preparation; not registered by default.
/// </summary>
public sealed class OpenShellBootstrapProbe : IOpenShellBootstrapProbe, IDisposable
{
    private readonly Func<OrchestratorDbContext> _createDb;
    private readonly IOpenShellGatewayClient _gateway;
    private readonly OpenShellBootstrapArtifacts _artifacts;
    private readonly HttpClient _control;
    public OpenShellBootstrapProbe(Func<OrchestratorDbContext> createDb, IOpenShellGatewayClient gateway,
        OpenShellBootstrapArtifacts artifacts, HttpMessageHandler qualifiedTlsHandler)
    {
        _createDb = createDb; _gateway = gateway; _artifacts = artifacts;
        if (qualifiedTlsHandler is HttpClientHandler http) { http.AllowAutoRedirect = false; http.UseCookies = false; http.UseProxy = false; }
        if (qualifiedTlsHandler is SocketsHttpHandler sockets) { sockets.AllowAutoRedirect = false; sockets.UseCookies = false; sockets.UseProxy = false; }
        _control = new HttpClient(qualifiedTlsHandler) { Timeout = Timeout.InfiniteTimeSpan };
    }
    public async Task VerifyAsync(Guid launchId, Guid epoch, OpenShellSandboxSnapshot target,
        IReadOnlyList<OpenShellSupervisorProof> proofs, CancellationToken ct)
    {
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(ct); bound.CancelAfter(TimeSpan.FromSeconds(5));
        var token = bound.Token;
        var plan = await ReadPlanAsync(launchId, epoch, target, proofs, token);
        foreach (var expected in plan.ReadExpectedProviders())
        {
            var original = proofs.SingleOrDefault(p => p.Provider == expected.Name)
                ?? throw new OpenShellStateConflictException("bootstrap_supervisor_missing");
            var current = await _gateway.ReadCurrentSupervisorAsync(target, expected.Name, token);
            if (!expected.Matches(current.Desired) || !current.Desired.Equals(original.Desired)
                || current.SessionId != original.SessionId || current.ProcessInstanceId != original.ProcessInstanceId
                || current.NetworkInstanceId != original.NetworkInstanceId)
                throw new OpenShellStateConflictException("bootstrap_supervisor_changed");
        }
        if (proofs.Count != plan.ReadExpectedProviders().Length) throw new OpenShellStateConflictException("bootstrap_supervisor_set_changed");
        var endpoint = await _gateway.ReadBootstrapEndpointAsync(target, token);
        if (endpoint.Scheme != "https" || endpoint.AbsolutePath != "/" || endpoint.Query != "" || endpoint.Fragment != "" || endpoint.UserInfo != "")
            throw new OpenShellStateConflictException("bootstrap_endpoint_unqualified");
        // Fresh SQL mapping immediately before disclosure. The outside receiver independently
        // rechecks nonce/mapping/deadlines and CURRENT supervisor; this is not data-tool admission.
        var fresh = await ReadPlanAsync(launchId, epoch, target, proofs, token);
        if (fresh != plan) throw new OpenShellStateConflictException("bootstrap_plan_changed");
        var capability = _artifacts.ReadCapability(plan.Hash, launchId, epoch);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint, "/bootstrap/probe"))
        { Content = new ByteArrayContent([]) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", capability);
        using var response = await _control.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).WaitAsync(token);
        if (response.StatusCode != HttpStatusCode.NoContent) throw new OpenShellStateConflictException("bootstrap_control_unconfirmed");
    }
    private async Task<OpenShellLaunchPlan> ReadPlanAsync(Guid id, Guid epoch, OpenShellSandboxSnapshot target, IReadOnlyList<OpenShellSupervisorProof> proofs, CancellationToken ct)
    {
        await using var db = _createDb();
        var launch = await db.OpenShellLaunches.AsNoTracking().SingleOrDefaultAsync(l => l.Id == id, ct);
        var lease = await db.OpenShellLeases.AsNoTracking().SingleOrDefaultAsync(l => l.Owner == id, ct);
        var now = DateTime.UtcNow;
        if (launch is null || launch.IdentityEpoch != epoch || launch.RevokedAt is not null
            || launch.Phase != "security_ready" || launch.ActiveAgentId != launch.AgentId
            || lease?.AgentId != launch.AgentId || lease.ExpiresAt <= now
            || launch.GatewayId != target.GatewayId || launch.SandboxId != target.Id || launch.Workspace != target.Workspace
            || launch.PreparationDeadline is null || launch.PreparationDeadline <= now
            || launch.CreateAttemptedAt is null || launch.CreateAttemptedAt.Value.AddSeconds(60) <= now
            || launch.CapabilitySecretRef != OpenShellBootstrapArtifacts.Reference(id, epoch)
            || launch.SecurityReadyJson != JsonSerializer.Serialize(proofs) || launch.PreparationJson is null
            || await db.OpenShellRetiredIds.AnyAsync(r => r.GatewayId == target.GatewayId && r.SandboxId == target.Id, ct))
            throw new OpenShellStateConflictException("bootstrap_owner_not_current");
        var scope = JsonSerializer.Deserialize<OpenShellPreparationScope>(launch.PreparationJson)
            ?? throw new OpenShellStateConflictException("bootstrap_scope_missing");
        if (scope.AgentId != launch.AgentId || scope.Plan.Hash != scope.PlanHash || scope.Plan.GatewayId != target.GatewayId
            || scope.Plan.Workspace != target.Workspace || scope.Plan.ImageHash != launch.ImageHash || scope.Plan.PolicyHash != launch.PolicyHash)
            throw new OpenShellStateConflictException("bootstrap_scope_changed");
        return scope.Plan;
    }
    public void Dispose() => _control.Dispose();
}
