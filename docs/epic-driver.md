# Epic driver wake handoffs

`UweCarteBlancheWorkflow` reconciles an authorized epic, then waits for a wake.
A wake is a hint to read real state, never an approval, rejection or completion.
The seed creates missing definitions only; existing runs retain their loaded
snapshot. Saving a replacement definition is a separate operator action.

## Heartbeats and bounded repair

Working uses a 60-second floor plus a 30-minute wait (31 minutes); blocked and
needs-authority use the same floor plus a 60-minute wait (61 minutes).
Unchanged signal-woken ticks build a streak: floors are 60 seconds before three,
600 seconds at three through five, and 1800 seconds from six onward.
Timeout, first tick or a material change resets the streak. Payloads are filtered
only for reporting; no branch terminates the driver based on a payload.

A failed, incomplete, timed-out or unparseable tick is not progress. The first two
failures retry after 60 seconds. The third sets `needs-repair`, emits one notice,
then uses an 1800-second floor and a 30-minute wake wait. Both repair paths clear
the previous status, fingerprint, next action and ask, retaining `epic_seen`.
A failed completion-findings write is suppressed and never loses the full review
findings; overflow carries the per-reviewer verdicts and every blocker verbatim.
The next tick reconciles those findings before proposing completion again.

The outer cap is 336 iterations. Blocked/working heartbeats take approximately
14.5/7.5 days, excluding tick time. A saturated unchanged wake storm takes at least
6.9 days; a storm claiming material changes at least 5.6 hours. The upper bound
with maximum tick/notices/cooldowns/waits is about 29.4 days. History is bounded
at approximately 15,100 events; exhaustion is reported as a finding.

## Read-only driver state

`GET /api/epic-drivers/<driver-id>/state?namespace=fleet` describes the latest
run, grants for that exact run and linked design/PR/doc children running or closed
within 24 hours. It writes nothing, sends no signals and never calls GitHub.
The dashboard proxy refuses this endpoint case-insensitively; internal clients
call the orchestrator directly. `{{config.OrchestratorUrl}}` is bound from
`TemporalBridge:OrchestratorUrl`. A missing URL requires a full reconcile.

Linkage preserves the grant D4 rule: direct `WaiterWorkflowId`, or a linked parent
at most two levels up. The scan reads at most 500 candidates and returns at most
50 linked children, with eight concurrent reads, 10-second Temporal call bounds
and a 45-second overall deadline. `errors` make the fingerprint `none`:
`candidates_incomplete`, `candidates_page_failed`, `linkage_unknown:<id>`,
`children_truncated`, `deadline` and Temporal/database/outcome read errors.
A partial list means a missing child is unknown, not absent. Never complete,
fail or propose completion of its row from partial state. Dispatch for that row
requires a complete independent Running-workflow/open-PR duplicate check.

Replies, including errors, expire 30 seconds after their original `computedAt`.
A cache hit changes only `cached`; it cannot renew expiry, clear errors or change
the fingerprint. Caller cancellation aborts without caching a partial deadline
reply. First reads after restart can hit the deadline while populating
immutable per-run linkage/outcome caches.

Early exit additionally requires unchanged issue `updatedAt` and fingerprint,
no errors/truncation, no parked gate or terminal-failed child, and a previous
status other than `none` or `proposed`. Replay the previous status, action and ask
unchanged. A parked child requires full reconciliation on every unchanged tick.
`needs-authority` means all remaining work needs fresh authority, not progress.

Design approval requires a strict close payload containing only a positive
integer `IssueNumber`, matching the run attribute and any `ExistingIssueNumber`.
JSON-string and object payloads are supported. Sentinels, fences, extra fields,
unknown encodings and mismatches never approve. A Completed DesignToPr wrapper
has `no_result`; read its linked design outcome and actual PR merge state.
An approved design shows `invalid_result` → check the run's `IssueNumber`
attribute and its close payload. No attribute write is inferred from approval.

Dry-run grant checks preserve D1–D5 and fail closed at D6 with
`visibility_unknown`: S1 adds no repository-visibility source and cannot emit
`cto_decide`. The real decision path remains unchanged and reads visibility live.

## Watcher and manual wake

| key | default |
|---|---|
| `FLEET_EPIC_WATCHER_ENABLED` | `false` |
| `FLEET_EPIC_WATCHER_INTERVAL_SECONDS` | `300` (valid 60–3600) |

The watcher is orchestrator-only and reads startup options, not peer config.
It wakes eligible drivers on abnormal child closes, newly entered gates or grant
creation/revocation/expiry. Completed children already send their own wakes.
Eligibility comes only from the run's loaded definition/version and a stored
`epic_wake_wait` step, never the current seed. At most one wake is sent per driver
per scan, targeting the exact run with only `decision: wake` and `blockerRef`.
It never calls an agent, GitHub or chat. Lookback overlaps 60 seconds and caps at
one hour; failed scans keep the watermark and failed sends keep triggers retryable.
In-memory eligibility and trigger LRUs are each bounded at 4096 entries.
After restart the trigger LRU is empty, so an already parked child can cause one
initial gate-entered wake per driver; the one-wake-per-scan bound still applies.
Expiry can also wake a driver for an already revoked grant: it is a harmless
extra reconciliation hint, never renewed authority.

A manual wake uses the existing signal tool, for example these arguments:

```json
{
  "workflow_id": "<driver-id>",
  "signal_name": "blocker-resolved",
  "args": {
    "decision": "wake",
    "blockerRef": "manual-check"
  }
}
```

This is not a gate signal. The start tool optionally accepts `id_reuse_policy`:
`allow_duplicate` (default) or `reject_duplicate`. Unknown values start nothing;
charter scheduling and deterministic attempt IDs are not introduced in S1.

## Triage, verification and rollback

Driver not ticking: check loaded-definition eligibility, Phase, current floor/
wait, watcher flag/configuration, `EpicWatcher scan` errors, and the actual run
history. A missing scan for two intervals, persistent `needs-repair`, failed wakes
or more than 70 seconds from wake to tick require investigation. Heartbeats cover
issue edits, merges, CI and chat decisions that the watcher cannot observe.

Unit/source and ordinary CI evidence is not isolated-stack acceptance. The real
agent, watcher, dashboard proxy, signal timing, replay and install/upgrade rows
in the issue remain separately verified; an unrun row is never a pass.

Rollout needs approval at each step: merge after independent review; pin the exact
merge and deploy flag-off; save/version/hash the definition; enable the watcher
and recreate only the orchestrator; then run throwaway-epic acceptance. Neither
author nor configured CTO reviews their own artifact. No agent container is
restarted or reprovisioned. Check Telegram start time and agent count unchanged.

Rollback independently disables the watcher and recreates only the orchestrator,
restores the prior definition as a new version, or reverts code (no schema change).
Never restart Telegram for this feature, restore a DB, change grant authority or
migrate running workflows as an implicit consequence. A new driver run needs a
new grant. Charter admission/budgets and plan scheduling belong to later stages.

## Autonomous charter dispatch (stage 2)

With no active autonomous grant, retain the legacy dispatch rule. With one, read
its immutable scope and plan, call admission before every start, then complete
the existing independent duplicate-owner check. Error, partial visibility or
deny means start nothing and record the reason. Admission is not approval.

Process ready items in plan order only after all `after` dependencies are done.
Start at most two per tick; more ready work requests one existing self wake.
Use `<Type>-<repo-name>-<plan-id>-d<first8-driver-run-id>-a<n>` with reject_duplicate,
three attempts maximum. Retry only Failed or TimedOut. Canceled, Terminated,
Completed without verified done, unknown state and a third failure stop automatic
redispatch with attempt_canceled, attempt_terminated, attempt_completed_not_done,
attempt_status_unknown or attempts_exhausted. Write the exact reason and attempt
ids to the checklist, `human:charter:<reason>:<plan-id>` as next action, and one
human ask. Never reset a run or reuse an attempt id to evade adoption or budgets.

Pass WaiterWorkflowId to every child, AutonomyGrantId to Design/DesignToPr and
RequireAcceptance=true to PR/DesignToPr. A design is done only with the exact
approved result and matching issue; PR requires MERGED and non-null mergedAt;
DesignToPr needs both linked outcomes. A CREATE row records its returned issue,
not an issue inferred from prose. An in-flight/start is working; all remaining
human gates is blocked except doc-review; missing charter authority is
needs-authority. Parked eligible autonomous gates can emit cto_decide only after
the read-only grant checks succeed; the independent one-send procedure is unchanged.

State adds grant mode and permanent budgets to the fingerprint. Autonomous state
requests recheck stored preflight rather than reuse an unchecked snapshot;
malformed/missing evidence stays human_decide with eligibility unknown.
No new workflow steps, sleeps, search attributes or live definition changes are
introduced by these source seeds. [Charter rollout and rollback](epic-grants.md#autonomy-charters-stage-2-default-off)
remain separate human-approved operations; source tests do not establish deployment.
