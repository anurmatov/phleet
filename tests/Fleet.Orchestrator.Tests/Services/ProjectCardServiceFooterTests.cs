using Fleet.Orchestrator.Services;

namespace Fleet.Orchestrator.Tests.Services;

public class ProjectCardServiceFooterTests
{
    [Fact]
    public void Footer_for_a_fresh_card_is_exact()
    {
        var footer = ProjectCardService.RenderFooter("project-a", 3, 5, 5);
        Assert.Equal(
            "[project card: project-a · card v3 · written for full v5 · full is v5]\n" +
            "The full project-a context is attached to turns routed to this project.\n" +
            "On any other turn that needs it, call get_project_context with name \"project-a\".",
            footer);
    }

    [Fact]
    public void Footer_for_a_stale_card_says_may_be_stale()
    {
        var footer = ProjectCardService.RenderFooter("project-a", 1, 1, 2);
        Assert.StartsWith(
            "[project card: project-a · card v1 · written for full v1 · full is v2 · may be stale]\n", footer);
    }

    [Fact]
    public void Resident_card_is_card_blank_line_footer()
    {
        var body = ProjectCardService.RenderResidentCard("project-a", "Card body.\n\n", 1, 1, 1);
        Assert.Equal("Card body.\n\n" + ProjectCardService.RenderFooter("project-a", 1, 1, 1) + "\n", body);
    }

    [Fact]
    public void Evaluate_reports_stale_missing_and_invalid()
    {
        var state = ProjectCardService.Evaluate(
            cardVersion: 2, basedOnFullVersion: 1, cardContent: "<!-- keep:a --> <!-- keep:Bad -->",
            currentFullVersion: 3, fullContent: "<!-- keep:a --> <!-- keep:b -->");

        Assert.True(state.Stale);
        Assert.Equal(["b"], state.MissingKeeps);
        Assert.Equal(["<!-- keep:Bad -->"], state.InvalidKeeps);
    }
}
