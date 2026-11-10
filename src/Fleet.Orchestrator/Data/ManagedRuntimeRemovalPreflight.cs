using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Fleet.Orchestrator.Data;

/// <summary>
/// Thrown by <see cref="ManagedRuntimeRemovalPreflight"/> when an agent still depends on the removed
/// managed sandbox runtime. Nothing has been migrated when it is thrown.
/// </summary>
public sealed class ManagedRuntimeRemovalBlockedException(string message) : Exception(message);

/// <summary>
/// The gate in front of <c>RemoveManagedStockRuntime</c>, called by <see cref="DbSeeder"/> before any
/// migration is applied.
/// </summary>
/// <remarks>
/// <para>
/// It runs only when <c>AddManagedStockRuntime</c> is applied and <c>RemoveManagedStockRuntime</c> is
/// pending. A fresh database, one that never had the managed schema, and one already past the removal
/// are not checked.
/// </para>
/// <para>
/// The managed columns are no longer in the EF model, so it reads them with raw read-only SQL and writes
/// nothing. An agent blocks the upgrade when its selected or applied runtime is not <c>docker</c>, or
/// when its stored runtime record is unreadable, mid-operation, or in any state other than
/// <c>none</c> or <c>revoked</c>. Such an agent may still have a sandbox running outside Docker, and this
/// release has no code left to stop it. The previous release must revoke it first.
/// </para>
/// </remarks>
public static class ManagedRuntimeRemovalPreflight
{
    public const string AddedMigration = "20261028135434_AddManagedStockRuntime";
    public const string RemovalMigration = "20261110112005_RemoveManagedStockRuntime";

    public const string NotAppliedMessage =
        "RemoveManagedStockRuntime not applied. The orchestrator will not start. "
        + "Redeploy the previous orchestrator image, revoke the managed runtime of each listed agent and "
        + "select the docker runtime for it, then deploy again. See docs/upgrading/remove-managed-runtime.md.";

    /// <exception cref="ManagedRuntimeRemovalBlockedException">An agent blocked the removal; nothing was migrated.</exception>
    public static async Task EnsureAsync(OrchestratorDbContext db, ILogger? logger, CancellationToken ct = default)
    {
        var applied = await db.Database.GetAppliedMigrationsAsync(ct);
        var pending = await db.Database.GetPendingMigrationsAsync(ct);
        if (!applied.Contains(AddedMigration, StringComparer.Ordinal)
            || !pending.Contains(RemovalMigration, StringComparer.Ordinal))
            return;

        var rows = await QueryAsync(db,
            "SELECT Name, RuntimeKind, AppliedRuntimeKind, AppliedRuntimeBinding FROM agents ORDER BY Name", ct);

        var blocked = new List<(string Agent, string Reason)>();
        foreach (var row in rows)
        {
            var reason = DescribeFault(row[1] as string, row[2] as string, row[3] as string);
            if (reason is not null)
                blocked.Add(((string)row[0]!, reason));
        }

        if (blocked.Count > 0)
        {
            foreach (var (agent, reason) in blocked)
                logger?.LogCritical("Managed runtime removal blocked: agent={Agent} reason={Reason}", agent, reason);
            logger?.LogCritical("{Message}", NotAppliedMessage);
            throw new ManagedRuntimeRemovalBlockedException(
                $"Managed runtime removal preflight blocked on {blocked.Count} agent(s) "
                + $"({string.Join(", ", blocked.Select(b => $"{b.Agent}: {b.Reason}"))}). {NotAppliedMessage}");
        }

        var policies = Convert.ToInt64(
            (await QueryAsync(db, "SELECT COUNT(*) FROM sandbox_policies", ct))[0][0], CultureInfo.InvariantCulture);
        var versions = Convert.ToInt64(
            (await QueryAsync(db, "SELECT COUNT(*) FROM sandbox_policy_versions", ct))[0][0], CultureInfo.InvariantCulture);
        logger?.LogInformation(
            "Managed runtime removal preflight: agents={Agents} savedPolicies={Policies} savedPolicyVersions={Versions} "
            + "result=pass (the saved policies and versions are dropped by the migration)",
            rows.Count, policies, versions);
    }

    /// <summary>
    /// Why this agent blocks the removal, or null when it does not. Fixed reason codes only: the stored
    /// record is never echoed.
    /// </summary>
    internal static string? DescribeFault(string? runtimeKind, string? appliedRuntimeKind, string? binding)
    {
        if (!string.Equals(runtimeKind, "docker", StringComparison.Ordinal))
            return "selected_runtime_not_docker";
        if (!string.Equals(appliedRuntimeKind, "docker", StringComparison.Ordinal))
            return "applied_runtime_not_docker";
        if (string.IsNullOrWhiteSpace(binding))
            return null;

        try
        {
            using var document = JsonDocument.Parse(binding);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return "runtime_record_unreadable";

            var state = root.TryGetProperty("state", out var s) && s.ValueKind == JsonValueKind.String
                ? s.GetString()
                : null;
            if (state is not ("none" or "revoked"))
                return "runtime_record_not_revoked";
            if (IsPresent(root, "operation") || IsPresent(root, "candidate"))
                return "runtime_record_in_progress";
            if (state == "none" && IsPresent(root, "applied"))
                return "runtime_record_not_revoked";
            return null;
        }
        catch (JsonException)
        {
            return "runtime_record_unreadable"; // unreadable is not absent
        }
    }

    private static bool IsPresent(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);

    private static async Task<List<object?[]>> QueryAsync(OrchestratorDbContext db, string sql, CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            await using DbCommand command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = sql;
            await using var reader = await command.ExecuteReaderAsync(ct);
            var rows = new List<object?[]>();
            while (await reader.ReadAsync(ct))
            {
                var row = new object?[reader.FieldCount];
                for (var i = 0; i < row.Length; i++)
                    row[i] = await reader.IsDBNullAsync(i, ct) ? null : reader.GetValue(i);
                rows.Add(row);
            }
            return rows;
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }
}
