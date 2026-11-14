using System.Text;
using Fleet.Contracts;

namespace Fleet.Protocol.Tests;

public sealed class RelayExecutionShapeTests
{
    [Theory]
    [InlineData(RelayShape.Agent)]
    [InlineData(RelayShape.Delegation)]
    public void Execution_identity_is_trailing_and_omitted_when_absent(RelayShape shape)
    {
        var message = new RelayMessage(7, "agent1", "answer", DateTimeOffset.UnixEpoch,
            ExecutionId: "execution-1");
        var bytes = RelayWire.Serialize(message, shape);
        var json = Encoding.UTF8.GetString(bytes);
        Assert.EndsWith(",\"ExecutionId\":\"execution-1\"}", json);
        Assert.Equal("execution-1", RelayWire.Deserialize(json)!.ExecutionId);

        var legacy = Encoding.UTF8.GetString(RelayWire.Serialize(message with { ExecutionId = null }, shape));
        Assert.Equal(legacy[..^1] + ",\"ExecutionId\":\"execution-1\"}", json);
        Assert.Null(RelayWire.Deserialize(legacy)!.ExecutionId);
        if (shape == RelayShape.Agent) Assert.DoesNotContain("Repo", json);
        else Assert.Contains("\"Repo\":null", json);
    }

    [Theory]
    [InlineData(RelayShape.Envelope)]
    [InlineData(RelayShape.Bridge)]
    public void Non_task_producers_keep_their_original_shapes(RelayShape shape)
    {
        var message = new RelayMessage(7, "bridge", "notice", DateTimeOffset.UnixEpoch,
            ExecutionId: "execution-1");
        Assert.Equal(RelayWire.Serialize(message with { ExecutionId = null }, shape),
            RelayWire.Serialize(message, shape));
    }
}
