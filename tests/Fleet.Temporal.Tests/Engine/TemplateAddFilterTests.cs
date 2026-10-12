using Fleet.Temporal.Engine;

namespace Fleet.Temporal.Tests.Engine;

public class TemplateAddFilterTests
{
    private static TemplateEngine Engine(string? value) => new(new() { ["vars"] = new Dictionary<string,object?> { ["value"] = value } });

    [Theory]
    [InlineData("4","1","5")]
    [InlineData("0","-1","-1")]
    [InlineData("-1","1","0")]
    public void Add_IntegerOperands_ReturnsCheckedSum(string value,string amount,string result)
        => Assert.Equal(result,Engine(value).ResolveString("{{vars.value | add: "+amount+"}}"));

    [Theory]
    [InlineData("x","1")]
    [InlineData("1","x")]
    [InlineData("1.0","1")]
    [InlineData("1","1.0")]
    [InlineData(" 1","1")]
    [InlineData(null,"1")]
    [InlineData("1","")]
    public void Add_NonIntegerOperands_Throws(string? value,string amount)
        => Assert.Throws<FormatException>(()=>Engine(value).ResolveString("{{vars.value | add: "+amount+"}}"));

    [Fact]
    public void Add_Overflow_FailsRatherThanWrapping()
        => Assert.Throws<OverflowException>(()=>Engine(long.MaxValue.ToString()).ResolveString("{{vars.value | add: 1}}"));
}
