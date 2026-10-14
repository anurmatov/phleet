using System.Globalization;
using Fleet.Agent.Configuration;
using Fleet.Shared;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fleet.Agent.Services;

/// <summary>
/// The harness plan this agent starts with (#460): <see cref="HarnessDefaults.Build"/> over the
/// agent's own generated config, computed once. <see cref="ClaudeExecutor"/> and
/// <see cref="CodexExecutor"/> apply it, and <see cref="OrchestratorHeartbeatService"/> reports its
/// sha so the effective-settings view can flag a pending reprovision.
/// </summary>
/// <remarks>
/// <para>
/// Null <see cref="Plan"/> for gemini: the defaults apply to claude and codex only, and its
/// heartbeats carry no sha.
/// </para>
/// <para>
/// The input mirrors the orchestrator's <c>HarnessInputFor</c>: the same mode rule, the context window
/// only in local mode, the <c>AllowedTools</c> the agent actually holds. A Claude local agent's window
/// arrives as <see cref="ContextWindow.EnvVar"/> in the container env, not in <c>appsettings.json</c>.
/// </para>
/// </remarks>
public sealed class AgentHarness
{
    private readonly HarnessInput? _input;
    private readonly ILogger _logger;
    private int _fellBack;

    public AgentHarness(IOptions<AgentOptions> options, ILogger<AgentHarness> logger)
        : this(options.Value, Environment.GetEnvironmentVariable, logger)
    {
    }

    internal AgentHarness(AgentOptions options, Func<string, string?> readEnv, ILogger logger)
    {
        _logger = logger;
        _input = InputFor(options, readEnv);
        if (_input is null)
            return;

        Plan = HarnessDefaults.Build(_input);
        // The same validator the orchestrator ran at save: a value this image no longer accepts is
        // skipped as a whole and the mode defaults apply.
        if (Plan.ExtraState == HarnessDefaults.ExtraIgnored)
            logger.LogWarning("harnessExtra ignored, mode defaults apply: {Reason}", Plan.ExtraReason);
        foreach (var note in Plan.Notes)
            logger.LogWarning("{Note}", note);
        logger.LogInformation("{StartupLine}", Plan.StartupLine);
    }

    /// <summary>The plan the executor starts with, or null for a provider without harness defaults.</summary>
    public HarnessPlan? Plan { get; private set; }

    /// <summary>
    /// Claude only (#460 Part 3b, failure at start): the CLI exited before <c>system/init</c> while
    /// extras were applied. Switches once to the mode defaults without the extra, logs one error
    /// naming <c>harnessExtra</c>, and returns true. False when no extra is applied or it already
    /// happened. The started sha changes with it, so the view shows the agent as pending.
    /// </summary>
    public bool FallBackWithoutExtras()
    {
        if (_input is null || Plan?.ExtraState != HarnessDefaults.ExtraApplied
            || Interlocked.Exchange(ref _fellBack, 1) == 1)
            return false;

        Plan = HarnessDefaults.Build(_input with { HarnessExtra = null });
        _logger.LogError(
            "harnessExtra: claude exited before system/init with the extra applied; restarting once "
            + "without it on the mode defaults (plan={PlanSha256}). Fix or clear harnessExtra and reprovision.",
            Plan.PlanSha256);
        return true;
    }

    /// <summary>
    /// The <see cref="HarnessDefaults.Build"/> input for this agent's config, or null for gemini.
    /// </summary>
    public static HarnessInput? InputFor(AgentOptions options, Func<string, string?> readEnv)
    {
        if (!HarnessExtra.IsSupportedProvider(options.Provider))
            return null;

        var claude = AgentProviders.Normalize(options.Provider) == AgentProviders.Claude;
        // Claude: ClaudeLocalModel.IsEnabled, the executor's own switch. Codex: the orchestrator writes
        // Agent:CodexOssBaseUrl only in local mode; a legacy Env Ref agent is cloud on both sides.
        var local = claude
            ? ClaudeLocalModel.IsEnabled(options.Provider, options.AnthropicBaseUrl)
            : !string.IsNullOrEmpty(options.CodexOssBaseUrl);
        int? window = !local ? null
            : claude ? ParseWindow(readEnv(ContextWindow.EnvVar))
            : options.ContextWindow;

        return new HarnessInput
        {
            Provider = options.Provider,
            Local = local,
            ContextWindow = window,
            AllowedTools = options.AllowedTools,
            HarnessExtra = options.HarnessExtra,
            LocalBaseUrl = local && claude ? options.AnthropicBaseUrl : null,
            Model = options.Model,
            Effort = options.Effort,
        };
    }

    private static int? ParseWindow(string? value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var tokens) ? tokens : null;
}
