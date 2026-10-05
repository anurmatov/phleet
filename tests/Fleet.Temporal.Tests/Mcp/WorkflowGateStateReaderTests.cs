using System.Text;
using Fleet.Temporal.Mcp;
using Google.Protobuf;
using NSubstitute;
using Temporalio.Api.Common.V1;
using Temporalio.Api.Enums.V1;
using Temporalio.Api.Workflow.V1;
using Temporalio.Api.WorkflowService.V1;
using Temporalio.Client;
using Temporalio.Converters;

namespace Fleet.Temporal.Tests.Mcp;

/// <summary>
/// #430 CEO feedback F1. The real reader, not the fake the tool tests use: it must bound the
/// describe with a 10-second RPC timeout and read Status and the Phase keyword correctly.
/// </summary>
public sealed class WorkflowGateStateReaderTests
{
    private sealed class TestDescription(DescribeWorkflowExecutionResponse raw)
        : WorkflowExecutionDescription(raw, DataConverter.Default);

    [Fact]
    public void BuildDescribeOptions_SetsTenSecondTimeoutAndPassesTheToken()
    {
        using var cts = new CancellationTokenSource();

        var options = WorkflowGateStateReader.BuildDescribeOptions(cts.Token);

        Assert.Equal(TimeSpan.FromSeconds(10), WorkflowGateStateReader.DescribeTimeout);
        Assert.Equal(TimeSpan.FromSeconds(10), options.Rpc!.Timeout);
        Assert.Equal(cts.Token, options.Rpc.CancellationToken);
    }

    [Fact]
    public async Task ReadAsync_SuppliesTheBoundedOptionsAndReadsStatusAndPhase()
    {
        WorkflowDescribeOptions? captured = null;
        var handle = HandleReturning(
            Describe(WorkflowExecutionStatus.Running, phase: "design-approval"),
            options => captured = options);

        var (status, phase) = await new WorkflowGateStateReader().ReadAsync(handle);

        Assert.Equal(WorkflowExecutionStatus.Running, status);
        Assert.Equal("design-approval", phase);
        Assert.NotNull(captured);
        Assert.Equal(TimeSpan.FromSeconds(10), captured!.Rpc!.Timeout);
    }

    [Fact]
    public async Task ReadAsync_MissingPhase_ReturnsNullPhase()
    {
        var handle = HandleReturning(Describe(WorkflowExecutionStatus.Completed, phase: null), _ => { });

        var (status, phase) = await new WorkflowGateStateReader().ReadAsync(handle);

        Assert.Equal(WorkflowExecutionStatus.Completed, status);
        Assert.Null(phase);
    }

    private static WorkflowHandle HandleReturning(
        WorkflowExecutionDescription description,
        Action<WorkflowDescribeOptions?> capture)
    {
        var client = Substitute.For<ITemporalClient>();
        var handle = Substitute.For<WorkflowHandle>(client, "workflow-1", null!, null!, null!);
        handle.DescribeAsync(Arg.Do<WorkflowDescribeOptions?>(capture)).Returns(description);
        return handle;
    }

    // Keyword search attributes travel as json/plain payloads tagged with their indexed type.
    private static WorkflowExecutionDescription Describe(WorkflowExecutionStatus status, string? phase)
    {
        var info = new WorkflowExecutionInfo
        {
            Execution = new Temporalio.Api.Common.V1.WorkflowExecution { WorkflowId = "workflow-1", RunId = "run-1" },
            Type = new WorkflowType { Name = "example-workflow" },
            Status = status,
            SearchAttributes = new SearchAttributes(),
        };
        if (phase is not null)
        {
            info.SearchAttributes.IndexedFields["Phase"] = new Payload
            {
                Metadata =
                {
                    ["encoding"] = ByteString.CopyFromUtf8("json/plain"),
                    ["type"] = ByteString.CopyFromUtf8("Keyword"),
                },
                Data = ByteString.CopyFrom(Encoding.UTF8.GetBytes($"\"{phase}\"")),
            };
        }

        return new TestDescription(new DescribeWorkflowExecutionResponse { WorkflowExecutionInfo = info });
    }
}
