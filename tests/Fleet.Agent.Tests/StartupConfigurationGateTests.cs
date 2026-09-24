using Fleet.Agent;
using Fleet.Agent.Configuration;
using Fleet.Agent.Services.HostedProviders;
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
    private static ServiceProvider BuildServices(
        string provider, string model, bool? hostedProvider = null, string? hostedKeyEnv = null,
        string? keyFilePath = null, string? anthropicBaseUrl = null, string? effort = null)
    {
        // DisableDefaults-equivalent: nothing ambient, only the values the gate reads.
        var values = new Dictionary<string, string?>
        {
            ["Agent:Name"] = "fleet-agent1",
            ["Agent:Role"] = "generic-role",
            ["Agent:WorkDir"] = "/workspace",
            ["Agent:Provider"] = provider,
            ["Agent:Model"] = model,
        };
        if (hostedProvider is not null)
            values["Agent:HostedProvider"] = hostedProvider.Value ? "true" : "false";
        if (hostedKeyEnv is not null)
            values["Agent:HostedProviderKeyEnv"] = hostedKeyEnv;
        if (anthropicBaseUrl is not null)
            values["Agent:AnthropicBaseUrl"] = anthropicBaseUrl;
        if (effort is not null)
            values["Agent:Effort"] = effort;

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.Configure<AgentOptions>(configuration.GetSection(AgentOptions.Section));
        if (keyFilePath is not null)
            services.AddSingleton(new HostedProviderKeyStore(keyFilePath));
        return services.BuildServiceProvider();
    }

    // ── #335: hosted-provider key file and D8 parity ────────────────────────

    private static string TempKeyPath() =>
        Path.Combine(Path.GetTempPath(), $"hosted-gate-{Guid.NewGuid():N}.key");

    [Fact]
    public void HostedProvider_ValidKeyFile_PassesAndDeletesTheFile()
    {
        var path = TempKeyPath();
        File.WriteAllText(path, "gate-test-key");
        using var services = BuildServices("codex", "zai/glm-5.3", true, "ZAI_CODING_PLAN_API_KEY", path);

        AgentHostRegistration.ValidateStartupConfiguration(services, _ => null);

        Assert.False(File.Exists(path));
        Assert.Equal("gate-test-key", services.GetRequiredService<HostedProviderKeyStore>().Key);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("<secret>")]
    [InlineData(null)]
    public void HostedProvider_UnusableOrMissingKeyFile_ThrowsNamingTheVariable(string? content)
    {
        var path = TempKeyPath();
        if (content is not null)
            File.WriteAllText(path, content);
        using var services = BuildServices("codex", "zai/glm-5.3", true, "ZAI_CODING_PLAN_API_KEY", path);

        var ex = Assert.Throws<InvalidOperationException>(
            () => AgentHostRegistration.ValidateStartupConfiguration(services, _ => null));

        Assert.Contains("ZAI_CODING_PLAN_API_KEY", ex.Message);
        Assert.False(File.Exists(path));
    }

    [Theory]
    // Orchestrator predates #335 (no flags) but the model is hosted.
    [InlineData("codex", "zai/glm-5.3", null, null)]
    // Orchestrator says hosted, this image does not.
    [InlineData("codex", "gpt-5", true, "ZAI_CODING_PLAN_API_KEY")]
    // An unknown prefix is not hosted, whatever the orchestrator says.
    [InlineData("codex", "acme/x", true, "ZAI_CODING_PLAN_API_KEY")]
    // Right flag, wrong key variable.
    [InlineData("codex", "zai/glm-5.3", true, "ACME_API_KEY")]
    // Hosted routing is codex-only, so a claude agent must not be flagged.
    [InlineData("claude", "zai/glm-5.3", true, "ZAI_CODING_PLAN_API_KEY")]
    public void HostedProvider_ParityMismatch_Throws(string provider, string model, bool? hosted, string? keyEnv)
    {
        using var services = BuildServices(provider, model, hosted, keyEnv, TempKeyPath());

        var ex = Assert.Throws<InvalidOperationException>(
            () => AgentHostRegistration.ValidateStartupConfiguration(services, _ => null));

        Assert.Contains("parity", ex.Message);
    }

    [Theory]
    [InlineData("claude", "zai/glm-5.3")]
    [InlineData("gemini", "zai/glm-5.3")]
    [InlineData("codex", "gpt-5")]
    [InlineData("codex", "acme/x")]
    public void NonHostedAgent_WithNoFlags_PassesParity(string provider, string model)
    {
        using var services = BuildServices(provider, model, hostedProvider: false);

        AgentHostRegistration.ValidateStartupConfiguration(services, _ => null);
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

    // ── #340: Claude local model mode ────────────────────────────────────────

    private const string LocalUrl = "http://inference-host:11434";
    private const string LocalTag = "qwen3.8:27b-agent";
    private static readonly Func<string, bool> NoFiles = _ => false;

    [Fact]
    public void ClaudeLocalModel_ValidAndNoCredentialFile_Passes()
    {
        using var services = BuildServices("claude", LocalTag, anthropicBaseUrl: LocalUrl);
        var probed = new List<string>();

        AgentHostRegistration.ValidateStartupConfiguration(
            services, _ => null, path => { probed.Add(path); return false; });

        Assert.Equal(["/root/.claude/.credentials.json", "/root/.claude-host/.credentials.json"], probed);
    }

    [Theory]
    // V1
    [InlineData("codex", LocalTag, LocalUrl, null, "applies only to provider claude")]
    [InlineData("gemini", LocalTag, LocalUrl, null, "applies only to provider claude")]
    // V2 (whitespace-only is a fault, never off)
    [InlineData("claude", LocalTag, "   ", null, "has surrounding whitespace")]
    [InlineData("claude", LocalTag, "http://inference-host:11434/v1", null, "has a path")]
    // V3
    [InlineData("claude", LocalTag, "http://localhost:11434", null, "is the container")]
    // V4
    [InlineData("claude", "qwen 27b", LocalUrl, null, "not allowed in a CLI argument")]
    // V5
    [InlineData("claude", "claude-opus-5-5", LocalUrl, null, "is a Claude model id")]
    // V6
    [InlineData("claude", "ollama/qwen3:8b", LocalUrl, null, "selects the codex path")]
    // V7
    [InlineData("claude", LocalTag, LocalUrl, "high", "Effort is not supported")]
    public void ClaudeLocalModel_EachFault_Throws(
        string provider, string model, string baseUrl, string? effort, string expected)
    {
        using var services = BuildServices(provider, model, anthropicBaseUrl: baseUrl, effort: effort);

        var ex = Assert.Throws<InvalidOperationException>(
            () => AgentHostRegistration.ValidateStartupConfiguration(services, _ => null, NoFiles));

        Assert.Contains(expected, ex.Message);
    }

    [Theory]
    [InlineData("http://inference-host:11434/")]
    [InlineData("HTTP://Inference-Host:11434")]
    public void ClaudeLocalModel_NonCanonicalBaseUrl_Throws(string baseUrl)
    {
        using var services = BuildServices("claude", LocalTag, anthropicBaseUrl: baseUrl);

        var ex = Assert.Throws<InvalidOperationException>(
            () => AgentHostRegistration.ValidateStartupConfiguration(services, _ => null, NoFiles));

        Assert.Contains("canonical", ex.Message);
    }

    [Theory]
    [InlineData("/root/.claude/.credentials.json")]
    [InlineData("/root/.claude-host/.credentials.json")]
    public void ClaudeLocalModel_CredentialFilePresent_Throws(string present)
    {
        using var services = BuildServices("claude", LocalTag, anthropicBaseUrl: LocalUrl);

        var ex = Assert.Throws<InvalidOperationException>(
            () => AgentHostRegistration.ValidateStartupConfiguration(services, _ => null, path => path == present));

        Assert.Contains(present, ex.Message);
    }

    [Theory]
    [InlineData("claude", "claude-opus-5-5", "high")]
    [InlineData("claude", "opus", null)]
    [InlineData("codex", "gpt-5", "low")]
    public void ClaudeLocalModel_Off_NeitherValidatesNorProbesFiles(string provider, string model, string? effort)
    {
        using var services = BuildServices(provider, model, effort: effort);

        // A present credential file is the normal case for an off claude agent.
        AgentHostRegistration.ValidateStartupConfiguration(
            services, _ => null, _ => throw new InvalidOperationException("file probe must not run when off"));
    }
}
