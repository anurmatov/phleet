# Workflow event values

Read a complete stored field only when someone explicitly requests it:

```text
GET /api/workflows/{ns}/event-value/{workflowId}
  ?runId={runId}&eventId={eventId}&field=input
```

The route's `{workflowId}` is a catch-all workflow ID, including slashes; the query's
`eventId` is a positive signed 64-bit integer. `field` is exactly `input`, `output`
or `failure`. Each required query parameter must occur once and be nonblank.
Encode IDs as URL components once. IDs are not trimmed or decoded a second time.
The namespace must match a configured name ordinally, including case.
An exact `runId` is mandatory: this read never chooses the latest run or follows
continue-as-new. Same workflow/event IDs in other runs or namespaces are unrelated.

## Response

HTTP 200 returns these camelCase keys, including nulls:

```json
{
  "namespace": "example",
  "workflowId": "workflow/id",
  "runId": "run-one",
  "eventId": 42,
  "field": "input",
  "sourceEventId": 42,
  "status": "complete",
  "values": [
    {"encoding":"json/plain","text":"\"hello\\nworld\""}
  ],
  "reason": null,
  "limitBytes": 1048576
}
```

`eventId` and `sourceEventId` are JSON int64 numbers, not strings. Each `values`
item corresponds to one original payload, in its original order. `text` is the
exact strict UTF-8 decoding of stored bytes. No JSON parsing, unquoting, pretty
printing, Unicode normalization, newline joining or truncation takes place.
The example's text remains a JSON string literal with quotes and escapes.
Malformed JSON with valid UTF-8 is complete raw text too.

| status | values and reason |
|---|---|
| `complete` | Nonempty array, `reason:null`, source event ID present; empty text is still a value |
| `absent` | Empty array, `no_value`, source ID null |
| `unsupported` | Empty array, `encoding`, `invalid_utf8`, `event_type` or `encoded_failure`; source ID null |
| `too_large` | Empty array, `value_size`, source ID null |

Only exact encoding names `json/plain`, `json/protobuf` and `text/plain` are
supported. Missing, unknown, binary, compressed or encrypted encodings are not
guessed. One unsupported payload rejects the entire field, not just that item.
The selected payload data total, or UTF-8 failure-message size, must be at most
1048576 bytes; equality is accepted. **Size takes precedence over encoding and
UTF-8 classification**, including mixed arrays and oversized encoded failures'
message text. Size is checked before decoding, and no prefix is returned.
Within the limit, payloads are checked in stored order; the first unsupported
item supplies the reason. Failure-attribute bytes are not exported or counted
as message text.

## Field mapping

| event | input | output | failure |
|---|---|---|---|
| WorkflowExecutionStarted | Original `Input.Payloads_` | absent | absent |
| ActivityTaskScheduled | Original `Input.Payloads_` | absent | absent |
| WorkflowExecutionSignaled | Original `Input.Payloads_` | absent | absent |
| WorkflowExecutionCompleted | absent | Original `Result.Payloads_` | absent |
| ActivityTaskCompleted | Referenced schedule's `Input.Payloads_` | Original `Result.Payloads_` | absent |
| WorkflowExecutionFailed | absent | absent | Entire top-level `Failure.Message` |
| ActivityTaskFailed | absent | absent | Entire top-level `Failure.Message` |
| WorkflowExecutionCanceled | absent | absent | absent |

For direct values `sourceEventId` is the requested event. A completed activity's
input comes only from its positive, earlier `ScheduledEventId` in the same run;
that schedule's ID becomes `sourceEventId`. Missing/wrong-type schedules or
inconsistent required event attributes are unavailable history, not absent text.
A missing field, empty payload list or missing failure object is `absent`.
An existing failure with an empty message is complete empty text; no causes,
stack traces or details are exported. Encoded failure attributes produce
`unsupported/encoded_failure` unless the message itself exceeds the byte limit.
Other event types, including activity timeout/cancellation, child workflows and
continue-as-new, are `unsupported/event_type`, not transport errors.

## Access and failures

The production `UseFleetAccess` middleware remains authoritative, without new
exemptions or credentials. With configured sign-in, edge reads need a verified
service, browser or enrolled-app caller; missing/invalid edge credentials retain
the existing 401 response. **Internal GETs remain readable without a credential**,
including complete values up to this limit, as with the existing history route.
This does not create a public route or widen the ingress boundary.

| HTTP status | error |
|---|---|
| 400 | `invalid_request`: blank ID, missing/blank/duplicate query parameter, invalid event ID or field |
| 404 | `not_found`: unconfigured/case-mismatched namespace, missing run or missing event |
| 503 | `history_unavailable`: unconfigured/unconnected Temporal, denied/failed read or inconsistent correlation |
| 503 | `history_timeout`: total deadline expired |

There is one 10-second budget for client acquisition and all history pages and
passes, not a fresh budget per RPC. Request abort cancels RPCs and iteration
rather than becoming a successful empty value or a generic 500. The SDK's
read-only paginated history traversal does not long-poll and stops at the target.
Only scheduled-input correlation makes a second bounded pass; the reader does
not retain every event's payload. Events beyond the summary's 300 rows remain
addressable. Missing events require a successful traversal before returning 404.

Handled value/error responses carry `Cache-Control: no-store`; failures before
the handler retain the existing authentication responses. Errors contain stable
codes, never exception text or stored payloads. This reader does not log, cache,
persist, evaluate or execute payload text and makes no Temporal write calls.

## Compatibility and verification

`GET /api/workflows/{ns}/history/{id}` and its DTO are unchanged: same event order,
filter, 300-row cap, 2000-UTF-16-unit previews plus ellipsis and legacy failure
message behavior. Ordinary summary requests never call this on-demand reader.
Native presentation is separate, tracked by [phleet-ios#41](https://github.com/anurmatov/phleet-ios/issues/41).

Run the synthetic real-HTTP endpoint regression and build:

```sh
(
  cd tests/Fleet.Orchestrator.Tests
  f=WorkflowEventValueEndpointsTests
  f="FullyQualifiedName~$f"
  dotnet test --filter "$f"
)
(
  cd src/Fleet.Orchestrator
  dotnet build
)
```

The fixtures use the production auth middleware, registry, strict decoder and SDK
pagination with synthetic histories, not a production Temporal server or native
client. Revert this additive source PR to roll back; no data/config migration,
permission change, private integration pin or production deployment is included.
