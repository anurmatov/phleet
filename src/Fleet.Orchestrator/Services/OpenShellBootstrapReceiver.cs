using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Fleet.Shared.Security;
using Microsoft.AspNetCore.Http.Features;

namespace Fleet.Orchestrator.Services;

public sealed class BootstrapRefusal(int status) : Exception
{
    public int Status { get; } = status;
}

/// <summary>Fresh authoritative mapping, immutable expected tuple and CURRENT gateway authority; no activation write.</summary>
public sealed class OpenShellBootstrapReader(Func<DbConnection> connections, IOpenShellGatewayClient gateway)
{
    public async Task VerifyAsync(VerifiedAgentIdentity identity, string jti, CancellationToken ct)
    {
        await using var db = connections(); await db.OpenAsync(ct);
        await using var tx = await db.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        await using var read = Command(db, tx, """
            SELECT a.Name,l.GatewayId,l.SandboxId,l.IdentityEpoch,l.Workspace,l.PreparationJson,l.SecurityReadyJson,
              l.PreparationDeadline,l.CreateAttemptedAt,lease.ExpiresAt,l.StopAttemptedAt,n.ExpiresAt,l.AgentId
            FROM agent_open_shell_launches l JOIN agents a ON a.Id=l.AgentId
            JOIN agent_open_shell_leases lease ON lease.AgentId=l.AgentId AND lease.Owner=l.Id
            JOIN identity_request_nonces n ON n.LaunchId=l.Id AND n.Jti=@jti
            WHERE l.Id=@launch AND l.ActiveAgentId=l.AgentId AND l.Phase='security_ready' AND l.RevokedAt IS NULL
              AND NOT EXISTS (SELECT 1 FROM open_shell_retired_ids r WHERE r.GatewayId=l.GatewayId AND r.SandboxId=l.SandboxId)
            """, ("@jti", jti), ("@launch", identity.LaunchId.ToString("D")));
        OpenShellPreparationScope scope; OpenShellSupervisorProof[] recorded; string workspace;
        DateTime deadline, createTime, leaseEnd, nonceEnd; DateTime? stopTime; int agentId;
        await using (var rows = await read.ExecuteReaderAsync(ct))
        {
            if (!await rows.ReadAsync(ct)) throw new BootstrapRefusal(403);
            if (rows.GetString(0) != identity.AgentName || rows.GetString(1) != identity.GatewayId
                || rows.GetString(2) != identity.SandboxId || rows.GetValue(3).ToString() != identity.IdentityEpoch.ToString("D"))
                throw new BootstrapRefusal(403);
            workspace = rows.GetString(4);
            scope = JsonSerializer.Deserialize<OpenShellPreparationScope>(rows.GetString(5)) ?? throw new BootstrapRefusal(403);
            recorded = JsonSerializer.Deserialize<OpenShellSupervisorProof[]>(rows.GetString(6)) ?? throw new BootstrapRefusal(403);
            deadline = rows.GetDateTime(7); createTime = rows.GetDateTime(8); leaseEnd = rows.GetDateTime(9);
            stopTime = rows.IsDBNull(10) ? null : rows.GetDateTime(10); nonceEnd = rows.GetDateTime(11); agentId = rows.GetInt32(12);
            if (await rows.ReadAsync(ct)) throw new BootstrapRefusal(403);
        }
        void CheckTime()
        {
            var now = DateTime.UtcNow;
            if (deadline <= now || createTime.AddSeconds(60) <= now || leaseEnd <= now || nonceEnd <= now
                || stopTime is { } stopped && stopped.AddSeconds(120) <= now) throw new BootstrapRefusal(403);
        }
        CheckTime();
        if (scope.AgentId != agentId || scope.PlanHash != scope.Plan.Hash || scope.Plan.GatewayId != identity.GatewayId || scope.Plan.Workspace != workspace
            || scope.Deadline.Ticks / 10 != deadline.Ticks / 10) throw new BootstrapRefusal(409);
        var expected = scope.Plan.ReadExpectedProviders();
        var providers = scope.Plan.ReadSpec().Providers;
        if (expected.Length == 0 || expected.Length != providers.Count || recorded.Length != expected.Length
            || expected.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != expected.Length
            || recorded.Select(p => p.Provider).Distinct(StringComparer.Ordinal).Count() != recorded.Length
            || expected.Any(p => !providers.Contains(p.Name))) throw new BootstrapRefusal(409);
        var target = await gateway.ReadExactAsync(identity.SandboxId, ct) ?? throw new BootstrapRefusal(409);
        if (target.GatewayId != identity.GatewayId || target.Workspace != workspace) throw new BootstrapRefusal(409);
        var fresh = await Task.WhenAll(expected.Select(p => gateway.ReadCurrentSupervisorAsync(target, p.Name, ct)));
        var completed = DateTime.UtcNow;
        foreach (var (required, proof) in expected.Zip(fresh))
        {
            var old = recorded.SingleOrDefault(p => p.Provider == required.Name);
            if (old is null || proof.Provider != required.Name || !required.Matches(proof.Desired)
                || !proof.Desired.Equals(old.Desired) || proof.SessionId != old.SessionId
                || proof.ProcessInstanceId != old.ProcessInstanceId || proof.NetworkInstanceId != old.NetworkInstanceId
                || proof.ProcessInstanceId != target.SupervisorInstanceId || proof.Desired.SandboxId != identity.SandboxId
                || proof.EvaluatedAt > completed || completed - proof.EvaluatedAt > TimeSpan.FromSeconds(5)) throw new BootstrapRefusal(409);
        }
        // The ingress consumed the issued nonce. This separate durable marker prevents a second
        // receiver delivery under the same mTLS custody, including across receiver restarts.
        await using var duplicate = Command(db, tx, "SELECT COUNT(*) FROM identity_request_nonces WHERE Jti=@jti", ("@jti", "bootstrap:" + jti));
        if (Convert.ToInt64(await duplicate.ExecuteScalarAsync(ct)) != 0) throw new BootstrapRefusal(403);
        await using var insert = Command(db, tx, "INSERT INTO identity_request_nonces (Jti,LaunchId,ExpiresAt) VALUES (@jti,@launch,@expires)",
            ("@jti", "bootstrap:" + jti), ("@launch", identity.LaunchId.ToString("D")), ("@expires", nonceEnd));
        await insert.ExecuteNonQueryAsync(ct); CheckTime(); await tx.CommitAsync(ct);
    }
    private static DbCommand Command(DbConnection db, DbTransaction tx, string sql, params (string Name, object Value)[] parameters)
    {
        var command = db.CreateCommand(); command.Transaction = tx; command.CommandText = sql; command.CommandTimeout = 2;
        foreach (var (name, value) in parameters) { var p = command.CreateParameter(); p.ParameterName = name; p.Value = value; command.Parameters.Add(p); }
        return command;
    }
}

public sealed class OpenShellBootstrapReceiver(OpenShellBootstrapTrust trust, OpenShellBootstrapReader reader)
{
    public async Task HandleAsync(HttpContext context)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted); deadline.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            if (!context.Request.IsHttps || context.Connection.LocalPort != trust.Port
                || !trust.AcceptCertificate(context.Connection.ClientCertificate)) throw new BootstrapRefusal(401);
            var target = context.Features.Get<IHttpRequestFeature>()?.RawTarget ?? context.Request.Path + context.Request.QueryString;
            if (context.Request.Method != "POST" || target != "/identity/ready" || context.Request.Headers.ContainsKey("Authorization")
                || context.Request.Headers.ContainsKey("Mcp-Session-Id") || context.Request.ContentLength is > 0
                || context.Request.Headers.ContainsKey("Transfer-Encoding")
                || await context.Request.Body.ReadAsync(new byte[1], deadline.Token) != 0) throw new BootstrapRefusal(403);
            if (context.Request.Headers.Count > 128 || context.Request.Headers.Sum(h => h.Key.Length + h.Value.Sum(v => v?.Length ?? 0)) > 65536
                || context.Request.Headers.Keys.Any(h => h.StartsWith("X-Fleet-", StringComparison.OrdinalIgnoreCase)
                    && !h.Equals("X-Fleet-Assertion", StringComparison.OrdinalIgnoreCase))) throw new BootstrapRefusal(403);
            var proof = context.Request.Headers["X-Fleet-Assertion"];
            if (proof.Count != 1 || proof[0] is null) throw new BootstrapRefusal(401);
            var (identity, jti) = trust.Assertions.Verify(proof[0]!, new("POST", "/identity/ready", FleetRequestBinding.AbsentSessionHash,
                FleetRequestBinding.AbsentSessionHash), "orchestrator", "/identity/ready");
            await reader.VerifyAsync(identity, jti, deadline.Token).WaitAsync(deadline.Token);
            context.Response.StatusCode = 204;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { context.Response.StatusCode = ex is BootstrapRefusal refusal ? refusal.Status : ex is CryptographicException ? 401 : 503; }
        context.Response.Headers.CacheControl = "no-store";
    }
}
