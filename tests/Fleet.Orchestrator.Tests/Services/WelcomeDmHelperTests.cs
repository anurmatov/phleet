using Fleet.Orchestrator.Services;

namespace Fleet.Orchestrator.Tests.Services;

public class WelcomeDmHelperTests
{
    [Fact]
    public void BuildWelcomeDirective_ContainsAuthTokenRefreshWorkflow()
    {
        var directive = WelcomeDmHelper.BuildWelcomeDirective("cto", 12345);
        Assert.Contains("AuthTokenRefreshWorkflow", directive);
    }

    [Fact]
    public void BuildWelcomeDirective_ContainsForceRefreshTrue()
    {
        var directive = WelcomeDmHelper.BuildWelcomeDirective("cto", 12345);
        Assert.Contains("ForceRefresh=true", directive);
    }
}
