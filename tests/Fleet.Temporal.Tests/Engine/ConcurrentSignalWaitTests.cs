using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fleet.Temporal.Activities;
using Fleet.Temporal.Engine;
using Fleet.Temporal.Models;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Temporalio.Activities;
using Temporalio.Client;
using Temporalio.Testing;
using Temporalio.Worker;

namespace Fleet.Temporal.Tests.Engine;

/// <summary>
/// The engine keeps one waiter per signal name, so two parallel branches parked on the same name
/// cannot both be woken: the second registration replaces the first. These pin the two answers to
/// that — a definition that spells it out with literal names is refused at load, naming the steps,
/// and a collision nobody could see in the tree is logged when it happens.
/// </summary>
public sealed class ConcurrentSignalWaitTests
{
    private const string Type = "ExampleWorkflow";

    // ── Refused at load ──────────────────────────────────────────────────────

    [Fact]
    public void TwoStaticBranchesOnTheSameLiteralSignal_AreRefused_NamingTheParallelAndBothWaits()
    {
        var root = new ParallelStep
        {
            Name = "both_reviews",
            Steps =
            [
                new WaitForSignalStep { Name = "first_wait", SignalName = "review-done" },
                new WaitForSignalStep { Name = "second_wait", SignalName = "review-done" },
            ],
        };

        var ex = Assert.Throws<InvalidOperationException>(() => WorkflowDefinitionValidator.Validate(root, Type));

        Assert.Contains("both_reviews", ex.Message);
        Assert.Contains("first_wait", ex.Message);
        Assert.Contains("second_wait", ex.Message);
        Assert.Contains("'review-done'", ex.Message);
    }

    /// <summary>The wait may be anywhere under the branch: in a sequence, a loop, a branch case.</summary>
    [Fact]
    public void AWaitNestedDeepInsideABranch_StillCounts()
    {
        var root = new SequenceStep
        {
            Steps =
            [
                new ParallelStep
                {
                    Name = "fan",
                    Steps =
                    [
                        new SequenceStep
                        {
                            Steps =
                            [
                                new NoopStep(),
                                new LoopStep { Steps = [new WaitForSignalStep { Name = "in_loop", SignalName = "go" }] },
                            ],
                        },
                        new BranchStep
                        {
                            On = "{{vars.mode}}",
                            Cases = new() { ["a"] = new NoopStep() },
                            Default = new WaitForSignalStep { Name = "in_default", SignalName = "go" },
                        },
                    ],
                },
            ],
        };

        var ex = Assert.Throws<InvalidOperationException>(() => WorkflowDefinitionValidator.Validate(root, Type));

        Assert.Contains("in_loop", ex.Message);
        Assert.Contains("in_default", ex.Message);
    }

    /// <summary>Every item's branch runs the same step, so a literal name is shared by all of them.</summary>
    [Fact]
    public void AForEachThatWaitsOnALiteralSignal_IsRefused_AndPointsAtTheItemVar()
    {
        var root = new ParallelStep
        {
            Name = "per_agent",
            ForEach = "{{input.Agents}}",
            ItemVar = "agent",
            Step = new SequenceStep
            {
                Steps =
                [
                    new DelegateStep { Target = "{{vars.agent}}", Instruction = "work" },
                    new WaitForSignalStep { Name = "await_ack", SignalName = "ack" },
                ],
            },
        };

        var ex = Assert.Throws<InvalidOperationException>(() => WorkflowDefinitionValidator.Validate(root, Type));

        Assert.Contains("per_agent", ex.Message);
        Assert.Contains("await_ack", ex.Message);
        Assert.Contains("ack-{{vars.agent}}", ex.Message);
    }

    // ── Not refused ──────────────────────────────────────────────────────────

    public static TheoryData<string, StepDefinition> Accepted() => new()
    {
        {
            "different names in different branches",
            new ParallelStep
            {
                Steps =
                [
                    new WaitForSignalStep { SignalName = "design-done" },
                    new WaitForSignalStep { SignalName = "review-done" },
                ],
            }
        },
        {
            "the same name twice in ONE branch, one after the other",
            new ParallelStep
            {
                Steps =
                [
                    new SequenceStep
                    {
                        Steps =
                        [
                            new WaitForSignalStep { SignalName = "go" },
                            new WaitForSignalStep { SignalName = "go" },
                        ],
                    },
                    new NoopStep(),
                ],
            }
        },
        {
            "the same name outside any parallel",
            new SequenceStep
            {
                Steps =
                [
                    new WaitForSignalStep { SignalName = "go" },
                    new LoopStep { Steps = [new WaitForSignalStep { SignalName = "go" }] },
                ],
            }
        },
        {
            "a templated name in a forEach: unknowable from the tree",
            new ParallelStep
            {
                ForEach = "{{input.Agents}}",
                ItemVar = "agent",
                Step = new WaitForSignalStep { SignalName = "ack-{{vars.agent}}" },
            }
        },
        {
            "templated names in static branches",
            new ParallelStep
            {
                Steps =
                [
                    new WaitForSignalStep { SignalName = "{{vars.gate}}" },
                    new WaitForSignalStep { SignalName = "{{vars.gate}}" },
                ],
            }
        },
        {
            "escalating delegates in a fan-out: their wait exists only if a step fails",
            new ParallelStep
            {
                ForEach = "{{input.Agents}}",
                ItemVar = "agent",
                Step = new DelegateWithEscalationStep { Target = "{{vars.agent}}", Instruction = "work" },
            }
        },
        {
            "a parallel with no waits at all",
            new ParallelStep
            {
                Steps =
                [
                    new DelegateStep { Target = "agent-a", Instruction = "work" },
                    new DelegateStep { Target = "agent-b", Instruction = "work" },
                ],
            }
        },
    };

    [Theory]
    [MemberData(nameof(Accepted))]
    public void WhatTheTreeDoesNotProve_IsNotRefused(string why, StepDefinition root)
    {
        Assert.NotNull(why);
        WorkflowDefinitionValidator.Validate(root, Type);
    }

    // ── Wired into the load path ─────────────────────────────────────────────

    /// <summary>
    /// The rule runs where every other one does: in the activity that loads a definition at the
    /// start of a run, against the real JSON-to-step-tree path. A run already in flight has its
    /// definition in history and never loads it again.
    /// </summary>
    [Fact]
    public async Task ADefinitionWithCollidingWaits_FailsToLoad()
    {
        var definition = """
            {"type":"parallel","name":"both_reviews","steps":[
              {"type":"wait_for_signal","name":"first_wait","signalName":"review-done"},
              {"type":"wait_for_signal","name":"second_wait","signalName":"review-done"}
            ]}
            """;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => LoadActivity(definition).LoadAsync(Type));

        Assert.Contains("both_reviews", ex.Message);
    }

    /// <summary>
    /// No definition this repository seeds is refused by the rule: every one of them still loads
    /// through the production path.
    /// </summary>
    [Fact]
    public void EverySeededDefinition_StillLoads()
    {
        var seed = JsonNode.Parse(File.ReadAllText(SeedPath()))!;
        var names = seed["workflowDefinitions"]!.AsArray().Select(d => d!["name"]!.GetValue<string>()).ToList();

        Assert.NotEmpty(names);
        Assert.All(names, name => Assert.NotNull(SeedHarness.Parse(name)));
    }

    // ── Logged when it happens ───────────────────────────────────────────────

    /// <summary>
    /// Two branches whose templated names resolve to the same signal. Nothing refuses that at
    /// load, so the engine says so when the second wait takes the name from the first. Behaviour
    /// is unchanged: the signal wakes the newer wait and the older one ends by its own timeout.
    /// </summary>
    [Fact]
    public async Task ASecondWaitTakingAPendingWaitersName_IsLoggedOnce_WithTheSignalName()
    {
        await using var env = await WorkflowEnvironment.StartTimeSkippingAsync();
        var queue = "waiter-replaced-" + Guid.NewGuid().ToString("N");
        var logs = new CapturingLogs();
        using var loggerFactory = LoggerFactory.Create(b => b.AddProvider(logs));
        var activities = new NotifyActivities(CollidingAtRunTime());

        using var worker = new TemporalWorker(
            env.Client,
            new TemporalWorkerOptions(queue) { LoggerFactory = loggerFactory }
                .AddWorkflow<UniversalWorkflow>()
                .AddAllActivities(activities.GetType(), activities));

        await worker.ExecuteAsync(async () =>
        {
            var handle = await env.Client.StartWorkflowAsync(
                Type,
                new object?[] { JsonSerializer.SerializeToElement(new { Gate = "shared-gate" }) },
                new WorkflowOptions(id: queue, taskQueue: queue));

            // Both waits have registered and sent their notification: both are parked.
            await activities.WaitForNotificationsAsync(2);
            await handle.SignalAsync("shared-gate", [JsonSerializer.SerializeToElement(new { Decision = "go" })]);

            // The stranded wait ends by its timeout, which the time-skipping server runs out.
            Assert.Equal("done", await handle.GetResultAsync<string>().WaitAsync(TimeSpan.FromSeconds(60)));
        });

        var replaced = logs.Messages
            .Where(m => m.StartsWith("signal waiter replaced while still pending", StringComparison.Ordinal))
            .ToList();
        Assert.Contains("'shared-gate'", Assert.Single(replaced));
    }

    private static WorkflowDefinitionModel CollidingAtRunTime()
    {
        WaitForSignalStep Wait(string name) => new()
        {
            Name = name,
            SignalName = "{{input.Gate}}",
            TimeoutMinutes = 5,
            AutoCompleteOnTimeout = true,
            NotifyStep = new DelegateStep { Name = name + "_notify", Target = "agent-a", Instruction = "waiting" },
        };

        return new WorkflowDefinitionModel
        {
            Name = Type,
            Namespace = "default",
            TaskQueue = "test",
            Root = new SequenceStep
            {
                Steps =
                [
                    new ParallelStep { Steps = [Wait("first_wait"), Wait("second_wait")] },
                    new SetVariableStep { Vars = new() { ["_result"] = "done" } },
                ],
            },
        };
    }

    private static LoadWorkflowDefinitionActivity LoadActivity(string definitionJson)
    {
        var body = JsonSerializer.Serialize(new
        {
            name = Type,
            @namespace = "default",
            taskQueue = "test",
            definition = definitionJson,
            version = 1,
        });

        var client = new HttpClient(new StubHandler(body)) { BaseAddress = new Uri("http://orchestrator.invalid/") };
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("orchestrator").Returns(client);
        return new LoadWorkflowDefinitionActivity(factory);
    }

    private static string SeedPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "seed.example.json")))
            dir = dir.Parent;
        return Path.Combine(dir!.FullName, "seed.example.json");
    }

    private sealed class StubHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
    }

    private sealed class NotifyActivities(WorkflowDefinitionModel definition)
    {
        private readonly SemaphoreSlim _notified = new(0);

        public async Task WaitForNotificationsAsync(int count)
        {
            for (var i = 0; i < count; i++)
                Assert.True(await _notified.WaitAsync(TimeSpan.FromSeconds(30)), "a wait never sent its notification");
        }

        [Activity("LoadWorkflowDefinition")]
        public WorkflowDefinitionModel LoadDefinition(string _) => definition;

        [Activity("LoadWorkflowConfig")]
        public JsonElement LoadConfig() => JsonSerializer.SerializeToElement(new { });

        [Activity("DelegateToAgent")]
        public AgentTaskResult Delegate(
            string target,
            string instruction,
            string taskId,
            bool retryOnIncomplete,
            int maxIncompleteRetries,
            int agentBudgetSeconds = 0,
            string? repo = null)
        {
            _notified.Release();
            return new AgentTaskResult("notified", "completed");
        }
    }

    private sealed class CapturingLogs : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _messages = new();

        public IReadOnlyCollection<string> Messages => _messages;

        public ILogger CreateLogger(string categoryName) => new Logger(this);

        public void Dispose() { }

        private sealed class Logger(CapturingLogs owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                owner._messages.Enqueue(formatter(state, exception));
        }
    }
}
