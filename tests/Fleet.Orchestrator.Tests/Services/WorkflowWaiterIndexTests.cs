using System.Text.Json;
using Fleet.Orchestrator.Models;
using Fleet.Orchestrator.Services;
using Google.Protobuf;
using Temporalio.Api.Common.V1;

namespace Fleet.Orchestrator.Tests.Services;

/// <summary>
/// Reading which run waits for a run out of its start input, and the shape a run's links have on
/// the wire. The read itself needs a Temporal server and is not covered here.
/// </summary>
public sealed class WorkflowWaiterIndexTests
{
    private static Payload Json(string json, string encoding = "json/plain")
    {
        var payload = new Payload { Data = ByteString.CopyFromUtf8(json) };
        payload.Metadata["encoding"] = ByteString.CopyFromUtf8(encoding);
        return payload;
    }

    [Fact]
    public void AnInputThatNamesItsWaiter_GivesThatWorkflowId()
    {
        var input = Json("""{"Repo":"example-org/example-repo","IssueNumber":7,"WaiterWorkflowId":" driver-example-1 "}""");

        Assert.Equal("driver-example-1", WorkflowWaiterIndex.WaiterIn(input));
    }

    [Theory]
    // No such member.
    [InlineData("""{"Repo":"example-org/example-repo"}""")]
    // Blank, or not text.
    [InlineData("""{"WaiterWorkflowId":""}""")]
    [InlineData("""{"WaiterWorkflowId":"   "}""")]
    [InlineData("""{"WaiterWorkflowId":null}""")]
    [InlineData("""{"WaiterWorkflowId":42}""")]
    [InlineData("""{"WaiterWorkflowId":{"id":"driver-example-1"}}""")]
    // Not an object at the top.
    [InlineData("""["WaiterWorkflowId","driver-example-1"]""")]
    [InlineData("\"WaiterWorkflowId: driver-example-1\"")]
    // Not JSON at all, or cut short.
    [InlineData("WaiterWorkflowId=driver-example-1")]
    [InlineData("""{"WaiterWorkflowId":"driver-exa""")]
    [InlineData("")]
    public void AnInputThatNamesNone_GivesNone(string json)
    {
        Assert.Null(WorkflowWaiterIndex.WaiterIn(Json(json)));
    }

    [Fact]
    public void AnInputInAnotherEncoding_IsNotRead()
    {
        var json = """{"WaiterWorkflowId":"driver-example-1"}""";

        Assert.Null(WorkflowWaiterIndex.WaiterIn(Json(json, "binary/plain")));
        Assert.Null(WorkflowWaiterIndex.WaiterIn(Json(json, "binary/encrypted")));
        Assert.Null(WorkflowWaiterIndex.WaiterIn(new Payload { Data = ByteString.CopyFromUtf8(json) }));
    }

    [Fact]
    public void AnIdTooLongToBeOne_IsNotKept()
    {
        var input = Json($$"""{"WaiterWorkflowId":"{{new string('x', WorkflowWaiterIndex.MaxIdLength + 1)}}"}""");

        Assert.Null(WorkflowWaiterIndex.WaiterIn(input));
    }

    [Fact]
    public void ARunsLinks_AreOnTheWireByTheseNames_AndNullWhenItStandsAlone()
    {
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var linked = new WorkflowSummary(
            "child-1", "run-2", "ConsensusReviewWorkflow", "fleet", "fleet", "Running", DateTimeOffset.UnixEpoch,
            ParentWorkflowId: "parent-1", ParentRunId: "run-1", WaiterWorkflowId: "driver-example-1");
        var alone = new WorkflowSummary("solo-1", "run-3", "UwePrImplementationWorkflow", "fleet", "fleet", "Running", DateTimeOffset.UnixEpoch);

        var linkedJson = JsonDocument.Parse(JsonSerializer.Serialize(linked, web)).RootElement;
        Assert.Equal("parent-1", linkedJson.GetProperty("parentWorkflowId").GetString());
        Assert.Equal("run-1", linkedJson.GetProperty("parentRunId").GetString());
        Assert.Equal("driver-example-1", linkedJson.GetProperty("waiterWorkflowId").GetString());

        var aloneJson = JsonDocument.Parse(JsonSerializer.Serialize(alone, web)).RootElement;
        Assert.Equal(JsonValueKind.Null, aloneJson.GetProperty("parentWorkflowId").ValueKind);
        Assert.Equal(JsonValueKind.Null, aloneJson.GetProperty("waiterWorkflowId").ValueKind);
        // The fields a client has always read are where they were.
        Assert.Equal("solo-1", aloneJson.GetProperty("workflowId").GetString());
    }
}
