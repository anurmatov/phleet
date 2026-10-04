# Primary-human priority

Set `FLEET_PRIMARY_HUMAN_USER_ID` in the deployment `.env` to one positive Telegram user id, then reprovision the bot agents. Blank means off and omits `Telegram.PrimaryHumanUserId`; malformed, zero, negative or overflowing values refuse provisioning with `primary_human_invalid` before an existing container is removed. Headless agents never get the field. This is not a peer-config key.

Only an allowed human's DM, dispatched group message or `/new` qualifies. The runtime checks Telegram `from.id`, `from.is_bot`, `sender_chat` and the live user/group allowlists. Text, usernames and display names do not confer priority. Relay, Bridge, CheckIn, debounced batches, reactions and client submissions stay routine. Commands such as `/stop` and `/cancel` still run before task queueing for every allowed user.

The running turn is never interrupted by priority. Same-chat human messages still use provider injection or the Inbox continuation before global queued work. An incoming `/new` always creates a separate entry and never injects or merges.

## Human steering of workflow turns

A human message reaches the agent's running turn, whatever the agent is doing, except when that turn is another human's task. Any allowed human qualifies, primary or not, by the same `from.id`, `from.is_bot`, `sender_chat` and live allowlist checks; only the router's regular-message path marks a message eligible. `/new`, reactions, debounced batches, relay/bridge and client submissions never steer.

The message first takes the normal queue path unchanged. Only when it is `Queued` does the runtime also send a text-only steering copy (a fixed header plus the prompt; media waits) into a running Relay or Bridge turn, found across every chat key. The workflow's callback, terminal event, binding and merged submissions do not change. The human's reply comes from their own queued turn in their own chat, prefixed to say the message was already seen.

For a fresh steering-eligible queue entry, the notice is chosen after the attempt: only a confirmed provider delivery gets `message delivered to my current task, reply pending here.`; every non-delivery keeps the existing busy text and enqueue-time position. Merged entries and suppressed groups get no extra notice, and an entry claimed before the notice is skipped. Each entry gets at most one notice attempt: send, lock-wait and logging failures are non-fatal, with no retry or busy fallback after a failed delivered notice. The send starts under the entry lock but is awaited after releasing it; steering-eligible `StartTask` completes after the transport's send timeout or completion, with no new timeout. The router discards that task, and existing awaiting callers do not enable steering; a future awaiting caller that does will see the notice latency. Delivery does not prove the model read or followed the message.

| running turn | a verified human's message |
|---|---|
| `UserMessage` / `DebouncedGroupBatch` in the same chat | injection or Inbox, as before |
| `UserMessage` / `DebouncedGroupBatch` / `NewCommand` in another chat | queued only |
| `NewCommand` in the same chat | queued only |
| CheckIn, any chat | queued only |
| Relay or Bridge, not yet steered | queued, then steered; the sender becomes the turn's steering owner |
| Relay or Bridge steered by the same (chat, user) | queued, then steered, up to 3 copies per turn |
| Relay or Bridge steered by another (chat, user) | queued only (`steer_refused_other_human`) |

Each decision logs `Steered human message into running {Source} task #{TaskId}: {outcome}` with no text. Counters: `steered_non_human_turn`, `steer_not_delivered`, `steer_refused_other_human`, `steer_answer_discarded`. A steering copy that Claude runs as its own turn is drained and discarded before the workflow callback; after a short drain, only `user`-tagged segments of the next turn's leading recovered answer are dropped. Whether the model obeys "do not reply here" is model behavior; the runtime guarantees only the structural split.

## Queue rules

Two in-memory FIFO lanes share one lock: 10 priority entries and 20 routine entries, with at most 10 parts per merged user entry. Dispatch takes at most three priority entries while routine work waits, then one routine entry. When no routine entry waits, that counter resets.

A primary arrival promotes every queued routine UserMessage/NewCommand entry of its chat together, in order, followed by a fresh entry when merging is impossible. If the complete move would exceed the priority cap, nothing moves: the part merges in place or a fresh entry goes to the routine tail. A full priority lane with no prior routine chat work also overflows to routine. Full routine capacity follows the existing queue-full notice and relay/bridge failure callback.

Busy notices use the entry's enqueue position: priority index, or priority count plus routine index. Promotion never sends a second notice. Non-primary parts do not promote or demote an existing priority entry. Cancellation scans both lanes without changing retained lane/sequence order; teardown preserves the priority carried by Inbox messages.

Primary `(chatId, telegramMessageId)` keys protect queued and running parts. Duplicate redelivery is dropped while protected and for ten minutes after dispatch. The 512-key cache evicts only inactive dispatched keys, oldest first; it never evicts queued/running keys. Clearing a never-dispatched entry releases its reservation. If all 512 keys are protected, a new distinct primary part follows the queue-full path rather than evicting a protected key or counting as a duplicate. Restart loses the queue and cache.

## Emission ownership (#277 §1.1 extension)

| send | owner | conversation event |
|---|---|---|
| Protected primary-key cache full | `TaskManager.StartTask` → `IMessageSink.SendTextByOriginAsync`, Human origin, existing queue-full text | `submission.accepted { queue_full }` |

| Fresh steering-eligible queue notice | `TaskManager.SendSteeringQueueNoticeAsync` → `IMessageSink.SendTextByOriginAsync`, Human origin, delivered receipt or existing busy text | none (non-journaled notice; disposition remains `queued`) |

These extensions add two sink call sites, for 28 in TaskManager. Client intake cannot classify a submission as primary, and the reserved-key sink guard remains in force.

## Diagnostics

`fleet_agent_queue_lane_total{result}` carries fixed outcomes only: `priority_enqueued`, `priority_promoted`, `priority_overflow_to_routine`, `promotion_refused_full`, `starvation_guard_dispatch`, `primary_duplicate_dropped`. No user, chat, message id or text appears in metric labels. Heartbeats report total queued entries, priority queued entries, and each preview's priority; journal-enabled agents also report `bindingFailed` as 0 or 1.

Rollback by clearing the key and reprovisioning; no database migration or tool grant is needed. Provider transcript and exact-head container acceptance are separate from deterministic runtime-double tests.
