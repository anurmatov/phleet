using Fleet.Agent;
using Fleet.Agent.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Fleet.Agent.Tests;

/// <summary>
/// <see cref="AgentHostRegistration.ValidateStartupConfiguration"/> — the gate <c>Program.cs</c>
/// runs against the built host before <c>app.Run()</c>.
///
/// <para>Asserted against the shipped method, not a re-typed copy, for the reason
/// <see cref="AgentHostRegistration"/> exists at all. What makes this gate load-bearing is what
/// sits downstream of it: <c>/health</c> answers <c>ok</c> unconditionally and
/// <c>WarmupService</c> catches executor startup failures as a warning, so a fault that does not
/// stop the host leaves the container up, reported healthy, and unable to answer a turn.</para>
/// </summary>
public class StartupConfigurationGateTests
{
    private static ServiceProvider BuildServices(string provider, string model)
    {
        // DisableDefaults-equivalent: nothing ambient, only the values the gate reads.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Agent:Name"] = "fleet-agent1",
                ["Agent:Role"] = "generic-role",
                ["Agent:WorkDir"] = "/workspace",
                ["Agent:Provider"] = provider,
                ["Agent:Model"] = model,
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.Configure<AgentOptions>(configuration.GetSection(AgentOptions.Section));
        return services.BuildServiceProvider();
    }

    [Theory]
    [InlineData("ollama/gpt-oss:20b")]
    [InlineData("lmstudio/qwen3-coder")]
    public void CodexLocalModelWithNoBaseUrl_ThrowsSoTheHostNeverRuns(string model)
    {
        using var services = BuildServices("codex", model);

        var ex = Assert.Throws<InvalidOperationException>(
            () => AgentHostRegistration.ValidateStartupConfiguration(services, _ => null));

        Assert.Contains("CODEX_OSS_BASE_URL", ex.Message);
        Assert.Contains(model, ex.Message);
    }

    [Fact]
    public void CodexLocalModelWithBlankBaseUrl_ThrowsSoTheHostNeverRuns()
    {
        using var services = BuildServices("codex", "ollama/gpt-oss:20b");

        var ex = Assert.Throws<InvalidOperationException>(
            () => AgentHostRegistration.ValidateStartupConfiguration(services, _ => "   "));

        Assert.Contains("CODEX_OSS_BASE_URL", ex.Message);
    }

    [Fact]
    public void CodexLocalModelWithBaseUrl_Passes()
    {
        using var services = BuildServices("codex", "ollama/gpt-oss:20b");

        AgentHostRegistration.ValidateStartupConfiguration(
            services, _ => "http://host.docker.internal:11434/v1");
    }

    [Theory]
    // A codex agent that never opted in, with or without the env var present.
    [InlineData("codex", "gpt-5")]
    [InlineData("codex", "owl/t-lite")]
    // Other providers never reach CodexExecutor, so a prefixed model there is not this gate's
    // business — it must not block an agent the fault cannot affect.
    [InlineData("claude", "ollama/gpt-oss:20b")]
    [InlineData("gemini", "ollama/gpt-oss:20b")]
    public void UnaffectedAgents_Pass(string provider, string model)
    {
        using var services = BuildServices(provider, model);

        AgentHostRegistration.ValidateStartupConfiguration(services, _ => null);
    }
}
