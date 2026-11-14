using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Services;
using Microsoft.EntityFrameworkCore;

namespace Fleet.Orchestrator.Application;

/// <summary>What an allowlist change did to the stored list.</summary>
public enum AllowlistChange
{
    Added,
    AlreadyPresent,
    Removed,
    NotPresent,
}

/// <summary>
/// The one implementation of "add or remove one Telegram user or group on an agent's allowlist".
/// The four REST routes and the two MCP tools are adapters over it.
/// </summary>
/// <remarks>
/// A change is saved and then told to the running agent, so it applies without a reprovision. When
/// the agent could not be told the outcome carries <see cref="WriteOutcome{T}.NotDelivered"/>:
/// a removed user can still write to the agent until it is reprovisioned, which is never a success.
/// </remarks>
public sealed class AgentAllowlist(
    IServiceScopeFactory scopes,
    AgentConfigPublisherService publisher,
    ITelegramSetup telegram)
{
    public async Task<WriteOutcome<AllowlistChange>> AddUserAsync(
        string agentName, long userId, string? username = null, string? firstName = null, CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetService<OrchestratorDbContext>();
        if (db is null) return WriteFaults.NoDatabase;

        var agent = await db.Agents.Include(a => a.TelegramUsers).FirstOrDefaultAsync(a => a.Name == agentName, ct);
        if (agent is null) return WriteFaults.AgentNotFound(agentName);

        if (agent.TelegramUsers.Any(u => u.UserId == userId))
            return WriteOutcome<AllowlistChange>.Ok(AllowlistChange.AlreadyPresent);

        agent.TelegramUsers.Add(new AgentTelegramUser { AgentId = agent.Id, UserId = userId });
        await db.SaveChangesAsync(ct);

        var delivered = await publisher.PublishAllowlistUpdateAsync(agent.ShortName,
            addedUsers: [new AddedUserInfo { UserId = userId, Username = username, FirstName = firstName }],
            removedUserIds: [], addedGroupIds: [], removedGroupIds: [], ct: ct);
        return WriteOutcome<AllowlistChange>.Ok(AllowlistChange.Added,
            delivered ? null : LiveDelivery.AllowlistNotDelivered($"Telegram user {userId}", agentName));
    }

    /// <summary>
    /// Removes a user. The operator's own id is never removed, whoever asks: without it the
    /// operator is locked out of the agent.
    /// </summary>
    public async Task<WriteOutcome<AllowlistChange>> RemoveUserAsync(
        string agentName, long userId, CancellationToken ct = default)
    {
        if (telegram.OwnerUserId == userId)
            return WriteFaults.Invalid("owner_not_removable",
                $"Cannot remove the owner's Telegram user ID ({userId}) from the allowlist.");

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetService<OrchestratorDbContext>();
        if (db is null) return WriteFaults.NoDatabase;

        var agent = await db.Agents.Include(a => a.TelegramUsers).FirstOrDefaultAsync(a => a.Name == agentName, ct);
        if (agent is null) return WriteFaults.AgentNotFound(agentName);

        var existing = agent.TelegramUsers.FirstOrDefault(u => u.UserId == userId);
        if (existing is null)
            return WriteOutcome<AllowlistChange>.Ok(AllowlistChange.NotPresent);

        db.AgentTelegramUsers.Remove(existing);
        await db.SaveChangesAsync(ct);

        var delivered = await publisher.PublishAllowlistUpdateAsync(agent.ShortName,
            addedUsers: [], removedUserIds: [userId], addedGroupIds: [], removedGroupIds: [], ct: ct);
        return WriteOutcome<AllowlistChange>.Ok(AllowlistChange.Removed,
            delivered ? null : LiveDelivery.AllowlistNotDelivered($"the removal of Telegram user {userId}", agentName));
    }

    public async Task<WriteOutcome<AllowlistChange>> AddGroupAsync(
        string agentName, long groupId, CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetService<OrchestratorDbContext>();
        if (db is null) return WriteFaults.NoDatabase;

        var agent = await db.Agents.Include(a => a.TelegramGroups).FirstOrDefaultAsync(a => a.Name == agentName, ct);
        if (agent is null) return WriteFaults.AgentNotFound(agentName);

        if (agent.TelegramGroups.Any(g => g.GroupId == groupId))
            return WriteOutcome<AllowlistChange>.Ok(AllowlistChange.AlreadyPresent);

        agent.TelegramGroups.Add(new AgentTelegramGroup { AgentId = agent.Id, GroupId = groupId });
        await db.SaveChangesAsync(ct);

        var delivered = await publisher.PublishAllowlistUpdateAsync(agent.ShortName,
            addedUsers: [], removedUserIds: [], addedGroupIds: [groupId], removedGroupIds: [], ct: ct);
        return WriteOutcome<AllowlistChange>.Ok(AllowlistChange.Added,
            delivered ? null : LiveDelivery.AllowlistNotDelivered($"Telegram group {groupId}", agentName));
    }

    public async Task<WriteOutcome<AllowlistChange>> RemoveGroupAsync(
        string agentName, long groupId, CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetService<OrchestratorDbContext>();
        if (db is null) return WriteFaults.NoDatabase;

        var agent = await db.Agents.Include(a => a.TelegramGroups).FirstOrDefaultAsync(a => a.Name == agentName, ct);
        if (agent is null) return WriteFaults.AgentNotFound(agentName);

        var existing = agent.TelegramGroups.FirstOrDefault(g => g.GroupId == groupId);
        if (existing is null)
            return WriteOutcome<AllowlistChange>.Ok(AllowlistChange.NotPresent);

        db.AgentTelegramGroups.Remove(existing);
        await db.SaveChangesAsync(ct);

        var delivered = await publisher.PublishAllowlistUpdateAsync(agent.ShortName,
            addedUsers: [], removedUserIds: [], addedGroupIds: [], removedGroupIds: [groupId], ct: ct);
        return WriteOutcome<AllowlistChange>.Ok(AllowlistChange.Removed,
            delivered ? null : LiveDelivery.AllowlistNotDelivered($"the removal of Telegram group {groupId}", agentName));
    }
}
