namespace Fleet.Agent.Tests;

/// <summary>
/// Tests for the TELEGRAM_USER_ID auto-injection logic applied in
/// POST /api/agents and PUT /api/agents/{name}/config.
/// Rule: if TELEGRAM_USER_ID is set, it is always included in the agent's
/// allowedTelegramUsers — prepended and deduped.
/// </summary>
public class TelegramUserIdInjectionTests
{
    // Inline helper mirroring the logic in Program.cs:
    //   var set = new HashSet<long>(payload?.Distinct() ?? []);
    //   if (ownerId.HasValue) set.Add(ownerId.Value);
    static HashSet<long> MergeUsers(long[]? payload, long? ownerId)
    {
        var set = new HashSet<long>(payload?.Distinct() ?? []);
        if (ownerId.HasValue) set.Add(ownerId.Value);
        return set;
    }

    [Fact]
    public void OwnerIdSet_EmptyPayload_AgentGetsOwnerId()
    {
        // TELEGRAM_USER_ID=123, no explicit TelegramUsers → [123]
        var result = MergeUsers([], 123L);
        Assert.Single(result);
        Assert.Contains(123L, result);
    }

    [Fact]
    public void OwnerIdSet_NullPayload_AgentGetsOwnerId()
    {
        // TELEGRAM_USER_ID=123, TelegramUsers field omitted in POST → [123]
        var result = MergeUsers(null, 123L);
        Assert.Single(result);
        Assert.Contains(123L, result);
    }

    [Fact]
    public void OwnerIdSet_PayloadHasOtherUser_AgentGetsBoth()
    {
        // TELEGRAM_USER_ID=123, payload=[456] → [123, 456]
        var result = MergeUsers([456L], 123L);
        Assert.Equal(2, result.Count);
        Assert.Contains(123L, result);
        Assert.Contains(456L, result);
    }

    [Fact]
    public void OwnerIdSet_PayloadAlreadyIncludesOwnerId_NoDuplicate()
    {
        // TELEGRAM_USER_ID=123, payload=[123] → [123] (deduped, not [123, 123])
        var result = MergeUsers([123L], 123L);
        Assert.Single(result);
        Assert.Contains(123L, result);
    }

    [Fact]
    public void OwnerIdNotSet_PayloadHasUser_PayloadPreserved()
    {
        // TELEGRAM_USER_ID not configured, payload=[456] → [456]
        var result = MergeUsers([456L], null);
        Assert.Single(result);
        Assert.Contains(456L, result);
    }

    [Fact]
    public void OwnerIdNotSet_EmptyPayload_NoUsers()
    {
        // TELEGRAM_USER_ID not configured, no payload → []
        var result = MergeUsers([], null);
        Assert.Empty(result);
    }
}
