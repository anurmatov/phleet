using Fleet.Shared;

namespace Fleet.Orchestrator.Tests.Helpers;

/// <summary>
/// #357 shared bounds: 10..600 inclusive, default 60, invalid values faulted rather than clamped.
/// REST create, PUT and MCP update_agent_config all call this single validator.
/// </summary>
public class WarmupTimeoutValidationTests
{
    [Theory]
    [InlineData(10)]
    [InlineData(60)]
    [InlineData(180)]
    [InlineData(600)]
    public void InRange_Accepted(int seconds)
    {
        Assert.Null(WarmupTimeout.DescribeFault(seconds));
    }

    [Theory]
    [InlineData(9)]
    [InlineData(601)]
    [InlineData(0)]
    [InlineData(-1)]
    public void OutOfRange_Faulted(int seconds)
    {
        var fault = WarmupTimeout.DescribeFault(seconds);

        Assert.NotNull(fault);
        Assert.Contains(seconds.ToString(), fault);
        Assert.Contains($"{WarmupTimeout.MinSeconds}..{WarmupTimeout.MaxSeconds}", fault);
    }

    [Fact]
    public void Default_StaysAtTheCloudFriendlySixty()
    {
        Assert.Equal(60, WarmupTimeout.DefaultSeconds);
    }
}
