using Fleet.Agent.Configuration;
using Fleet.Agent.Services;
using Fleet.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Fleet.Agent.Tests;

public class PromptBuilderFormattingModeTests
{
    private static PromptBuilder CreateBuilder(FormattingMode mode) =>
        new(
            Options.Create(new AgentOptions
            {
                Name           = "test-agent",
                Role           = "test",
                WorkDir        = Path.GetTempPath(),
                FormattingMode = mode,
            }),
            NullLogger<PromptBuilder>.Instance);

    [Fact]
    public void BuildSystemPrompt_Rich_ContainsOutputFormattingSection()
    {
        var prompt = CreateBuilder(FormattingMode.Rich).BuildSystemPrompt();
        Assert.Contains("## Output Formatting", prompt);
    }

    [Fact]
    public void BuildSystemPrompt_LegacyHtml_DoesNotContainOutputFormattingSection()
    {
        var prompt = CreateBuilder(FormattingMode.LegacyHtml).BuildSystemPrompt();
        Assert.DoesNotContain("## Output Formatting", prompt);
    }

    [Fact]
    public void BuildSystemPrompt_PlainText_DoesNotContainOutputFormattingSection()
    {
        var prompt = CreateBuilder(FormattingMode.PlainText).BuildSystemPrompt();
        Assert.DoesNotContain("## Output Formatting", prompt);
    }
}
