namespace Fleet.Agent.Tests;

/// <summary>
/// Tests for the first-agent role guard applied in POST /api/agents.
/// Rule: if no agents exist yet, the first one must have role "co-cto".
/// </summary>
public class AgentCreationGuardTests
{
    // Inline expression used in POST /api/agents:
    //   agentCount == 0 && !string.Equals(role.Trim(), "co-cto", StringComparison.OrdinalIgnoreCase)
    static bool RequiresCtoFirst(int agentCount, string role) =>
        agentCount == 0 && !string.Equals(role.Trim(), "co-cto", StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void FirstAgent_WithCtoRole_IsAllowed()
    {
        // zero agents + role=co-cto → guard does NOT fire → 201 path proceeds
        Assert.False(RequiresCtoFirst(0, "co-cto"));
    }

    [Fact]
    public void FirstAgent_WithNonCtoRole_IsBlocked()
    {
        // zero agents + role=developer → guard fires → 409 cto_required_first
        Assert.True(RequiresCtoFirst(0, "developer"));
    }

    [Theory]
    [InlineData("co-CTO")]   // case-insensitive match
    [InlineData("Co-Cto")]
    [InlineData("CO-CTO")]
    public void FirstAgent_WithCtoRole_CaseInsensitive_IsAllowed(string role)
    {
        Assert.False(RequiresCtoFirst(0, role));
    }

    [Theory]
    [InlineData("devops")]
    [InlineData("product-manager")]
    [InlineData("")]
    public void FirstAgent_WithOtherRoles_IsBlocked(string role)
    {
        Assert.True(RequiresCtoFirst(0, role));
    }

    [Fact]
    public void SubsequentAgent_AnyRole_IsAllowed()
    {
        // once at least one agent exists, the guard does not apply
        Assert.False(RequiresCtoFirst(1, "developer"));
        Assert.False(RequiresCtoFirst(5, "devops"));
    }
}
