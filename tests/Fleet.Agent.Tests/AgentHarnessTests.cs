using System.Reflection;
using System.Text;
using System.Text.Json;
using Fleet.Agent.Configuration;
using Fleet.Agent.Services;
using Fleet.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Fleet.Agent.Tests;

/// <summary>
/// #460 agent side: <see cref="AgentHarness"/> builds the plan from the agent's own config with the
/// orchestrator's rules (mode, window, the bound <c>AllowedTools</c>), logs the startup line once,
/// re-validates <c>harnessExtra</c> (AC-X4: an ignored value warns once and starts on defaults),
/// falls back once without extras (AC-X4, Claude), and its sha rides on every heartbeat.
/// </summary>
public sealed class AgentHarnessTests
{
    private const string Url = "http://inference-host:11434";

    private static AgentOptions Options(string provider, Action<AgentOptions>? edit = null)
    {
        var options = new AgentOptions { Name = "agent1", Role = "developer", WorkDir = "/tmp", Provider = provider };
        edit?.Invoke(options);
        return options;
    }

    private static (AgentHarness Harness, CapturingLogger<AgentHarness> Log) Create(
        AgentOptions options, string? claudeWindowEnv = null)
    {
        var log = new CapturingLogger<AgentHarness>();
        return (new AgentHarness(options, name => name == ContextWindow.EnvVar ? claudeWindowEnv : null, log), log);
    }

    // ── the input mirrors the orchestrator's rules ─────────────────────────────────────────────────

    [Fact]
    public void ClaudeLocal_TakesItsWindowFromTheContainerEnv()
    {
        var input = AgentHarness.InputFor(
            Options("claude", o => { o.AnthropicBaseUrl = Url; o.Model = "qwen3.8:27b"; o.ContextWindow = 999_999; }),
            name => name == ContextWindow.EnvVar ? "131072" : null)!;

        Assert.True(input.Local);
        Assert.Equal(131_072, input.ContextWindow);   // the env value, not Agent.ContextWindow
        Assert.Equal(Url, input.LocalBaseUrl);
    }

    [Fact]
    public void ClaudeCloud_IgnoresAnyWindow()
    {
        var input = AgentHarness.InputFor(Options("claude"), _ => "131072")!;

        Assert.False(input.Local);
        Assert.Null(input.ContextWindow);
        Assert.Null(input.LocalBaseUrl);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(Url + "/v1", true)]
    public void Codex_IsLocalOnlyWithTheProvisionedServerUrl(string? ossBaseUrl, bool local)
    {
        var input = AgentHarness.InputFor(
            Options("codex", o => { o.Model = "ollama/qwen3.8:27b"; o.CodexOssBaseUrl = ossBaseUrl; o.ContextWindow = 131_072; }),
            _ => null)!;

        Assert.Equal(local, input.Local);
        Assert.Equal(local ? 131_072 : null, input.ContextWindow);
    }

    [Fact]
    public void Gemini_HasNoPlan()
    {
        var (harness, log) = Create(Options("gemini", o => o.HarnessExtra = "web_search = \"live\""));

        Assert.Null(harness.Plan);
        Assert.Empty(log.Entries);
    }

    [Fact]
    public void TheBoundAllowedTools_StartWithTheSixDefaults_WhichNeverNarrow_ButStayInAnExplicitList()
    {
        // The real binder over a generated appsettings.json: it appends to the default list.
        var json = """{"Agent":{"Name":"agent1","Role":"developer","WorkDir":"/tmp","Provider":"claude","AllowedTools":["mcp__fleet-memory__memory_get","Read","WebFetch"]}}""";
        var configuration = new ConfigurationBuilder().AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json))).Build();
        var options = new AgentOptions { Name = "", Role = "", WorkDir = "" };
        configuration.GetSection(AgentOptions.Section).Bind(options);

        Assert.Equal([.. HarnessDefaults.AgentDefaultAllowedTools, "mcp__fleet-memory__memory_get", "Read", "WebFetch"], options.AllowedTools);
        Assert.DoesNotContain("--tools", Create(options).Harness.Plan!.ClaudeArgs!);

        // The agent's real bound list with nothing else: still no --tools.
        var sixOnly = new AgentOptions { Name = "", Role = "", WorkDir = "" };
        new ConfigurationBuilder().AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(
            """{"Agent":{"Name":"agent1","Role":"developer","WorkDir":"/tmp","Provider":"claude"}}"""))).Build()
            .GetSection(AgentOptions.Section).Bind(sixOnly);
        Assert.Equal(HarnessDefaults.AgentDefaultAllowedTools, sixOnly.AllowedTools);
        Assert.DoesNotContain("--tools", Create(sixOnly).Harness.Plan!.ClaudeArgs!);

        // An explicit narrowing keeps them.
        options.HarnessExtra = "--tools NotebookEdit";
        var tools = Create(options).Harness.Plan!.ClaudeArgs![1].Split(',');
        Assert.Superset(HarnessDefaults.AgentDefaultAllowedTools.ToHashSet(), tools.ToHashSet());
        Assert.Equal(HarnessDefaults.AgentDefaultAllowedTools, new AgentOptions { Name = "", Role = "", WorkDir = "" }.AllowedTools);
    }

    // ── startup line, ignored extra, notes ─────────────────────────────────────────────────────────

    [Fact]
    public void Startup_LogsTheLineOnce()
    {
        var (harness, log) = Create(Options("codex"));

        var entry = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Equal(harness.Plan!.StartupLine, entry.Message);
        Assert.Matches("^harness: mode=cloud plan=[0-9a-f]{64} extra=none$", entry.Message);
    }

    [Fact]
    public void AnExtraTheImageRefuses_WarnsOnce_AndStartsOnTheDefaults()
    {
        var (harness, log) = Create(Options("codex", o =>
        {
            o.Model = "ollama/qwen3.8:27b"; o.CodexOssBaseUrl = Url + "/v1"; o.HarnessExtra = "web_search = \"live\"";
        }));
        var (defaults, _) = Create(Options("codex", o => { o.Model = "ollama/qwen3.8:27b"; o.CodexOssBaseUrl = Url + "/v1"; }));

        var warning = Assert.Single(log.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("harnessExtra ignored", warning.Message);
        Assert.Contains("line 1", warning.Message);
        Assert.Equal(HarnessDefaults.ExtraIgnored, harness.Plan!.ExtraState);
        Assert.Equal(defaults.Plan!.CodexThreadConfig, harness.Plan.CodexThreadConfig);
        Assert.EndsWith("extra=ignored", log.Entries.Last().Message);
    }

    // ── AC-X4: the one Claude fallback ─────────────────────────────────────────────────────────────

    [Fact]
    public void FallBack_DropsTheExtraOnce_AndLogsOneErrorNamingTheField()
    {
        var (harness, log) = Create(Options("claude", o => o.HarnessExtra = "--tools NotebookEdit"));
        var before = harness.Plan!;
        Assert.Contains("NotebookEdit", before.ClaudeArgs![1]);

        Assert.True(harness.FallBackWithoutExtras());
        Assert.False(harness.FallBackWithoutExtras());

        Assert.Equal(HarnessDefaults.ExtraNone, harness.Plan!.ExtraState);
        Assert.DoesNotContain("NotebookEdit", harness.Plan.ClaudeArgs![1]);
        Assert.NotEqual(before.PlanSha256, harness.Plan.PlanSha256);
        var error = Assert.Single(log.Entries, e => e.Level == LogLevel.Error);
        Assert.Contains("harnessExtra", error.Message);
        Assert.Contains(harness.Plan.PlanSha256, error.Message);
    }

    [Fact]
    public void FallBack_WithoutAnAppliedExtra_ChangesNothing()
    {
        var (harness, log) = Create(Options("claude"));
        var plan = harness.Plan;

        Assert.False(harness.FallBackWithoutExtras());
        Assert.Same(plan, harness.Plan);
        Assert.DoesNotContain(log.Entries, e => e.Level == LogLevel.Error);
    }

    // ── AC-X6: the started sha on every heartbeat ──────────────────────────────────────────────────

    [Theory]
    [InlineData("claude", true)]
    [InlineData("codex", true)]
    [InlineData("gemini", false)]
    public async Task Heartbeat_CarriesThePlanSha_OnlyWhenThereIsAPlan(string provider, bool expected)
    {
        var options = Options(provider, o => o.ShortName = "agent1");
        var harness = Create(options).Harness;
        await using var executor = new MidTurnIsolationTests.ControlledExecutor(MidTurnInjectionStatus.Injected);
        var manager = MidTurnIsolationTests.Manager(provider, executor);
        var channel = Substitute.For<RabbitMQ.Client.IChannel>();
        byte[]? body = null;
        channel.BasicPublishAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(),
                Arg.Any<RabbitMQ.Client.BasicProperties>(), Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<CancellationToken>())
            .Returns(call => { body = call.Arg<ReadOnlyMemory<byte>>().ToArray(); return ValueTask.CompletedTask; });
        var heartbeat = new OrchestratorHeartbeatService(
            Microsoft.Extensions.Options.Options.Create(options),
            Microsoft.Extensions.Options.Options.Create(new RabbitMqOptions()), manager,
            Substitute.For<IFleetConnectionState>(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<OrchestratorHeartbeatService>.Instance,
            harness: harness);
        typeof(OrchestratorHeartbeatService).GetField("_channel", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(heartbeat, channel);
        try
        {
            foreach (var routingKey in new[] { "registration", "heartbeat" })
            {
                await (Task)typeof(OrchestratorHeartbeatService).GetMethod("PublishAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .Invoke(heartbeat, [routingKey, CancellationToken.None])!;
                using var json = JsonDocument.Parse(body!);
                Assert.Equal(expected, json.RootElement.TryGetProperty("PlanSha256", out var sha));
                if (expected) Assert.Equal(harness.Plan!.PlanSha256, sha.GetString());
            }
        }
        finally { await manager.CancelAllAsync(); await heartbeat.DisposeAsync(); }
    }
}
