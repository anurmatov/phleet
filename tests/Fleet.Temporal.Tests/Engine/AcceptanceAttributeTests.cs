using Fleet.Shared;
using Fleet.Temporal.Services;
using Temporalio.Api.Enums.V1;

namespace Fleet.Temporal.Tests.Engine;

public class AcceptanceAttributeTests
{
    private static string Value(string result = "passed", string iso = "isolated", string block = "none", string fix = "0", string batch = "0")
        => $"v1;head={new string('a',40)};result={result};iso={iso};block={block};fix={fix};batch={batch}";

    [Fact]
    public void IsValid_WholeStringGrammar_RejectsMalformedValues()
    {
        Assert.True(AcceptanceAttribute.IsValid(Value()));
        Assert.True(AcceptanceAttribute.IsValid(Value("blocked","production","ci_timeout","3","2")));
        Assert.True(AcceptanceAttribute.IsValid(Value("not_required","production")));
        foreach (var bad in new[] { Value().Replace(new string('a',40),new string('A',40)),
            Value().Replace(new string('a',40),new string('a',39)),Value("blocked"),
            Value(block:"ci_timeout"),Value(batch:"6"),Value(fix:"01"),Value(fix:"10000"),
            Value().Replace(";fix=0;batch=0",";batch=0;fix=0"),Value()+";extra=x",
            Value().Replace("v1;","v2;"),Value()+"\n",Value()+"\r\n",Value()+"é","" })
            Assert.False(AcceptanceAttribute.IsValid(bad),bad);
        Assert.False(AcceptanceAttribute.IsValid(null));
    }

    [Fact]
    public void Registry_OnlyTwoKeywordsAdded_InitializerMatches()
    {
        Assert.Equal(new[] {"IssueNumber","PositionId","PrNumber"},
            SearchAttributeTypeRegistry.GetNames().Where(n => SearchAttributeTypeRegistry.GetType(n)==SearchAttributeTypeRegistry.AttributeType.Int).Order().ToArray());
        Assert.Equal(new[] {"Acceptance","DocPrs","GateRef","GateVisit","Phase","Repo","ReviewDate","ReviewRef","ReviewScrub"},
            SearchAttributeTypeRegistry.GetNames().Where(n => SearchAttributeTypeRegistry.GetType(n)==SearchAttributeTypeRegistry.AttributeType.Keyword).Order().ToArray());
        Assert.Equal(SearchAttributeTypeRegistry.GetNames().Order(),SearchAttributeInitializer.RequiredAttributes.Keys.Order());
        foreach (var name in SearchAttributeTypeRegistry.GetNames())
            Assert.Equal(SearchAttributeTypeRegistry.GetType(name)==SearchAttributeTypeRegistry.AttributeType.Int ? IndexedValueType.Int : IndexedValueType.Keyword,
                SearchAttributeInitializer.RequiredAttributes[name]);
    }
}
