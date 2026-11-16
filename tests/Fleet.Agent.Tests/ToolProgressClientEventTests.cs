using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fleet.Agent.Configuration;
using Fleet.Agent.Models;
using Fleet.Agent.Services;
using Fleet.Agent.Tests.Harness;
using Fleet.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Fleet.Agent.Tests;

/// <summary>
/// The <c>turn.progress</c> client event carries a tool NAME and nothing of the call's arguments:
/// no command line, path, query or prompt. Asserted on what each provider's real mapping produces,
/// taken through the real runtime to the event a client receives.
/// </summary>
public class ToolProgressClientEventTests
{
    /// <summary>Present in every argument below; it must be in no client event.</summary>
    private const string Marker = "synthetic-marker";

    [Fact]
    public async Task ToolProgress_CarriesOnlyAToolName_ForEveryProvider()
    {
        var calls = new List<(string Name, AgentProgress Progress)>
        {
            ("shell", Codex("""{"type":"commandExecution","id":"i1","command":"grep -rn --include=*.cs \"synthetic-marker\" /workspace/example/src /workspace/example/tests"}""")),
            ("edit", Codex("""{"type":"fileChange","id":"i2","status":"inProgress","changes":[{"path":"/workspace/example/synthetic-marker.cs","kind":{"type":"add"},"diff":"+synthetic-marker"}]}""")),
            ("web_search", Codex("""{"type":"webSearch","id":"i3","query":"synthetic-marker release notes"}""")),
            ("agent", Codex("""{"type":"collabAgentToolCall","id":"i4","tool":"spawnAgent","prompt":"Look for synthetic-marker everywhere."}""")),
            ("image_view", Codex("""{"type":"imageView","id":"i5","path":"/workspace/example/synthetic-marker.png"}""")),
            ("image_generation", Codex("""{"type":"imageGeneration","id":"i6","status":"in_progress"}""")),
            ("read_file", Codex("""{"type":"mcpToolCall","id":"i7","server":"files","tool":"read_file","arguments":{"path":"/workspace/example/synthetic-marker.txt"}}""")),
            ("Bash", Claude("Bash", """{"command":"grep -rn synthetic-marker /workspace/example"}""")),
            ("Read", Claude("Read", """{"file_path":"/workspace/example/synthetic-marker.txt"}""")),
            ("run_shell_command", Gemini("""{"type":"tool_call","name":"run_shell_command","args":{"command":"grep -rn synthetic-marker ."}}""")),
        };
        // The arguments are really there (an image generation has none), so their absence from
        // the client event means something.
        Assert.All(
            calls.Where(call => call.Name != "image_generation"),
            call => Assert.Contains(Marker, call.Progress.Summary + call.Progress.ToolArgs, StringComparison.Ordinal));

        var steps = calls.Select(call => call.Progress).Append(ScriptedExecutor.Final("Synthetic answer A.")).ToList();
        var harness = ConversationHarness.Build(new ScriptedExecutor(steps));
        var (key, identity) = harness.OpenClientConversation();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        await harness.Manager.StartTask(key, "task", "display", isSessionTask: true, identity: identity);
        var events = await harness.PumpUntilTerminalAsync(timeout.Token);

        var toolEvents = events
            .Where(e => e.Kind == ConversationEventKind.TurnProgress
                        && e.PayloadAs<TurnProgressPayload>()?.Activity == ProgressActivity.Tool)
            .ToList();
        Assert.Equal(calls.Select(call => call.Name), toolEvents.Select(e => e.PayloadAs<TurnProgressPayload>()!.ToolName));
        Assert.All(toolEvents, e => Assert.DoesNotContain(Marker, e.Payload!.ToJsonString(), StringComparison.Ordinal));
        Assert.All(toolEvents, e => Assert.DoesNotContain("/workspace", e.Payload!.ToJsonString(), StringComparison.Ordinal));
    }

    private static AgentOptions Options(string provider) =>
        new() { Name = "agent-a", Role = "test", WorkDir = "/workspace/example", Provider = provider };

    private static AgentProgress Codex(string itemJson)
    {
        var options = Microsoft.Extensions.Options.Options.Create(Options("codex"));
        var executor = new CodexExecutor(
            options, Microsoft.Extensions.Options.Options.Create(new TelegramOptions()),
            new PromptBuilder(options, NullLogger<PromptBuilder>.Instance), NullLogger<CodexExecutor>.Instance);
        return executor.BuildItemStartedProgressForTests(new JsonObject { ["item"] = JsonNode.Parse(itemJson) })!;
    }

    private static AgentProgress Claude(string tool, string inputJson)
    {
        var options = Microsoft.Extensions.Options.Options.Create(Options("claude"));
        var executor = new ClaudeExecutor(
            options, NullLogger<ClaudeExecutor>.Instance, new PromptBuilder(options, NullLogger<PromptBuilder>.Instance));
        var frame = $$"""{"type":"assistant","message":{"content":[{"type":"tool_use","id":"toolu_1","name":"{{tool}}","input":{{inputJson}}}]},"parent_tool_use_id":null}""";
        return executor.ParseProgressForTests(JsonSerializer.Deserialize<ClaudeStreamEvent>(frame)!);
    }

    private static AgentProgress Gemini(string eventJson)
    {
        var options = Microsoft.Extensions.Options.Options.Create(Options("gemini"));
        var executor = new GeminiExecutor(options, new PromptBuilder(options, NullLogger<PromptBuilder>.Instance), NullLogger<GeminiExecutor>.Instance);
        return executor.MapEvent(JsonDocument.Parse(eventJson).RootElement, new StringBuilder())!;
    }
}
