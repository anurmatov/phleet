# Conversation protocol (`fleet.conversation.v1`)

A versioned, channel-neutral event contract plus the runtime adapter seam that produces and
consumes it. This is Phase 0: it ships **no** client, no network adapter, no persistence, no
authentication, no push and no object storage.

Contracts live in `src/Fleet.Protocol/`, which has **zero dependencies beyond the BCL** so a
first-party client can reference them without inheriting channel-specific rendering helpers.

---

## Session semantics — read this first

**One agent is one persistent executor process, which is one model context.**

`SessionManager` maps a conversation to a session id, but no executor reads it — its only
consumers are the `/status` display and reset bookkeeping. Meanwhile each agent holds exactly one
executor whose send lock is held for a whole turn, and the runtime refuses a second concurrent
turn outright.

So separate conversations, separate channels and separate `conversationId` values give you
**routing** separation and **not** context isolation. Content from one conversation can influence
the model's answer in another.

Phase 0 is **owner-only**: every conversation belongs to the single already-authorized owner, so
the shared context is not a cross-principal leak today. Multi-human isolation is a separate piece
of work, and neither code, docs nor client-visible copy may imply it already exists.

---

## The envelope

```json
{
  "protocol": "fleet.conversation.v1",
  "eventId": "01JEXAMPLE0000000000000001",
  "seq": 4,
  "emittedAt": "2026-01-01T12:00:00+00:00",
  "kind": "turn.progress",
  "identity": {
    "principalId": "p_0000000000000001",
    "role": "owner",
    "channelId": "example-adapter",
    "conversationId": "c_0000000000000001",
    "submissionId": "s_0000000000000001",
    "turnId": "t_0000000000000001",
    "attempt": 1
  },
  "payload": { "activity": "tool", "toolName": "Read" }
}
```

- `seq` is monotonic per conversation, assigned synchronously at publish, starting at 1. It is a
  **gap-detection aid, not a delivery guarantee** — there is no store in this phase, so a dropped
  event leaves a permanent gap.
- `eventId` is unique per emitted event. A redelivered event keeps its `eventId` and increments
  `identity.attempt`; receivers dedupe on `eventId`.
- **Unknown `kind` values and unknown payload fields must be ignored, never treated as fatal.**

### Routing identity

Immutable. An instance is never mutated: a turn-bearing or redelivery derivation produces a new
record differing only in `turnId` or `attempt`, with every other field copied rather than
recomputed.

| Field | Meaning |
|---|---|
| `principalId` | Opaque, stable per human. Never a numeric platform id. |
| `role` | `owner` in v1. `member` and `guest` are reserved values the runtime rejects. |
| `channelId` | Owning channel, fixed at first use. No cross-channel fan-out. |
| `conversationId` | Opaque, stable per conversation. |
| `submissionId` | One user submission. |
| `turnId` | One executor turn. **Nullable** — absent before a turn exists. |
| `attempt` | 1-based. Incremented only by the in-process resume path. |
| `replyToEventId` | Client-visible reply target. An *event* id, not text. |

The relay correlation id is deliberately **not** part of identity. It stays on the relay path and
is never client-visible.

---

## Event taxonomy

Every kind listed here has a real producer in the runtime. Two kinds an earlier draft carried —
`conversation.notification` and `attachment.offered` — are deliberately **absent**: the MCP send
path is out-of-process and invisible to the runtime, and there is no outbound attachment producer.
Shipping a kind with no producer is forbidden, so they are named here only so a later phase can add
them additively.

### Runtime → client

| `kind` | Payload |
|---|---|
| `protocol.rejected` | `code`, `message` — intake refusal, before any turn exists |
| `submission.text` | `text` — the transcript entry: what the user actually sent |
| `submission.accepted` | `disposition` ∈ `ran`\|`injected`\|`queued`\|`queue_full`\|`dropped`, `queuePosition?` |
| `turn.started` | — |
| `turn.progress` | `activity` ∈ `typing`\|`tool`, `toolName?` |
| `turn.notice` | `text`, `truncated?` |
| `turn.recovered_answer` | `text`, `truncated?` |
| `turn.final` | `text`, `completion` ∈ `completed`\|`incomplete`\|`idle`\|`failed`, `isPartial`, `truncated`, `mergedSubmissionIds[]` |
| `turn.error` | `code`, `message` |
| `turn.canceled` | `reason` ∈ `user`\|`operator`\|`bridge`\|`unknown`, `mergedSubmissionIds[]` |
| `turn.outcome_unknown` | `reason` ∈ `turn_reaped`\|`terminal_event_oversize` |
| `control.ack` | `target` ∈ `cancel`, `accepted`, `hadRunningTask` |

`turn.final`, `turn.error`, `turn.canceled` and `turn.outcome_unknown` are **terminal**.

**`turn.final.text` is the chat-visible text.** The runtime also delivers a *different* string to
the workflow callback — every assistant text of the turn, concatenated. Those two genuinely differ,
and a contract that assumed one canonical "final answer" would silently change one of the two
consumers. The concatenated form is not a client event.

**Idle turns still terminate.** An `IDLE`-only result or a no-output check-in publishes
`turn.final { completion: "idle", text: "" }`. The literal string `IDLE` is an internal contract
marker and never appears in `text`.

### Client → runtime

| `kind` | Payload |
|---|---|
| `conversation.open` | `channelId`, `principalBinding`, `role?`, `externalConversationRef?` |
| `submission.create` | `text`, `replyToEventId?`, `attachments?` |
| `submission.steer` | `text`, `attachments?` |
| `submission.cancel` | `scope` ∈ `current`\|`all` |

That is the complete Phase-0 client command set. There is **no** client equivalent of `/new`,
`/reset`, `/stop`, `/run`, `/status`, `/tts` or `/cancel_bg` — see *Constraints* below.

`submission.steer` is not a separate code path: inject-vs-queue is decided by the runtime's
ordinary dispatch, exactly as it is for the existing chat channel.

---

## Worked examples

### Opening a conversation and submitting

```json
{
  "protocol": "fleet.conversation.v1",
  "kind": "conversation.open",
  "payload": {
    "channelId": "example-adapter",
    "principalBinding": { "scheme": "legacy-owner", "value": "<operator-set token>" },
    "role": "owner"
  }
}
```

```json
{
  "protocol": "fleet.conversation.v1",
  "kind": "submission.create",
  "payload": { "text": "summarise the open items", "replyToEventId": "01JEXAMPLE0000000000000004" }
}
```

### A turn, end to end

```json
{"protocol":"fleet.conversation.v1","eventId":"01JEXAMPLE0000000000000010","seq":1,"emittedAt":"2026-01-01T12:00:00+00:00","kind":"submission.accepted","identity":{"principalId":"p_0000000000000001","role":"owner","channelId":"example-adapter","conversationId":"c_0000000000000001","submissionId":"s_0000000000000001","attempt":1},"payload":{"disposition":"ran"}}
{"protocol":"fleet.conversation.v1","eventId":"01JEXAMPLE0000000000000011","seq":2,"emittedAt":"2026-01-01T12:00:01+00:00","kind":"turn.started","identity":{"principalId":"p_0000000000000001","role":"owner","channelId":"example-adapter","conversationId":"c_0000000000000001","submissionId":"s_0000000000000001","turnId":"t_0000000000000001","attempt":1}}
{"protocol":"fleet.conversation.v1","eventId":"01JEXAMPLE0000000000000012","seq":3,"emittedAt":"2026-01-01T12:00:02+00:00","kind":"turn.progress","identity":{"principalId":"p_0000000000000001","role":"owner","channelId":"example-adapter","conversationId":"c_0000000000000001","submissionId":"s_0000000000000001","turnId":"t_0000000000000001","attempt":1},"payload":{"activity":"tool","toolName":"Read"}}
{"protocol":"fleet.conversation.v1","eventId":"01JEXAMPLE0000000000000013","seq":4,"emittedAt":"2026-01-01T12:00:09+00:00","kind":"turn.final","identity":{"principalId":"p_0000000000000001","role":"owner","channelId":"example-adapter","conversationId":"c_0000000000000001","submissionId":"s_0000000000000001","turnId":"t_0000000000000001","attempt":1},"payload":{"text":"Three items are open.","completion":"completed","isPartial":false,"truncated":false,"mergedSubmissionIds":["s_0000000000000001"]}}
```

All identifiers above are synthetic placeholders.

---

## The privacy boundary

Only the identity fields above, `seq`, `eventId`, `emittedAt`, `kind` and the enumerated payload
fields may appear in a client event. Enforcement is **structural**: payload DTOs are sealed records
with exactly the allowlisted properties, so a denied field cannot be attached without editing the
contract — and a test asserts the serialized property-name set of every kind is a subset of the
allowlist.

**Never emitted, at any nesting depth:** raw model reasoning; tool *arguments* and tool *results*
(only `toolName` leaves); local filesystem paths, including the attachment directory and
`[IMAGE:...]` marker content; provider session ids, executor process detail, raw stdout/stderr;
credentials, tokens, environment values; capability or signed URLs; correlation ids, workflow ids,
signal names, relay sender names, other agents' identities; the execution stats block.

Runtime control markers are part of the chat contract, not user content, and several carry local
paths. Every client-bound text field is sanitized:

| Marker | Handling |
|---|---|
| `[IMAGE:<path>]` | removed **with** its path |
| `[reply_to: N]` | removed |
| `[TASK_FAILED: reason]` | removed; mapped to `turn.error { code: "executor_error" }` |
| the configured attachment directory | redacted wherever it appears |

**Error text is a fixed table, never runtime text.** `turn.error.message` and
`protocol.rejected.message` are constants selected by `code`. Provider text and exception messages
are logged server-side and never serialized — `internal` in particular never carries an exception
message. The chat path still shows the raw string, because that is the owner's own channel.

---

## Publication is non-blocking

The executor and the chat send path must never wait on an adapter.

- `Publish` is synchronous: it assigns `seq` and `eventId`, hands the event to a bounded structure,
  and returns. It never awaits delivery.
- **Two structures.** A single shared *progress channel* (capacity 256) and a *per-conversation
  terminal outbox* (capacity 4). Terminal events use the outbox, so a chatty progress stream can
  never evict one. Both are written with `TryWrite`, so a full queue is counted and logged rather
  than blocking or silently discarding.
- A background pump drains **every terminal outbox first**, then progress. Each delivery runs under
  a **5 s** timeout; a hung adapter is abandoned, counted, and the pump proceeds.
- **Ordering:** `seq` reflects emission order. Within a conversation the single-reader pump
  preserves order, *except* that a terminal event may overtake still-queued progress events.
  Clients must tolerate that; `seq` makes it detectable.
- Adapter exceptions are caught by the pump. **An adapter can never fault a turn.**

### `turn.outcome_unknown`

Three distinct causes, with deliberately different handling:

| Cause | Behaviour |
|---|---|
| Adapter delivery failed or timed out | **No event.** The adapter is exactly the thing that is not working. Recorded in metrics and logs; the turn is unaffected. |
| Turn reaped without a terminal event | `turn.outcome_unknown { reason: "turn_reaped" }`. Terminal for the submission. |
| Terminal event over the hard cap after truncation | `turn.outcome_unknown { reason: "terminal_event_oversize" }` replaces it. |

Clients render it as **indeterminate, never as success**.

---

## Bounds

| Bound | Value | On exceed |
|---|---|---|
| Inbound `text` | 32 KiB | `protocol.rejected { code: "payload_too_large" }` |
| Inbound `attachments` | any non-empty | `protocol.rejected { code: "unsupported_attachments" }` |
| Outbound `turn.final.text` | 64 KiB | truncate, `truncated: true` |
| Outbound notice / recovered-answer text | 4 KiB | truncate, `truncated: true` |
| Outbound `toolName` | 64 chars | truncate silently |
| Any serialized event | 128 KiB | drop + counter; if terminal, see above |

All truncation goes through the shared surrogate-safe cut helper. Splitting a surrogate pair here
has already been a shipped bug once.

---

## Versioning

- `protocol` is `fleet.conversation.v<major>`; `v1` today.
- **Minor evolution is additive only**: new optional payload fields, new kinds, new enum members.
  Receivers ignore what they do not understand.
- A breaking change is a **new major**, with both majors servable through one migration window.
- Enum values are **append-only**. Renaming or removing a `kind`, `disposition`, `completion`,
  `reason` or `code` is a major bump.
- An unknown inbound `kind` yields `protocol.rejected { code: "unsupported_kind" }`, never a
  connection drop.

### Additions the durable-store slice ships

All additive, all appended, and `protocol` stays `fleet.conversation.v1`.

| Addition | |
|---|---|
| `OutcomeUnknownReason.attempt_abandoned` | the reconciler's reason, and **its only producer**. `turn_reaped` belongs to the agent's own run loop and is stored verbatim rather than produced here |
| `ProtocolErrorCode.idempotency_conflict` | the same key presented with a different payload fingerprint |
| `ProtocolErrorCode.invalid_cursor` | a cursor that is negative, non-integral, out of range, or at or beyond `nextSeq` |
| kind `submission.text` | the transcript entry, appended in the accept transaction (#305) — see below |
| kind `conversation.replay_gap` | owed durable history, **synthetic and never stored** — it carries `seq: null` |
| kinds `conversation.catchup`, `conversation.ack` | the client's catch-up request and cursor advance |
| optional `idempotencyKey` on `submission.create` | |
| optional `clientInstanceId` on `conversation.open` | opaque bookkeeping for cursors; **not a credential and not device identity** |

### `submission.text` — the user's own messages are part of the log (#305)

Before this, no event kind carried the text a client had sent. A client that relaunched and caught
up from its cursor got the agent's events and nothing of its own: answers with no questions.

**The entry is appended in the accept transaction**, under the conversation row lock that
transaction already holds, and it takes the very `seq` the accept reports as `acceptedSeq`. Four
consequences, each of which is the reason for a decision rather than a restatement of it:

- **It sorts before everything its turn produces.** `submission.accepted`, `turn.started` and the
  terminal are all appended later and take higher seqs.
- **It cannot interleave ahead of an earlier submission.** Concurrent accepts serialize on the row
  lock and now consume a seq each. *(Before, no accept advanced `next_seq`, so two accepts on one
  conversation both stored the same floor.)*
- **`acceptedSeq` now names an event that exists** rather than predicting one. The documented
  `afterSeq = acceptedSeq - 1` arithmetic is unchanged and now replays the user's own message first.
- **It is `durable`**, on the existing `DurableEventRetention` horizon — this change adds no second
  retention knob. Collecting it advances the retained floor in the same transaction as the delete,
  so a client that catches up from beneath the floor is told by `conversation.replay_gap` rather
  than handed a transcript quietly missing its own messages.

**A separate kind, not a field on `submission.accepted`.** That event is written at the agent's
*disposition* and is never written at all when the agent never claims the command — so a field on it
would be missing in exactly the accepted-but-never-answered case, which is the one that matters most
and the one a local client-side echo renders most misleadingly. It is also a kind deployed clients
already parse; a new kind is additive, widening an existing one is not.

**Exactly one per submission, and only for speech.** Every submission passes the accept transaction
once, so a steer gets its own entry and coalescing — which happens later, in the agent — cannot
merge two messages into one. A `submission.cancel` goes through the same transaction and gets **no**
entry: it is a control action, not something the user said.

**Nothing rejected leaves a trace.** An over-cap `413`, an idempotency `409` and a malformed `400`
all return before the submission is durable, so there is no entry and no partial one. The stored
text is therefore never truncated, which is why the payload has no `truncated` flag.

**Older clients are unaffected.** The envelope contract already requires a receiver to ignore an
unknown `kind`. A conversation that predates this change simply has no `submission.text` rows, and a
newer client renders what it receives rather than inventing entries that were never stored.

⚠️ **Its only producer is the accept transaction**, so it appears on the durable path and not on the
in-process seam — which has no accept transaction and no store. The worked example above is the
in-process path and is correct without it.

### Three amendments the durable store makes

**`seq` is allocated at durable append.** It does not exist before the row commits. Anything that
handed a caller a `seq` before the append would be handing back a number the store then replaced —
and the invented one is what would reach the wire if it were ever read back from the request.

**⚠️ `submission.accepted` and `turn.started` may arrive in EITHER order, and a client must not
assume one.** The in-process path emits `started` first; the durable path records the disposition
first, because the agent reports what it decided to do before it starts doing it. Both orders are
correct, and a client that keys behaviour on seeing one before the other breaks against the other
transport.

**Progress and notice events are prunable.** They are `ephemeral`, and their absence at a seq is
**not** a gap — only a missing *durable* event beneath the retained floor is. A client that treated
a hole in the seq sequence as a loss would report one on every reconnect after a day.

---

## Transports

This document defines the event contract. It does not define how that contract reaches a client
that is not in the process.

**[`docs/first-party-api.md`](first-party-api.md)** defines the authenticated north boundary: the
HTTPS routes and WebSocket framing a first-party client speaks, its credential lifecycle, the
resume case table, backpressure and close codes. It carries this contract without inventing a
second conversation model — an event delivered over that boundary's stream is byte-identical to the
same event delivered by its catch-up route, and both are the envelope defined above.

Three facts from that boundary's inherited set belong here, because a client author reading only
this document would otherwise get them wrong. All three are owned by the durability design, not by
the transport:

- **`seq` is allocated at durable append, not at publish.** The value does not exist until the event
  is durable.
- **`submission.accepted` does not necessarily precede `turn.started`.** On the `Ran` path
  `turn.started` is emitted first and takes the lower `seq`. A client that waits for `accepted`
  before rendering a running turn will hang.
- **`turn.progress` and `turn.notice` are prunable.** A missing `seq` for one of those is **not** a
  gap, and a `seq` jump on a live stream is not proof of loss.

---

## Principal binding is not authentication

`conversation.open` carries `{ "scheme": "legacy-owner", "value": "<opaque token>" }`, resolved
against operator configuration only — the binder **never parses a numeric id out of client input**.

Resolution, in order; any failure yields `unauthorized` and creates **no** registry entry:

1. `ClientChannel:OwnerPrincipalToken` is non-empty and `ClientChannel:OwnerUserId` is non-zero.
   Absent configuration disables client conversations entirely; there is no default token.
2. `scheme` is `legacy-owner` — the only v1 scheme.
3. `value` matches under a **fixed-time** comparison.
4. The configured owner is allowlisted **at open time**, so revoking them in the live allowlist
   also closes the client channel with no reprovision.
5. `role` is `owner`; anything else yields `unsupported_role`.

`principalId` is derived deterministically and non-reversibly from the matched token and the agent
name — stable across restarts, containing neither the numeric owner id nor any token material.

**A shared operator-set token is not an auth scheme.** It exists so the owner check is deterministic
rather than a guess, and because the only adapter permitted in this phase is an in-memory loopback
in the test project. Real authentication — device registration, per-principal credentials,
revocation, replay resistance — is separate work. `scheme` exists rather than a bare string so
future schemes are additive.

---

## Runtime ownership and optional Telegram

The conversation seam and the existing chat path share one `TaskManager`, executor and model
context, but outbound and completion effects no longer depend on the Telegram transport:

- `MessageSinkHolder` is the dependency-free outbound seam. It forwards to `AgentTransport` when
  Telegram is configured and otherwise serves a counted `NullMessageSink`.
- `CompletionContextBuffer` owns tool-use and final-response buffering for every channel. It reads
  the last Telegram message id through the sink seam; a Telegram-free host receives `0`, matching
  the existing no-prior-send value.
- `RelayCompletionPublisher` alone publishes relay and bridge completions. Its lifetime is
  independent of Telegram, so workflow callbacks still work when no bot token exists.
- `RuntimeWiringService` verifies both completion subscribers are attached, subscribes the relay
  handler, and only then starts broker consumption. Missing wiring therefore fails startup instead
  of silently losing callbacks.
- `AgentTransport` remains the Telegram adapter and owns Telegram polling, formatting, message-id
  tracking and chat delivery. Its daemon registration is conditional on a non-empty bot token;
  malformed configured tokens disable Telegram without disabling the rest of the runtime.

This split does **not** create another agent or another model session. Telegram and a future
first-party adapter are two front doors to the same runtime and shared model context.

---

## The south adapter — where durable conversations join the seam

The seam above is in-process. The durable half lives in the conversation service, and the agent
reaches it through the **south adapter**: `ConversationSouthConsumer` drains the per-agent inbound
queue, and `ConversationSouthAdapter` is the `IChannelAdapter` registered for the `client` channel.
Together they close the loop a client submission travels.

```
north API → store (TX1a) → command outbox → broker
          → ConversationSouthConsumer  (claim, disposition, dispatch)
          → ConversationIntake → TaskManager → executor
          → ConversationEventBus → pump → ConversationSouthAdapter
          → ConversationSouthConsumer  (turn start, appends, terminal commit, ack)
```

**The split between the two is load-bearing, not organisational.** `ConversationEventPump` wraps
every adapter call in a five-second timeout and swallows every exception, so an adapter can never
fault a turn. That is right for delivery and wrong for durability: a bounded retry written inside
`DeliverAsync` is cancelled mid-retry and its failure discarded, while the turn is counted as
delivered. So the adapter **translates only** — no HTTP, no retry, no blocking, no ordinal — and the
consumer owns every south call, all backoff, the terminal commit and the broker acknowledgement.

Five properties are worth knowing before changing anything here:

- **The ack follows the commit, never the other way round.** An ack-then-commit ordering loses a
  turn's outcome permanently on a crash in the window and nothing detects it. Commit-then-ack can at
  worst redeliver, and a redelivery meets a `done` claim and becomes a no-op.
- **Ordinals are allocated at the send site, in the consumer.** The bus drains terminal outboxes
  before the shared progress channel, so a terminal legitimately reaches the adapter ahead of
  progress published earlier. Allocating when an event is created would give that terminal the higher
  ordinal, transmit it first, and trip the store's fence with a reordering that was purely ours.
  Every south call for one conversation goes through a single in-order sender.
- **`submission.accepted` is suppressed by the adapter.** The disposition endpoint already writes it,
  and append idempotency keys on `eventId` — so forwarding the runtime's copy writes a second row
  rather than deduplicating. `turn.started` is not suppressed: it is what drives `/turns:start`, and
  that endpoint writes the stored event.
- **A terminal that overtakes its own `turn.started` starts the turn before committing it.** Because
  terminal outboxes drain first, this is ordinary rather than exceptional. The store requires the
  start: `/turns:commit` only moves an attempt out of `running`, so committing a `pending` attempt
  appends the terminal, leaves the attempt open for the reconciler, and the `turn.started` that
  follows is dropped with a null seq because the submission is already terminal — an answered turn
  reported to the client as `turn.outcome_unknown { attempt_abandoned }`. The terminal carries the
  same `identity.turnId`, so the consumer starts the turn from it. A `turn.started` arriving
  afterwards is then suppressed, because a second `/turns:start` on a non-`pending` attempt takes the
  store's lost-attempt branch and abandons the turn's merged children.
- **Terminal kinds go to `/turns:commit`, never `/events:append`.** Appending one would write the
  event without closing the attempt, and the reconciler would later abandon an attempt that had
  already answered.

### The feature gate

The whole seam is conditional on `Conversations__SouthBaseUrl`. Absent, nothing is registered — no
adapter, no consumer, no hosted service, no HTTP client — and every existing path is byte-identical
to an agent built before the feature existed. Present but incomplete **fails startup** with the
offending key named, because an operator who configured half of it would otherwise see a healthy
process beside a queue nobody drains.

The section holds two settings — the base URL and the bearer — plus the heartbeat interval and the
prefetch. It deliberately holds **no agent name and no broker connection string**. The agent already
has an identity on this broker (`Agent:ShortName`, which `GroupRelayService` builds
`fleet.agent.{shortName}` from) and already holds a connection to it (`RabbitMq:Host`); a second
field for one identity is a value that can drift, and a second connection string to one broker is a
credential rotation with two places to land and one to miss. Both are *validated* at startup —
`ShortName` against the pattern the service enforces, the broker host for presence — because a check
is not a second field. The retry base, retry ceiling and request timeout are constants for the same
reason: they were knobs with no operator who would turn them.

### Cancel is claimed and terminated, not executed

`submission.cancel` arrives on the same queue as any other submission, with its own submission id and
its own attempt. This slice does **not** execute it: the consumer claims the delivery, completes it
with `dropped`, and acknowledges. Ignoring it is not an option — an unclaimed or undispositioned
cancel leaves a held claim and a live lease, which the reconciler turns into
`turn.outcome_unknown { attempt_abandoned }` against a client that is still waiting.

**The user-visible consequence, stated plainly:** the north cancel route still returns `200` and the
client receives `submission.accepted { dropped }` and **no `control.ack`**. Cancel execution — and
the open question of what closes a cancel submission's attempt, given that `control.ack` is not one
of the four terminal kinds — is a separate issue and must land before a client ships a cancel
affordance. An envelope whose `kind` the consumer does not recognise takes the identical path, for
the identical reason; requeuing it would be an infinite broker loop.

---

## Constraints

Things that must stay true; several encode defects that have already cost real outages.

1. **The existing chat render path is not altered.** Chunk sizes, prefix format, parse-mode
   selection, the formatting fallback ladder, reply-parameter fallback, marker extraction and
   Telegram message-id map remain transport-owned. Send consumers reach that transport through
   `MessageSinkHolder`; completion buffering reads its last message id through the same seam.
2. **Runtime conversation keys for new channels are positive.** Several live sites treat a negative
   key as "this is a group" — the channel anchor renders `group` instead of `dm`, and queue-notice
   suppression keys off the sign. The reserved band is `[2^56, 2^56 + 2^32)`, above the 52
   significant bits a chat id can occupy, so no existing heuristic has to be edited.
3. **A non-chat conversation never reaches the chat send path or the bot client.** The guard is
   per method and independently tested.
4. **No raw tool arguments, tool results, reasoning, filesystem paths, provider session ids,
   credentials, capability URLs or raw error text in any client event.**
5. **Runtime control markers are stripped**, and the `[IMAGE:...]` path is never exposed.
6. **Nothing blocks on adapter delivery** — not the executor, not the chat send path, not dispatch.
7. **Relay, bridge and workflow-callback traffic never reaches a client adapter**, and no adapter
   may complete, acknowledge or cancel a workflow delegation.
8. **The command surface is not exposed to clients.** It includes a global kill switch and a raw
   executor passthrough; neither may be reachable from a channel that cannot authenticate its
   caller. Slash-prefixed submission text is conversation content, verbatim.
9. **Attachment bytes, storage references and URLs are not accepted.** A submission carrying
   attachments is rejected outright and must not silently proceed as text-only.
10. **One turn at a time.** No second concurrent turn, no bypass of the per-turn executor lock, no
    path reaching the executor without passing through the normal dispatch decision.
11. **Injection eligibility, the final-answer gate, queue ordering, the merge-part cap, the
    queue-depth cap and silent-queue behaviour are unchanged** — including not adding a
    user-facing notice on a path that is currently silent.
12. **Telegram is an optional transport, not a runtime dependency.** A daemon with no bot token
    omits `AgentTransport` and uses the counted null sink, while relay consumption, relay and bridge
    completion publication, shared-context buffering and conversation events remain active. A
    malformed configured token disables Telegram only; it must not take those runtime services
    down.
13. **Relay handlers attach before consumption starts.** The consumer dispatches under auto-ack, so
    a directive arriving before the subscriber exists is acknowledged and dropped.
14. **No durable delivery, replay or at-least-once semantics are claimed.** There is no store;
    `seq` is gap detection only, and a dropped event is permanently gone.
15. **No kind, enum member or payload field ships without a producer.**
16. **No database, outbox, durable queue, broker topic, authentication scheme, push transport or
    object storage**, and **no network-facing adapter** — the only permitted adapter is an
    in-memory loopback in the test project.
17. **Conversation separation does not imply context isolation.** One agent has one model context;
    say so.

---

## Observability

| Counter | Notes |
|---|---|
| `conversation_events_published_total` | by channel and kind |
| `conversation_events_not_routed_total{channelId}` | **expected** traffic for runtime-owned channels |
| `conversation_events_dropped_total{reason}` | `progress_queue_full`, `terminal_outbox_overflow`, `oversize`, `serialize_failed`, `unknown_conversation`, `shutdown` |
| `channel_adapter_delivery_failures_total{channelId,reason}` | `threw`, `timeout` |
| `conversation_submissions_total{disposition}` | mirrors the dispatch outcome |
| `turn_outcome_unknown_total{reason}` | |
| `sink_suppressed_total{reason}` | Counted null-sink sends; `null_sink` is expected in a Telegram-free host but suspicious when Telegram is configured |
| `startup_telegram_state` | `configured` or `malformed` when the transport is constructed; unset means the transport was omitted because no token was configured |
| `relay_completions_published_total{type}` | Relay/bridge completions published by the transport-independent owner |

The `not_routed` / `dropped` split is load-bearing. Every existing-channel turn publishes events
that no adapter owns — that is the expected steady state, not a fault. Counting it as a drop would
make the drop counter the dominant metric in production and indistinguishable from real misrouting,
which is how a genuine signal gets ignored.

Delivery failures log at `Warning` with channel, kind, conversation and event id — and never
payload text.

---

## Out of scope

Durable storage, outbox and consumer-claim; client authentication and device registration; push
delivery; object storage and capability URLs for attachments; routing out-of-process MCP sends back
through the runtime; voice and WebRTC; client UI; multi-human context isolation. Each of those
consumes the contracts defined here; none is designed here.

## Known per-provider gaps

The contract above says what the runtime emits. It does not say what each provider can actually
produce, and the three executors are not uniform. That was measured rather than assumed:
**`docs/spikes/real-adapter-capability-matrix.md`** records, per provider and per scenario, the exact
ordered client event sequence, or an explicit `unsupported` / `inferred` / `leaky` verdict with its
reason. `docs/spikes/session-continuity-scenarios.md` is the scoping table behind it.

Three gaps apply to every provider and are worth knowing before building a client against this
contract:

- **Tool completion is invisible** (#285). A client sees a tool start and never a tool finish, so a
  per-tool spinner has no event that clears it.
- **There is no incremental assistant-text event** (#286). `ConversationEventKind` has no delta kind,
  so a client cannot begin rendering — or speaking — before the whole answer exists.
- **An executor-reported failure arrives as `turn.final` with `completion: incomplete`, not as
  `turn.error`.** A client that watches only `turn.error` will miss the most common provider failure.
  `turn.error` is reserved for a thrown executor (`internal`) and for an executor that reports an
  error through an `error`-typed progress event (`executor_error`).

Two ordering facts a client must not assume away, both measured:

- **Arrival order is not emission order.** `seq` is assigned inside `Publish` and the channel write
  happens afterwards, so concurrent publishers can interleave. Separately, terminal outboxes drain
  ahead of the shared progress channel by design. What the runtime does guarantee is that `seq` is
  unique per conversation, each event is delivered at most once, and a turn's terminal is sequenced
  after that turn's own `turn.started`.
- **`submission.accepted` is not a checkpoint.** The disposition is reported by the caller thread
  *after* the turn has been dispatched, so it can arrive — and be sequenced — after the `turn.final`
  that answers it. Correlate on `submissionId` and treat the disposition as metadata about dispatch,
  never as a position in the stream.
- **`control.ack` and `turn.canceled` race.** The ack is published by the intake on the caller's
  thread; the terminal by the turn's own catch block on the turn's thread. Read the ack as "the
  request was accepted", and use `ControlAckPayload.HadRunningTask` to decide whether a
  `turn.canceled` is coming at all.

This section is a pointer. Nothing in the contract changes because of it. One further gap —
`GeminiExecutor` having no test seam below `ExecuteAsync`, so its start and terminal events cannot
be verified — is tracked in #288, and the Codex `ToolName` leak in #287.
