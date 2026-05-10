using Microsoft.Extensions.Configuration;

namespace Fleet.Agent.Services;

/// <summary>
/// Resolves the CTO agent short name from the FLEET_CTO_AGENT environment variable at call-time.
/// Reads on every call — never snapshots at startup — so a peer-config update propagates
/// without a container reprovision.
/// </summary>
public class CtoAgentNameService(IConfiguration configuration)
{
    public virtual string GetCtoAgentName() =>
        configuration["FLEET_CTO_AGENT"] ?? string.Empty;
}
