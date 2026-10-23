using Fleet.Memory.Configuration;
using Fleet.Shared.Security;
using Microsoft.Extensions.Options;
using MySqlConnector;

namespace Fleet.Memory.Services;

public interface IVerifiedLaunchReader
{
    Task<bool> IsCurrentAsync(VerifiedAgentIdentity identity, string consumedJti, CancellationToken ct);
}

/// <summary>Read-only authoritative check per request, never a positive mapping cache or writer.</summary>
public sealed class VerifiedLaunchReader(IOptions<FleetIdentityOptions> options) : IVerifiedLaunchReader
{
    public async Task<bool> IsCurrentAsync(VerifiedAgentIdentity identity, string consumedJti, CancellationToken ct)
    {
        await using var connection = new MySqlConnection(options.Value.ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 5;
        command.CommandText = """
            SELECT COUNT(*) FROM agent_open_shell_launches l
            JOIN agents a ON a.Id=l.AgentId
            JOIN identity_request_nonces n ON n.LaunchId=l.Id
            WHERE l.Id=@launch AND l.IdentityEpoch=@epoch AND l.GatewayId=@gateway
              AND l.SandboxId=@sandbox AND BINARY a.Name=BINARY @agent
              AND l.ActiveAgentId=l.AgentId AND l.RevokedAt IS NULL
              AND l.Phase IN ('activated','committed') AND n.Jti=@jti AND n.ExpiresAt>UTC_TIMESTAMP(6)
            """;
        command.Parameters.AddWithValue("@launch", identity.LaunchId);
        command.Parameters.AddWithValue("@epoch", identity.IdentityEpoch);
        command.Parameters.AddWithValue("@gateway", identity.GatewayId);
        command.Parameters.AddWithValue("@sandbox", identity.SandboxId);
        command.Parameters.AddWithValue("@agent", identity.AgentName);
        command.Parameters.AddWithValue("@jti", consumedJti);
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct)) == 1;
    }
}
