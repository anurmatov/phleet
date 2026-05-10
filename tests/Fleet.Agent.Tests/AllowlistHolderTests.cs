using Fleet.Agent.Configuration;
using Fleet.Agent.Models;
using Fleet.Agent.Services;
using Microsoft.Extensions.Options;

namespace Fleet.Agent.Tests;

/// <summary>
/// Unit tests for <see cref="AllowlistHolder.Apply"/> covering add, remove,
/// duplicate-add, and the NewlyAddedUsers changeset returned to callers.
/// </summary>
public class AllowlistHolderTests
{
    private static AllowlistHolder Holder(long[]? users = null, long[]? groups = null)
    {
        var opts = Options.Create(new TelegramOptions
        {
            AllowedUserIds  = [..(users  ?? [])],
            AllowedGroupIds = [..(groups ?? [])],
        });
        return new AllowlistHolder(opts);
    }

    // ── Seeded-at-startup behaviour ───────────────────────────────────────────

    [Fact]
    public void SeededUsers_AreAllowed_AtStartup()
    {
        var holder = Holder(users: [111, 222]);
        Assert.True(holder.IsUserAllowed(111));
        Assert.True(holder.IsUserAllowed(222));
        Assert.False(holder.IsUserAllowed(999));
    }

    [Fact]
    public void SeededGroups_AreAllowed_AtStartup()
    {
        var holder = Holder(groups: [-100]);
        Assert.True(holder.IsGroupAllowed(-100));
        Assert.False(holder.IsGroupAllowed(-200));
    }

    // ── Apply — adding users ──────────────────────────────────────────────────

    [Fact]
    public void Apply_AddNewUser_ReturnsInChangeset()
    {
        var holder = Holder();
        var changeset = holder.Apply(new AllowlistDiff(
            AddedUsers:    [new AddedUserInfo { UserId = 42, Username = "alice", FirstName = "Alice" }],
            RemovedUserIds: [],
            AddedGroupIds: [],
            RemovedGroupIds: []));

        Assert.Single(changeset.NewlyAddedUsers);
        var u = changeset.NewlyAddedUsers[0];
        Assert.Equal(42, u.UserId);
        Assert.Equal("alice", u.Username);
        Assert.Equal("Alice", u.FirstName);
        Assert.True(holder.IsUserAllowed(42));
    }

    [Fact]
    public void Apply_AddExistingUser_NotInChangeset()
    {
        var holder = Holder(users: [42]);
        var changeset = holder.Apply(new AllowlistDiff(
            AddedUsers:    [new AddedUserInfo { UserId = 42 }],
            RemovedUserIds: [],
            AddedGroupIds: [],
            RemovedGroupIds: []));

        Assert.Empty(changeset.NewlyAddedUsers);
    }

    [Fact]
    public void Apply_AddMultipleUsers_AllNewReturnedInChangeset()
    {
        var holder = Holder(users: [1]);
        var changeset = holder.Apply(new AllowlistDiff(
            AddedUsers:    [
                new AddedUserInfo { UserId = 1 },   // existing
                new AddedUserInfo { UserId = 2, FirstName = "Bob" },
                new AddedUserInfo { UserId = 3 },
            ],
            RemovedUserIds: [],
            AddedGroupIds: [],
            RemovedGroupIds: []));

        Assert.Equal(2, changeset.NewlyAddedUsers.Count);
        Assert.Contains(changeset.NewlyAddedUsers, u => u.UserId == 2 && u.FirstName == "Bob");
        Assert.Contains(changeset.NewlyAddedUsers, u => u.UserId == 3);
    }

    // ── Apply — removing users ────────────────────────────────────────────────

    [Fact]
    public void Apply_RemoveUser_DisallowsSubsequentAccess()
    {
        var holder = Holder(users: [99]);
        holder.Apply(new AllowlistDiff(
            AddedUsers: [],
            RemovedUserIds: [99],
            AddedGroupIds: [],
            RemovedGroupIds: []));

        Assert.False(holder.IsUserAllowed(99));
    }

    // ── Apply — groups ────────────────────────────────────────────────────────

    [Fact]
    public void Apply_AddGroup_AllowsSubsequentGroupCheck()
    {
        var holder = Holder();
        holder.Apply(new AllowlistDiff(
            AddedUsers: [],
            RemovedUserIds: [],
            AddedGroupIds: [-1001],
            RemovedGroupIds: []));

        Assert.True(holder.IsGroupAllowed(-1001));
    }

    [Fact]
    public void Apply_RemoveGroup_DisallowsSubsequentGroupCheck()
    {
        var holder = Holder(groups: [-1001]);
        holder.Apply(new AllowlistDiff(
            AddedUsers: [],
            RemovedUserIds: [],
            AddedGroupIds: [],
            RemovedGroupIds: [-1001]));

        Assert.False(holder.IsGroupAllowed(-1001));
    }

    // ── GetAllowedGroupIds ────────────────────────────────────────────────────

    [Fact]
    public void GetAllowedGroupIds_ReflectsAppliedDiffs()
    {
        var holder = Holder(groups: [-100]);
        holder.Apply(new AllowlistDiff([], [], [-200], []));
        holder.Apply(new AllowlistDiff([], [], [], [-100]));

        var ids = holder.GetAllowedGroupIds();
        Assert.Single(ids);
        Assert.Contains(-200L, ids);
    }
}
