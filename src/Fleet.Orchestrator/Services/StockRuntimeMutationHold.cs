using Fleet.Orchestrator.Data;
using Microsoft.EntityFrameworkCore;
using Fleet.Orchestrator.Helpers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Fleet.Shared.Security;

namespace Fleet.Orchestrator.Services;

/// <summary>Immutable operator enrollment, retained after revocation until manual reconciliation.</summary>
public sealed class StockRuntimeMutationHold
{
    public const string Reason = "stock_runtime_manual_control_required";
    private readonly string? _agent;
    private readonly IServiceScopeFactory? _scopeFactory;

    public StockRuntimeMutationHold(IConfiguration configuration, IServiceScopeFactory? scopeFactory = null)
    {
        _scopeFactory = scopeFactory;
        var authority = configuration["StockOpenShell:AuthorityFile"];
        HasEnrollment = !string.IsNullOrEmpty(authority)
            || !string.IsNullOrEmpty(configuration["StockOpenShell:BrokerPasswordFile"]);
        if (!HasEnrollment) return;
        try
        {
            // This is only a mutation hold, never authentication. Keep the public identity even
            // when inactive/expired. An unreadable or ambiguous identity holds ALL mutations
            // rather than taking down ordinary telemetry or silently enabling legacy fallback.
            if (authority is null || !Path.IsPathFullyQualified(authority)) return;
            var bytes = StockEnrollmentAuthority.ReadBounded(authority);
            using var document = JsonDocument.Parse(bytes);
            StockRequestProof.Unique(document.RootElement);
            var record = JsonSerializer.Deserialize<StockEnrollmentAuthority>(bytes,
                new JsonSerializerOptions { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow });
            var agent = record?.Agent;
            if (!string.IsNullOrWhiteSpace(agent) && agent.Length <= 64
                && agent.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
                _agent = agent;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
            or InvalidOperationException or ArgumentException or StockIdentityDenied)
        {
            // Unknown enrollment remains fail-closed until operator reconciliation.
        }
    }

    // Desired changes do not redirect ordinary Start/Stop/cancel. An applied non-Docker binding
    // must never reach legacy Docker/name routing while the managed adapter is unavailable.
    public async Task<string?> DescribeAppliedFaultAsync(string name, bool applying = false)
    {
        if (_scopeFactory is null) return null; // hosts without database-backed agent configuration
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetService<OrchestratorDbContext>();
        if (db is null) return null;
        try
        {
            var agent = await db.Agents.AsNoTracking().FirstOrDefaultAsync(a => a.Name == name);
            return agent is null ? null : applying
                ? AgentPatchHelpers.RuntimeApplyFault(agent)
                : agent.AppliedRuntimeKind != "docker" ? "managed_runtime_unavailable" : null;
        }
        catch (Exception ex) when (ex is System.Data.Common.DbException or Microsoft.EntityFrameworkCore.Storage.RetryLimitExceededException)
        { return "runtime_state_unavailable"; }
    }

    public bool HasEnrollment { get; }
    internal bool IsEnrolledAgent(string agentName) =>
        _agent is not null && string.Equals(_agent, agentName, StringComparison.OrdinalIgnoreCase);
    public bool Denies(string agentName) => HasEnrollment && (_agent is null || IsEnrolledAgent(agentName));
}
