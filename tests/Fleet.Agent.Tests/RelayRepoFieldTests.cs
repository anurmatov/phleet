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
        // The shape Fleet.Temporal's RelayMessage serializes to with default options.
        const string json = """
            {"ChatId":0,"Sender":"temporal-bridge","Text":"do the task","Timestamp":"2026-09-25T00:00:00+00:00",
             "Type":"directive","CorrelationId":"c1","TaskId":null,"WorkflowId":"wf-1","SignalName":null,"Repo":"org/app"}
            """;
        var type = typeof(GroupRelayService).GetNestedType("RelayMessage", BindingFlags.NonPublic)!;

        // The same call OnRelayMessageReceived makes.
        var message = JsonSerializer.Deserialize(json, type)!;

        Assert.Equal("do the task", type.GetProperty("Text")!.GetValue(message));
        Assert.Equal("directive", type.GetProperty("Type")!.GetValue(message));
        Assert.Equal("wf-1", type.GetProperty("WorkflowId")!.GetValue(message));
        Assert.Null(type.GetProperty("Repo"));
    }
}
