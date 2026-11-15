using Fleet.Temporal.Models;
using Temporalio.Converters;
using Temporalio.Api.Enums.V1;

namespace Fleet.Temporal.Tests.Engine;

public sealed partial class PrReadinessHoldTests
{
    [Theory]
    [InlineData("REVIEW_REQUIRED", "completed")]
    [InlineData("CHANGES_REQUESTED", "completed")]
    [InlineData("UNKNOWN", "completed")]
    [InlineData("", "completed")]
    [InlineData("APPROVED", "failed")]
    public async Task RequiredGithubReview_WithGreenCi_NeverOffersMergeApproval(string review, string status)
    {
        var script = new Script();
        var run = await RunAsync(script, async (handle, h) =>
        {
            // An old seed incorrectly offers this gate; reject it to let the causal test finish.
            await DriveToAsync(handle, h, history =>
                h.LastUpsert(history, "Phase") is "merge-approval" or "technical-stalled");
            if (h.LastUpsert(await handle.FetchHistoryAsync(), "Phase") == "merge-approval")
                await handle.SignalAsync("merge-approval", [Json(new { Decision = "rejected", Ref = h.LastUpsert(await handle.FetchHistoryAsync(), "GateRef") })]);
        }, delegateOverride: (call, _) => call.Step is "merge_preflight" or "merge_gate_preflight"
            ? Task.FromResult(new AgentTaskResult(
                SeedHarness.PreflightClear.Replace("GITHUB_REVIEW: NOT_REQUIRED", review == "" ? "" : "GITHUB_REVIEW: " + review), status)) : null);

        Assert.DoesNotContain("merge-approval", run.Upserts("Phase"));
        Assert.DoesNotContain(run.Calls, c => c.Step is "phase4_merge" or "phase4_merge_pinned");
        Assert.Contains(run.Calls, c => c.Step == "notify_merge_prerequisite" && c.Instruction.Contains(PrUrl, StringComparison.Ordinal) && c.Instruction.Contains(script.Head, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("incomplete")]
    public async Task TypedScopeBlocker_DoesNotSpendUnchangedHeadRetryBudget(string status)
    {
        var script = new Script();
        var report = $"IMPLEMENTATION: blocked\nBLOCKER: explicit file restriction\nSCOPE_QUESTION: May the approved route fixture be registered?\nPR_URL: {PrUrl}\nHEAD_RANGE: {script.Head}..{script.Head}\nHEAD_SHA: {script.Head}";
        var run = await RunAsync(script, (_, _) => Task.CompletedTask, allowFailure: true, humanInputNamespace: true,
            delegateOverride: (call, _) => call.Step == "phase1_implement" || call.Step.StartsWith("revise_", StringComparison.Ordinal)
                ? Task.FromResult(new AgentTaskResult(report, status)) : null);

        Assert.Single(run.Calls, c => c.Step == "phase1_implement" || c.Step.StartsWith("revise_", StringComparison.Ordinal));
        Assert.Single(run.ChildStarts("UweHumanInputWorkflow"));
        Assert.DoesNotContain(run.Calls, c => c.Step is "phase4_merge" or "phase4_merge_pinned");
    }
}

public sealed partial class PrReadinessHoldTests
{
    [Theory]
    [InlineData("approved", false, "provided")]
    [InlineData("approved", true, "provided")]
    [InlineData("rejected", false, "provided")]
    [InlineData("expired", false, "provided")]
    [InlineData("changed-head", false, "provided")]
    [InlineData("repeated", false, "provided")]
    [InlineData("cancelled", false, "provided")]
    [InlineData("approved", true, "omitted")]
    [InlineData("approved", true, "empty")]
    [InlineData("approved", true, "reported")]
    public async Task ScopeChild_IsOwned_Literal_Bounded_AndResumesOnlyOnce(string outcome, bool noPr, string branchMode)
    {
        var script = new Script();
        const string question = "May the explicitly restricted fixture be registered? {{config.Secret}}";
        const string comment = "  literal \"Кыргыз\" \\ {{config.Secret}}\nnot a command  ";
        var report = "IMPLEMENTATION: blocked\nBLOCKER: explicit restriction\nSCOPE_QUESTION: " + question +
            (noPr ? "\nno PR yet" : $"\nPR_URL: {PrUrl}\nHEAD_RANGE: {script.Head}..{script.Head}\nHEAD_SHA: {script.Head}");
        if (branchMode == "reported") report += "\nBRANCH_NAME: feat/selected-description";
        Temporalio.Client.WorkflowHandle? child = null;
        string? raw = null;
        var input = Input(required: false, waiter: false);
        if (branchMode is "omitted" or "reported") input.Remove("BranchName");
        if (branchMode == "empty") input["BranchName"] = "";
        if (branchMode == "reported") input.Remove("IssueNumber");
        var expectedBranch = branchMode switch
        {
            "provided" => "feat/example",
            "reported" => "feat/selected-description",
            _ => "feat/issue-7"
        };
        var run = await SeedHarness.RunAsync(Workflow, input, script.Respond, async (handle, h) =>
        {
            child = await h.WaitForChildAsync(handle, "UweHumanInputWorkflow");
            await h.WaitForGateVisitAsync(child, "request-input:1");
            var history = await child.FetchHistoryAsync();
            var payload = history.Events.First().WorkflowExecutionStartedEventAttributes.Input.Payloads_[0];
            var request = Temporalio.Converters.DataConverter.Default.PayloadConverter.ToValue<System.Text.Json.JsonElement>(payload);
            Assert.Equal(question, request.GetProperty("Question").GetString());
            var origin = request.GetProperty("Origin");
            Assert.Equal(handle.Id, origin.GetProperty("WorkflowId").GetString());
            Assert.Equal(handle.ResultRunId, origin.GetProperty("RunId").GetString());
            Assert.Contains(noPr ? "no PR yet" : PrUrl, request.GetProperty("Context").GetString());
            Assert.Contains("branch " + expectedBranch + ";", request.GetProperty("Context").GetString());
            Assert.Single(h.Calls, c => c.Step == "phase1_implement");
            Assert.DoesNotContain(h.Calls, c => c.Step.StartsWith("revise_", StringComparison.Ordinal) || c.Step is "phase4_merge" or "phase4_merge_pinned");
            if (outcome == "cancelled") { await handle.CancelAsync(); return; }
            if (outcome == "expired") return; // the real child timer expires under the existing time-skipping server
            await child.SignalAsync("request-input", [Json(new { Value = outcome == "rejected" ? "rejected" : "approved", Comment = comment, Ref = child.RunId, VisitId = "request-input:1" })]);
            raw = await child.GetResultAsync<string>().WaitAsync(TimeSpan.FromSeconds(20));
            if (outcome == "approved")
            {
                await h.WaitForGateVisitAsync(handle, "merge-approval:1");
                await h.SignalHumanApprovalAsync(handle);
            }
        }, allowFailure: true, humanInputNamespace: true, delegateOverride: (call, _) =>
        {
            if (call.Step == "phase1_implement" || (outcome == "repeated" && call.Step.StartsWith("revise_", StringComparison.Ordinal)))
                return Task.FromResult(new AgentTaskResult(report, "failed"));
            if (call.Step == "validate_scope_answer") return ValidateAsync();
            if (call.Step == "scope_head_read")
            {
                Assert.Contains("branch " + expectedBranch + ";", call.Instruction);
                return Task.FromResult(new AgentTaskResult(noPr ? "ARTIFACT: none" : "HEAD_OID: " + (outcome == "changed-head" ? new string('f', 40) : script.Head), "completed"));
            }
            return null;

            async Task<AgentTaskResult> ValidateAsync()
            {
                var original = call.Instruction.Split("ORIGINAL_AWAITED_CHILD_JSON:\n", StringSplitOptions.None)[1];
                raw ??= await child!.GetResultAsync<string>();
                Assert.Equal(raw, original);
                var stdout = await RunScopeValidatorAsync(call.Instruction, original, raw, child!.Id, child.RunId!);
                return new AgentTaskResult(stdout, "completed");
            }
        });

        Assert.Single(run.ChildStarts("UweHumanInputWorkflow"));
        var authors = run.Calls.Where(c => c.Step == "phase1_implement" || c.Step.StartsWith("revise_", StringComparison.Ordinal)).ToList();
        Assert.Equal(outcome is "approved" or "repeated" ? 2 : 1, authors.Count);
        if (outcome == "approved")
        {
            Assert.Contains(report, authors[1].Instruction);
            Assert.Contains("Preserve the captured branch " + expectedBranch, authors[1].Instruction);
            Assert.Contains(raw!, authors[1].Instruction);
            Assert.Contains("{{config.Secret}}", authors[1].Instruction);
            Assert.Equal(comment, System.Text.Json.JsonDocument.Parse(raw!).RootElement.GetProperty("Response").GetProperty("Answer").GetProperty("Comment").GetString());
            Assert.Single(run.Calls, c => c.Step == "phase4_merge");
            Assert.Single(run.ConsensusInputs); // no pre-answer approval is inherited
        }
        else Assert.DoesNotContain(run.Calls, c => c.Step is "phase4_merge" or "phase4_merge_pinned");
        if (outcome is "changed-head" or "repeated") Assert.Equal(WorkflowExecutionStatus.Failed, run.Status);
        if (outcome == "cancelled") Assert.Equal(WorkflowExecutionStatus.Canceled, run.Status);
    }

    // Execute the exact validator shipped in the delegate prompt, not a second parser that could drift.
    private static async Task<string> RunScopeValidatorAsync(string instruction, string raw, string exactRaw, string childId, string childRun)
    {
        var dir = Path.Combine(Path.GetTempPath(), "scope-validation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var code = instruction.Split("```python\n", StringSplitOptions.None)[1].Split("```", StringSplitOptions.None)[0];
            var file = Path.Combine(dir, "validate.py");
            await File.WriteAllTextAsync(file, code);
            await File.WriteAllTextAsync(Path.Combine(dir, "result.json"), raw);
            await File.WriteAllTextAsync(Path.Combine(dir, "exact.json"), System.Text.Json.JsonSerializer.Serialize(new { workflowId = childId, runId = childRun, result = exactRaw }));
            string Field(string label) => instruction.Split(label + ": ", StringSplitOptions.None)[1].Split('\n')[0];
            var start = new System.Diagnostics.ProcessStartInfo("python3") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var arg in new[] { file, Path.Combine(dir, "result.json"), Path.Combine(dir, "exact.json"), Field("Expected parent ID"), Field("Expected parent run"), Field("Expected original question"), Field("Expected original context") }) start.ArgumentList.Add(arg);
            using var process = System.Diagnostics.Process.Start(start)!;
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            return (await process.StandardOutput.ReadToEndAsync()).TrimEnd('\r', '\n');
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}

public sealed partial class PrReadinessHoldTests
{
    [Theory]
    [InlineData("approved", "SCOPE_RESULT: approved")]
    [InlineData("run-v7", "SCOPE_RESULT: approved")]
    [InlineData("parent", "SCOPE_RESULT: invalid")]
    [InlineData("parent-run", "SCOPE_RESULT: invalid")]
    [InlineData("question", "SCOPE_RESULT: invalid")]
    [InlineData("context", "SCOPE_RESULT: invalid")]
    [InlineData("child-run", "SCOPE_RESULT: invalid")]
    [InlineData("child-id", "SCOPE_RESULT: invalid")]
    [InlineData("namespace", "SCOPE_RESULT: invalid")]
    [InlineData("delivery", "SCOPE_RESULT: invalid")]
    [InlineData("status", "SCOPE_RESULT: invalid")]
    [InlineData("value", "SCOPE_RESULT: invalid")]
    [InlineData("comment", "SCOPE_RESULT: invalid")]
    [InlineData("double-json", "SCOPE_RESULT: invalid")]
    [InlineData("duplicate", "SCOPE_RESULT: invalid")]
    [InlineData("exact-result", "SCOPE_RESULT: invalid")]
    public async Task LiteralScopeValidator_RejectsMismatchedOrMalformedEvidence(string change, string expected)
    {
        const string childId = "human-input-00000000-0000-4000-8000-000000000001";
        var childRun = change == "run-v7" ? "00000000-0000-7000-8000-000000000002" : "00000000-0000-4000-8000-000000000002";
        const string parentRun = "00000000-0000-4000-8000-000000000003";
        const string question = "literal {{config.Secret}} question";
        const string context = "only this artifact";
        var raw = System.Text.Json.JsonSerializer.Serialize(new
        {
            Namespace = "fleet", WorkflowId = childId, RunId = childRun, Delivery = "parent-result",
            Request = new { Question = question, Context = context, AnswerMode = "approve-reject", Origin = new { Kind = "parent", Namespace = "fleet", WorkflowId = "parent", RunId = parentRun } },
            Response = new { Status = "answered", Answer = new { Value = "approved", Comment = "  literal\n{{config.Secret}} \\" } }
        });
        var value = System.Text.Json.Nodes.JsonNode.Parse(raw)!;
        switch (change)
        {
            case "parent": value["Request"]!["Origin"]!["WorkflowId"] = "another-parent"; break;
            case "parent-run": value["Request"]!["Origin"]!["RunId"] = childRun; break;
            case "question": value["Request"]!["Question"] = question + " "; break;
            case "context": value["Request"]!["Context"] = "another artifact"; break;
            case "child-run": value["RunId"] = parentRun; break;
            case "child-id": value["WorkflowId"] = "human-input-" + parentRun; break;
            case "namespace": value["Namespace"] = "other"; break;
            case "delivery": value["Delivery"] = "acknowledged"; break;
            case "status": value["Response"]!["Status"] = "invalid"; break;
            case "value": value["Response"]!["Answer"]!["Value"] = "Approved"; break;
            case "comment": value["Response"]!["Answer"]!["Comment"] = 42; break;
        }
        raw = value.ToJsonString();
        if (change == "double-json") raw = System.Text.Json.JsonSerializer.Serialize(raw);
        if (change == "duplicate") raw = raw.Insert(1, "\"Namespace\":\"fleet\",");
        var validator = Assert.Single(Fleet.Temporal.Engine.WorkflowDefinitionValidator.Walk(SeedHarness.Parse(Workflow)).OfType<Fleet.Temporal.Engine.DelegateStep>(), d => d.Name == "validate_scope_answer");
        var template = new Fleet.Temporal.Engine.TemplateEngine(new()
        {
            ["workflow"] = new Dictionary<string, object?> { ["id"] = "parent", ["runId"] = parentRun },
            ["vars"] = new Dictionary<string, object?> { ["scope_question"] = question, ["scope_context"] = context, ["scope_result"] = raw }
        });
        var instruction = template.ResolveString(validator.Instruction);
        Assert.Equal(expected, await RunScopeValidatorAsync(instruction, raw, change == "exact-result" ? raw + " " : raw, childId, childRun));
    }

    [Theory]
    [InlineData("APPROVED")]
    [InlineData("NOT_REQUIRED")]
    public async Task GithubReviewHold_ResumesThroughHumanGate_AndChecksAgainAfterIt(string clear)
    {
        var script = new Script();
        int reads = 0;
        var run = await RunAsync(script, async (handle, h) =>
        {
            await DriveToAsync(handle, h, history => h.LastUpsert(history, "GateVisit") == "merge-approval:1");
            Assert.DoesNotContain(h.Calls, c => c.Step == "phase4_merge");
            await h.SignalHumanApprovalAsync(handle);
        }, delegateOverride: (call, _) =>
        {
            if (call.Step is not ("merge_gate_preflight" or "merge_preflight")) return null;
            var review = Interlocked.Increment(ref reads) is 1 or 4 ? "REVIEW_REQUIRED" : clear;
            return Task.FromResult(new AgentTaskResult(SeedHarness.PreflightClear.Replace("GITHUB_REVIEW: NOT_REQUIRED", "GITHUB_REVIEW: " + review), "completed"));
        });
        Assert.Single(run.Calls, c => c.Step == "phase4_merge");
        Assert.Equal(1, run.Upserts("GateVisit").Count(v => v == "merge-approval:1"));
        Assert.Equal(0, run.Upserts("GateVisit").Count(v => v == "merge-approval:2"));
        Assert.True(reads >= 4);
    }

    [Fact]
    public async Task TypedFailedComplete_IsNeverAttested_AndKeepsConcreteFeedback()
    {
        var run = await RunAsync(new Script(), (_, _) => Task.CompletedTask, allowFailure: true,
            delegateOverride: (call, _) => call.Step == "phase1_implement" || call.Step.StartsWith("revise_", StringComparison.Ordinal)
                ? Task.FromResult(new AgentTaskResult($"IMPLEMENTATION: complete\nHEAD_RANGE: {new string('a', 40)}..{new string('a', 40)}\nBLOCKER: compiler error E123", "failed")) : null);
        Assert.Empty(run.ConsensusInputs);
        Assert.Empty(run.ChildStarts("UweHumanInputWorkflow"));
        Assert.Equal(5, run.Calls.Count(c => c.Step == "phase1_implement" || c.Step.StartsWith("revise_", StringComparison.Ordinal)));
        Assert.All(run.Calls.Where(c => c.Step.StartsWith("revise_", StringComparison.Ordinal)), c => Assert.Contains("compiler error E123", c.Instruction));
        Assert.Equal(WorkflowExecutionStatus.Failed, run.Status);
    }
}

public sealed partial class PrReadinessHoldTests
{
    [Fact]
    public async Task CustomHumanWaitsAndReminders_SurviveReadinessChanges()
    {
        var script = new Script { Review = n => n == 1 ? "needs_human_review" : "approved" };
        var controls = new List<(int timeout, int interval, int count)>();
        var run = await RunAsync(script, async (handle, h) =>
        {
            await WaitPhaseAsync(handle, h, "human-review");
            await handle.SignalAsync("human-review", [Json(new { Decision = "approved" })]);
            await h.WaitForGateVisitAsync(handle, "merge-approval:1");
            await h.SignalHumanApprovalAsync(handle);
        }, mutateSeed: root =>
        {
            void Visit(System.Text.Json.Nodes.JsonNode? node)
            {
                if (node is System.Text.Json.Nodes.JsonObject o)
                {
                    if (o["type"]?.ToString() == "wait_for_signal" && o["signalName"]?.ToString() is "human-review" or "merge-approval")
                    {
                        var interval = (int)o["reminderIntervalMinutes"]!;
                        var count = (int)o["maxReminders"]!;
                        o["timeoutMinutes"] = 43200;
                        controls.Add(((int)o["timeoutMinutes"]!, interval, count));
                    }
                    foreach (var item in o.ToList()) Visit(item.Value);
                }
                else if (node is System.Text.Json.Nodes.JsonArray a) foreach (var item in a) Visit(item);
            }
            // Only this synthetic source candidate, never an existing definition/snapshot.
            Visit(root["workflowDefinitions"]!.AsArray().Single(n => n!["name"]!.ToString() == Workflow)!["definition"]);
        });
        Assert.NotEmpty(controls);
        Assert.All(controls, c => { Assert.Equal(43200, c.timeout); Assert.Contains(c.interval, new[] { 60, 180 }); Assert.Equal(2, c.count); });
        Assert.Single(run.Calls, c => c.Step == "phase4_merge");
        Assert.Empty(run.ChildStarts("UwePrAcceptanceWorkflow"));
        Assert.Empty(run.ChildStarts("UweAcceptanceCleanupWorkflow"));
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("incomplete")]
    public async Task ScopeValidationTechnicalFailure_CannotBecomeApproval(string status)
    {
        var script = new Script();
        var run = await RunAsync(script, async (handle, h) =>
        {
            var child = await h.WaitForChildAsync(handle, "UweHumanInputWorkflow");
            await h.WaitForGateVisitAsync(child, "request-input:1");
            await child.SignalAsync("request-input", [Json(new { Value = "approved", Ref = child.RunId, VisitId = "request-input:1" })]);
        }, humanInputNamespace: true, allowFailure: true, delegateOverride: (call, _) => call.Step switch
        {
            "phase1_implement" => Task.FromResult(new AgentTaskResult("IMPLEMENTATION: blocked\nBLOCKER: explicit restriction\nSCOPE_QUESTION: Is this still approved?\nno PR yet", "failed")),
            "validate_scope_answer" => Task.FromResult(new AgentTaskResult("SCOPE_RESULT: approved", status)),
            _ => null
        });
        Assert.Single(run.Calls, c => c.Step == "phase1_implement");
        Assert.DoesNotContain(run.Calls, c => c.Step.StartsWith("revise_", StringComparison.Ordinal) || c.Step is "scope_head_read" or "phase4_merge" or "phase4_merge_pinned");
        Assert.Equal(WorkflowExecutionStatus.Failed, run.Status);
    }
}

public sealed partial class PrReadinessHoldTests
{
    [Fact]
    public void EveryAuthorPath_CapturesTypedStatus_AndMechanicalFixtureRule()
    {
        var authors = Fleet.Temporal.Engine.WorkflowDefinitionValidator.Walk(SeedHarness.Parse(Workflow))
            .OfType<Fleet.Temporal.Engine.DelegateStep>()
            .Where(d => d.Name == "phase1_implement" || d.Name?.StartsWith("revise_", StringComparison.Ordinal) == true).ToList();
        Assert.Equal(6, authors.Count);
        Assert.All(authors, d =>
        {
            Assert.Equal("impl_status", d.StatusVar);
            Assert.False(d.RetryOnIncomplete);
            Assert.Contains("mechanically necessary test fixture/manifest solely registering already-approved behavior needs no separate permission", d.Instruction);
            Assert.Contains("explicit prohibition still needs normal human resolution", d.Instruction);
        });
    }

    [Fact]
    public async Task OversizedScopeRequest_StopsWithoutTruncationOrNewAuthorCall()
    {
        var run = await RunAsync(new Script(), (_, _) => Task.CompletedTask, allowFailure: true, humanInputNamespace: true,
            delegateOverride: (call, _) => call.Step == "phase1_implement"
                ? Task.FromResult(new AgentTaskResult("IMPLEMENTATION: blocked\nBLOCKER: explicit restriction\nSCOPE_QUESTION: " + new string('q', 1800), "incomplete")) : null);
        Assert.Equal(WorkflowExecutionStatus.Failed, run.Status);
        Assert.Contains("1800", run.Failure!.ToString());
        Assert.Empty(run.ChildStarts("UweHumanInputWorkflow"));
        Assert.Single(run.Calls, c => c.Step == "phase1_implement");
        Assert.DoesNotContain(run.Calls, c => c.Step.StartsWith("revise_", StringComparison.Ordinal));
    }
}

public sealed partial class PrReadinessHoldTests
{
    [Fact]
    public async Task DelegatedApproval_StillRequiresFormalReviewAfterItsRepin()
    {
        var script = new Script();
        var run = await RunAsync(script, async (handle, h) =>
        {
            await h.WaitForGateVisitAsync(handle, "merge-approval:1");
            await handle.SignalAsync("merge-approval", [Delegated("merge-approval:1", script.Head)]);
        }, delegateOverride: (call, _) => call.Step == "merge_approval_preflight"
            ? Task.FromResult(new AgentTaskResult(SeedHarness.PreflightClear.Replace("GITHUB_REVIEW: NOT_REQUIRED", "GITHUB_REVIEW: REVIEW_REQUIRED"), "completed")) : null);
        Assert.Single(run.Calls, c => c.Step == "merge_approval_preflight");
        Assert.Single(run.Calls, c => c.Step == "notify_merge_prerequisite");
        Assert.Contains("merge-hold", run.Upserts("Phase"));
        Assert.Equal(1, Timers(run, 300));
        Assert.Single(run.Calls, c => c.Step == "phase4_merge_pinned");
    }

    [Fact]
    public async Task CancellationDuringScopeValidation_PropagatesWithoutAuthorOrHeadRead()
    {
        var run = await RunAsync(new Script(), async (handle, h) =>
        {
            var child = await h.WaitForChildAsync(handle, "UweHumanInputWorkflow");
            await h.WaitForGateVisitAsync(child, "request-input:1");
            await child.SignalAsync("request-input", [Json(new { Value = "approved", Ref = child.RunId, VisitId = "request-input:1" })]);
            await WaitForCallAsync(h, "validate_scope_answer");
            await handle.CancelAsync();
        }, allowFailure: true, humanInputNamespace: true, delegateOverride: (call, _) => call.Step switch
        {
            "phase1_implement" => Task.FromResult(new AgentTaskResult("IMPLEMENTATION: blocked\nBLOCKER: explicit restriction\nSCOPE_QUESTION: Is this approved?\nno PR yet", "failed")),
            "validate_scope_answer" => WaitForCancellation(),
            _ => null
        });
        Assert.Equal(WorkflowExecutionStatus.Canceled, run.Status);
        Assert.DoesNotContain(run.Calls, c => c.Step.StartsWith("revise_", StringComparison.Ordinal) || c.Step is "scope_head_read" or "phase4_merge" or "phase4_merge_pinned");

        static async Task<AgentTaskResult> WaitForCancellation()
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, Temporalio.Activities.ActivityExecutionContext.Current.CancellationToken);
            throw new InvalidOperationException("unreachable");
        }
    }
}

public sealed partial class PrReadinessHoldTests
{
    [Fact]
    public async Task NoPrDescriptionBlocker_WithoutSelectedBranch_StopsBeforeQuestion()
    {
        var input = Input(required: false, waiter: false);
        input.Remove("BranchName");
        input.Remove("IssueNumber");
        input["Description"] = "synthetic description-only task";
        var run = await SeedHarness.RunAsync(Workflow, input, _ => "unused", (_, _) => Task.CompletedTask,
            allowFailure: true, humanInputNamespace: true, delegateOverride: (call, _) => call.Step == "phase1_implement"
                ? Task.FromResult(new AgentTaskResult("IMPLEMENTATION: blocked\nBLOCKER: explicit restriction\nSCOPE_QUESTION: Is this allowed?\nno PR yet", "failed")) : null);
        Assert.Equal(WorkflowExecutionStatus.Failed, run.Status);
        Assert.Contains("scope_branch_unavailable", run.Failure!.ToString());
        Assert.Empty(run.ChildStarts("UweHumanInputWorkflow"));
        Assert.Single(run.Calls, c => c.Step == "phase1_implement");
        Assert.DoesNotContain(run.Calls, c => c.Step.StartsWith("revise_", StringComparison.Ordinal) || c.Step is "scope_head_read" or "phase4_merge" or "phase4_merge_pinned");
    }
}
