using Fleet.Agent.Configuration;
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
    /// Applies a diff and returns the user_ids that were genuinely new (not previously allowed).
    /// </summary>
    public AllowlistChangeset Apply(AllowlistDiff diff)
    {
        lock (_lock)
        {
            var newlyAddedUsers = new List<long>();
            foreach (var id in diff.AddedUserIds)
            {
                if (_allowedUserIds.Add(id))
                    newlyAddedUsers.Add(id);
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
    IReadOnlyList<long> AddedUserIds,
    IReadOnlyList<long> RemovedUserIds,
    IReadOnlyList<long> AddedGroupIds,
    IReadOnlyList<long> RemovedGroupIds);

public sealed record AllowlistChangeset(IReadOnlyList<long> NewlyAddedUserIds);
