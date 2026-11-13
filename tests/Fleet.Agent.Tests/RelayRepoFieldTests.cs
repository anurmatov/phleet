using System.Reflection;
using System.Text.Json;
using Fleet.Agent.Services;

namespace Fleet.Agent.Tests;

/// <summary>
/// #346: the Temporal bridge still sends the optional <c>Repo</c> field on a delegation (kept for
/// workflow replay), and the agent must treat that message as a normal directive, ignoring the field.
/// </summary>
public class RelayRepoFieldTests
{
    [Fact]
    public void A_relay_message_carrying_Repo_deserializes_as_a_normal_directive()
    {
        // What the temporal bridge writes for a delegation: every field, Repo included.
        const string json = """
            {"ChatId":0,"Sender":"temporal-bridge","Text":"do the task","Timestamp":"2026-09-25T00:00:00+00:00",
             "Type":"directive","CorrelationId":"c1","TaskId":null,"WorkflowId":"wf-1","SignalName":null,"Repo":"org/app"}
            """;
        // The same call OnRelayMessageReceived makes.
        var message = RelayWire.Deserialize(json)!;

        Assert.Equal("do the task", message.Text);
        Assert.Equal("directive", message.Type);
        Assert.Equal("wf-1", message.WorkflowId);

        // The record is the one the bridge writes, so it has the field. What the agent hands on to
        // its handlers does not: nothing downstream of the relay can route by it.
        var handler = typeof(GroupRelayService).GetEvent(nameof(GroupRelayService.MessageReceived))!
            .EventHandlerType!.GetMethod("Invoke")!;
        Assert.Equal(8, handler.GetParameters().Length);
        Assert.Equal(
            [typeof(long), typeof(string), typeof(string), typeof(string), typeof(string), typeof(string),
             typeof(string), typeof(string)],
            handler.GetParameters().Select(p => p.ParameterType));
    }
}
