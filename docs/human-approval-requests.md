# Exact-request human approvals

`UweHumanApprovalRequestWorkflow` records a human decision on one literal Action
and Scope, then reports it to the attributed requester. It never executes the
approved action, grants autonomy, or replaces design, merge, document, advisory
or acceptance gates. Do not submit secrets: input and decisions remain in history.

## Submission

Use the existing attributed `temporal_start_workflow` endpoint with:

```json
{
  "workflow_type":
    "UweHumanApprovalRequestWorkflow",
  "workflow_id":
    "human-approval-123e4567-e89b-42d3-a456-426614174000",
  "namespace": "fleet",
  "task_queue": "fleet",
  "input": "{\"Action\":\"Read a report\",\"Scope\":\"Read only\"}"
}
```

Generate a new lowercase canonical RFC 4122 UUID for each new request and retain
it on ambiguous RPC failure. Never retry with a different ID to hide uncertainty.
Only Action and Scope are caller-owned, exact nonblank strings. Unknown,
duplicate and case-variant keys are refused. The MCP endpoint injects
RequesterAgent from its existing provisioned identity and writes ApprovalRequester
in the Temporal memo. Enriched input, measured with the client's payload
converter, must fit 1800 serialized characters. No trimming, summarizing or second
template expansion is applied. The workflow also verifies routing, ID, input and
memo before loading a definition or creating a gate. REST, child and schedule
starts without that server-owned memo fail admission. A privileged Temporal
administrator is outside this boundary.

The start receipt returns workflow/run IDs immediately. End the initiating turn;
do not wait or poll inside it. Duplicate IDs are rejected, including after close.
Dashboard restart refuses this type before terminating or starting anything.

## Human confirmation

The existing waiting list uses Phase=request-approval. Approve and Reject always
open the dashboard's confirmation modal, which loads the original enriched input
from the rendered run's history. Action and Scope are plain, wrapping text.
Missing, malformed or unavailable history blocks Send. Read-only dashboards cannot
send. The optional history `runId` query is passed to Temporal without a latest-run
fallback. The separate native companion must provide equivalent display and
matching notification-tap navigation before rollout.

The request-approval API requires the configured operator bearer even when legacy
unconfigured-auth development mode is in use. It describes the exact run, checks
workflow type, Running status, Phase, GateRef and GateVisit, compares the rendered
expectedGateRef, and stamps Ref/VisitId itself. Decision must be exactly approved
or rejected. Optional Comment is an unchanged string of at most 2000 characters.
Unknown, duplicate and case-variant payload fields are refused. Missing run is
400, stale/closed/missing gate or visit is 409, unavailable describe is 503.

`request-approval` is reserved in the shared CeoGateSignals list. MCP callers,
including the configured coordinator, definition signal steps and agent relay
messages cannot send it. Existing feedback and epic-grant exceptions are unchanged.

## Settlement and return

One bound, visit-guarded wait expires after 1440 minutes, with no reminders or
initial delegate. Stale marked payloads are discarded without extending its
original deadline. Existing timer/signal tie ordering is retained: a valid signal
co-batched with the timer may win; a later signal cannot change a settled result.
Malformed outcomes are invalid, never approved. GateRef and Phase are cleared by
required writes before the one report-only callback.

The native Temporal result is a **string**. Decode the Temporal payload, then
parse that string once as JSON. It contains Namespace, WorkflowId, RunId,
Request (the whole enriched input), Decision (approved/rejected/expired/invalid),
DecisionPayload (the accepted signal, timeout object, or empty object), and
Delivery (acknowledged/unknown). Whole JSON objects preserve literal quotes,
newlines, backslashes, Unicode and template-looking text.

The requester-only notify-requester delegate receives the same snapshot with
Delivery=unknown. Only completed status plus the entire trimmed response exactly
`APPROVAL_RECEIVED:<workflowId>:<runId>:<decision>` changes final Delivery to
acknowledged. Extra lines, a wrong receipt, failure or timeout leave it unknown.
The definition uses existing branch/extract/backreference matching, not a new
serializer or step. Callback output never becomes the decision. The stable task
ID is `<workflowId>/notify-requester`; no incomplete retry or rerouting is added.
A 10-minute step has a 12-minute activity StartToClose limit and two-minute
heartbeat timeout; requester budget still comes from AgentTimeoutSeconds.
Termination/cancellation history is authoritative; callback delivery is not
promised after either. APNs and callback delivery are not exactly once.

## Adoption and rollback

The public seed adds this definition only when its name is absent. It does not
upgrade existing/custom definitions. Separately approved adoption must save the
prior definition/hash, verify Phase/GateRef/GateVisit Keyword registration,
validate the candidate, write a new version and read it back. Do not replace the
whole seed or modify unrelated definitions. Deploy compatible backend and native
companion before adopting the definition. Before a worker deploy, check for open
request-approval runs and preserve their loaded snapshots.

Rollback first uses the existing definition toggle-active API with isActive=false
to prevent new definition loads/adoption. Parked runs keep their loaded snapshot
and must drain on a compatible worker. Deactivation can make new starts fail at
load; it does not terminate existing runs or provide a successful request receipt.
Restore/deactivate as a new definition version, then revert runtime/client pins
only after separately approved drain and rollback checks.

## Evidence boundary

Source tests cover admission, literal string results, approved/rejected/invalid
outcomes, expired results, stale visit rejection, duplicate IDs, recorded replay,
cancellation, callback receipt/failure handling, reserved signal denial and API binding/auth.
Component click-path tests cover both workflow-card decisions, blocking Send while
history loads or fails, and displaying literal content before a successful send.
Test-server histories batch a fired timer with approved/rejected/stale decisions
before an uncached worker resumes, preserve expiry after a late approval, and
cancel without callbacks with either cancellation/decision arrival order.
The source PR reports exact executed counts and remaining checks. Desktop/mobile
screenshot and accessibility qualification, isolated versioned adoption/rollback
and deployed-runtime checks
are not established by these source tests. Native simulator and real-phone
notification/display/tap/decision/requester smoke acceptance remain separate,
human-approved work. A backend merge alone is not feature completion.
