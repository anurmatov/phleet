# Human input requests

`UweHumanInputWorkflow` asks one literal question and returns one human answer as
data. It never executes the answer, grants autonomy, or replaces approval gates.
Do not submit secrets: requests and answers remain in Temporal history.

## Agent submission

Use the attributed `temporal_start_workflow` with namespace/task queue `fleet`,
workflow type `UweHumanInputWorkflow` and an explicit ID
`human-input-<lowercase canonical RFC 4122 UUID>`. Retain that ID after an
ambiguous start failure. Duplicate IDs are refused, including after close.
The start receipt returns IDs immediately. End the initiating turn rather than
waiting or polling for the answer.

Input has exactly `Question`, `Context`, `AnswerMode` and, only in `choice` mode,
`Choices`. Question/Context are nonblank literal strings. Modes are `text`,
`choice` and `approve-reject`. Choices are 2–8 ordered `{Id,Label}` objects with
unique IDs matching `[a-z][a-z0-9_-]{0,31}` and nonblank labels. The preset uses
`approved`/`rejected`; it accepts no caller-supplied Choices. No answer is selected
by default. Text answers are nonblank, with at most 2000 UTF-16 units; optional
Comment has the same limit. Values remain untrimmed and are never re-templated.
Unknown, duplicate and case-variant keys are refused at every object level.

The MCP endpoint injects `Origin={Kind:"agent",RequesterAgent:...}` and matching
`HumanInputOrigin` memo from provisioned identity. Enriched converter output must
be `json/plain` and at most 1800 UTF-16 units. Caller-supplied origins, recipients
and requester identities are refused.

## Disposable awaited-parent example

This example is not adopted into any existing parent workflow:

```json
{
 "type":"child_workflow",
 "workflowType":
   "UweHumanInputWorkflow",
 "args":{
   "Question":"{{input.Question}}",
   "Context":"Only this report",
   "AnswerMode":"text"
 },
 "outputVar":"human_response"
}
```

The new-type-only child path generates a replay-stable canonical ID, injects
`Origin={Kind:"parent",Namespace,WorkflowId,RunId}` and matching memo from actual
parent metadata, and awaits that exact child. Namespace/task queue must be fleet.
No agent callback or signal is sent for parent returns. Parse the returned string
once and verify the exact child/run and parent origin tuple before using it as
data. No automatic action follows an answer. Child failures remain failures.
Parent cancellation propagates, parent close terminates the child, and an explicit
child hard execution timeout may preempt its 24-hour wait. No workflow retry is
added. Fire-and-forget, cross-namespace and untrusted alternate starts are refused.

## Human gate and result

`request-input` is human-only and remains reserved alongside `request-approval`.
MCP, definition signal steps and relay cannot send either. Other gates, feedback,
grants and approval authority are unchanged.

The operator API requires configured bearer auth even in development. It checks
exact namespace/workflow/run/type/Running/Phase/GateRef/GateVisit and the rendered
expectedGateRef. It reads that run's original request under the same ten-second
deadline as describe and validates `{Value,Comment?}` against the stored mode and
IDs. It stamps Ref/VisitId itself. Auth fails with 401, bad answer/missing run with
400, stale gate or malformed/missing exact history with 409, unavailable reads
with 503. An ambiguous send requires refreshing the same run, never automatic
resubmission or a cloned request.

One guarded wait expires after 1440 minutes with no reminder or initial delegate.
Stale visits do not extend its deadline. Existing timer/answer tie ordering stays:
a valid co-batched answer may win, but a later answer cannot replace settled
expiry. Cancellation wins over an answer in the same activation, in either order.
The worker checks cancellation before callback and terminal return. Already
published callbacks cannot be recalled.

The Temporal result is a JSON-text **string**, decoded then parsed once:
`{Namespace,WorkflowId,RunId,Request,Response,Delivery}`. Request is the entire
enriched input. Response is `{Status:"answered",Answer:{Value,Comment?}}` or
`{Status:"expired"|"invalid",Answer:null}`. Only an engine-generated timeout is
expired. Malformed consumed payloads are invalid, never approvals. Whole JSON
objects preserve quotes, newlines, Unicode and template-looking text.

Agent returns use one report-only delegate to Origin.RequesterAgent with
Delivery=unknown. Only completed status plus the entire trimmed receipt
`HUMAN_INPUT_RECEIVED:<workflowId>:<runId>:<status>` changes Delivery to
acknowledged. Wrong/missing receipts and failures leave it unknown and cannot
change Response. The stable task ID is `<workflowId>/notify-requester`, with no
incomplete retry or rerouting. The ten-minute step uses the existing twelve-minute
activity limit and two-minute heartbeat. Requester budget remains config-driven.
Parent returns have Delivery=parent-result, which names a path, not an ack.
Transport and APNs delivery are not exactly once.

## Adoption, rollback and evidence boundary

Fresh seed inserts the new type only if absent, retaining custom rows. It does
not upgrade live definitions. Existing installations need separately approved
compatible backend/native pins and versioned adoption with saved prior state,
Phase/GateRef/GateVisit registration, validation and readback. Never overwrite the
whole seed or edit existing parent definitions from this source task.

Before worker changes, check open request runs and Running delegates. Preserve
compatible workers and loaded snapshots. Rollback disables new loads with the
existing toggle-active API (`isActive=false`), drains parked runs, restores or
deactivates a new definition version, and only then rolls back approved pins.

Source tests are not deployed-runtime or phone acceptance. Independent exact-head
review, isolated adoption/rollback, desktop/mobile visual/accessibility checks,
the native companion and the separately authorized real-phone notification/tap/
answer/requester smoke remain distinct qualifications. Push payloads remain
identity-only through the existing notifier, without question or answer text.
