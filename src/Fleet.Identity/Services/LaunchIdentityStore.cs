using System.Data;
using System.Data.Common;
using Fleet.Identity.Configuration;
using Fleet.Shared.Security;
using MySqlConnector;

namespace Fleet.Identity.Services;

public interface IIdentityConnectionFactory
{
    DbConnection Create();
}
public sealed class MySqlIdentityConnectionFactory(IdentityOptions options) : IIdentityConnectionFactory
{
    public DbConnection Create() => new MySqlConnection(options.ConnectionString);
}

// A connection/transaction per operation; no tracked entities, positive caches or retry-on-unknown-commit.
public sealed class LaunchIdentityStore(IIdentityConnectionFactory connections)
{
    public async Task CheckAvailableAsync(CancellationToken ct)
    {
        await using var connection = connections.Create(); await connection.OpenAsync(ct);
        await using var command = Command(connection, null, """
            SELECT n.Jti,s.SessionHash,s.IdentityEpoch,l.Id,l.Phase,l.ActiveAgentId,a.Name,r.SandboxId
            FROM identity_request_nonces n
            JOIN agent_open_shell_launches l ON l.Id=n.LaunchId JOIN agents a ON a.Id=l.AgentId
            LEFT JOIN agent_identity_sessions s ON s.LaunchId=l.Id
            LEFT JOIN open_shell_retired_ids r ON r.GatewayId=l.GatewayId AND r.SandboxId=l.SandboxId WHERE 1=0
            """);
        await command.ExecuteScalarAsync(ct);
    }

    public async Task<VerifiedAgentIdentity> ResolveAsync(string gateway, string sandbox, string? session, CancellationToken ct, bool allowExpiredSession = false)
    {
        await using var connection = connections.Create(); await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var (identity, _) = await ReadAsync(connection, transaction, gateway, sandbox, ct);
        await CheckSessionAsync(connection, transaction, identity, session, ct, allowExpiredSession);
        await transaction.CommitAsync(ct);
        return identity;
    }

    // Called only after cryptographic verification. Nonce insertion and fresh mapping/session admission are atomic.
    public async Task ConsumeAsync(VerifiedAgentIdentity expected, string jti, DateTime expires, string? session, CancellationToken ct)
    {
        await using var connection = connections.Create(); await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var (identity, _) = await ReadAsync(connection, transaction, expected.GatewayId, expected.SandboxId, ct);
        if (identity != expected) throw new IdentityDeniedException("launch_changed");
        await CheckSessionAsync(connection, transaction, identity, session, ct);
        await using (var duplicate = Command(connection, transaction,
            "SELECT COUNT(*) FROM identity_request_nonces WHERE Jti=@jti", ("@jti", jti)))
            if (Convert.ToInt64(await duplicate.ExecuteScalarAsync(ct)) != 0) throw new IdentityDeniedException("replay");
        await using var insert = Command(connection, transaction,
            "INSERT INTO identity_request_nonces (Jti,LaunchId,ExpiresAt) VALUES (@jti,@launch,@expires)",
            ("@jti", jti), ("@launch", identity.LaunchId.ToString("D")), ("@expires", expires));
        try { await insert.ExecuteNonQueryAsync(ct); }
        catch (MySqlException ex) when (ex.Number == 1062) { throw new IdentityDeniedException("replay"); }
        // Deadlocks and uncertain commits remain unavailable; never retried.
        await transaction.CommitAsync(ct);
    }

    public async Task BindSessionAsync(VerifiedAgentIdentity expected, string session, CancellationToken ct)
    {
        if (!RequestRules.ValidSession(session)) throw new IdentityDeniedException("session_shape");
        await using var connection = connections.Create(); await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var (identity, agentId) = await ReadAsync(connection, transaction, expected.GatewayId, expected.SandboxId, ct);
        if (identity != expected) throw new IdentityDeniedException("launch_changed");
        var hash = RequestRules.SessionHash(session);
        await using (var count = Command(connection, transaction,
            "SELECT COUNT(*) FROM agent_identity_sessions WHERE LaunchId=@launch AND ExpiresAt>@now",
            ("@launch", identity.LaunchId.ToString("D")), ("@now", DateTime.UtcNow)))
            if (Convert.ToInt64(await count.ExecuteScalarAsync(ct)) >= 100) throw new IdentityDeniedException("session_limit");
        await using var insert = Command(connection, transaction, """
            INSERT INTO agent_identity_sessions (SessionHash,Service,Route,AgentId,LaunchId,IdentityEpoch,ExpiresAt)
            VALUES (@hash,'memory','/mcp',@agent,@launch,@epoch,@expires)
            """, ("@hash", hash), ("@agent", agentId), ("@launch", identity.LaunchId.ToString("D")),
            ("@epoch", identity.IdentityEpoch.ToString("D")), ("@expires", DateTime.UtcNow.AddHours(1)));
        // No UPSERT: collision (including an old, expired or foreign session) is never a rebind.
        await insert.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
    }

    public async Task EndSessionAsync(VerifiedAgentIdentity expected, string session, CancellationToken ct)
    {
        await using var connection = connections.Create(); await connection.OpenAsync(ct);
        await using var command = Command(connection, null, """
            UPDATE agent_identity_sessions SET ExpiresAt=@expired WHERE SessionHash=@hash
              AND Service='memory' AND Route='/mcp' AND LaunchId=@launch AND IdentityEpoch=@epoch
            """, ("@expired", DateTime.UnixEpoch), ("@hash", RequestRules.SessionHash(session)),
            ("@launch", expected.LaunchId.ToString("D")), ("@epoch", expected.IdentityEpoch.ToString("D")));
        if (await command.ExecuteNonQueryAsync(ct) != 1) throw new IdentityDeniedException("session_changed");
    }

    private static async Task<(VerifiedAgentIdentity, int)> ReadAsync(DbConnection connection, DbTransaction transaction,
        string gateway, string sandbox, CancellationToken ct)
    {
        await using var command = Command(connection, transaction, """
            SELECT a.Name,l.Id,l.IdentityEpoch,a.Id,l.GatewayId,l.SandboxId FROM agent_open_shell_launches l
            JOIN agents a ON a.Id=l.AgentId
            WHERE l.GatewayId=@gateway AND l.SandboxId=@sandbox AND l.ActiveAgentId=l.AgentId
              AND l.RevokedAt IS NULL AND l.Phase IN ('activated','committed')
              AND NOT EXISTS (SELECT 1 FROM open_shell_retired_ids r WHERE r.GatewayId=l.GatewayId AND r.SandboxId=l.SandboxId)
            """, ("@gateway", gateway), ("@sandbox", sandbox));
        await using var rows = await command.ExecuteReaderAsync(ct);
        if (!await rows.ReadAsync(ct)) throw new IdentityDeniedException("launch_inactive");
        var identity = new VerifiedAgentIdentity(rows.GetString(0), rows.GetString(4), rows.GetString(5),
            Guid.Parse(rows.GetValue(1).ToString()!), Guid.Parse(rows.GetValue(2).ToString()!), "memory", "/mcp");
        var agentId = rows.GetInt32(3);
        // Equality is rechecked ordinally; database collation is not a cryptographic identity comparison.
        if (identity.GatewayId != gateway || identity.SandboxId != sandbox || await rows.ReadAsync(ct))
            throw new IdentityDeniedException("launch_ambiguous");
        return (identity, agentId);
    }
    private static async Task CheckSessionAsync(DbConnection connection, DbTransaction transaction,
        VerifiedAgentIdentity identity, string? session, CancellationToken ct, bool allowExpiredSession = false)
    {
        if (session is null) return;
        await using var command = Command(connection, transaction, """
            SELECT s.LaunchId,s.IdentityEpoch,a.Name,s.ExpiresAt FROM agent_identity_sessions s JOIN agents a ON a.Id=s.AgentId
            WHERE s.SessionHash=@hash AND s.Service='memory' AND s.Route='/mcp'
            """, ("@hash", RequestRules.SessionHash(session)));
        await using var rows = await command.ExecuteReaderAsync(ct);
        if (!await rows.ReadAsync(ct) || rows.GetValue(0).ToString() != identity.LaunchId.ToString("D")
            || rows.GetValue(1).ToString() != identity.IdentityEpoch.ToString("D") || rows.GetString(2) != identity.AgentName)
            throw new IdentityDeniedException("session_foreign");
        var expired = rows.GetDateTime(3) <= DateTime.UtcNow;
        if (await rows.ReadAsync(ct)) throw new IdentityDeniedException("session_foreign");
        if (expired && !allowExpiredSession) throw new IdentitySessionExpiredException();
    }
    private static DbCommand Command(DbConnection connection, DbTransaction? transaction, string sql,
        params (string Key, object Value)[] parameters)
    {
        var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql; command.CommandTimeout = 2;
        foreach (var (key, value) in parameters)
        { var parameter = command.CreateParameter(); parameter.ParameterName = key; parameter.Value = value; command.Parameters.Add(parameter); }
        return command;
    }
}
