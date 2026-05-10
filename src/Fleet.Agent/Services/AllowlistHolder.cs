using Fleet.Agent.Configuration;
using Fleet.Agent.Models;
using Microsoft.Extensions.Options;

namespace Fleet.Agent.Services;

/// <summary>
/// Thread-safe in-memory store for the agent's live Telegram allowlists.
/// Seeded at startup from TelegramOptions; updated at runtime via
/// config.update messages without a container reprovision.
/// </summary>
public sealed class AllowlistHolder
{
    private readonly object _lock = new();
    private HashSet<long> _allowedUserIds = [];
    private HashSet<long> _allowedGroupIds = [];

    public AllowlistHolder(IOptions<TelegramOptions> options)
    {
        _allowedUserIds = [..options.Value.AllowedUserIds];
        _allowedGroupIds = [..options.Value.AllowedGroupIds];
    }

    public bool IsUserAllowed(long userId) { lock (_lock) return _allowedUserIds.Contains(userId); }
    public bool IsGroupAllowed(long groupId) { lock (_lock) return _allowedGroupIds.Contains(groupId); }

    /// <summary>Returns a snapshot of the current allowed group IDs (for the proactive loop).</summary>
    public IReadOnlyList<long> GetAllowedGroupIds() { lock (_lock) return [.._allowedGroupIds]; }

    /// <summary>
    /// Applies a diff and returns the users that were genuinely new (not previously allowed),
    /// including any username/first_name info that was carried in the config.update message.
    /// </summary>
    public AllowlistChangeset Apply(AllowlistDiff diff)
    {
        lock (_lock)
        {
            var newlyAddedUsers = new List<NewlyAddedUser>();
            foreach (var info in diff.AddedUsers)
            {
                if (_allowedUserIds.Add(info.UserId))
                    newlyAddedUsers.Add(new NewlyAddedUser(info.UserId, info.Username, info.FirstName));
            }
            foreach (var id in diff.RemovedUserIds)
                _allowedUserIds.Remove(id);

            foreach (var id in diff.AddedGroupIds)
                _allowedGroupIds.Add(id);
            foreach (var id in diff.RemovedGroupIds)
                _allowedGroupIds.Remove(id);

            return new AllowlistChangeset(newlyAddedUsers);
        }
    }
}

public sealed record AllowlistDiff(
    IReadOnlyList<AddedUserInfo> AddedUsers,
    IReadOnlyList<long> RemovedUserIds,
    IReadOnlyList<long> AddedGroupIds,
    IReadOnlyList<long> RemovedGroupIds);

public sealed record AllowlistChangeset(IReadOnlyList<NewlyAddedUser> NewlyAddedUsers);

public sealed record NewlyAddedUser(long UserId, string? Username, string? FirstName);
