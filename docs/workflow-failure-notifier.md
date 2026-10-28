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

## How it works

- Every poll interval, the bridge lists `Failed`/`TimedOut` runs closed within the lookback window, in each namespace in `TemporalBridge:Namespaces`. This runs from outside the failed workflow, so the failed run needs no final activity.
- Each failure is handed to the existing `NotifyCtoWorkflow` (the same path `notify_cto` uses) under the deterministic id `workflow-failure-<namespace>-<runId>`.
- Temporal's duplicate-id rejection is the persisted "already notified" record. A repeat poll or a bridge restart does not notify twice, as long as the lookback stays below the notification namespace's retention.
- If the hand-over fails, the failure stays pending with its attempt count and is retried on later polls, up to `MaxDeliveryAttempts`. After that it is parked as an error until the next restart. `GET /workflow-failures` on the bridge (port 3001) lists pending and parked identities with attempt counts and error type names.
- "Sent" means handed to `NotifyCtoWorkflow`. Delivery to the agent keeps that workflow's existing semantics: one attempt, with failures logged and ignored. There is no exactly-once claim.

## What the notice contains

Namespace, workflow id, run id, workflow type, close status, close time, a `temporal://` reference, and a cause category built only from type names (for example `explicit_fail`, `application_error:<ErrorType>`, `activity_failure:<ActivityType>/<ErrorType>`, `timeout:<TimeoutType>`).
The failure message, inputs, results and logs are never copied. The coordinator reads them through its existing Temporal tools and their access rules.

## Configuration

| setting | default | meaning |
|---|---|---|
| `FLEET_WORKFLOW_FAILURE_NOTIFIER_ENABLED` → `WorkflowFailureNotifier__Enabled` | `false` | off: nothing is polled or sent |
| `WorkflowFailureNotifier__CoordinatorAgent` | empty | recipient; empty means `FLEET_CTO_AGENT` |
| `WorkflowFailureNotifier__PollIntervalSeconds` | `60` | poll interval (minimum 5) |
| `WorkflowFailureNotifier__LookbackHours` | `6` | what a poll, and a restart, re-examines |
| `WorkflowFailureNotifier__MaxDeliveryAttempts` | `5` | hand-over attempts before a failure is parked |

With no recipient configured, failures are held, visible as pending with zero attempts, and no recipient is guessed.
Turning it on is an operator decision: set the key in `.env` and recreate `fleet-temporal-bridge`. On first enable, failures from the last `LookbackHours` are notified, at most 10 per poll.
To roll back, set it to `false` and recreate the bridge; nothing else changes.

## Not covered

- No recovery automation, reservation, decision ledger or dashboard panel.
- Typed failure evidence in `Completed` results does not exist in this codebase, so `Completed` never notifies.
- A run that fails with a workflow-level retry policy notifies for each failed attempt.
