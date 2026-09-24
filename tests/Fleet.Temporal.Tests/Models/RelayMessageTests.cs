using System.Text.Json;
using Fleet.Temporal.Models;

namespace Fleet.Temporal.Tests.Models;

/// <summary>
/// #347 D9 — <see cref="RelayMessage.Repo"/> on the wire. The bridge serializes with default
/// System.Text.Json options (PascalCase), and agents on either side of a deploy must read what
/// the other wrote.
/// </summary>
public sealed class RelayMessageTests
{
    private static readonly DateTimeOffset At = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    [Fact]
    public void WithRepo_RoundTrips()
    {
        var message = new RelayMessage(
            -100000000001, "temporal-bridge", "do the thing", At,
            TaskId: "example-workflow/step", Repo: "org/app");

        var json = JsonSerializer.Serialize(message);
        var back = JsonSerializer.Deserialize<RelayMessage>(json);

        Assert.Contains("\"Repo\":\"org/app\"", json);
        Assert.Equal(message, back);
    }

    [Fact]
    public void WithoutRepo_RoundTripsAsNull()
    {
        var message = new RelayMessage(-100000000001, "temporal-bridge", "do the thing", At);

        var back = JsonSerializer.Deserialize<RelayMessage>(JsonSerializer.Serialize(message));

        Assert.Equal(message, back);
        Assert.Null(back!.Repo);
    }

    /// <summary>
    /// A payload written before the field existed — by an old bridge, or still sitting in a queue
    /// across the deploy — has no <c>Repo</c> key at all and must read as "no repo signal".
    /// </summary>
    [Fact]
    public void APayloadWrittenBeforeRepoExisted_DeserializesWithRepoNull()
    {
        const string legacy =
            """
            {"ChatId":-100000000001,"Sender":"temporal-bridge","Text":"do the thing",
             "Timestamp":"2026-01-02T03:04:05+00:00","Type":"directive","CorrelationId":null,
             "TaskId":"example-workflow/step","WorkflowId":null,"SignalName":null}
            """;

        var message = JsonSerializer.Deserialize<RelayMessage>(legacy);

        Assert.NotNull(message);
        Assert.Null(message.Repo);
        Assert.Equal("do the thing", message.Text);
        Assert.Equal("example-workflow/step", message.TaskId);
        Assert.Equal(RelayMessageType.Directive, message.Type);
    }
}
