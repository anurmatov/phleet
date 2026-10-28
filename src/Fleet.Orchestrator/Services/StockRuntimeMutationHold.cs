using System.Text.Json;
using System.Text.Json.Serialization;
using Fleet.Shared.Security;

namespace Fleet.Orchestrator.Services;

/// <summary>Immutable operator enrollment, retained after revocation until manual reconciliation.</summary>
public sealed class StockRuntimeMutationHold
{
    public const string Reason = "stock_runtime_manual_control_required";
    private readonly string? _agent;

    public StockRuntimeMutationHold(IConfiguration configuration)
    {
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

    public bool HasEnrollment { get; }
    internal bool IsEnrolledAgent(string agentName) =>
        _agent is not null && string.Equals(_agent, agentName, StringComparison.OrdinalIgnoreCase);
    public bool Denies(string agentName) => HasEnrollment && (_agent is null || IsEnrolledAgent(agentName));
}
