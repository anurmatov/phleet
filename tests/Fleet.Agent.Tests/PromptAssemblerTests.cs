using Fleet.Agent.Models;
using Fleet.Agent.Services;
using NSubstitute;

namespace Fleet.Agent.Tests;

public class PromptAssemblerTests
{
    [Fact]
    public void ForRelayDirective_ColdProcess_ReturnsSlimDirective()
    {
        var executor = Substitute.For<IAgentExecutor>();
        executor.IsProcessWarm.Returns(false);
        var assembler = new PromptAssembler(executor);

        var buffer = new GroupChatBuffer();
        buffer.Add("alice", "some chat message", null, DateTimeOffset.UtcNow);
        buffer.Add("bob", "another message", null, DateTimeOffset.UtcNow);

        var result = assembler.ForRelayDirective(buffer, "temporal-bridge", "do the task");

        Assert.Contains("[Directive from temporal-bridge]", result);
        Assert.Contains("do the task", result);
        Assert.DoesNotContain("some chat message", result);
        Assert.DoesNotContain("another message", result);
        Assert.DoesNotContain("Recent group conversation", result);
    }

    [Fact]
    public void ForRelayDirective_WarmProcess_ReturnsSlimDirective()
    {
        var executor = Substitute.For<IAgentExecutor>();
        executor.IsProcessWarm.Returns(true);
        var assembler = new PromptAssembler(executor);

        var buffer = new GroupChatBuffer();
        buffer.Add("alice", "some chat message", null, DateTimeOffset.UtcNow);

        var result = assembler.ForRelayDirective(buffer, "temporal-bridge", "do the task");

        Assert.Contains("[Directive from temporal-bridge]", result);
        Assert.Contains("do the task", result);
        Assert.DoesNotContain("some chat message", result);
    }
}
