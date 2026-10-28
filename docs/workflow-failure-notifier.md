# Workflow failure notifier

The temporal bridge can tell the configured coordinator when a workflow closes because of failure (#488).
The coordinator then decides what to do with its existing tools. The notifier never restarts, resumes or reserves anything.

## What counts as a failure

| run closes as | notified |
|---|---|
| `Failed`, including a UWE `fail` step (`ExplicitFail`, labelled an explicit business failure) | yes |
| `TimedOut` | yes |
| `Completed`, with or without a result (null included) | no |
| `Canceled`, `Terminated`, continue-as-new | no |

A `Completed` run is never inferred to be a failure from its result. Deliberate human rejections and holds that end a run normally are therefore excluded.

## Delivery and state

- Every poll interval, the bridge lists `Failed`/`TimedOut` runs closed within the lookback window, in each namespace in `TemporalBridge:Namespaces`. This runs from outside the failed workflow.
- Each failure becomes a record in the state file: `pending`, `in_flight`, `delivered` or `parked`, with an attempt count and the last error category.
- An attempt starts the existing `NotifyCtoWorkflow` (the `notify_cto` path) under `workflow-failure-<namespace>-<runId>-<attempt>`, with `ReportDeliveryFailure` on.
  - That run completes only when the coordinator's answer comes back `completed`. A broker or agent error, or a returned `failed`, `incomplete` or `idle` result, closes it `Failed` with a fixed error that copies none of the result text.
  - `notify_cto` itself is unchanged: the flag defaults to off.
- A failure is `delivered` only after its notification run completed.
- A failed hand-over (`handover_failed`) or a failed notification run (`delivery_failed`) is an attempt. The failure goes back to `pending` and is retried on a later poll, and after `MaxDeliveryAttempts` it is `parked`. Parked records are kept for 30 days.
- The state file survives restarts whatever the failure's age; mount its directory (the example compose maps `${FLEET_BASE_DIR}/temporal-bridge`).
  - If the file is missing, failures still inside the lookback are rebuilt from the per-attempt notification ids without notifying again.
  - If the file exists but cannot be read or holds an invalid record, the notifier holds: it does no discovery, sends nothing and writes nothing. The file is left untouched and the status reports `stateUnreadable: true`. Recover explicitly: repair or restore the file, or move it aside to start fresh. The notifier resumes on its next poll.
  - If the file cannot be written, state stays in memory and the status reports `durable: false`.
- `GET /workflow-failures` on the bridge (port 3001) returns the counts and the pending, in-flight and parked records: identity, state, attempts and error category.

## What the notice contains

Namespace, workflow id, run id, workflow type, close status, close time, a `temporal://` reference, and one fixed cause category:
`explicit_fail`, `refused_signal`, `application_error`, `activity_failure`, `activity_timeout`, `child_workflow_failure`, `workflow_timeout`, `canceled`, `terminated` or `unknown`.
Failure messages, caller-supplied error types, inputs, results and logs are never copied into a notice, the state file or the logs. The coordinator reads details through its existing Temporal tools and their access rules.

## Configuration

| setting | default | meaning |
|---|---|---|
| `FLEET_WORKFLOW_FAILURE_NOTIFIER_ENABLED` → `WorkflowFailureNotifier__Enabled` | `false` | off: nothing is polled, sent or written |
| `WorkflowFailureNotifier__CoordinatorAgent` | empty | recipient; empty means `FLEET_CTO_AGENT` |
| `WorkflowFailureNotifier__StateFile` | `/var/lib/fleet-temporal-bridge/workflow-failures.json` | durable record; empty keeps it in memory |
| `WorkflowFailureNotifier__PollIntervalSeconds` | `60` | poll interval (minimum 5) |
| `WorkflowFailureNotifier__LookbackHours` | `6` | how far back a poll looks for new failures |
| `WorkflowFailureNotifier__MaxDeliveryAttempts` | `5` | attempts before a failure is parked |

With no recipient configured, failures are held as `pending` with zero attempts, and no recipient is guessed.
Turning it on is an operator decision: set the key in `.env` and recreate `fleet-temporal-bridge`. On first enable, failures from the last `LookbackHours` are notified, at most 10 starts per poll.
To roll back, set it to `false` and recreate the bridge. The state file can stay.

## Real-broker acceptance (operator-run)

`WorkflowFailureNotifierRealBrokerTests` is the only check that uses real AMQP end to end, and it is opt-in. Without `FLEET_TEST_RABBITMQ_HOST` it is reported as skipped, with an `UNRUN` reason. It exercises:
- a real local Temporal server (downloaded by the test SDK);
- the production source and sender, `NotifyCtoWorkflow`, `DelegateToAgentActivity` on a real `ConnectionFactory`, and `TemporalRelayListener`;
- a synthetic coordinator that answers over AMQP.

Every first attempt hits an unreachable endpoint (closed port, 3 s connect timeout) and its run closes `Failed`. Attempt 2 then goes through the real broker and is delivered. A restarted notifier sends nothing, and no synthetic secret reaches the coordinator.

It consumes the reply queue `fleet.agent.temporal-bridge`, so run it only through the owned recipe. It never points at a broker a real bridge uses:

```bash
bash scripts/run-workflow-failure-real-broker.sh <reviewed-commit-sha>
```

The recipe:
- takes the full reviewed commit SHA and refuses (exit 2) if that commit is not local or the recipe being run differs from that commit's copy;
- feeds the SDK container only `git archive <sha>`, the commit's tracked files, so ignored files such as `.env`, untracked files and uncommitted edits never reach it; it prints the commit and tree it used;
- creates its own Docker network, RabbitMQ container (data on tmpfs, so no volume) and named SDK container, with no host port and no bind mount, and fails fast if any cannot be created;
- waits at most `READY_SECONDS` (default 90) for the broker, and bounds the SDK container run with `TEST_SECONDS` (default 1800, exit 124);
- removes each container and the network by the ID it recorded, plus its temp directory, then lists them to confirm they are gone;
- exits with the test's status, with `RESULT: PASS` or `RESULT: FAIL (exit N)`. A skipped or unmatched test is a failure (exit 3). A resource that is left or cannot be confirmed gone prints `CLEANUP: UNRESOLVED …`; it turns a passing run into exit 4 and never replaces an earlier failure status.

The SDK container needs internet egress for NuGet restore and the Temporal dev-server download.
`FLEET_TEST_RABBITMQ_UNREACHABLE_PORT` (default `1`) sets the closed port the first attempts use.
`scripts/test-run-workflow-failure-real-broker.sh` checks the recipe's input and cleanup handling with a stub `docker` and a throwaway Git repo.

## Not covered

- No recovery automation, reservation, decision ledger or dashboard panel.
- Typed failure evidence in `Completed` results does not exist in this codebase, so `Completed` never notifies.
- A run that fails under a workflow-level retry policy notifies for each failed attempt.
- "Delivered" means the coordinator agent answered the directive. It does not mean the coordinator acted.
