using System.Text.Json;
using System.Text.Json.Nodes;
using Fleet.Temporal.Engine;
using Temporalio.Api.Enums.V1;
using Temporalio.Converters;
using Temporalio.Exceptions;

namespace Fleet.Temporal.Tests.Engine;

public sealed class DesignReviewAttrsRequiredTests
{
    private const string Workflow = "UweDesignWorkflow";
    private static Dictionary<string, object?> Input()
    {
        var input = RequireAcceptanceTests.Input(); input["ExistingIssueNumber"] = 7;
        input["Description"] = "example feature"; return input;
    }

    [Fact]
    public void Design_ThreeReviewWrites_AreRequiredAndExact()
    {
        var writes = WorkflowDefinitionValidator.Walk(SeedHarness.Parse(Workflow)).OfType<SetAttributeStep>()
            .Where(s => s.Name is "design_clear_review_attrs" or "design_publish_review_attrs" or "design_withdraw_review_attrs").ToArray();
        Assert.Equal(3, writes.Length);
        Assert.All(writes, w => { Assert.True(w.Required); Assert.False(w.IgnoreFailure); Assert.Equal(new[] { "ReviewRef", "ReviewScrub" }, w.Attributes.Keys.OrderBy(k => k)); });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Design_ReviewEvidence_IsWrittenBeforeGateAndNotice(bool human)
    {
        var run = await SeedHarness.RunAsync(Workflow, Input(), c => human && c.Step.StartsWith("review-", StringComparison.Ordinal)
            ? "SUMMARY: human choice\nBLOCKER: decision needed\nVERDICT: needs_human_review" : RequireAcceptanceTests.DesignRespond(c), async (handle, h) =>
        { await h.WaitForGateVisitAsync(handle, "design-approval:1"); await handle.SignalAsync("design-approval", [JsonSerializer.SerializeToElement(new { Decision = "rejected" })]); });
        var expected = human ? "" : RequireAcceptanceTests.Body;
        var phase = run.History.Events.First(e => e.EventType == EventType.UpsertWorkflowSearchAttributes
            && e.UpsertWorkflowSearchAttributesEventAttributes.SearchAttributes.IndexedFields.TryGetValue("Phase", out var p)
            && DataConverter.Default.PayloadConverter.ToValue<string>(p) == "design-approval");
        var write = run.History.Events.Last(e => e.EventId < phase.EventId && e.EventType == EventType.UpsertWorkflowSearchAttributes
            && e.UpsertWorkflowSearchAttributesEventAttributes.SearchAttributes.IndexedFields.ContainsKey("ReviewRef"));
        Assert.Equal(expected, DataConverter.Default.PayloadConverter.ToValue<string>(write.UpsertWorkflowSearchAttributesEventAttributes.SearchAttributes.IndexedFields["ReviewRef"]));
        var notice = run.History.Events.First(e => e.EventType == EventType.ActivityTaskScheduled
            && e.ActivityTaskScheduledEventAttributes.ActivityType.Name == "DelegateToAgent"
            && DataConverter.Default.PayloadConverter.ToValue<string>(e.ActivityTaskScheduledEventAttributes.Input.Payloads_[1]).StartsWith("Post to Telegram group: Design spec for issue", StringComparison.Ordinal));
        Assert.True(write.EventId < phase.EventId && phase.EventId < notice.EventId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Design_UnknownPublishAttribute_RequiredFailsOptionalReachesGate(bool required)
    {
        Action<JsonNode> mutate = seed =>
        {
            var definition = seed["workflowDefinitions"]!.AsArray().Single(d => d!["name"]!.GetValue<string>() == Workflow)!["definition"]!;
            var publish = Objects(definition).Single(n => n["name"]?.GetValue<string>() == "design_publish_review_attrs");
            publish["required"] = required; publish["attributes"]!["UnregisteredReviewAttribute"] = "synthetic";
        };
        if (required)
        {
            var error = await Assert.ThrowsAsync<WorkflowFailedException>(() => SeedHarness.RunAsync(Workflow, Input(), RequireAcceptanceTests.DesignRespond, async (handle, h) =>
            {
                await Assert.ThrowsAsync<WorkflowFailedException>(() => handle.GetResultAsync<JsonElement?>());
                Assert.DoesNotContain("design-approval", SeedHarness.Upserts(await handle.FetchHistoryAsync(), "Phase"));
                Assert.DoesNotContain(h.Calls, c => c.Instruction.StartsWith("Post to Telegram group: Design spec for issue", StringComparison.Ordinal));
            }, mutateSeed: mutate));
            Assert.Equal("RequiredSearchAttribute", Assert.IsType<ApplicationFailureException>(error.InnerException).ErrorType);
        }
        else
        {
            var run = await SeedHarness.RunAsync(Workflow, Input(), RequireAcceptanceTests.DesignRespond, async (handle, h) =>
            { await h.WaitForGateVisitAsync(handle, "design-approval:1"); await handle.SignalAsync("design-approval", [JsonSerializer.SerializeToElement(new { Decision = "rejected" })]); }, mutateSeed: mutate);
            Assert.Contains("design-approval", run.Upserts("Phase")); Assert.Contains(run.Calls, c => c.Instruction.StartsWith("Post to Telegram group: Design spec for issue", StringComparison.Ordinal));
        }
    }

    private static IEnumerable<JsonObject> Objects(JsonNode? node)
    {
        if (node is JsonObject obj) { yield return obj; foreach (var item in obj) foreach (var child in Objects(item.Value)) yield return child; }
        else if (node is JsonArray array) foreach (var item in array) foreach (var child in Objects(item)) yield return child;
    }
}
