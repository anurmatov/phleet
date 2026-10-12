using Fleet.Temporal.Engine;

namespace Fleet.Temporal.Tests.Engine;

public class TemplateHeadEqualityTests
{
    [Fact]
    public void Extract_HeadPair_ReturnsOnlyIdenticalLowercaseHeads()
    {
        var h=new string('a',40);
        var vars=new Dictionary<string,object?>();
        var engine=new TemplateEngine(new() {["vars"]=vars});
        foreach (var (pair,want) in new[] {(h+"="+h,h),(h+"="+new string('b',40),""),("none="+h,""),(h.ToUpperInvariant()+"="+h.ToUpperInvariant(),"")})
        {
            vars["pair"]=pair;
            Assert.Equal(want,engine.ResolveString("{{vars.pair | extract: '^([0-9a-f]{40})=\\1$'}}"));
        }
    }
}
