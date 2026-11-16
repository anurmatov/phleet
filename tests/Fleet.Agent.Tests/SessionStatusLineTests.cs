using Fleet.Agent.Abstractions;
using Fleet.Agent.Configuration;
using Fleet.Agent.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Fleet.Agent.Tests;

/// <summary>
/// <c>/status</c> says which session the agent is on. One provider session serves every chat, so
/// for a provider that keeps it the line reports that session and how it began: <c>resumed</c> is
/// the one from before the container was replaced, <c>new</c> is one started since.
/// </summary>
public sealed class SessionStatusLineTests
{
    private const long Chat = 77;

    [Theory]
    [InlineData("resumed", "Session: active (resumed)")]
    [InlineData("new", "Session: active (new)")]
    [InlineData(null, "Session: none")]
    public async Task Status_SaysHowTheProviderSessionBegan(string? origin, string expectedLine)
    {
        var executor = Substitute.For<IAgentExecutor>();
        executor.SessionOrigin.Returns(origin);
        var sink = Substitute.For<IMessageSink>();
        var manager = new TaskManager(
            Options.Create(new AgentOptions { Name = "agent1", Role = "test", WorkDir = Path.GetTempPath() }),
            executor, new SessionManager(), NullLogger<TaskManager>.Instance, sink: sink);

        await manager.HandleStatus(Chat);

        var status = (string)Assert.Single(sink.ReceivedCalls(), c => c.GetMethodInfo().Name == nameof(IMessageSink.SendTextAsync))
            .GetArguments()[1]!;
        Assert.Contains(expectedLine, status.Split('\n'));
    }
}
