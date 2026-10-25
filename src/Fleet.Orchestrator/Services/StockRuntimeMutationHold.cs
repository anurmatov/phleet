using Fleet.Shared.Security;

namespace Fleet.Orchestrator.Services;

/// <summary>Immutable operator enrollment, retained after revocation until manual reconciliation.</summary>
public sealed class StockRuntimeMutationHold
{
    public const string Reason = "stock_runtime_manual_control_required";
    private readonly string? _agent;

    public StockRuntimeMutationHold(IConfiguration configuration)
    {
        var identity = StockBrokerIdentity.Optional(configuration["StockOpenShell:AuthorityFile"],
            configuration["StockOpenShell:BrokerPasswordFile"], "observer");
        _agent = identity?.Agent;
    }

    public bool HasEnrollment => _agent is not null;
    public bool Denies(string agentName) => _agent is not null && string.Equals(_agent, agentName, StringComparison.OrdinalIgnoreCase);
}
