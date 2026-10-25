namespace Fleet.WorkflowOutcomeQualification;

public sealed record ProcessScenarioObservation(
    string Scenario,
    int InboxItems,
    int ModelTurns,
    int Fsyncs,
    int Acks,
    int Holds,
    int HumanEmissions,
    int BrokerConfirms,
    bool WrongTokenDenied,
    bool WrongOwnerDenied,
    bool DisabledDenied,
    bool CrashAfterAdmission,
    bool SecondTurn,
    bool LostPostReconciled,
    bool LateConfirmBeforeAck,
    bool OutageObserved,
    bool HandoffAccepted,
    bool DatabaseRowsPreserved,
    bool KeyringRestored,
    string? BatchId,
    string? ItemId,
    int? Version,
    long? Generation,
    string? TurnOutputSha256,
    string? Nonce,
    string? ProcessAncestry);

public sealed record ProcessScenarioResult(
    string Scenario,
    string Status,
    IReadOnlyList<string> Failures,
    string? Reason);

public static class ScenarioAssertionRunner
{
    public const string Pass = "pass";
    public const string Fail = "fail";
    public const string Blocked = "blocked";
    public const string Unrun = "unrun";

    public static IReadOnlyList<ProcessScenarioResult> Evaluate(
        ProcessQualificationManifest manifest,
        IReadOnlyList<ProcessScenarioObservation> observations)
    {
        var results = new List<ProcessScenarioResult>();
        var byScenario = observations
            .GroupBy(item => item.Scenario, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);

        foreach (var scenario in manifest.Scenarios)
        {
            if (!byScenario.TryGetValue(scenario, out var candidates) || candidates.Count == 0)
            {
                results.Add(new ProcessScenarioResult(scenario, Blocked, [], "observation_not_run"));
                continue;
            }
            if (candidates.Count > 1)
            {
                results.Add(new ProcessScenarioResult(scenario, Fail, ["duplicate_observation"], null));
                continue;
            }
            var failures = Assert(scenario, candidates[0]).ToList();
            MatchIdentity(manifest, candidates[0], failures);
            results.Add(new ProcessScenarioResult(
                scenario, failures.Count == 0 ? Pass : Fail, failures, null));
        }

        foreach (var oracle in QualificationManifestParser.RuntimeOracleScenarios)
        {
            // Declaring an oracle authorized is not an executed collector or an operational grant.
            results.Add(new ProcessScenarioResult(oracle, Unrun, [], "separate_runtime_authorization_required"));
        }
        return results;
    }

    private static void MatchIdentity(ProcessQualificationManifest manifest, ProcessScenarioObservation observation, List<string> failures)
    {
        if (observation.Scenario is "success-zero-turn" or "denials") return;
        var target = manifest.Runtime;
        if (observation.BatchId != target.ScenarioBatchId.ToString("D")) failures.Add("batch_id_mismatch");
        if (observation.ItemId != target.ScenarioItemId.ToString("D")) failures.Add("item_id_mismatch");
        if (observation.Version != target.ScenarioVersion) failures.Add("version_mismatch");
        if (observation.Generation != target.ScenarioGeneration) failures.Add("generation_mismatch");
        if (observation.TurnOutputSha256 != target.ExpectedTurnOutputSha256) failures.Add("turn_output_mismatch");
        if (observation.Nonce != target.ScenarioNonce) failures.Add("nonce_mismatch");
    }

    private static IReadOnlyList<string> Assert(string scenario, ProcessScenarioObservation observation)
    {
        var failures = new List<string>();
        if (observation.HumanEmissions != 0) failures.Add("human_emissions_not_zero");
        Nonnegative(observation, failures);

        switch (scenario)
        {
            case "success-zero-turn":
                Exact(observation, failures, inboxItems: 0, modelTurns: 0, fsyncs: 0, acks: 0, holds: 0,
                    brokerConfirms: 0);
                break;
            case "failure-native-turn":
                Exact(observation, failures, inboxItems: 1, modelTurns: 1, fsyncs: 1, acks: 1, holds: 1,
                    brokerConfirms: 0);
                Linked(observation, failures);
                Nonempty(observation.Nonce, "nonce", failures);
                Nonempty(observation.ProcessAncestry, "process_ancestry", failures);
                if (observation.SecondTurn) failures.Add("second_turn_not_refused");
                break;
            case "denials":
                Exact(observation, failures, inboxItems: 0, modelTurns: 0, fsyncs: 0, acks: 0, holds: 0,
                    brokerConfirms: 0);
                if (!observation.WrongTokenDenied) failures.Add("wrong_token_not_denied");
                if (!observation.WrongOwnerDenied) failures.Add("wrong_owner_not_denied");
                if (!observation.DisabledDenied) failures.Add("disabled_not_denied");
                break;
            case "crash-after-admission":
                Exact(observation, failures, inboxItems: 1, modelTurns: 1, fsyncs: 1, acks: 1, holds: 1,
                    brokerConfirms: 0);
                Linked(observation, failures);
                if (!observation.CrashAfterAdmission) failures.Add("crash_not_observed");
                if (observation.SecondTurn) failures.Add("second_turn_after_crash");
                break;
            case "lost-post-reconciliation":
                Exact(observation, failures, inboxItems: 1, modelTurns: 1, fsyncs: 1, acks: 1, holds: 1,
                    brokerConfirms: 0);
                Linked(observation, failures);
                if (!observation.LostPostReconciled) failures.Add("lost_post_not_reconciled");
                if (observation.SecondTurn) failures.Add("second_turn_after_lost_post");
                break;
            case "no-human-emissions":
                Exact(observation, failures, inboxItems: 1, modelTurns: 1, fsyncs: 1, acks: 1, holds: 1,
                    brokerConfirms: 0);
                break;
            case "late-confirm-before-ack":
                Exact(observation, failures, inboxItems: 1, modelTurns: 1, fsyncs: 1, acks: 1, holds: 1,
                    brokerConfirms: 1);
                Linked(observation, failures);
                if (!observation.LateConfirmBeforeAck) failures.Add("late_confirm_not_before_ack");
                break;
            case "outage-handoff":
                Exact(observation, failures, inboxItems: 1, modelTurns: 1, fsyncs: 1, acks: 1, holds: 1,
                    brokerConfirms: 1);
                Linked(observation, failures);
                if (!observation.OutageObserved) failures.Add("outage_not_observed");
                if (!observation.HandoffAccepted) failures.Add("handoff_not_accepted");
                break;
            case "db-keyring-restore":
                Exact(observation, failures, inboxItems: 1, modelTurns: 1, fsyncs: 1, acks: 1, holds: 1,
                    brokerConfirms: 0);
                Linked(observation, failures);
                if (!observation.DatabaseRowsPreserved) failures.Add("database_rows_not_preserved");
                if (!observation.KeyringRestored) failures.Add("keyring_not_restored");
                break;
            default:
                failures.Add("unknown_scenario");
                break;
        }
        return failures;
    }

    private static void Exact(
        ProcessScenarioObservation observation, List<string> failures,
        int inboxItems, int modelTurns, int fsyncs, int acks, int holds, int brokerConfirms)
    {
        if (observation.InboxItems != inboxItems) failures.Add("inbox_items=" + observation.InboxItems);
        if (observation.ModelTurns != modelTurns) failures.Add("model_turns=" + observation.ModelTurns);
        if (observation.Fsyncs != fsyncs) failures.Add("fsyncs=" + observation.Fsyncs);
        if (observation.Acks != acks) failures.Add("acks=" + observation.Acks);
        if (observation.Holds != holds) failures.Add("holds=" + observation.Holds);
        if (observation.BrokerConfirms != brokerConfirms) failures.Add("broker_confirms=" + observation.BrokerConfirms);
    }

    private static void Linked(ProcessScenarioObservation observation, List<string> failures)
    {
        Nonempty(observation.BatchId, "batch_id", failures);
        Nonempty(observation.ItemId, "item_id", failures);
        Nonempty(observation.TurnOutputSha256, "turn_output_sha256", failures);
        if (observation.Version is not > 0) failures.Add("item_version_missing");
        if (observation.Generation is not > 0) failures.Add("generation_missing");
    }

    private static void Nonempty(string? value, string name, List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(value)) failures.Add(name + "_missing");
    }

    private static void Nonnegative(ProcessScenarioObservation observation, List<string> failures)
    {
        if (observation.InboxItems < 0 || observation.ModelTurns < 0 || observation.Fsyncs < 0
            || observation.Acks < 0 || observation.Holds < 0 || observation.HumanEmissions < 0
            || observation.BrokerConfirms < 0)
            failures.Add("negative_count");
    }
}
