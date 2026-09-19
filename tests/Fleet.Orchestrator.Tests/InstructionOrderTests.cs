using System.Text.Json;
using Fleet.Orchestrator.Data;
using Fleet.Orchestrator.Services;

namespace Fleet.Orchestrator.Tests;

/// <summary>
/// The instruction order the orchestrator hands the agent (#309).
/// </summary>
/// <remarks>
/// <para>
/// The agent inlines instructions in the order this produces, so the ordering rule and the
/// <c>base</c> → <c>_base</c> mapping are tested here rather than inferred from the files that
/// happen to land on disk. Both generators — the <c>roles/</c> tree and <c>appsettings.json</c> —
/// go through the same two helpers on purpose: two sort expressions would be two answers to one
/// question, and the disagreement would show up as an instruction that exists and is never
/// assembled.
/// </para>
/// </remarks>
public class InstructionOrderTests
{
    private static Agent AgentWith(params (string Name, int LoadOrder)[] instructions)
    {
        var agent = new Agent
        {
            Name = "test-agent",
            DisplayName = "Test Agent",
            Role = "co-cto",
            Model = "claude-opus-5",
            ContainerName = "fleet-test-agent",
        };

        foreach (var (name, loadOrder) in instructions)
        {
            agent.Instructions.Add(new AgentInstruction
            {
                LoadOrder = loadOrder,
                Instruction = new Instruction { Name = name },
            });
        }

        return agent;
    }

    // ── ordering ─────────────────────────────────────────────────────────────

    /// <summary>Ascending load order, which is what the assignment tool already promises.</summary>
    [Fact]
    public void OrderedInstructions_SortsByAscendingLoadOrder()
    {
        var agent = AgentWith(("etiquette", 30), ("base", 0), ("co-cto", 10), ("review", 20));

        var ordered = ContainerProvisioningService.OrderedInstructions(agent)
            .Select(ai => ai.Instruction.Name)
            .ToArray();

        Assert.Equal(["base", "co-cto", "review", "etiquette"], ordered);
    }

    /// <summary>
    /// A shared load order is broken by name, ordinally — never left to the database.
    /// </summary>
    /// <remarks>
    /// <c>LoadOrder</c> is not unique, so it is not a total order on its own. Without a tiebreak,
    /// two instructions sharing one would be assembled in whatever order the query returned, which
    /// can differ between runs of the same configuration. The fixture is inserted in reverse so a
    /// missing tiebreak fails rather than passing by insertion luck.
    /// </remarks>
    [Fact]
    public void OrderedInstructions_BreaksATiedLoadOrderByName()
    {
        var agent = AgentWith(("zulu", 10), ("alpha", 10), ("mike", 10), ("base", 0));

        var ordered = ContainerProvisioningService.OrderedInstructions(agent)
            .Select(ai => ai.Instruction.Name)
            .ToArray();

        Assert.Equal(["base", "alpha", "mike", "zulu"], ordered);
    }

    /// <summary>The order is stable across repeated calls on the same configuration.</summary>
    [Fact]
    public void OrderedInstructions_IsStable()
    {
        var agent = AgentWith(("zulu", 10), ("alpha", 10), ("base", 0), ("mike", 5));

        var first = ContainerProvisioningService.OrderedInstructions(agent)
            .Select(ai => ai.Instruction.Name).ToArray();
        var second = ContainerProvisioningService.OrderedInstructions(agent)
            .Select(ai => ai.Instruction.Name).ToArray();

        Assert.Equal(first, second);
    }

    // ── the directory mapping ────────────────────────────────────────────────

    /// <summary>
    /// <c>base</c> maps to <c>_base</c>; everything else keeps its name.
    /// </summary>
    /// <remarks>
    /// The agent is handed directory names rather than instruction names precisely so it never has
    /// to re-implement this. A second copy of the rename is a second thing that can drift.
    /// </remarks>
    [Theory]
    [InlineData("base", "_base")]
    [InlineData("co-cto", "co-cto")]
    [InlineData("review-security", "review-security")]
    public void InstructionDirectoryName_MapsBaseAndLeavesTheRest(string name, string expected)
    {
        Assert.Equal(expected, ContainerProvisioningService.InstructionDirectoryName(name));
    }

    // ── what reaches the agent ───────────────────────────────────────────────

    /// <summary>
    /// The generated <c>appsettings.json</c> carries every assigned instruction, in order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the half of #309 the agent cannot supply for itself. A directory listing under
    /// <c>roles/</c> has no load order, and it cannot tell an assigned instruction from a stale
    /// directory left behind by one that was unassigned — the generator only ever writes files.
    /// </para>
    /// <para>
    /// The extras are the point: before this change they were written to disk and never read, with
    /// the config API still reporting them as assigned.
    /// </para>
    /// </remarks>
    [Fact]
    public void GenerateAppsettingsJson_CarriesEveryAssignedInstructionInOrder()
    {
        var agent = AgentWith(
            ("base", 0), ("co-cto", 10), ("review-security", 20), ("chat-etiquette", 30));

        var json = ContainerProvisioningService.GenerateAppsettingsJson(agent, ctoAgentName: "acto");

        var order = JsonDocument.Parse(json)
            .RootElement.GetProperty("Agent").GetProperty("InstructionOrder")
            .EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToArray();

        Assert.Equal(["_base", "co-cto", "review-security", "chat-etiquette"], order);
    }

    /// <summary>
    /// An agent with only base and role emits exactly those two, in that order.
    /// </summary>
    /// <remarks>
    /// The regression surface: this is the configuration almost every existing agent has, and its
    /// assembled prompt must not move. The agent-side test asserts the resulting bytes; this
    /// asserts the list that produces them.
    /// </remarks>
    [Fact]
    public void GenerateAppsettingsJson_BaseAndRoleOnly_EmitsExactlyThoseTwo()
    {
        var agent = AgentWith(("base", 0), ("co-cto", 10));

        var json = ContainerProvisioningService.GenerateAppsettingsJson(agent, ctoAgentName: "acto");

        var order = JsonDocument.Parse(json)
            .RootElement.GetProperty("Agent").GetProperty("InstructionOrder")
            .EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToArray();

        Assert.Equal(["_base", "co-cto"], order);
    }

    /// <summary>
    /// An agent with no assigned instructions emits an empty list, not a missing key.
    /// </summary>
    /// <remarks>
    /// An empty list and an absent key mean the same thing to the agent — fall back to the two old
    /// paths — but only because the fallback keys on <c>Count == 0</c>. Asserted so the shape is
    /// pinned rather than assumed.
    /// </remarks>
    [Fact]
    public void GenerateAppsettingsJson_NoInstructions_EmitsAnEmptyList()
    {
        var json = ContainerProvisioningService.GenerateAppsettingsJson(
            AgentWith(), ctoAgentName: "acto");

        var order = JsonDocument.Parse(json)
            .RootElement.GetProperty("Agent").GetProperty("InstructionOrder");

        Assert.Equal(JsonValueKind.Array, order.ValueKind);
        Assert.Empty(order.EnumerateArray());
    }
}
