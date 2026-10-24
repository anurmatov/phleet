# Workflow failure accountability

An orchestrator-side observer records every **proven failure-caused** workflow exit, hands it to
the configured coordinator through a durable pull-only inbox, and keeps it visible until the
coordinator (or an operator) has acknowledged it and recorded an explicit disposition. Specified in
[phleet #476](https://github.com/anurmatov/phleet/issues/476).

> **Status.** Source implementation only. The feature is **off by default**
> (`FLEET_WORKFLOW_OUTCOMES_ENABLED=false`). Merging the source is not deployment, and nothing here
> asserts a running deployment, a selected image or a namespace's retention. Operator-owned host
> qualification (AC25, `scripts/verify-workflow-outcomes.sh verify`) is **UNRUN** and needs its own
> approval. Operational procedures are in the [runbook](runbooks/workflow-outcomes.md).

## Scope: failures only

| In scope | Out of scope |
|---|---|
| Failed / TimedOut runs of every type: UWE and compiled, root, child (even consumed, abandoned or with a closed parent), fire-and-forget and scheduled, in every configured namespace | Normal completion, normal continue-as-new, cron/retry transitions, successful scheduled runs |
| Canceled / Terminated / aborted runs **with a validated causal link** to a failure | Deliberate human rejection, cancellation or hold; a still-Running typed hold or stop |
| Completed runs whose pinned classifier proves a business failure (failed task, automatic deadline, review cap, required artifact absent) | Completed with null / empty / unparseable output, `needs_human_review` or human rejection text alone |

Excluded runs produce **no** inbox item, receipt, acknowledgement or model turn — there is no
success inbox and no routine success report. Scanning all statuses is discovery bookkeeping, not a
success-reporting feature; non-failure metadata is minimal and expires (TTL ≤ namespace retention
plus one scan). Notification workflows (NotifyCto, MemoryStore, AuthTokenRefresh) follow the same
rules with no type exemption: a void or null result alone is never a failure.

The observer is a `BackgroundService` in the orchestrator, outside every Temporal worker. It does
not depend on a workflow's `finally` block or callback, which cannot see external termination or
worker loss. It adds no workflow command, signal or MCP tool, and it never starts, signals,
cancels or retries a workflow.

## Classification and evidence

Every observed run gets exactly one classification (`WorkflowOutcomeClassifications` in
`src/Fleet.Shared/WorkflowOutcomeContracts.cs`). Only `confirmed_failure` can enter the inbox.

| Exact evidence | Classification / reason code |
|---|---|
| `WorkflowExecutionFailed` / `WorkflowExecutionTimedOut` terminal event | `confirmed_failure` / `workflow_failed`, `workflow_timed_out` — regardless of a null result; typed failure, retry state, run/event ids and payload hash preserved |
| Canceled / Terminated after a proven failure: exact history of the failure or deadline branch causing closure, or an authenticated operator cause attestation referencing the exact run and terminal event | `confirmed_failure` / `failure_caused_cancel`, `failure_caused_terminate` |
| Authenticated decision or signal plus the controlling branch show a deliberate disposition | `intentional_nonfailure` / `deliberate_disposition` — a prior real failed run stays recorded separately |
| Abort cause absent or conflicting (a cancel has no cause; terminate free text and caller identity are not trusted) | `cause_unresolved` / `abort_cause_unknown`, `abort_cause_conflicting` — visible operator cause review, health incident after 300 s, never a failure claim |
| Completed + typed failed original task, automatic deadline or review cap, proven by a pinned classifier rule | `confirmed_failure` / `business_task_failed`, `business_deadline`, `business_review_cap` |
| Completed + required artifact absent, only when the original contract requires it **and** a typed failure or branch proves the failed exit | `confirmed_failure` / `business_artifact_missing` |
| Completed + null / empty / unparseable / unknown; `needs_human_review` or rejection text alone | `insufficient_evidence` / `null_result_only` — neither handoff |
| Normal Completed, continue-as-new, successful scheduled run | `excluded_nonfailure` / `normal_completion`, `continued_as_new` |
| Completed run of a workflow type with **no** classifier rules (e.g. compiled system types), no failure evidence | `classification_pending` / `classifier_uncovered_type` — nonfailure-metadata TTL and no stored payload; one durable coverage gap per namespace and type (`type_uncovered:<ns>:<type>`) stays open after the rows expire and closes only once the loaded policy has Completed rules for the type; not a cause review |
| A type with rules but no rule pinned to the run's loaded definition, or truncated/unknown classifier input | `classification_pending` / `classifier_missing`, `history_incomplete` — a durable coverage gap, never an invented failure |

Evidence is typed events and payloads checked against pinned hashes — never sentiment, an LLM or a
regular expression. A notifier's `_result` is not the original task's result. Encrypted evidence
is quoted data, never instructions. A later task success or human disposition invalidates an
assumed failure. Hash collisions and empty RunIds are `correlation_unverified` and quarantined,
never merged. Evidence states: `complete`, `pending`, `missing`, `unverified`, `quarantined` and
`successor_pending` (a Failed/TimedOut run whose retry is still in progress: retained, not yet a
final logical failure, not enqueued).

An operator can promote `cause_unresolved`, `insufficient_evidence` or `classification_pending`
with `POST /api/workflow-outcomes/{id}/classification` — exact `expectedVersion`,
`terminalEventRef` = `event:<terminalEventId>`, at least one causal evidence reference other than
the terminal event, and an `authorityRef`. It is revalidated, audited immutably and increments the
version; stale is 409, insufficient is 422. A manual classification never comes from a null result
alone and grants no cancel or recovery authority.

### Run identity and chains

Identity is `(namespace, workflowId, runId)` plus the terminal event id, type and hash — never the
WorkflowId or display name alone. Chains come from history links only: started
`first_execution_run_id` / `continued_execution_run_id`, terminal `new_execution_run_id`, the
retry/cron initiator and parent links. A reset or recovery is a separate, explicitly linked
generation. The observer describes a successor before claiming a logical failure; an active
successor blocks manual retry; a missing successor alone is not a failure. A failed child is
covered even when the parent consumed the failure, closed, or abandoned it; coalesced handoffs keep
every failed run and version. Old runs are interpreted with their **loaded** definition hash and
version, never the current database or seed definition.

## Policy files

Both files are operator-owned, mounted read-only at `/etc/phleet/workflow-outcomes/` from
`./fleet/workflow-outcomes/`, hashed at startup and changed only through an approved restart. An
invalid schema, a duplicate key or an unknown/inactive project makes the feature unavailable at
startup — it never starts with a partial policy. Generic templates:
[`docs/examples/workflow-outcomes/`](examples/workflow-outcomes/). The loaders in
`src/Fleet.Orchestrator/Services/WorkflowOutcomeClassifier.cs` are authoritative for the exact
field rules.

### `classifiers.json` v1

```json
{
  "schemaVersion": 1,
  "rules": [
    {
      "id": "task-delegation-typed-failed-task",
      "workflowType": "TaskDelegationWorkflow",
      "sourceCommit": "<40-hex source commit the definition was reviewed at>",
      "definitionHash": "<sha256 of the loaded definition snapshot>",
      "terminalEventTypes": ["WorkflowExecutionCompleted"],
      "evidence": { "kind": "delegate_result", "path": "/Status", "equals": "failed", "exitBranch": "delegate" },
      "classification": "confirmed_failure",
      "reasonCode": "business_task_failed"
    }
  ]
}
```

| Field | Rule |
|---|---|
| `id` | Unique across the file |
| `workflowType`, `sourceCommit`, `definitionHash` | Exact type, reviewed source and **loaded** definition hash; there is no catch-all type and no wildcard. A Completed rule **with** `definitionHash` covers only runs that loaded exactly that definition. A Completed rule **without** it is a compiled rule pinned by `workflowType` + `sourceCommit`: it covers only runs whose complete history proves no `LoadWorkflowDefinition` was scheduled (compiled workflows) **and** whose every `WorkflowTaskCompleted` event recorded exactly that commit as the worker build id. A hashed rule never covers a compiled run, a compiled rule never covers a run with a loaded definition, and an unreadable, unresolved or incomplete history is never treated as compiled |
| `terminalEventTypes` | Exact Temporal terminal event types the rule applies to |
| `evidence.kind` | `terminal_event`, `delegate_result` or `failure_branch` |
| `evidence.path` | RFC 6901 JSON pointer into the delegate result payload (`delegate_result` only) |
| `evidence.equals` | One scalar compared exactly at `path`; no expressions (`delegate_result` only) |
| `evidence.exitBranch` | Exact step name of the exit branch; a `delegate_result` is the scheduled `DelegateToAgent` task whose task id is `<workflowId>/<step.Name>` (`UniversalWorkflow.cs`) |
| `classification`, `reasonCode` | The stored result; business codes ride on `confirmed_failure` |

Failed/TimedOut need no per-type rule: the terminal event is the evidence for every type. A
Completed exit is a failure only when a rule matches the exact type, source and loaded hash and its
evidence is read from history. Memory-only workflow variables (for example the PR workflow's
`outer_done` / `terminal_decision`) are never read; an absent or ambiguous branch proof is a gap.
The public seed fixtures the example covers:

| Workflow type | Business-failure proof | Not a failure |
|---|---|---|
| `UwePrImplementationWorkflow` | `notify_consensus_exhausted` and `notify_outer_loop_exhausted` exit branches (review cap); the exact review-deadline branch of your pinned fixture (deadline) | Human rejection, `needs_human_review`, normal merge |
| `UweDesignWorkflow` | `emit_exhausted_error` delegate result `ERROR:consensus_exhausted` | Human `REJECTED`, `emit_ceo_needs_human_error` |
| `TaskDelegationWorkflow` | The original `delegate` step's typed `failed` result | The `notify-result` notifier output |
| `NotifyCtoWorkflow`, `MemoryStoreRequestWorkflow`, `AuthTokenRefreshWorkflow` | Only a source-proven unresolved caught failure at exit, as a compiled rule pinned by `sourceCommit` (no public rule ships) | Void, null or no-op results |

**Compiled-run source provenance.** The bridge stamps its image's source commit as the Temporal
worker build id (`FLEET_BUILD_COMMIT`, baked from `--build-arg GIT_COMMIT=<40-hex>` in
`Dockerfile.temporal`; worker versioning stays off, so the id is recorded, never routed on).
Temporal records it on every workflow task, so a compiled run's source is read from its own
history, not attested by an operator. A compiled run whose tasks recorded no build id, a value that
is not the rule's exact commit, or more than one build (a run spanning a bridge deploy) stays
`classification_pending` / `classifier_missing` under a `source_unverified:<ns>` coverage gap: never
matched, never a nonfailure, and its type's `type_uncovered` gap stays open. An image built without
the build arg logs a startup warning and every compiled run stays unverified. Rules for two commits
of one type may coexist (a rollout); one commit twice is a duplicate.

`setup.sh` and `upgrade.sh` pass the commit from `scripts/source-commit.sh`, which prints `HEAD`
only for a clean checkout (no modified, staged or untracked files; gitignored runtime files such as
`./fleet/` are fine) and `unknown` otherwise, with a warning. A manual Compose build reads
`FLEET_SOURCE_COMMIT` from the command line
(`FLEET_SOURCE_COMMIT=$(bash scripts/source-commit.sh) docker compose build fleet-temporal-bridge`)
and defaults to `unknown`; never set it in `.env`, where it would outlive the checkout it names.
The build id is read from the deprecated unversioned `WorkerVersion.BuildId` field. That was
verified on the Temporal dev server, not on every server version, so confirm it on your server
after rollout (runbook §4, compiled source unverified).

Pin `sourceCommit` (and, for UWE types, `definitionHash`) for every enrolled namespace **before** enrollment. The
example values are deliberately not hex, so an unpinned copy fails startup instead of claiming
coverage it does not have. Verify the JSON pointer casing against your own pinned fixture.

### `ownership.json` v1

```json
{
  "schemaVersion": 1,
  "defaultCoordinator": "coordinator",
  "repoProjects": [{ "namespace": "namespace-a", "repo": "owner/repo-a", "project": "project-a" }],
  "namespaceProjects": [{ "namespace": "namespace-c", "project": "project-c" }],
  "rules": [{ "namespace": "namespace-a", "project": "project-a", "coordinator": "coordinator" }]
}
```

- `repo` is a validated `owner/name`, trimmed and lowercased; `project` is the exact name of an
  active project context, never an inferred repository name.
- Project derivation: the decoded original Started `input.Repo` → the exact `namespace+repo` entry.
  A present but invalid or unmapped Repo is `project_unknown` with **no fallback**. Without a Repo:
  the verified parent run's derived project, else an explicit `namespaceProjects` entry. There is no
  implicit namespace = project, and nothing is inferred from the workflow type, agent assignment or
  `input.Project`. The project, policy hash and Started/parent evidence are recorded.
- Coordinator precedence: exact `namespace+project` rule → verified same-project parent's
  coordinator → `defaultCoordinator`. No priorities.
- Duplicate keys (`repoProjects`: namespace+repo, `namespaceProjects`: namespace, `rules`:
  namespace+project), an invalid schema or an inactive/unknown project fail startup.
- Missing or conflicting evidence is `project_unknown`: the item goes to the default coordinator as
  an opaque id with `accessNeeded`, and no access control is widened. A namespace never broadens
  private access.

The coordinator owns recovery; the original target still owns the task; the author, target and task
owner stay separate. `FLEET_CTO_AGENT` names the configured coordinator and is never hard-coded.
Missing, it makes intake 503 and leaves outcomes unassigned and escalated; the observer still
records them.

## Discovery, checkpoints and retention

| Stage | Behaviour and bounds |
|---|---|
| Enrollment | Records the namespace enrollment time, coordinator, policy hashes and DescribeNamespace retention/archival; inspects all Running and retained closed runs regardless of type or start age; enqueues confirmed failures only. A full sweep that cannot finish within 300 s is `capacity_degraded` until an explicit adjusted budget is set |
| Windows | Recent: UTC StartTime daily half-open partitions. Full sweep: StartTime below a fixed scan-start upper bound with **no lower bound** (retention counts from closure). All statuses, page size 100, opaque token; ids and token are committed atomically in MySQL |
| Pages | An empty page with a token continues. An error, repeated token or shutdown leaves the window incomplete; an invalid token restarts the same window with dedup and no cursor advance. Capped lists and approximate counts are never proof |
| Recheck | Recent 24 h windows every `RECENT_SCAN_SECONDS` (60), full retention sweep within `FULL_SCAN_SECONDS` (300); pass start/end/cursor/errors persisted. A failing namespace stays registered and reconnects (backoff 5–60 s); it is never cached out |
| Known runs | Describe nonterminal ids every `POLL_SECONDS` (30); on closure, read the exact-RunId raw history, all pages. Observations are monotonic; a material failure change increments the version and needs a new ack, a duplicate does not |
| Timing | Healthy known-run close detection ≤ 60 s, indexed new-run discovery ≤ 300 s after Visibility has it, confirmed-failure enqueue ≤ 30 s. A never-seen external run has no unconditional bound because Visibility is eventually consistent; queue and ack latency are measured separately |
| Gaps | Retention overrun or missing history records a durable namespace + interval gap with the known ids and an unknown-run count. Archival is read only where already configured and authorized. No fabricated failure and no pre-retention coverage claim |
| Resources | 4 concurrent reads, 10 s RPC timeout, history page size 100 with at most 200 pages per scheduling slice, resumable without truncation. Quota, parser or codec errors become visible pending or quarantined encrypted evidence, never omissions |

Temporal errors or an inaccessible namespace mark it `scan_pending` / degraded and leave the
checkpoint unchanged — an error is never read as "no runs". A MySQL error or a lost fenced lease
stops page advance, batch claims and recovery actions; committed rows survive a restart. A database
outage cannot guarantee delivery; on recovery the observer records the coverage gap and catches up
from the unchanged checkpoints.

## Handoff stages

| Stage | Durable meaning |
|---|---|
| `detected` / `stored` | The confirmed failure and its cause evidence are committed. A DB failure means no stored claim, an unchanged cursor and a health signal |
| `enqueued` | A batch and its immutable per-item `(outcomeId, version)` membership are committed — not delivery |
| `delivered` | The coordinator runtime fetched the batch, fsynced it to its local inbox and returned the scoped receipt. An HTTP fetch or a broker publish alone is not delivery |
| `acknowledged` | The coordinator returned the exact batch member ids and versions in its structured output, or an authenticated operator acknowledged them. Storage, heartbeats or completion text are not acknowledgement |
| `decision_made` | A per-run explicit disposition with owner, reason, evidence and, if unresolved, a review time. A batch ack never finishes its items |
| `unresolved` / `escalated` | Delivery failure, ambiguous receipt, unassigned owner, missing evidence, hold or unsafe recovery. Never expires into success and never leaves the attention counts |

Each item also reports `deliveryState` (`none`, `pending`, `delivered`, `acknowledged`, `unknown`)
and `turnAmbiguous`. An unacknowledged failure raises an attention flag and health escalation after
15 minutes — not a message to a human.

## Coordinator inbox protocol

**The coordinator runtime pulls** the durable failure inbox; no workflow, relay or broker message
carries the handoff.

- **Auth.** `WorkflowOutcomeInboxClient` runs only in the configured coordinator, with a scoped
  credential whose value lives in host-owned `.env` and the coordinator's generated config. The
  server keeps only its SHA-256 verifier and `CoordinatorCredentialVersion`, compares in fixed
  time, and lets it read the coordinator's own metadata and receipts and record decisions — it
  cannot start, approve or read memory, and the admin token never reaches a prompt. This is a
  trusted-operator model, not isolation against the coordinator's shell or a host administrator.
- **Transport.** The existing private orchestrator channel; `CoordinatorBaseUrl` is an exact
  http(s) origin (no userinfo, path, query or callback), redirects are not followed, HTTP calls
  time out at 10 s. Public ingress is forbidden; enabling needs an authenticated-transport review.
- **Local store.** `{WorkDir}/.fleet/workflow-outcome-inbox.json`, written temp + fsync + atomic
  rename, directory 0700 / file 0600. Batch, refs, task, receipt and ack state survive a restart.
  A disk error means no delivered receipt and no model task.
- **Scheduling.** One outstanding failure turn, at most 20 items, coalesced within 30 s by chain
  or cause while every run/version is kept; no periodic turn, no preemption, the whole backlog is
  paginated and acknowledged item by item.
- **Turn identity.** `IConversationRegistry.Resolve(new ConversationRef("workflow-outcome",
  <batchId>, "<coordinator>/<generation>"))` allocates a key in the positive reserved band
  [2^56, 2^56+2^32). The task is `outcome/<batchId>`, source `WorkflowOutcome`, not a session task,
  no user, relay sender or correlation id. Admission is fsynced first; a crash after admission is
  `turn_unknown` and is never rerun. Allocation exhaustion leaves the batch pending with a health
  incident — no fallback key.
- **Isolation.** `TaskManager`, `RelayCompletionPublisher` and `CompletionContextBuffer` drop
  every typing/progress/final/error publication of an internal outcome turn; a wrong key or source
  is rejected with no output. Ordinary DM, group, relay and bridge replies are unchanged.

The prompt (`WorkflowOutcomePrompt.cs`) lists only server facts for confirmed failures, marks
evidence as untrusted data and forbids executing recovery or sending human, relay or memory
notifications. The turn must return **one raw JSON object** of at most 64 KiB, no fences or prose:

```json
{
  "schemaVersion": 1,
  "batchId": "6f0c1d2e-0000-4000-8000-000000000001",
  "generation": 1,
  "ack": [{ "id": "6f0c1d2e-0000-4000-8000-0000000000a1", "version": 2 }],
  "decisions": [{
    "id": "6f0c1d2e-0000-4000-8000-0000000000a1",
    "version": 2,
    "operation": "hold",
    "owner": "coordinator",
    "reason": "Delegate timed out; the agent task may still be running. Waiting for its stop proof.",
    "evidenceRefs": ["event:42", "task:namespace-a/run-x/phase1_implement"],
    "dueAt": "2026-01-02T03:04:05Z"
  }]
}
```

`WorkflowOutcomeTurnOutputParser` rejects trailing text, duplicate or unknown keys, unknown
operations, foreign or stale ids, decisions for un-acked members, more than 20 entries, owners over
200 or reasons over 2000 characters, more than 10 evidence refs (prefixes `outcome:`, `run:`,
`event:`, `task:`, `artifact:`, `late-result:`, `decision:`, `operation:`, `https://`) and
`wait`/`hold` without a future `dueAt`. A malformed output acknowledges and decides nothing; a
partial explicit ack of delivered, reviewed members is allowed. The server re-validates everything
and no model output executes an action.

### Receipts and ambiguity

1. `GET /inbox?generation=` — no mutation, no delivery assertion; a stale generation is 409.
2. fsync locally, then `POST /batches/{id}/delivered` with nonce, generation and `itemVersionsHash`
   (SHA-256 over the ordinal-sorted `<id>:<version>\n` lines). A duplicate returns the same receipt;
   a stale generation or hash is 409.
3. A lost response is reconciled with `GET /batches/{id}/receipt?nonce=` before resending the same
   receipt — never a guessed delivered or acked state and never a second possibly active turn.
   404 = no receipt yet; 409 = superseded generation.
4. `POST /batches/{id}/ack` with the exact members (never a wildcard); stale or not-delivered is 409.
5. `POST /{id}/decisions` per item.

A coordinator that is offline, busy or misconfigured leaves the batch pending; the runtime catches
up from the server inbox after restart. Nothing expires unresolved items.

## Decisions

Nine operations, record-only (`201 {decisionId, status:"accepted"}`, stale 409, unsafe 422; rejected
attempts are audited too):

| Decision | Semantics |
|---|---|
| `wait` / `hold` | Owned unresolved decision with a reason and future `dueAt`; no automatic timeout, cancel or retry. A live run stays live; a closed run is not resumed. The review wake joins the same ledger |
| `resume` | Only the same still-running run with a verified task/session identity through a deployed supported recovery handler. A terminal run returns `closed_run_cannot_resume`; without the separate delegate-recovery capability it is blocked for a human decision |
| `retry` / `reassign` | A new attempt only after an authoritative terminal/run-chain check, all task owners stopped or safely tombstoned, late replies and artifacts inspected, authority verified and an operation reserved. Reassign names an eligible target and grants no tools or context. An active PR branch is never reused blindly |
| `accept_late` | Exact late ResultId/hash/status and artifact review recorded — not an approved artifact and never a forged Temporal completion. Injection into a live run only through a deployed, qualified delegate-recovery handler; otherwise durable accepted evidence with the remaining handoff explicit |
| `finish` / `close` | Closes the failure item with an explicit disposition: `recovered_with_existing_artifact` (needs existing acceptance proof), `accepted_failure` or deliberate closure. Parent/child links stay |
| `cancel` | Exact current run/task target under existing authority; a closed target is a recorded no-op. A cancel request is not stop proof: observe the actual Canceled state and task state before any replacement |

An acknowledgement is never an approval, a retry authority or an artifact acceptance.

Example decision record (operator or coordinator):

```json
{
  "expectedVersion": 2,
  "operation": "retry",
  "owner": "coordinator",
  "reason": "Run timed out after the agent task was confirmed stopped; no PR was opened.",
  "evidenceRefs": ["event:42", "task:namespace-a/run-x/phase1_implement", "artifact:owner/repo-a#pr-none"],
  "dueAt": null,
  "taskRef": "task:namespace-a/run-x/phase1_implement",
  "artifactRef": null,
  "batchId": "6f0c1d2e-0000-4000-8000-000000000001",
  "generation": 1
}
```

## Recovery (operator-mediated)

V1 recovery never executes from a decision. The coordinator records intent; a human operator with
the admin bearer runs the read-only `POST /{id}/recovery-plan`, then `POST
/recovery/{operationId}/reserve` with the stop proofs and authority reference; then the coordinator
uses an **existing, already granted** guarded tool (`temporal_start_workflow`,
`temporal_cancel_workflow`, `temporal_signal_workflow`) with the optional `recovery_operation_id`.
Without an operator or approval the item stays an owned hold with an exact human request. No admin
token is provisioned into the coordinator.

- Before a reserve, `WorkflowRecoveryService` reads the exact source and successor Describe and
  history, agent task and late results, and the issue/PR/head/branch/checks. An unavailable,
  ambiguous or degraded probe, or an absent delegate-recovery ledger, blocks retry/reassign/resume
  with the missing proof named. A timeout is not stop proof.
- The bridge calls `POST /recovery/{operationId}/authorize-dispatch`, which moves
  `reserved → dispatching` exactly once; a second call is 409 `already_dispatching`. The start id is
  `recovery-<OperationUUID>` with RejectDuplicate and the exact input/task-queue/type hash.
- An ambiguous start/signal/cancel is `dispatch_unknown`: the reservation is permanent, the same id
  and its history are read back before anything else, and it is never recycled. Recovery states:
  `reserved`, `dispatching`, `dispatched`, `dispatch_unknown`, `verified`, `blocked`.
- One reservation per version even under races; a late success or head change invalidates the
  proof and needs a renewed decision. A failed dispatch keeps the original PR/work/replacement links.
- **Safe same-PR recovery:** a replacement for a PR workflow continues the existing PR only after the
  stop-proof checklist in the runbook; it never opens a second PR or dispatches a second author or
  reviewer while the first may still write.
- A tool call without `recovery_operation_id` is byte-identical to today, with every gate unchanged.
  The legacy admin restart is not considered safe; the UI and client never use it and the runbook
  forbids it as a bypass.

## Late results

When the bridge's waiter for an agent reply is gone, it confirms a persistent, mandatory publish of
the original bytes and stable MessageId (or body SHA-256) to the durable private queue
`fleet.workflow-outcomes.late` **before** acknowledging the original — existing queue arguments are
unchanged. If that handoff is refused (quota, missing queue, unknown confirm) the same copy is
published, confirmed, to the durable `fleet.workflow-outcomes.hold` queue and the original is then
acknowledged, so late-result backpressure never occupies the relay consumer's prefetch and live
replies keep flowing. Only when the broker confirms neither copy is the reply held unacknowledged
and retried in the background (5–60 s); the relay prefetch with capture on is 65, so at most 64 such
held replies can be outstanding before relay deliveries stall (logged as an error). The consumer
drains the hold queue exactly like the late queue. `WorkflowOutcomeLateResultConsumer` (prefetch 1) stores it through `POST
/api/workflow-outcomes/late-results` (idempotent on namespace + taskId + resultSha256, committed
before 2xx; at most 256 KiB, larger is 413 with quarantine metadata). On API failure it publishes a
confirmed replacement with attempt 1/2/3 and a UTC due time 5/30/60 s later, then acknowledges the
previous; the consumer waits until the due time (at most 60 s; one delayed message can hold later
ones — an accepted head-of-line cost). After the third failure the message goes to
`fleet.workflow-outcomes.quarantine`, which has no consumer and raises a deduplicated health alarm.

The late, hold and quarantine queues are durable, non-exclusive, not auto-delete, `max-length-bytes` 268435456 with
`overflow=reject-publish`, no TTL and no main-queue DLX. A publish return, nack, unknown confirm or
quota error is never acknowledged: the consumer is cancelled, the broker keeps the message and it
reconnects after 5–60 s (an unknown confirm can duplicate; the stable id and hash dedupe). A broker
without confirm/queue support blocks capture activation instead of falling back to
acknowledge-and-drop. Matched tasks keep today's behaviour. With capture off
(`FLEET_WORKFLOW_OUTCOMES_LATE_CAPTURE_ENABLED=false`) the legacy orphan behaviour stays and one
explicit warning is logged.

## Authority limits

Notification and acknowledgement never authorize retry, approval, deploy, cancellation, financial
action or credential use. Existing human gates, epic grants, signal guards
(`CeoGateSignals.cs`, `EpicGrantService.cs`, `SignalRegistry.cs`), private ACLs, tool grants and
host/runtime authority are unchanged. Production or financial recovery is denied unless an existing
approval covers the exact action; a namespace is never authority. A required but unauthorized stop
becomes an explicit human escalation, never a planning side effect. The observer cannot fail a
workflow, and a DB outage delays handoff but never starts recovery. Infrastructure failures go to
the deduplicated health path — never to `notify_cto`, `MemoryStoreRequestWorkflow` or
`TaskDelegationWorkflow`, so the feature cannot recurse.

## API

All routes are under `/api/workflow-outcomes`, camelCase JSON. Every method, including GET, HEAD
and OPTIONS, is authorized **before** any database access: feature disabled or auth not configured
503, missing/invalid bearer 401, wrong owner or scope 403, no query-string identity and no dev
bypass. *operator* = the admin bearer `Orchestrator:AuthToken`; *coordinator* = the scoped token;
*bridge* = the bridge's existing admin bearer (bridge routes refuse the coordinator token).

| Route | Caller | Purpose |
|---|---|---|
| `GET /?namespace=&state=&cursor=&limit=` | operator | `failures` (default), `unresolved_cause`, `all_attention`; limit 1–100, keyset cursor, per-namespace errors and counts |
| `GET /{id}` (`?detail=payload` decrypts) | operator | Version, evidence refs, stages, owner, decisions, batches, observations |
| `POST /{id}/classification` | operator | Cause review (above) |
| `GET /coverage` | operator | Per-namespace state, retention, enrollment, passes, known pending, gaps |
| `GET /health`, `GET /health/public` | operator / anonymous | Incident detail; public is `{healthy}` only |
| `GET /inbox`, `POST /batches/{id}/delivered`, `GET /batches/{id}/receipt`, `POST /batches/{id}/ack` | coordinator (receipt/ack also operator) | Inbox protocol |
| `POST /{id}/decisions` | coordinator, operator | Record-only decision |
| `POST /late-results` | bridge | Late-result capture |
| `POST /{id}/recovery-plan`, `POST /recovery/{op}/reserve` | operator | Read-only plan; reservation |
| `GET /recovery/{op}` | operator, bridge | Operation readback |
| `POST /recovery/{op}/authorize-dispatch`, `POST /recovery/{op}/receipt` | bridge | Dispatch once; monotonic receipt (`unknown` → `dispatch_unknown`) |

The dashboard's Workflow Failures panel uses the operator routes through the existing dashboard
bearer: the six handoff stages, owner, due time, chain/parent links, artifact refs, coverage gaps
and a separate unresolved-cause review, with zero results under a degraded namespace shown as an
error rather than "all clear".

## Configuration

Startup-only: a change needs an approved orchestrator restart (and a coordinator reprovision for
the coordinator keys). Invalid values make the feature unavailable; nothing is clamped. Fresh
`./setup.sh` writes every key with the feature off and creates `./fleet/workflow-outcomes/` (0700,
empty); `./upgrade.sh` adds only missing keys and that directory, never changes a present value, a
volume or an assignment, and never generates a credential.

| `.env` key | Orchestrator option | Default | Notes |
|---|---|---|---|
| `FLEET_WORKFLOW_OUTCOMES_ENABLED` | `WorkflowOutcomes__Enabled` | `false` | Kill switch; off starts no observer, batcher or intake (routes 503) |
| `FLEET_WORKFLOW_OUTCOMES_NAMESPACES` | `…__Namespaces` | blank | Required CSV; missing/unreachable namespace is degraded, never a filter |
| `FLEET_WORKFLOW_OUTCOMES_POLL_SECONDS` | `…__PollSeconds` | `30` | 5–300; known-run Describe |
| `FLEET_WORKFLOW_OUTCOMES_RECENT_SCAN_SECONDS` | `…__RecentScanSeconds` | `60` | 10–3600 |
| `FLEET_WORKFLOW_OUTCOMES_FULL_SCAN_SECONDS` | `…__FullScanSeconds` | `300` | 60–86400; keep ≥ 2 sweeps inside retention |
| `FLEET_WORKFLOW_OUTCOMES_BATCH_SECONDS` | `…__BatchSeconds` | `30` | 5–30; failure coalescing only |
| `FLEET_WORKFLOW_OUTCOMES_BATCH_SIZE` | `…__BatchSize` | `20` | 1–20 |
| `FLEET_WORKFLOW_OUTCOMES_OWNERS_FILE` | `…__OwnersFile` | `/etc/phleet/workflow-outcomes/ownership.json` | Read-only, hashed |
| `FLEET_WORKFLOW_OUTCOMES_CLASSIFIERS_FILE` | `…__ClassifiersFile` | `/etc/phleet/workflow-outcomes/classifiers.json` | Read-only, hashed |
| `FLEET_WORKFLOW_OUTCOMES_KEYRING_DIR` | `…__KeyringDir` | `/var/lib/phleet/workflow-outcomes/keys` | Persisted 0700 data-protection keyring (`workflow_outcomes_keys` volume) |
| `FLEET_WORKFLOW_OUTCOMES_COORDINATOR_TOKEN_KEY` | `…__CoordinatorTokenKey` | `FLEET_WORKFLOW_OUTCOMES_COORDINATOR_TOKEN` | Name of the token variable; never the admin or config token |
| `FLEET_WORKFLOW_OUTCOMES_COORDINATOR_TOKEN` | (read by name) | blank | The scoped token; never logged or returned |
| `FLEET_WORKFLOW_OUTCOMES_COORDINATOR_CREDENTIAL_VERSION` | `…__CoordinatorCredentialVersion` | `1` | Increment on rotation; the old token gets 401 |
| `FLEET_WORKFLOW_OUTCOMES_COORDINATOR_GENERATION` | `…__CoordinatorGeneration` | `1` | Increment on an approved owner change; stale acks 409 |
| `FLEET_WORKFLOW_OUTCOMES_COORDINATOR_BASE_URL` | `…__CoordinatorBaseUrl` | blank | Exact private origin, e.g. `http://fleet-orchestrator:3600` in the reference stack |
| `FLEET_WORKFLOW_OUTCOMES_LATE_CAPTURE_ENABLED` | `TemporalBridge__WorkflowOutcomeLateCaptureEnabled` | `false` | Bridge; enable only after the intake is ready |
| `FLEET_CTO_AGENT` (existing) | — | — | The configured coordinator |

Fixed bounds (options, not in `.env`): 4 concurrent reads, 10 s RPC, page sizes 100, 200 history
pages per slice, unacked attention 15 min, cause-unresolved incident 300 s, resolved-failure
retention 90 days (changing it needs approval).

Compose wiring: the orchestrator alone mounts `./workflow-outcomes:/etc/phleet/workflow-outcomes:ro`
(a directory mount, because a missing single-file bind turns into a directory) and the named volume
`workflow_outcomes_keys` at `/var/lib/phleet/workflow-outcomes/keys`. No agent gets a keyring
mount. No new image, port, `EXPOSE`, Docker socket or package is added. The orchestrator image
creates the empty 0700 keyring mount point and both images fail their build if the outcome services
are not compiled in. The keyring volume is **not** under `./fleet/`: back it up separately.

Coordinator provisioning: when the feature is enabled and the token variable has a value,
provisioning writes a `WorkflowOutcomes` section (`Enabled`, `BaseUrl`, `ScopedToken`,
`Coordinator`, `CoordinatorGeneration`, `PollSeconds` 30, `InboxPath` under its own WorkDir) into
the generated config of the agent named by `FLEET_CTO_AGENT` only. Every other agent's generated
bytes are unchanged. Never attach the token as an agent Env Ref. Provisioning changes need an
approved reprovision; nothing grants tools automatically.

## Rollout and rollback

| Stage | Gate |
|---|---|
| Review | Independent reviewers (not the author or coordinator), then the existing human approval gate |
| Code verification | Exact-head isolated stack, named builds, MySQL migration and restore before merge; no bridge deploy during consensus or delegate turns |
| Off rollout | Pin the reviewed commit; deploy with the feature off; the additive migration `20261024050000_AddWorkflowOutcomeAccountability` runs and changes no existing table; record image/build ids, Running ids, retention/archival and a DB + keyring restore proof |
| Enrollment | Approved namespace manifest and credential; store-only catch-up classifies retained runs, enqueues proven failures and records gaps; no restart or definition edits |
| Delivery | Separate coordinator reprovision/enable approval and a scoped-auth synthetic batch; then failure batches. No approver DMs; a busy backlog is durable |
| Late capture | Enable the bridge flag after the intake is healthy |
| Recovery | Record-only; any action needs existing authority, stop proof, a handler and a guarded existing tool |

**Disable:** set `FLEET_WORKFLOW_OUTCOMES_ENABLED=false` (and the late-capture flag) and restart.
Polling and model admissions stop; the database, cursors, inbox, keyring and unacked backlog are
retained; coverage reports *disabled*. Nothing is marked finished and no active work is cancelled.

**Rollback:** disable intake, keep admitted state, roll back the coordinator client and the bridge,
then the orchestrator. The additive tables and the keyring stay. No workflow command changes. A
destructive migration Down or a restore over unresolved evidence needs explicit approval.

## Monitoring

Track separately: namespace pass/window/history age, known-run detection lag, measurable Visibility
lag, pending evidence and gaps (approximate counts are not coverage), and the
stored/enqueued/delivered/ack/decision timestamps. Alert on:

| Condition | Threshold |
|---|---|
| Missing coverage (namespace not healthy) | > 300 s |
| Confirmed failure stored but not enqueued | > 30 s |
| Failure not acknowledged | > 15 min |
| Cause unresolved | > 300 s |
| Retention margin (retention − full sweep) | < 2 full-scan intervals |
| Receipt or turn ambiguity (`deliveryState=unknown`, `turnAmbiguous`) | any |
| Blocked recovery or `dispatch_unknown` | any |
| Keyring / decryption errors | any |
| Late-result quarantine depth | > 0 |

Health incidents are deduplicated by component and outage in `workflow_outcome_health`. Use your
existing Prometheus/Alertmanager or operator dashboard path, never a Temporal notification workflow
or a default approver DM. **This source revision emits no Prometheus metrics**: the rules in
[`alerts.yml`](examples/workflow-outcomes/alerts.yml) use placeholder metric names for an exporter
the operator wires to `GET /coverage` and `GET /health`; only the blackbox probe of
`GET /health/public` works with standard tooling as shipped. Logs and metrics carry ids, hashes,
states, reasons, counts and latencies only — no bodies, tokens, private artifacts or full URLs.

## Confidentiality

Real routing, namespace maps, rollout files and operational evidence are operator-owned and never
committed; public examples use `coordinator`, `agent1`, `namespace-a`, `owner/repo` placeholders.
Coordinator inbox access is distinct from broad human access: default summaries are minimal,
per-record scope is enforced through the existing project access and the namespace/project
mapping, and an item outside the coordinator's scope is an opaque id with `accessNeeded`. Raw
payloads are encrypted at rest and decrypted only for an explicit authorized detail request. No
memory, project, journal ACL or human routing changes.

## Tests and acceptance

```bash
dotnet test tests/Fleet.Orchestrator.Tests --filter WorkflowOutcome   # needs FLEET_ORCHESTRATOR_MIGRATION_CONNECTION (migration tests fail without MySQL, never skip)
dotnet test tests/Fleet.Agent.Tests --filter WorkflowOutcome
dotnet test tests/Fleet.Temporal.Tests --filter WorkflowOutcome
npm --prefix src/fleet-dashboard test && npm --prefix src/fleet-dashboard run build
bash tests/integration/workflow-outcomes/run.sh <suite>|all [--manifest <file>]
```

`run.sh` exits 0 PASS, 1 FAIL, 2 INVALID, 3 BLOCKED. A suite runs the tests tagged
`[Trait("WorkflowOutcomeSuite", "<suite>")]` in its mapped projects
(`tests/integration/workflow-outcomes/fixtures.json`) and fails when zero tests ran, any failed or
any skipped; a missing prerequisite (for example `FLEET_ORCHESTRATOR_MIGRATION_CONNECTION` for
`migration`) is BLOCKED, never PASS. It writes TRX files and a JSON receipt with the git HEAD.

| Suite | AC | Suite | AC | Suite | AC |
|---|---|---|---|---|---|
| `terminal-matrix` | AC2 | `receipts` | AC10 | `late-results` | AC17 |
| `business` | AC3 | `coordinator` | AC11 | `ownership` | AC18 |
| `kill` | AC4 | `recursion` | AC12 | `migration` | AC19 |
| `chains` | AC5 | `privacy-auth` | AC13 | `timing` | AC20 |
| `pagination` | AC6 | `decisions` | AC14 | `failure-matrix` | AC21 |
| `ordering` | AC7 | `recovery` | AC15 | `replay` | AC22 |
| `catchup` | AC8 | `dispatch` | AC16 | `dashboard` | AC23 |
| `retention` | AC9 | | | `public-scrub` | AC24 |

`public-scrub` scans the added lines of `git diff origin/main...HEAD` for generic private-data
shapes (private and CGNAT IPv4 literals, long chat-like ids, email addresses, non-public repository
references, private hostnames, home paths, token shapes, deployment policy or `.env` files) plus an
optional operator denylist (`WORKFLOW_OUTCOMES_SCRUB_DENYLIST`, never committed); it does not
replace the independent manual scrub AC24 also requires. Dashboard screenshot, snapshot and
bounding-rect evidence (AC23) is collected separately and must be synthetic.

AC25 — production-host qualification with throwaway namespaces and a coordinator clone — runs only
through `scripts/verify-workflow-outcomes.sh verify --manifest <approved-manifest> --approval
<https-url>` after its own approval. It is UNRUN.

## Out of scope

Successful-outcome receipts or batching; reclassifying deliberate human decisions; the separate
delegate-recovery capability (its ledger, probes and handlers must be verified as deployed before
`resume`, `reassign` or `accept_late` injection, otherwise those stay blocked holds); the separate
human-gate DM outbox ([#345](https://github.com/anurmatov/phleet/issues/345)), which this feature
neither needs nor reuses; reconstruction of inaccessible private host logs; deployment provenance;
any financial, host, credential or grant change; and redesign of the agent task ledger, legacy
admin APIs, workflow business logic, memory ACLs or human notifications.
