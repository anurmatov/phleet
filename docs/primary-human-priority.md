# Primary-human priority

Set `FLEET_PRIMARY_HUMAN_USER_ID` in the deployment `.env` to one positive Telegram user id, then reprovision the bot agents. Blank means off and omits `Telegram.PrimaryHumanUserId`; malformed, zero, negative or overflowing values refuse provisioning with `primary_human_invalid` before an existing container is removed. Headless agents never get the field. This is not a peer-config key.

Only an allowed human's DM, dispatched group message or `/new` qualifies. The runtime checks Telegram `from.id`, `from.is_bot`, `sender_chat` and the live user/group allowlists. Text, usernames and display names do not confer priority. Relay, Bridge, CheckIn, debounced batches, reactions and client submissions stay routine. Commands such as `/stop` and `/cancel` still run before task queueing for every allowed user.

The running turn is never interrupted by priority. Same-chat human messages still use provider injection or the Inbox continuation before global queued work. A human message cannot enter a workflow turn or another chat's turn. An incoming `/new` always creates a separate entry and never injects or merges.

## Queue rules

Two in-memory FIFO lanes share one lock: 10 priority entries and 20 routine entries, with at most 10 parts per merged user entry. Dispatch takes at most three priority entries while routine work waits, then one routine entry. When no routine entry waits, that counter resets.

A primary arrival promotes every queued routine UserMessage/NewCommand entry of its chat together, in order, followed by a fresh entry when merging is impossible. If the complete move would exceed the priority cap, nothing moves: the part merges in place or a fresh entry goes to the routine tail. A full priority lane with no prior routine chat work also overflows to routine. Full routine capacity follows the existing queue-full notice and relay/bridge failure callback.

Busy notices use the entry's enqueue position: priority index, or priority count plus routine index. Promotion never sends a second notice. Non-primary parts do not promote or demote an existing priority entry. Cancellation scans both lanes without changing retained lane/sequence order; teardown preserves the priority carried by Inbox messages.

Primary `(chatId, telegramMessageId)` keys protect queued and running parts. Duplicate redelivery is dropped while protected and for ten minutes after dispatch. The 512-key cache evicts only inactive dispatched keys, oldest first; it never evicts queued/running keys. Clearing a never-dispatched entry releases its reservation. If all 512 keys are protected, a new distinct primary part follows the queue-full path rather than evicting a protected key or counting as a duplicate. Restart loses the queue and cache.

## Emission ownership (#277 §1.1 extension)

| send | owner | conversation event |
|---|---|---|
| Protected primary-key cache full | `TaskManager.StartTask` → `IMessageSink.SendTextByOriginAsync`, Human origin, existing queue-full text | `submission.accepted { queue_full }` |

This adds one sink call site, for 27 in TaskManager. Client intake cannot classify a submission as primary, and the reserved-key sink guard remains in force.

## Diagnostics

`fleet_agent_queue_lane_total{result}` carries fixed outcomes only: `promotion_refused_full`, `priority_overflow_to_routine`, `primary_duplicate_dropped`. No user, chat, message id or text appears in metric labels. Heartbeats report total queued entries, priority queued entries, and each preview's priority; journal-enabled agents also report `bindingFailed` as 0 or 1.

Rollback by clearing the key and reprovisioning; no database migration or tool grant is needed. Provider transcript and exact-head container acceptance are separate from deterministic runtime-double tests.
