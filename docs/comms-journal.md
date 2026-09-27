# The conversation journal (slice 1)

A durable, channel-neutral record of the messages a human exchanges with an agent. This slice ships
the **store and the ingest contract**: a MySQL journal in the conversation database, an internal
HTTP listener that accepts text-only records from trusted publishers, and the operator, readiness
and status surfaces around them. It is **off by default**; with it off a deployment is unchanged.

## Purpose and non-goals

Telegram history today lives only in Telegram and in bounded in-memory buffers, and nothing is
replayable after a restart. The journal is the durable, queryable record.

Not in this slice, and not accepted by it: capture inside the agent runtime, media bytes or object
storage, read tools, capture of tool-initiated sends and copies, projection of first-party client
conversations, edits and deletions, cross-observer reads, backfill, and a metrics exporter.

## The listener

| | |
|---|---|
| Address | `Comms__Journal__Url`, default `http://0.0.0.0:8083` |
| Built | only when `Comms__Journal__Enabled=true`; otherwise nothing is bound, registered or started |
| Reachable | the container network only. It is **never** published as a host port and never proxied |
| Routes | `POST /journal/v1/messages` (token purpose `ingest`), `GET /journal/v1/status` (`status`) |

It is a separate application from north, south and ops. The journal routes are not mapped on any
of those, and the south bearer is not a journal credential. Authentication runs before routing and
before any body byte is read, on every path: an absent, malformed, bad-MAC or wrong-purpose token,
and any path that is not one of the two routes, all get the same `401 {"error":"unauthorized"}`.

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
| `origin` | `telegram_update` \| `agent_runtime` |
| `sendGroup` | optional `{id: ULID, part, parts}` linking the chunks of one long outbound reply; outbound only, `1 ≤ part ≤ parts ≤ 64` |
| `attachments[]` | at most 16; see below |

Unknown or duplicated fields, and a value of the wrong JSON type (a string where an integer belongs, an escaped lone surrogate), are `422 invalid_record{field}`. `observer` is not a field: **the observer is always the
token subject**, and a body `observer` is `422 invalid_record{observer}`.

**Attachments are metadata only**: `ordinal` (0–15, unique), `kind`
(`photo|document|voice|video|video_note|audio|animation|sticker|other`), `mimeType`, `byteSize?`,
`fileName?`, `fileUniqueId?`, and a required `notArchivedReason`
(`media_disabled|over_bot_api_limit|over_size_cap|unsupported_kind|download_failed|source_expired`).
Rows are stored `not_archived`, so a message is never visible with a dangling attachment. An
`uploadId`, `objectId` or `bytes` field is `409 media_disabled`.

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

One transaction per record, with the conversation row locked `FOR UPDATE`:

| case | response | writes |
|---|---|---|
| new natural key `(conversation, sourceKey)` | `201 {messageId, result:"created"}` | conversation (upsert), message, observer, attachments |
| `eventId` already recorded with the same fingerprint | `200 duplicate` | none |
| `eventId` already recorded with a different fingerprint | `409 idempotency_conflict{event_id_reused}` | none |
| same natural key and fingerprint, same observer | `200 duplicate` | none — not even a transcript this observer did not send first time |
| same natural key and fingerprint, new observer | `200 observer_added` | observer row; `transcript` filled only if still null |
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
- **Outbound**: one record per Bot API message Telegram accepted, with its format (`plain`, `html`,
  or `rich` carrying the Markdown source). A reply split into several messages shares one
  `sendGroup`. Relay and bridge output (`OutboundOrigin`) is excluded, including its images.
- **Spool**: `{WorkDir}/.fleet/journal-spool/{pending,media,dead}`; inbound media hardlinked from
  the attachment directory, outbound media copied. Limits: 10,000 records or 1 GiB; at the limit
  the new record is dropped. S2 sends every attachment as `not_archived(media_disabled)`.
- **Drainer**: one request at a time, oldest due record first, never FIFO-blocked; backoff 1 s
  doubling to 5 min; 15 s timeout; 30 s pause after 5 straight transport/5xx failures. `401` stalls
  everything, `404` pauses 5 min, `422 excluded_chat|unknown_conversation` drops, a refused record
  goes to `dead/` (kept 30 days). Redrive: move a file from `dead/` back to `pending/`.
- **Heartbeat**: `Journal{enabled,spoolDepth,oldestAgeSeconds,dropped,dead,authFailed}`, absent
  when the journal is off. Logs carry reason codes and record ids only — never text or the token.

## Tokens and rotation

`cj1.<purpose>.<subject>.<mac>`, with `mac = base64url(HMAC-SHA256(key, "cj1|" + purpose + "|" + subject))`
unpadded. The subject is `^[A-Za-z0-9_-]{1,128}$` and names the publishing runtime; it becomes the
observer. Purposes accepted: `ingest`, `status`. `read` and `ingest-service` are reserved and refused.
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

Every `FLEET_COMMS_JOURNAL_` key is denied by the orchestrator config API: it is never returned by
`/api/config/all` or `/api/config/values` and cannot be written by `set_config_values`. Edit `.env`.

The exclusion list is split on `,` and trimmed. Empty elements and `0` are ignored — `0` names no
chat, and it is what `setup.sh` writes for `FLEET_GROUP_CHAT_ID` when no group is configured — and
duplicates collapse. An element that is not an integer, or more than 256 distinct ids, is invalid.
The compose file always prepends `FLEET_GROUP_CHAT_ID`, the workflow-activity group.

Startup refuses, exiting 1 with a message that never contains a key: `journal_requires_conversations`,
`journal_key_invalid`, `journal_excluded_ids_invalid`, `journal_url_invalid`,
`journal_retention_invalid`, and `journal_bind_failed` for a port in use.

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

## Status and metrics

`GET /journal/v1/status` (a `status` token) and `journal status` report the schema version and, per
observer, its message count and last ingest time. The route adds refusals since start by reason,
the last sweep and what it deleted, and ingest p50/p95 over the last hour. Never message text.

Under the `Fleet.Conversations` meter: `fleet.journal.ingest{result}` (`created`, `duplicate`,
`observer_added`, `conflict`, `invalid`, `excluded`, `unavailable`), `fleet.journal.rejected{reason}`,
`fleet.journal.gc.deleted{kind}` and the histogram `fleet.journal.ingest.duration` (ms).

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
