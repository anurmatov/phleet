# One-shot Telegram message copying

`copy_message(message_id, to_chat_id)` copies one message without reading,
downloading, forwarding, re-uploading or changing its caption.

## Trust boundary

| Fact | Runtime check |
|---|---|
| Caller | Headerless, container-local `127.0.0.1:8092/telegram-copy/v1/mcp` |
| Source | The only active private Telegram human turn, `UserMessage` or `NewCommand`, with a non-null `TurnId` |
| Requester | Current task's user, equal to its private chat, still in the live allowlist |
| Recipient | Different allowlisted user/group chat, resolved through the polling bot's `getChat` |
| Consent | Matching single-use Copy tap from that requester, in the prompt's chat and message |
| Copy authorization | Same task instance and captured `TurnId`, not closed/cancelled, with live requester/recipient permissions rechecked under the lock |
| Bot | The agent's own polling client, never a notifier or fallback |

Recipient opt-in is **missing support**: v1 uses only existing allowlisted chats
reachable by this bot, and names the recipient in the runtime-authored prompt.

## Confirmation and deadlines

The prompt replies to the source message and offers **Copy** and **Cancel**;
one copy flow may be active per agent, and callbacks never enter task routing.

| Stage | Limit |
|---|---|
| Entire call | 50 seconds from entry |
| `getChat` | 5 seconds |
| Prompt | 5 seconds, refusing a missing reply target |
| Wait | Until 38 seconds from entry, shortened by earlier calls |
| `copyMessage` | 10 seconds, exactly once |
| Awaited answer/edit cleanup | One independent budget of min(2 seconds, time left to the original 50-second deadline), created when cleanup starts |

Request/task cancellation ends a waiting confirmation immediately; normal turn
closure or continuation is detected only at the next tap or the 38-second expiry,
without polling, and a changed `TurnId` cannot authorize a copy.

Cleanup still runs after cancellation, but cannot extend the original deadline;
a cancellation during copying is ambiguous and must never trigger another send.

## Outcomes and recovery

| Outcome | Action |
|---|---|
| `copied` | Use the returned destination message id |
| `scheduled` | Synthetic zero-id mapping only; live acceptance must confirm it or remove it before merge |
| `not_a_human_request`, `group_source_unsupported`, `destination_not_allowed`, `invalid_argument` | Respect the denial; do not bypass the source or permission checks |
| `busy` | Finish the existing confirmation first |
| `cancelled`, `confirmation_timeout` | Nothing copied; prompt is closed |
| `source_not_found`, `prompt_failed`, `destination_unreachable` | Check the source message, recipient and bot reachability |
| `protected_content`, `unsupported_message` | No download, forward or re-upload fallback |
| `rate_limited` | Inspect `retry_after`; no automatic retry |
| `telegram_rejected` | Inspect the bounded Telegram description |
| `ambiguous` | Ask the person to check the destination first; no retry |

Prompts, callbacks and copies produce no journal rows, media archives or tool-send
receipts, and results always carry `journal_recorded:false`; journal copy records
are **missing support**, not empty-message records.

## Grant, reprovision and rollback

Only an explicit per-agent `mcp__fleet-telegram-copy__copy_message` grant plus a
configured bot emits the canonical permission, `Telegram.MessageCopyEnabled:true`
and headerless loopback server; there is no template or automatic grant.

Grant/reprovision only after **both agent and orchestrator images from the merge**
are running; a stored `fleet-telegram-copy` endpoint row is reserved and refuses
before deprovisioning, while grant-without-bot warns `message_copy_unavailable:no_telegram_bot`.

For enabled agents only, the polling client's SDK retries are disabled to preserve
one-shot copying; agents without the grant retain their original retry policy,
configuration bytes and polling update list.

Remove the grant and reprovision to disable, or revert the code to roll back;
no schema, persisted state, host port, network, production config or journal key changes.

## Verification and operator signal

Before merge, the verification owner must use the exact PR-head canary, only a
dedicated test bot and two consenting test accounts, to verify text/photo copying,
identical captions and ids, Cancel/expiry, and actual Telegram error descriptions.

The operator signal is `Message copy finished: outcome=… destination_kind=… elapsed_ms=…`;
`MessageCopyCounter` is test-only and not exported, and logs never include content,
recipient labels or nonces.
