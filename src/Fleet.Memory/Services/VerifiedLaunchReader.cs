using Fleet.Memory.Configuration;
using Fleet.Shared.Security;
using Microsoft.Extensions.Options;
using MySqlConnector;

namespace Fleet.Memory.Services;

public interface IVerifiedLaunchReader
{
    Task<bool> IsCurrentAsync(VerifiedAgentIdentity identity, string consumedJti, CancellationToken ct);
    // A missing implementation must deny, never infer ownership from a well-formed session hash.
    Task<bool> OwnsSessionAsync(VerifiedAgentIdentity identity, string sessionHash, string consumedJti, CancellationToken ct)
        => Task.FromResult(false);
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
    public async Task<bool> OwnsSessionAsync(VerifiedAgentIdentity identity, string sessionHash, string consumedJti, CancellationToken ct)
    {
        await using var connection = new MySqlConnection(options.Value.ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand(); command.CommandTimeout = 5;
        // Expired ownership is sufficient only for authenticated 404, not tool admission.
        command.CommandText = """
            SELECT COUNT(*) FROM agent_identity_sessions s
            JOIN agent_open_shell_launches l ON l.Id=s.LaunchId AND l.IdentityEpoch=s.IdentityEpoch AND l.AgentId=s.AgentId
            JOIN agents a ON a.Id=l.AgentId JOIN identity_request_nonces n ON n.LaunchId=l.Id
            WHERE l.Id=@launch AND l.IdentityEpoch=@epoch AND l.GatewayId=@gateway AND l.SandboxId=@sandbox
              AND BINARY a.Name=BINARY @agent AND l.ActiveAgentId=l.AgentId AND l.RevokedAt IS NULL
              AND l.Phase IN ('activated','committed') AND n.Jti=@jti AND n.ExpiresAt>UTC_TIMESTAMP(6)
              AND s.SessionHash=@session AND s.Service='memory' AND s.Route='/mcp'
            """;
        command.Parameters.AddWithValue("@launch", identity.LaunchId);
        command.Parameters.AddWithValue("@epoch", identity.IdentityEpoch);
        command.Parameters.AddWithValue("@gateway", identity.GatewayId);
        command.Parameters.AddWithValue("@sandbox", identity.SandboxId);
        command.Parameters.AddWithValue("@agent", identity.AgentName);
        command.Parameters.AddWithValue("@jti", consumedJti);
        command.Parameters.AddWithValue("@session", sessionHash);
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct)) == 1;
    }

}
