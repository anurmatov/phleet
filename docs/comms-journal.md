# The conversation journal (slice 1)

A durable, channel-neutral record of the messages a human exchanges with an agent. This slice ships
the **store and the ingest contract**: a MySQL journal in the conversation database, an internal
HTTP listener that accepts text-only records from trusted publishers, and the operator, readiness
and status surfaces around them. It is **off by default**; with it off a deployment is unchanged.

## Purpose and non-goals

Telegram history today lives only in Telegram and in bounded in-memory buffers, and nothing is
replayable after a restart. The journal is the durable, queryable record.

Later slices added capture inside the agent runtime (slice 2), media bytes (slice 4), and
tool-initiated sends and the read tools (slice 5, below). Still not in the journal: copies,
projection of first-party client conversations, edits and deletions, backfill, and a metrics
exporter.

## The listener

| | |
|---|---|
| Address | `Comms__Journal__Url`, default `http://0.0.0.0:8083` |
| Built | only when `Comms__Journal__Enabled=true`; otherwise nothing is bound, registered or started |
| Reachable | the container network only. It is **never** published as a host port and never proxied |
| Routes | `POST /journal/v1/messages` (token purpose `ingest`), `GET /journal/v1/status` (`status`), `POST /journal/v1/mcp` (`read`, see [Read tools](#read-tools-slice-5)) |

It is a separate application from north, south and ops. The journal routes are not mapped on any
of those, and the south bearer is not a journal credential. Authentication runs before routing and
before any body byte is read, on every path: an absent, malformed, bad-MAC or wrong-purpose token,
and any path that is not one of the routes, all get the same `401 {"error":"unauthorized"}`.

After authentication each subject may hold **8** requests in flight. The ninth gets
`429 {"error":"too_many_requests"}` with `Retry-After: 1`, before its body is read.

## Record contract

One record is one Telegram message as one runtime observed it. UTF-8 JSON, at most 1 MiB.

| field | rule |
|---|---|
| `eventId` | ULID, required. The publisher's idempotency key |
| `channel` | `telegram` |
| `telegram.botId`, `.chatId`, `.messageId` | int64, required; `chatId ≠ 0` |
| `telegram.chatKind` | `private` \| `group` \| `supergroup`, copied from the platform's chat type |
| `telegram.chatTitle` | optional, ≤ 256 characters |
| `telegram.replyToMessageId` | optional int64 |
| `telegram.mediaGroupId` | optional, printable ASCII ≤ 64. An album is N records sharing it |
| `direction` | `inbound` \| `outbound` |
| `sender` | `{kind: human\|agent, id, display?}`: `id` a JSON **string** of ≤ 128 characters (a numeric id is refused), `display` ≤ 128 |
| `sentAt` | ISO-8601 **with an offset**: the platform's message date, never receive time. `2013-01-01T00:00:00Z` ≤ sentAt ≤ server now + 10 min |
| `text` | raw platform text or caption, ≤ 65,536 UTF-8 bytes, or null |
| `textFormat` | `plain` \| `html` \| `rich`; required when `text` is set |
| `transcript` | speech-to-text result, ≤ 65,536 UTF-8 bytes, or null. Never merged into `text` |
| `transcriptTruncated` | optional bool |
| `origin` | `telegram_update` \| `agent_runtime` \| `agent_tool`. `agent_tool` (slice 5) only with `direction: outbound` and `sender.kind: agent`; anything else is `422 invalid_record{origin}`. Not in the fingerprint |
| `sendGroup` | optional `{id: ULID, part, parts}` linking the chunks of one long outbound reply; outbound only, `1 ≤ part ≤ parts ≤ 64` |
| `attachments[]` | at most 16; see below |

Unknown or duplicated fields, and a value of the wrong JSON type (a string where an integer belongs, an escaped lone surrogate), are `422 invalid_record{field}`. `observer` is not a field: **the observer is always the
token subject**, and a body `observer` is `422 invalid_record{observer}`.

**Attachments are metadata only** on a deployment with no object store, which is the default:
`ordinal` (0–15, unique), `kind`
(`photo|document|voice|video|video_note|audio|animation|sticker|other`), `mimeType`, `byteSize?`,
`fileName?`, `fileUniqueId?`, and a required `notArchivedReason`
(`media_disabled|over_bot_api_limit|over_size_cap|unsupported_kind|download_failed|source_expired`).
Rows are stored `not_archived`, so a message is never visible with a dangling attachment. An
`uploadId`, `objectId` or `bytes` field is `409 media_disabled`.

**With `Comms__Media__Endpoint` set, the same field carries bytes.** See [Media](#media-slice-4)
below: an attachment names an `uploadId` whose bytes the sending subject physically uploaded, and
`notArchivedReason` is then absent rather than nullable.

`delivery_state` is derived: inbound → `received`, outbound → `sent`.

### ⚠️ Trusted publishers

`telegram.botId`, `chatId`, `messageId` and `sender` are **self-asserted**. A holder of an `ingest`
token can write into any conversation key it names. Mint tokens only for your own runtimes.

`text` must be the raw source. It never contains local file paths, attachment hints, default
prompts, prompt-assembly text or transcripts. This is a publisher obligation; the server cannot
detect a violation.

## Conversation keys

Derived by the server from `chatKind`, never from the sign of the chat id:

| kind | key | why |
|---|---|---|
| `private` | `tg:dm:<botId>:<chatId>` | message ids are numbered per bot |
| `group` | `tg:bgroup:<botId>:<chatId>` | basic groups number messages per account too |
| `supergroup` | `tg:group:<chatId>` | one shared sequence: one message row, one observer row per bot |

The source key is `tg:<messageId>`, and `order_key = messageId`.

## Fingerprint and idempotency

The fingerprint is lowercase hex SHA-256 over a **hand-built** canonical encoding, not a serializer,
so stored fingerprints survive library upgrades: one JSON object with keys in ordinal order and no
whitespace; strings escaping only `\"`, `\\` and `\u00xx` below U+0020, everything else raw UTF-8;
integers in invariant decimal; `sentAt` as `yyyy-MM-ddTHH:mm:ss.fffZ` in UTC; absent optionals as
`null`; attachments sorted by ordinal. `JournalFingerprintTests` pins the bytes.

Encoded: the conversation key, `messageId`, `direction`, `sender.kind`, `sender.id`, `sentAt` and
`text`, and per attachment `ordinal`, `kind`, `mimeType`, `byteSize` and `fileUniqueId`. Excluded:
`transcript`, `textFormat`, `chatTitle`, `sender.display`, `notArchivedReason` and `eventId` — so two
bots that saw one supergroup message agree.

**The rule the exclusion list encodes: no field the observers cannot agree on may enter the
fingerprint.** Whether any one agent archived an attachment is exactly such a field — it differs for
ordinary reasons (`upgrade.sh` reprovisions agents one at a time, so two of them run different media
policies for a while; one download failed; one size cap is lower). Slice 4 briefly encoded the
server-resolved object id and turned that disagreement into `409 idempotency_conflict`, which the
drainer dead-letters. An archived and a declined attachment are different rows, and that is what the
attachment table is for; they are not different messages.

One transaction per record, with the conversation row locked `FOR UPDATE`:

| case | response | writes |
|---|---|---|
| new natural key `(conversation, sourceKey)` | `201 {messageId, result:"created"}` | conversation (upsert), message, observer, attachments |
| `eventId` already recorded with the same fingerprint | `200 duplicate` | none |
| `eventId` already recorded with a different fingerprint | `409 idempotency_conflict{event_id_reused}` | none |
| same natural key and fingerprint, same observer | `200 duplicate` | none — not even a transcript this observer did not send first time |
| same natural key and fingerprint, new observer | `200 observer_added` | observer row; `transcript` filled only if still null; an attachment this observer proved and the stored row left `not_archived` is committed and pointed at that object |
| same natural key, different fingerprint | `409 idempotency_conflict` | none |
| chat in the exclusion list | `422 excluded_chat` | none |
| a field violation | `422 invalid_record{field}` | none |
| body over 1 MiB | `413 too_large` | none |
| database unavailable, or schema below 0004 | `503 store_unavailable` (`reason: schema_behind`) | none |

A deadlock or lock-wait timeout retries the whole transaction once, then answers `503`. Publishers
retry `503` and `429`; every other answer is final for that record. Send the transcript on the
first attempt: a repeat from the same observer never fills it.

## Classifier rules and publisher obligations

`JournalClassifier.Classify` (in `Fleet.Conversations.Contracts`, BCL-only) applies, in order,
stopping at the first match:

1. `chatId == 0` → `NoChat`
2. outbound, and the task origin is `Relay` or `Bridge` → `SystemOrigin`
3. the chat is in the exclusion list → `OperationalChat`
4. an allowlist is given and does not hold the chat (`UserIds` for private chats, `GroupIds` for
   groups and supergroups) → `UnauthorizedChat`
5. otherwise include

The task origin (`JournalTaskOrigin`) is why an outbound message exists, not the wire `origin`: the
agent's `Relay` and `Bridge` task sources map one to one, everything else maps to `Human`. A null
allowlist skips rule 4.

**Publishers classify first and write nothing for an excluded message** — no local file, no
request. The server repeats the check with `Human` and no allowlist, so only rules 1 and 3 apply
there, and it runs before any write.

## The agent publisher (slice 2)

`Fleet.Journal.Client` (Contracts only) holds the classifier wrapper, spool, wire serializer and
drainer; `Fleet.Agent` captures in the Telegram transport.

| agent setting | purpose | default |
|---|---|---|
| `Journal__IngestToken` | enabling key; an `ingest` token for this agent's subject | empty = off |
| `Journal__BaseUrl` | the listener | `http://fleet-comms:8083` |
| `Journal__ExcludedChatIds` | same parsing as the listener's list | empty |

Blank token: nothing is registered and the agent is byte-identical. A token not shaped
`cj1.ingest.*`, a non-http(s) URL or a bad exclusion list stops startup (exit 1, key named, value
never printed).

- **Inbound**: one record per raw Telegram message, from the raw text or caption, Telegram's date,
  media group id, file unique ids and sizes, and the transcript. Never the placeholder, the image
  prompt or attachment hints. Commands (`/new …`, `/tts`) are journaled like any message.
- **Outbound**: replies only, one record per Bot API message Telegram accepted, with its format
  (`plain`, `html`, or `rich` carrying the Markdown source). A reply split into several messages
  shares one `sendGroup`. Relay and bridge output (`OutboundOrigin`) is excluded, including its
  images. See [Capture policy](#capture-policy-slice-5) for what counts as a reply.
- **Spool**: `{WorkDir}/.fleet/journal-spool/{pending,media,dead}`; inbound media hardlinked from
  the attachment directory, outbound media copied. Limits: 10,000 records or 1 GiB; at the limit
  the new record is dropped. With media off every attachment goes as `not_archived(media_disabled)`;
  with media on the drainer uploads the spooled file first and then names it (see
  [Media](#media-slice-4)).
- **Drainer**: one request at a time, oldest due record first, never FIFO-blocked; backoff 1 s
  doubling to 5 min; 15 s timeout; 30 s pause after 5 straight transport/5xx failures. `401` stalls
  everything, `404` pauses 5 min, `422 excluded_chat|unknown_conversation` drops, a refused record
  goes to `dead/` (kept 30 days). Redrive: move a file from `dead/` back to `pending/`.
- **Heartbeat**: `Journal{enabled,spoolDepth,oldestAgeSeconds,dropped,dead,authFailed}`, absent
  when the journal is off. Logs carry reason codes and record ids only — never text or the token.

## Capture policy (slice 5)

Outbound capture is **opt-in per send**. `IMessageSink.SendReplyAsync(chatId, AgentReply, origin)`
is the only outbound path that journals; `AgentReply` carries the body, the stats line and the
tool-call block separately. `SendTextAsync`, `SendHtmlTextAsync` and `SendPhotoAsync` never open a
journal batch, whatever their origin. Nothing is decided by matching text, so a new notice is
excluded without anyone having to list it.

| Send | Journaled |
|---|---|
| The final reply, the merged-turn reply, an injected turn's answer, a recovered answer | yes, body only |
| Photos from `[IMAGE:]` markers inside a reply | yes, with media |
| The TTS voice reply | yes |
| Tool-progress blockquotes, the provider warning, "Task failed", "Error:", "Task cancelled.", "Done! (no text output)" | no |
| Queue, busy, cancel and command notices; image-skip notices | no |
| The photo-missing hint and the photo-failed notice inside a reply (they carry a local path) | no |
| The transcript echo `🎤 …` (the inbound row already holds the transcript) | no |

**Telegram output does not change.** Journaling only observes: every Bot API call a reply makes —
method, text or caption, parse mode, reply target, message count — is what it was before slice 5.
`ReplyRenderGoldenTests` compares them with fixtures recorded from the code before the change. The
journal never holds the footer (the stats line or the tool-call block), tool arguments, runtime
error text or a local path.

Which footer a reply has picks its render path:

| Path | When | Telegram | Journal text |
|---|---|---|---|
| **T** | a tool block is present, any `FormattingMode` | `prefix + HtmlEncode(body) + stats + toolBlock` in hard 4,000-character HTML chunks. `[IMAGE:]` and `[reply_to:]` are sent as literal text and no photo goes out (existing behaviour) | chunk *k* of `prefix + HtmlEncode(body)`, cut the same way, with every `[IMAGE:…]` and `[reply_to: N]` removed, then blockquote-balanced. A chunk that is only footer, or empty after removing markers, is not journaled |
| **S** | no tool block (a stats line or nothing) | the normal path: `[reply_to:]`, `[IMAGE:]` split, per-mode rendering and the rich → HTML → plain fallbacks | each part as sent, except the last `[IMAGE:]` part, which alone carries the stats line: it is rendered again without it through the stage that actually sent it and zipped by index. Surplus body pieces go on the last zipped message. A photo whose caption held the stats line is journaled with the body-only caption (null when empty) |

A split reply is one `sendGroup`, and `parts` counts journaled messages only.

## Tool sends (slice 5)

Messages an agent sends through the Telegram MCP tools (`send_message`, `send_to_ceo`) are sent by
`fleet-telegram`, not by the agent. Only the agent knows the turn's origin, its allowlist and its
exclusion list, so `fleet-telegram` reports what it sent and the agent classifies and spools it with
its own ingest token. The records carry `origin: agent_tool`; the observer is the agent.

**Receipts.** Every Bot API send in `Fleet.Telegram` goes through `TelegramSender`, and a source-scan
test fails if anything else calls a send method on a bot client. When a tool call ends with at least
one message accepted, `fleet-telegram` publishes one receipt:

- to the direct exchange `fleet.journal.tool-sends`, routing key the lower-cased `?agent=` name,
  persistent, JSON `v: 1`: `agent`, `tool`, `requestedAt` (UTC, before the first Bot API call),
  `botId`, `chat{id,type,title}` and `messages[{messageId, date, replyToMessageId?, text,
  textFormat}]` in send order;
- only when `Journal__ToolSendReceipts=true` (compose sets it from `FLEET_COMMS_JOURNAL_ENABLED`).
  Off, `fleet-telegram` opens no broker connection and declares nothing;
- fire-and-forget through a 1,000-entry queue. A full queue, a broker error, a 10 s publish timeout
  or shutdown drops the receipt and counts it. **The tool result never waits on the broker.**

Sends through the notifier fallback bot are never recorded: that bot is not in the agent's
conversation.

**The agent's queue.** An agent with the journal on declares the durable queue
`fleet.journal.tool-sends.<name>` (24 h TTL, 10,000 messages, `drop-head`), bound with its
lower-cased name, and consumes it on its broker connection with manual ack. An agent with the
journal off declares nothing and deletes that queue at startup if it exists. A receipt that fails
to parse, has the wrong version or is over 1 MiB is `receipt_invalid`; one whose `botId` is not the
agent's own bot is `tool_send_foreign_bot`. Both are acked and dropped.

### Origin attribution (fail closed)

`TurnOriginLedger` records every period in which the provider may be acting, tagged `human`,
`relay`, `bridge` or `unknown`. With `W = [requestedAt − 2 s, requestedAt + 2 s]`:

- a tool send is journaled **only if** the union of `human` intervals covers every instant of `W`
  **and** no `relay`, `bridge` or `unknown` interval touches `W`. There is no precedence between
  intervals;
- an uncovered part of `W` (before a turn, after it, between two turns) → `tool_send_unattributed`;
  any non-human overlap → `tool_send_non_human`;
- the decision waits until the agent clock passes `requestedAt + 2.25 s`. The receipt waits unacked
  in a list of at most 1,000 (overflow → `receipt_deferred_overflow`, acked, excluded) and is acked
  after the decision. An interval still open counts as covering up to decision time;
- `requestedAt` later than the receipt's arrival → `receipt_clock_skew`. The guarantee holds while
  the `fleet-telegram` and agent clocks differ by at most 2 s: trivially on one host, with NTP on
  several;
- closed intervals are kept 10 minutes, open ones forever. A window that starts before that horizon
  or before the agent started — including a receipt redelivered after an agent restart — is excluded.

Where intervals come from:

| Source | Interval |
|---|---|
| An executor turn (`ExecuteAsync`, Claude `ReadInjectedTurnAnswersAsync`) | opens right **after** the executor takes its turn lock and closes before it releases it. Its origin is the one `TaskManager` set with `Pending(origin)` before enumerating, else `unknown` (warmup). A task waiting for the lock has none |
| `/run` (`SendCommandAsync`) | lock-held, always `unknown`, **plus** an `unknown` interval opened before the command is submitted that outlives the lock. It ends on the command's own terminal event or a confirmed process exit, and the stdout reader retires it even after its caller stopped reading: Codex binds it to the turn of the first `userShell` command item and closes it on that turn's `turn/completed`; Claude closes the oldest waiting command on the next result for a stdin message (human or unstamped origin — claude answers stdin messages one at a time, in order). Another turn's terminal never closes it. A refused request closes it at once |
| A Codex turn interrupted on cancellation whose end is not seen within the drain | `unknown`, opened while the lock is still held, until the reader reads that turn's own `turn/completed` or the process exit is confirmed |
| Gemini | one CLI process per call, from start to confirmed exit. After a cancellation the CLI is killed and its exit awaited; a kill that fails or does not take effect keeps the interval open until the process really exits |
| A turn the provider starts by itself (an injected message before the lock is taken, a background-task notification) | `unknown`, opened by the stdout reader on a turn-content event (Claude `assistant`, `user`, `stream_event`, `system/init`; Codex `turn/started`, `item/*`) while no other interval is open |

An `unknown` interval from the stdout reader closes **only** on the first terminal event after it
opened (Claude `result`, Codex `turn/completed`) or on confirmed process end (stdout EOF, or a kill
after `WaitForExitAsync` returned). `RequestRestart()`, reader cancellation, a `TryStopProcessAsync`
that returned `false`, silence and another lock acquisition do not close it. After 10 minutes open it
logs `journal_ledger_unknown_stuck` once and stays open.

The trade-off is chosen: a genuine human tool send is not journaled when it falls within 2 s of the
start or end of its own turn, within 2 s of any relay, bridge, `/run` or warmup activity, or while an
untracked provider turn is still unterminated. Relay and bridge sends are never journaled.

**Idempotency.** A duplicate receipt or a redelivery resolves on the natural key
`(conversation, tg:<messageId>)`: same observer and fingerprint answer `200 duplicate`.

**Known limit.** Any broker client can publish to an agent's receipt queue, as it can already publish
relay directives. The `botId` check blocks cross-bot rows; broker authentication is out of scope.

## Tokens and rotation

`cj1.<purpose>.<subject>.<mac>`, with `mac = base64url(HMAC-SHA256(key, "cj1|" + purpose + "|" + subject))`
unpadded. The subject is `^[A-Za-z0-9_-]{1,128}$` and names the publishing runtime; it becomes the
observer. Purposes accepted: `ingest`, `status`, `read` (slice 5). `ingest-service` is reserved and
refused. A `read` token carries no scope: what its subject may read is decided on the server.
There is no token table: identity is derived. Tokens are never logged or used as metric labels.

```bash
docker compose run --rm fleet-comms-ops journal token --purpose ingest --subject <runtime-name>
```

`Comms__Journal__TokenKeys` is a comma-separated list; every key verifies and the **first** mints.
Rotate: prepend the new key and recreate `fleet-comms`, re-mint every publisher's token, then remove
the old key and recreate again. Its tokens then answer `401`.

## Configuration

| .env key | service setting | default |
|---|---|---|
| `FLEET_COMMS_JOURNAL_ENABLED` | `Comms__Journal__Enabled` | `false` |
| `FLEET_COMMS_JOURNAL_BIND` | `Comms__Journal__Url` | `http://0.0.0.0:8083` |
| `FLEET_COMMS_JOURNAL_KEY` | `Comms__Journal__TokenKeys` (`fleet-comms` and `fleet-comms-ops` only) | empty |
| `FLEET_COMMS_JOURNAL_EXCLUDED_CHAT_IDS` | `Comms__Journal__ExcludedChatIds=${FLEET_GROUP_CHAT_ID},${…}` | empty |
| `FLEET_COMMS_JOURNAL_RETENTION` | `Comms__Journal__MessageRetention` | `365.00:00:00`, minimum `1.00:00:00` |
| `FLEET_COMMS_MEDIA_ENDPOINT` | `Journal:MediaEnabled` **on each agent** (see below) | `false` |
| `FLEET_COMMS_JOURNAL_ENABLED` | `Journal__ToolSendReceipts` on `fleet-telegram` (see [Tool sends](#tool-sends-slice-5)) | `false` |
| `FLEET_COMMS_JOURNAL_READ_ALL_SUBJECTS` | `Comms__Journal__ReadAllSubjects` (`fleet-comms`). Comma-separated read subjects with scope `all` | empty |

### `Journal:MediaEnabled` — the agent's half of the media switch

Two settings enable media, on two different containers, and only one of them is settable per agent:

| setting | container | what it does |
|---|---|---|
| `Comms__Media__Endpoint` | Comms | makes the upload route and the object store exist |
| `Journal:MediaEnabled` | each agent | makes the agent's drainer upload the bytes it captures |

**`Journal:MediaEnabled` is derived at provisioning, never configured per agent.** The orchestrator
writes it into the agent's `appsettings.json` when — and only when — this deployment's provisioning
env file sets `FLEET_COMMS_MEDIA_ENDPOINT` to a non-blank value
(`ContainerProvisioningService.MediaEndpointIsConfigured`).

The ENDPOINT ALONE is the gate, because that is the key compose treats as the media switch
(`Comms__Media__Endpoint=${FLEET_COMMS_MEDIA_ENDPOINT:-}`) and the key `upgrade.sh` uses to decide
whether to start the `comms-media` profile. One switch, three readers, no way to disagree.

⚠️ It deliberately does **not** also require `FLEET_COMMS_MEDIA_BUCKET`. That key is defaulted by
compose and was not written by `setup.sh`, so requiring it meant a fresh install that accepted media
provisioned every agent with the flag **off** — media silently off on a deployment with a working
bucket. `setup.sh` now records the bucket name too, so `grep FLEET_COMMS_MEDIA .env` shows the whole
decision, but the gate does not depend on that.

That derivation is the design, not a convenience. Nothing on the agent side can know whether a
bucket exists, and a flag nobody can set is a flag that is never on: without it the agent journals
every attachment as `not_archived(media_disabled)` forever while Comms happily accepts uploads, and
the media plane silently never runs. The agent's own env is denylisted for media credentials, so it
cannot be the source either — an agent must not read a secret to decide a boolean.

Because it is derived, **changing it means reprovisioning the agent**, not restarting it, and turning
media off for one agent is not possible without turning it off for the deployment. `upgrade.sh` prints
that reminder when media is on. The bucket name and the scoped credential pair are outside the gate
on purpose: an agent must not read a secret to decide a boolean, and a half-provisioned bucket is
Comms' startup failure to raise, not the agent's to guess at.

Every `FLEET_COMMS_JOURNAL_` key is denied by the orchestrator config API: it is never returned by
`/api/config/all` or `/api/config/values` and cannot be written by `set_config_values`. Edit `.env`.

The exclusion list is split on `,` and trimmed. Empty elements and `0` are ignored — `0` names no
chat, and it is what `setup.sh` writes for `FLEET_GROUP_CHAT_ID` when no group is configured — and
duplicates collapse. An element that is not an integer, or more than 256 distinct ids, is invalid.
The compose file always prepends `FLEET_GROUP_CHAT_ID`, the workflow-activity group.

Startup refuses, exiting 1 with a message that never contains a key: `journal_requires_conversations`,
`journal_key_invalid`, `journal_excluded_ids_invalid`, `journal_url_invalid`,
`journal_retention_invalid`, and `journal_bind_failed` for a port in use.

## Media (slice 4)

`Comms__Media__Endpoint` is the enabling key. Blank means media does not exist: no upload route, no
object store, no credentials held, and every attachment is `not_archived` exactly as in slice 1.

### Bytes are always proven

**There is no hash-only shortcut anywhere in the upload path.** An attachment can only name an
object whose bytes the attaching subject itself sent. That is the whole security property: without
it, a subject could name a digest it read somewhere and then `fetch_attachment` content it never
held. Dedup happens *after* proof and only ever moves an attachment onto an object the caller
already proved.

`journal_objects.owner` is the token subject that opened the row. It binds who may complete and
commit an upload and nothing else — **read access never depends on it.** `fetch_attachment` is
authorised by message observership (D4).

### Scoped acceptance amendment (#388, 2026-09-30)

Human-approved scope: AC1b's observer-authorized `fetch_attachment` read-denial check is not
required for this upload-only slice. An authenticated private deployment may authorize agents
without per-message observership; this is not a change to the broader public D4 read policy,
and this slice introduces no journal read API.

AC1b's digest-only commit refusal remains required. Private storage, agent authentication,
owner-bound uploads and byte proof are unchanged; UUID secrecy never substitutes for
authentication. Exact pushed-head isolated runtime acceptance remains required before merge,
including pinned-image identity, unmodified init/verifier execution, scoped `mc ls`,
media-enabled startup/S3 acceptance and verified teardown with final exit 0. No runtime waiver
or merge approval is granted by this amendment.

### Lifecycle

| Step | Answer |
|---|---|
| `POST /journal/v1/uploads {sha256, byteSize, mimeType}` | `201 {uploadId}`, row `uploading`, `owner = subject`. No dedup here. |
| `PUT /journal/v1/uploads/{id}` | same subject only; `404` (identical to unknown id) otherwise. Bytes stream through SHA-256 into the bucket. |
| mismatch, declared vs actual | object deleted, row `aborted`, `422 sha256_mismatch` |
| store error | `503`, row left `uploading` so the subject can retry |
| commit naming the `uploadId` | row `committed`, or deduped onto an existing committed object |

A foreign `uploadId` answers `404`, never `403`: a 403 would confirm the id exists and turn the
upload surface into an oracle for enumerating other runtimes' uploads.

### Sweeper

Runs on the existing GC tick, after retention.

| Class | Rule |
|---|---|
| abandoned | `uploading/uploaded/aborted` past `created_at + 24 h` |
| retired | `deleting` past `delete_after` (72 h grace) |
| orphan | weekly `ListObjectsV2 j1/`, keys older than 24 h with no row |

Bytes are deleted before the row, so a failure between the two leaves a row pointing at nothing —
which the next tick resolves, because a missing object is not an error. **A live attachment stops
every class absolutely**: a dedup loser is exactly the row whose bytes an attachment can still be
read through, and an age-based sweep must not delete them.

### Startup and degraded mode

A missing field exits 1. `HeadBucket` then classifies the answer:

- 403 / `InvalidAccessKeyId` / `SignatureDoesNotMatch` / `NoSuchBucket` / `NotFound` /
  `AccessDenied`, or HTTP 404 → exit 1 naming only the failure class. These need an operator, not a
  retry.
- network error or timeout → start **degraded**, re-probe every 30 s, uploads answer
  `503 media_unavailable`.
- An unsigned `GET {endpoint}/{bucket}?list-type=2` must answer 403. Anything else exits 1 with
  `bucket_public` — a world-readable bucket is not a degraded one.

⚠️ **`HeadBucket`, not `GetBucketLocation`, and the answer is not the error code you expect.**
`GetBucketLocation` needs `s3:GetBucketLocation`, which the shipped policy does NOT grant — and
MinIO answers a missing action with `403 AccessDenied`, so a probe using it reports a correctly
configured deployment as a rejected credential. `HeadBucket` is `s3:ListBucket`, which the policy
does grant on the bucket ARN.

Then the servers disagree about what a missing bucket means, and the classifier has to know all
three answers: a missing bucket answers `404 NotFound` with `ErrorCode` **`NotFound`** on SeaweedFS,
`NoSuchBucket` on real S3, and `AccessDenied` on MinIO. That is why the HTTP status leads and the
error-code list is a secondary match — matching `NoSuchBucket` alone is a startup that hangs
"degraded" on the servers that are telling you the bucket is gone.

### ⚠️ Three S3 facts that cost a full debug cycle

These are in `S3ObjectStore` with comments; they are here because every one of them passed a
fake-bucket test and failed against a real server.

1. **A `PutObject` length must be stated on the request.** The SDK reads `Headers.ContentLength`
   and, when it is absent and the stream is not seekable, fails client-side with "Could not
   determine content length" before sending anything. An HTTP request body is never seekable, so an
   upload cannot work without it. Setting it also makes the SDK *stop consulting* `Stream.Length` —
   so a stream reporting one length while the request states another uploads the request's number
   and reports success.
2. **The SDK takes the synchronous read path** when a content length is known. An ASP.NET Core
   request body throws on a sync read ("Synchronous operations are disallowed"), so a wrapping
   stream must delegate `Read` to `ReadAsync`.
3. **An empty listing has no `Contents` element**, which the SDK surfaces as a null collection, not
   an empty one. That is the first orphan sweep of every fresh deployment.

### Backup CLI

| Command | Does |
|---|---|
| `media backup --out DIR` | copies `committed`/`deleting` objects the bucket is missing, re-hashing each; writes `manifest-<UTC>.jsonl` atomically; non-zero on any failure |
| `media restore --in DIR` | `PutObject` for each missing object, then verifies |
| `journal verify-media [--sample N]` | `GetObject` + full re-hash of committed attachments; prints `rows= objects= bytes= mismatches= missing=`; exits 1 on any mismatch |

A dump that has never been restored is not a backup: `backup → wipe → restore → verify-media` is the
acceptance, and it is what proves the manifest rather than the manifest's own self-report.

### What the CI fixture proves, and what it still does not

`tests/Fleet.Conversations.Tests/S3Fixture.cs` runs SeaweedFS, because MinIO publishes no public
image source. CI runs **two** of them:

| fixture | env var | signs? | proves |
|---|---|---|---|
| anonymous | `FLEET_COMMS_S3_ENDPOINT` | no | the data plane: bytes in, identical bytes out, correct digest, correct key |
| signed | `FLEET_COMMS_S3_SIGNED_ENDPOINT` | yes | the credential classes, and the operator CLI |

An earlier version of this section claimed a signed fixture was impossible — that SeaweedFS without
a signing key answers a signed request with `400 InvalidRequest`, so testing credential rejection
against it would be a false green. **That claim was wrong and it cost coverage.** SeaweedFS
authenticates when started with `-s3.iam.config=<file>` naming an identity whose key lookup is
**exact**, and with it in place a signed PUT succeeds, a wrong secret answers a real
`403 AccessDenied`, and an unknown key is refused. The suite therefore does prove the classes the
startup probe classifies, against a server that actually checks the signature.

Three things the signed fixture still does not prove, so do not quote it as if it did:

- **It is SeaweedFS, not MinIO or real S3.** The error-code disagreements above are exactly why the
  classifier matches several answers; this fixture pins one of them.
- **The identity is an `Admin` identity**, because SeaweedFS has no IAM policy engine to express the
  shipped policy's narrower grants. A test that passes here proves the credential is *accepted and
  checked*, and that `HeadBucket` is an action the deployment's credential can perform — it does
  NOT prove `deploy/comms-minio-init/comms-runtime-policy.json` is scoped correctly.
- **MinIO itself has never been the subject of a startup test.** MinIO publishes no pullable image
  (Docker Hub and quay.io both refuse anonymous pulls, `dl.min.io` returns 410), so no CI job and no
  local run has ever started the shipped policy against the server it is written for. The
  `GetBucketLocation`/`HeadBucket` reasoning above is reasoned from the policy document and MinIO's
  documented behaviour, not measured. **The first deploy to a real MinIO is the proof of that
  reasoning**, and the thing to watch is `credentials_rejected` at startup on a bucket that exists.

The operator CLI (`media backup`, `media restore`, `journal verify-media`) builds its store through
the real constructor, which always signs — so those tests run against the **signed** bucket and
cannot run against the anonymous one. `EnsureSignedBucketAsync` PUTs before it probes: `HeadBucket`
on a bucket that does not exist answers `404`, and auto-creation happens on the first admin `PUT`.
A filer `POST /<bucket>/` creates a *filer* directory that S3 does not see, so it is not a way to
make a bucket.

## Read tools (slice 5)

A read-only MCP server over the journal: streamable HTTP, **stateless**, at `POST /journal/v1/mcp`
on the journal listener only (it exists only with the journal on; never north, south or ops, never a
host port). The endpoint URL agents use is `http://fleet-comms:8083/journal/v1/mcp`.

- **Auth** runs first, as on every journal route. The token is `cj1.read.<subject>`; no token, an
  `ingest` token or any other token gets the identical `401`. An authenticated `GET` or `DELETE`
  answers `405` with `Allow: POST` — a `401` there would push MCP clients into an OAuth flow. The
  8-in-flight cap per subject applies.
- **Grant, per agent only**: the `fleet-comms-journal` endpoint (S3 already provisions its bearer
  header) plus the tools `mcp__fleet-comms-journal__{search_messages,get_message,get_conversation}`.
  Never in a template, never auto-granted.

**Scope.**

| scope | rule | granted by |
|---|---|---|
| `observed` (default) | message M is readable iff a `journal_message_observers` row `(M, subject)` exists | any valid `read` token |
| `all` | every message | the subject is listed in `Comms__Journal__ReadAllSubjects` (`FLEET_COMMS_JOURNAL_READ_ALL_SUBJECTS`, comma-separated, exact match) |

Message-level observership is "the conversations the agent's own bot is in", exactly: a DM is all
observed by that agent, and in a group an agent never reads what its bot was not delivered. An agent
with read access but the journal off has observed nothing. The `all` key sits under the
`FLEET_COMMS_JOURNAL_` prefix the orchestrator config API denies, so no agent can widen its own
scope; an invalid subject in it exits 1 with `journal_read_grants_invalid` (position named, value
never printed). Scope is never encoded in the token.

**Tools** (all `readOnlyHint: true`, JSON output, ids are `journal_messages.id` /
`journal_conversations.id` ULIDs):

| tool | input | output |
|---|---|---|
| `search_messages` | `query?` (≤ 256 chars over `text` + `transcript`; operators `+-<>()~*"@` stripped, every term required, short terms and stopwords dropped, nothing left → `invalid_query`), `conversation_id?`, `sender_kind?`, `sender_id?`, `direction?`, `since?` / `until?` (ISO-8601 with offset on `sent_at`, half-open), `limit` 1–100 (20), `cursor?` | `items[]` with `text_preview` (≤ 500 chars), `text_truncated`, `has_transcript`, `attachment_count`; `next_cursor`. Newest first |
| `get_message` | `message_id`, or `telegram_chat_id` + `telegram_message_id` | the full record. The Telegram form with several in-scope matches → `ambiguous` with up to 10 candidate ids |
| `get_conversation` | `conversation_id`, `from_message_id?` (exclusive), `direction` `forward` (default) or `backward`, `limit` 1–200 (50), `cursor?` | `conversation{…}`, full records, `next_cursor`. A page also ends at 262,144 bytes of text + transcript, with at least one record |

Attachments are metadata only (`ordinal, kind, mime_type, byte_size, file_name, state,
not_archived_reason`) — never an object id, key, bucket, `sha256`, file unique id or URL.

**No oracle.** An out-of-scope id and a missing one answer the same bytes, `{"error":"not_found"}`
with `isError`. Search returns no totals. A conversation with no in-scope message is `not_found`, and
`search_messages` with a hidden or missing `conversation_id` returns empty `items` and a null cursor.
`reply_to.message_id` is `null` whether the target is missing or hidden. A replay anchor must be in
scope and in the given conversation; anything else answers exactly like a missing conversation.
Other errors: `invalid_cursor`, `invalid_query`, `invalid_argument{field}` and
`store_unavailable` (retryable, never a partial page).

**Pagination (no cross-page snapshot).** Keyset only on immutable keys — search on `(sent_at, id)`
descending (`ix_sent`), replay on `(order_key, id)` (`ix_order`), never `OFFSET`. Each page is one
READ COMMITTED query. The cursor is opaque base64url JSON `{v, tool, filterHash, conversationId?,
direction?, last:{key, id}}`, holds no server state and so survives a restart; reused with other
filters, another tool, conversation or direction it is `invalid_cursor`, decided from the cursor and
arguments alone. Scope is re-applied on every page. One traversal guarantees:

| # | Guarantee |
|---|---|
| G1 | No message appears twice |
| G2 | A message committed, in scope and matching both at traversal start and when the page covering its key is read is returned exactly once |
| G3 | A message that becomes visible mid-traversal (a delayed commit, a late spool drain with an old `sent_at`, a new observer row, a transcript that now matches `query`) is returned at most once, and only if its key is still ahead of the cursor |
| G4 | A record is shown as it stood when its page was read, and is never re-sent after a later update |

## Retention, purge and deletion semantics

The existing hourly garbage-collection tick deletes messages with `sent_at < now − retention` in
batches, their observers and attachments with them, then conversations left with no message. It runs
last in the tick; an error is logged, counted and retried next tick and never blocks the
conversation steps.

```bash
docker compose run --rm fleet-comms-ops journal purge --telegram-chat <chatId> [--before <ISO-8601>]
docker compose run --rm fleet-comms-ops journal purge --telegram-chat <chatId> --confirm
```

`--message <id>` and `--conversation <id>` select the same way. Without `--confirm` the purge runs
in a transaction that is rolled back, so it prints exactly what a confirmed run would delete and
changes nothing. A database error rolls back and exits 1.

A platform delete or edit does not reach bots, so the journal keeps the original until retention or
purge. Backups keep purged rows until your backup rotation removes them.

Both paths retire the archived objects their messages held, in the same transaction as the delete and
before it. The order is not stylistic: the attachment rows go by cascade, and after that nothing
connects an object to any message — the sweeper never sees it and the bucket keeps those bytes for the
life of the archive.

Retirement is guarded. One object can be referenced by several messages, because dedup points every
attachment with the same digest at the one committed object, so a delete that names one message must
not retire bytes another message is still serving. Only objects no surviving message references are
retired, and `journal_objects.committed_sha256` is released at the same moment: the unique key on that
column is what makes "one committed object per digest" a property of the schema, and a row already
scheduled for deletion must not keep claiming it while a re-send of the same bytes is still possible.

Retirement is a mark, not a delete. `state = 'deleting'` and `delete_after` are 72 hours out, and the
object sweeper takes the bytes after that — never before, and never while an attachment still points
at the object. `journal purge` counts those objects in the line it prints, because they are the only
number in it that is not yet gone.

## Status and metrics

`GET /journal/v1/status` (a `status` token) and `journal status` report the schema version and, per
observer, its message count and last ingest time. The route adds refusals since start by reason,
the last sweep and what it deleted, and ingest p50/p95 over the last hour. Never message text.

The route also reports `read.allScopeSubjects` (sorted) and `read.requestsSinceStart` (tool → result
→ count). The `journal status` CLI is a one-shot process and has no in-process counts.

Under the `Fleet.Conversations` meter: `fleet.journal.ingest{result}` (`created`, `duplicate`,
`observer_added`, `conflict`, `invalid`, `excluded`, `unavailable`), `fleet.journal.rejected{reason}`,
`fleet.journal.gc.deleted{kind}`, `fleet.journal.read{tool,result}` and the histograms
`fleet.journal.ingest.duration` and `fleet.journal.read.duration{tool}` (ms).

Agent counters (in-process `JournalCounters`, like the capture counters): `journal_excluded{reason}`,
`tool_send_captured`, `tool_send_unattributed`, `tool_send_non_human`, `tool_send_foreign_bot`,
`receipt_clock_skew`, `receipt_deferred_overflow`, `receipt_invalid`, and the one-off
`journal_ledger_unknown_stuck` warning. `fleet-telegram` counts `journal_receipts_published` and
`journal_receipts_dropped` and logs both in its drop warning, at most once a minute.

Working: `agent_tool` rows appear, and new outbound rows hold no `<blockquote expandable>`. Broken:
the unattributed or drop counters climb, or `dead/` grows with `invalid_record{origin}` (Comms older
than the agents).

`/ready` gets no new reason: its existing schema check reports `503 {status:"unhealthy", schema:…}`
until `conversations migrate` has applied 0004. Ingest reads the schema version itself and caches
only a current answer, so the first post after the migration succeeds.

## Backup and rollback

The journal tables live in the conversation database, so the existing conversation database dump
covers them.

- **Disable:** set `FLEET_COMMS_JOURNAL_ENABLED=false` and recreate `fleet-comms`. The listener is
  gone; the tables and rows stay.
- **Image rollback** below 0004 is not supported once it has applied: an older image reports the
  schema ahead and `/ready` answers 503. Roll forward, or restore the pre-migration dump and then
  roll back — the existing conversation-migration rule.
