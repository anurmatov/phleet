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
  - That run completes only when the coordinator answered. If the broker or agent fails, it closes `Failed`.
  - `notify_cto` itself is unchanged: the flag defaults to off.
- A failure is `delivered` only after its notification run completed.
- A failed hand-over (`handover_failed`) or a failed notification run (`delivery_failed`) is an attempt. The failure goes back to `pending` and is retried on a later poll, and after `MaxDeliveryAttempts` it is `parked`. Parked records are kept for 30 days.
- The state file survives restarts whatever the failure's age; mount its directory (the example compose maps `${FLEET_BASE_DIR}/temporal-bridge`).
  - If the file is lost, failures still inside the lookback are rebuilt from the per-attempt notification ids without notifying again.
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

## Not covered

- No recovery automation, reservation, decision ledger or dashboard panel.
- Typed failure evidence in `Completed` results does not exist in this codebase, so `Completed` never notifies.
- A run that fails under a workflow-level retry policy notifies for each failed attempt.
- "Delivered" means the coordinator agent answered the directive. It does not mean the coordinator acted.
