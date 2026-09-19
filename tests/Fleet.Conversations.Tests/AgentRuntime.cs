using Microsoft.Extensions.Configuration;
using Fleet.Agent;
using Fleet.Agent.Abstractions;
using Fleet.Agent.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Fleet.Conversations.Tests;

/// <summary>
/// A real agent runtime, built from <see cref="AgentHostRegistration"/> — the same two calls
/// <c>Program.cs</c> makes.
/// </summary>
/// <remarks>
/// <para>
/// A hand-wired service collection would prove the copy is self-consistent and nothing about the
/// process that ships: every claim the round-trip suite makes about dispatch, injection and the
/// final-answer gate is a claim about the graph <c>Program.cs</c> builds. So this builds THAT graph
/// and substitutes exactly two things — the executor, because a real provider would make the suite
/// non-deterministic without testing anything more, and the channel adapter, because the suite
/// supplies its own consumer and hand-off queue.
/// </para>
/// <para>
/// ⚠️ <c>Conversations:SouthBaseUrl</c> is deliberately LEFT UNSET here. The seam's own registration
/// would construct a second consumer with its own broker connection, and the suite needs the one it
/// built against the in-process south listener. That the seam stays absent when the key is absent is
/// itself the feature gate, asserted separately in
/// <c>ConversationSouthRegistrationTests</c>.
/// </para>
/// </remarks>
internal sealed class AgentRuntime : IAsyncDisposable
{
    private readonly IHost _host;

    private AgentRuntime(IHost host, string workDir)
    {
        _host = host;
        WorkDir = workDir;
    }

    public string WorkDir { get; }

    /// <summary>The runtime's inbound seam — what the south consumer dispatches through.</summary>
    public ConversationIntake Intake => _host.Services.GetRequiredService<ConversationIntake>();

    public static AgentRuntime Start(IAgentExecutor executor, IChannelAdapter adapter)
    {
        var workDir = Path.Combine(Path.GetTempPath(), $"fleet-roundtrip-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);

        // DisableDefaults keeps the graph deterministic: no appsettings.json from a referenced
        // project's output directory, and no ambient environment variables.
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            DisableDefaults = true,
        });

        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Agent:Name"] = "example-agent",
            ["Agent:Role"] = "generic-role",
            ["Agent:WorkDir"] = workDir,
            ["Agent:Provider"] = "claude",

            // No Telegram token: the transport is not registered, and the sink falls back to the
            // counted no-op. No RabbitMq:Host either, so the relay never dials out.
            ["Telegram:BotToken"] = "",
        });

        builder.Services.AddLogging();

        builder.Services.AddAgentCoreServices(builder.Configuration);
        builder.Services.AddAgentDaemonServices(builder.Configuration);

        builder.Services.AddSingleton(executor);

        // WarmupService calls the executor a few seconds after start, which would appear as a
        // phantom turn in every assertion about what ran.
        foreach (var descriptor in builder.Services
                     .Where(d => d.ImplementationType == typeof(WarmupService)).ToList())
        {
            builder.Services.Remove(descriptor);
        }

        builder.Services.AddSingleton(adapter);

        var host = builder.Build();
        host.Start();

        return new AgentRuntime(host, workDir);
    }

    public async ValueTask DisposeAsync()
    {
        try { await _host.StopAsync(TimeSpan.FromSeconds(10)); }
        catch (Exception) { /* a host that never started has nothing to stop */ }

        _host.Dispose();

        try { Directory.Delete(WorkDir, recursive: true); }
        catch (Exception) { /* best effort */ }
    }
}
