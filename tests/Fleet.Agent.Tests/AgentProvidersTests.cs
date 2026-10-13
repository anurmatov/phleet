using Fleet.Agent;
using Fleet.Agent.Configuration;
using Fleet.Agent.Services;
using Fleet.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Fleet.Agent.Tests;

/// <summary>
/// PV1 (#386 §4.1) in the agent: every provider has an explicit executor, an empty provider is
/// still claude, and any other string is refused — by the startup gate (exit 1, see
/// <see cref="StartupConfigurationGateTests"/>) and by the executor switch itself, which never falls
/// through to <see cref="ClaudeExecutor"/> and the Claude credentials it would use.
/// </summary>
public sealed class AgentProvidersTests
{
    private static ServiceProvider BuildCore(string? provider)
    {
        var values = new Dictionary<string, string?>
        {
            ["Agent:Name"] = "fleet-agent1",
            ["Agent:Role"] = "generic-role",
            ["Agent:WorkDir"] = Path.GetTempPath(),
            ["Agent:Model"] = "a-model",
        };
        if (provider is not null)
            values["Agent:Provider"] = provider;

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAgentCoreServices(configuration);
        return services.BuildServiceProvider();
    }

    [Theory]
    [InlineData("claude", typeof(ClaudeExecutor))]
    [InlineData("codex", typeof(CodexExecutor))]
    [InlineData("gemini", typeof(GeminiExecutor))]
    [InlineData("pi", typeof(PiExecutor))]
    public async Task EachKnownProvider_ResolvesItsOwnExecutor(string provider, Type executor)
    {
        await using var services = BuildCore(provider);

        Assert.IsType(executor, services.GetRequiredService<IAgentExecutor>());
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public async Task AnEmptyProvider_IsStillClaude(string? provider)
    {
        await using var services = BuildCore(provider);

        Assert.IsType<ClaudeExecutor>(services.GetRequiredService<IAgentExecutor>());
    }

    [Theory]
    [InlineData("foo")]
    [InlineData("Claude")]
    [InlineData("Pi")]
    [InlineData("claude ")]
    public void AnUnknownProvider_IsRefused_NeverClaude(string provider)
    {
        using var services = BuildCore(provider);

        var ex = Assert.Throws<InvalidOperationException>(() => services.GetRequiredService<IAgentExecutor>());

        Assert.Equal($"Unknown provider '{provider}'; use claude, codex, gemini or pi.", ex.Message);
    }

    [Fact]
    public void TheStartupGate_RefusesAnUnknownProviderBeforeAnythingElse()
    {
        using var services = BuildCore("foo");

        var ex = Assert.Throws<InvalidOperationException>(() => AgentHostRegistration.ValidateStartupConfiguration(
            services,
            _ => throw new InvalidOperationException("no environment read before PV1"),
            _ => throw new InvalidOperationException("no file probe before PV1"),
            _ => throw new InvalidOperationException("no directory probe before PV1")));

        Assert.Equal("Unknown provider 'foo'; use claude, codex, gemini or pi.", ex.Message);
    }

    [Fact]
    public void TheGateAndTheSwitchUseTheSameText()
    {
        Assert.Equal(AgentProviders.UnknownProviderText("foo"), AgentProviders.DescribeFault("foo"));
        Assert.Null(AgentProviders.DescribeFault(""));
        Assert.Null(AgentProviders.DescribeFault(null));
        Assert.All(AgentProviders.All, p => Assert.Null(AgentProviders.DescribeFault(p)));
    }

    [Fact]
    public async Task ThePiExecutorIsOneSingleton()
    {
        await using var services = BuildCore("pi");

        Assert.Same(services.GetRequiredService<PiExecutor>(), services.GetRequiredService<IAgentExecutor>());
    }
}
