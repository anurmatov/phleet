using Fleet.Shared;

namespace Fleet.Orchestrator.Tests.Helpers;

/// <summary>
/// #367 shared bounds: 4,096..1,048,576 tokens inclusive, invalid values faulted rather than
/// clamped, no default. PUT and MCP update_agent_config both call this single validator.
/// </summary>
public class ContextWindowValidationTests
{
    [Theory]
    [InlineData(4_096)]
    [InlineData(65_536)]
    [InlineData(131_072)]
    [InlineData(1_048_576)]
    public void InRange_Accepted(int tokens)
    {
        Assert.Null(ContextWindow.DescribeFault(tokens));
    }

    [Theory]
    [InlineData(4_095)]
    [InlineData(1_048_577)]
    [InlineData(-1)]
    public void OutOfRange_Faulted_NamingTheRange(int tokens)
    {
        var fault = ContextWindow.DescribeFault(tokens);

        Assert.NotNull(fault);
        Assert.Contains(tokens.ToString(), fault);
        Assert.Contains($"{ContextWindow.MinTokens}..{ContextWindow.MaxTokens}", fault);
    }

    [Fact]
    public void EnvVar_IsTheOneClaudeCliReads()
    {
        Assert.Equal("CLAUDE_CODE_MAX_CONTEXT_TOKENS", ContextWindow.EnvVar);
    }
}
