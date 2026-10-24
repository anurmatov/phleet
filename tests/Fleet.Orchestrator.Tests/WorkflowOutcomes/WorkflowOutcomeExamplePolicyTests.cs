using System.Text;
using System.Text.Json.Nodes;
using Fleet.Orchestrator.Services;

namespace Fleet.Orchestrator.Tests.WorkflowOutcomes;

/// <summary>
/// The shipped example policies must load through the real strict loaders once an operator pins
/// them, and must refuse to load while they still carry the placeholders.
/// </summary>
public sealed class WorkflowOutcomeExamplePolicyTests
{
    private static readonly string ExamplesDir = Path.Combine(RepoRoot(), "docs", "examples", "workflow-outcomes");

    [Fact]
    [Trait("WorkflowOutcomeSuite", "ownership")]
    public void ExampleClassifiers_AsShipped_RefuseToLoadUntilPinned()
    {
        var bytes = File.ReadAllBytes(Path.Combine(ExamplesDir, "classifiers.json"));
        Assert.Throws<WorkflowOutcomePolicyException>(() => WorkflowOutcomeClassifierPolicy.Parse(bytes));
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "business")]
    public void ExampleClassifiers_PinnedByAnOperator_LoadEveryRule()
    {
        var text = File.ReadAllText(Path.Combine(ExamplesDir, "classifiers.json"))
            .Replace("OPERATOR-PINNED-SOURCE-COMMIT", new string('a', 40))
            .Replace("OPERATOR-PINNED-LOADED-DEFINITION-SHA256", new string('b', 64));
        var expected = JsonNode.Parse(text)!["rules"]!.AsArray().Count;

        var policy = WorkflowOutcomeClassifierPolicy.Parse(Encoding.UTF8.GetBytes(text));

        Assert.True(expected > 0);
        Assert.Equal(expected, policy.Rules.Count);
    }

    [Fact]
    [Trait("WorkflowOutcomeSuite", "ownership")]
    public void ExampleOwnership_WithItsProjectsActive_Loads()
    {
        var bytes = File.ReadAllBytes(Path.Combine(ExamplesDir, "ownership.json"));
        var policy = WorkflowOutcomeOwnershipPolicy.Parse(bytes, [("project-a", true), ("project-b", true), ("project-c", true)]);
        Assert.Equal("coordinator", policy.DefaultCoordinator);
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Fleet.sln"))) return dir.FullName;
        throw new InvalidOperationException("repository root (Fleet.sln) not found above the test output");
    }
}
