# Durable delegation

A workflow hands a task to an agent through the temporal bridge and waits for the answer. Without
this feature the wait lives inside one activity execution in the bridge process, so restarting the
bridge fails every delegation in flight, and the agent's answer arrives afterwards with nothing
waiting for it. With it, the wait is a record on disk and the bridge can be restarted.

Off by default. With it off, nothing described here runs and the bridge behaves exactly as before.

## What changes when it is on

`DelegateToAgentActivity` no longer waits. It writes a record, publishes the directive and tells
Temporal the activity will be completed later (asynchronous activity completion). From then on
`DurableDelegationService` works from the record alone:

| what | when | same as before? |
|---|---|---|
| heartbeat the activity | every pass (`TickSeconds`, default 15) | yes; it was every 30 s |
| re-send the directive | every 5 minutes | yes |
| fail the activity with `TimeoutException` | the agent's budget runs out | yes, same message and error type |
| tell the escalation target about the timeout | once, at that moment | yes |
| continue an `incomplete` answer (`/incomplete-retry-N`) | when it arrives, up to `maxIncompleteRetries` | yes, same prompt and same joined text |
| tell the agent to stop | the workflow cancels the activity, or Temporal no longer has it | yes |
| complete the activity | the agent's answer arrives at the relay listener | yes, same result |

The workflow sees an ordinary activity. It schedules the same command with the same input, so runs
recorded before the change replay unchanged and no patch marker is involved. This applies to every
workflow that delegates: `UniversalWorkflow`, `ConsensusReviewWorkflow` and `NotifyCtoWorkflow` all
schedule the same activity.

After a restart the service reads the file and carries on: the first pass heartbeats every pending
delegation, and an answer that reached the broker while the bridge was down is consumed from the
bridge's durable queue and completes its activity.

## The switch

| setting | default | meaning |
|---|---|---|
| `FLEET_DURABLE_DELEGATION_ENABLED` → `DurableDelegation__Enabled` | `false` | off: the in-process wait, exactly as before |
| `DurableDelegation__StateFile` | `/var/lib/fleet-temporal-bridge/pending-delegations.json` | the records; empty keeps them in memory, which survives nothing |
| `DurableDelegation__TickSeconds` | `15` | seconds between passes; keep it well under the shortest heartbeat timeout |

Fresh setup writes the key as `false`; upgrade adds it only when it is missing. The state directory
is the one the workflow failure notifier already uses (`${FLEET_BASE_DIR}/temporal-bridge`).

`GET /pending-delegations` on the bridge (port 3001) lists what is waiting: task id, agent,
workflow, activity, continuation count, when the wait started, its deadline and when the directive
was last sent, plus `enabled` and `durable` (false when the file cannot be written).

## Limits

- **An outage longer than the heartbeat timeout still loses the delegation.** Temporal times out an
  activity nobody heartbeats: after 90 seconds for consensus reviews, 2 minutes for UWE delegate
  steps and `notify_cto`. The last heartbeat can be one pass old when the bridge stops, and the
  first pass of the new process runs as soon as it starts, so the new process has to be up within
  about a minute of the old one stopping. A container recreate fits; a host reboot
  or a long image build with the old container already stopped may not. When it does not, the
  activity fails as it would have before, the next pass finds it gone, tells the agent to stop and
  drops the record with one log line.
- **A delegation is one attempt.** `MaximumAttempts = 1` is unchanged: a retried attempt publishes
  the directive again, and an agent that had already finished would do the work twice.
- **The state file holds payload.** Instructions and partial answers are in it (mode `0600`), and
  it is as sensitive as the workflows are. Nothing from it is logged or returned by
  `/pending-delegations`: identifiers, counters and times only.
- **A file that cannot be read is moved aside**, to `pending-delegations.json.unreadable-<timestamp>`,
  and the bridge starts with no pending delegations. The delegations it held then time out. A
  record that cannot be acted on (no task token) is skipped and counted in the log.
- **A file that cannot be written** leaves the records in memory: delegations work and do not
  survive a restart. `/pending-delegations` reports `durable: false` and the log says so once.
- **An answer for a delegation Temporal cannot be told about** (Temporal unreachable) is written to
  the record and the message is acknowledged; a later pass completes the activity with it.
- **Temporal or the broker being away** costs a pass one bounded call each (10 seconds), not one
  per delegation, so heartbeats for the reachable ones are not starved.
- Not covered: local activities (none delegate today) keep the in-process wait.

Differences from the in-process wait, all deliberate:

- A continuation that cannot be published is kept and sent by a later pass. The activity used to
  fail there, discarding the partial answer.
- A cancel after a continuation names the continuation task (`…/incomplete-retry-N`), which is the
  one the agent is running. It used to name the original task.
- A budget is checked on a pass, so a timeout is reported up to a pass late when Temporal or the
  loop is slow; the message still carries the time measured when it was noticed.

## Rollout

1. Merge and deploy with the key `false`. Nothing changes.
2. Pick a moment with no delegation in flight: no `ConsensusReviewWorkflow` running and no UWE run
   inside a delegate step. **The restart that turns the feature on loses delegations in flight**,
   because they were started by the old process and exist only in its memory. This is the last
   restart the old rule applies to.
3. Set `FLEET_DURABLE_DELEGATION_ENABLED=true` in `.env` and recreate `fleet-temporal-bridge`.
4. Check the log line `Durable delegation is on: state …` and that `/pending-delegations` reports
   `enabled: true, durable: true`.
5. Start a workflow that delegates, confirm it appears in `/pending-delegations`, restart the
   bridge, and confirm it is still there and completes when the agent answers.

## Rollback

Set the key to `false` and recreate the bridge, again with nothing in flight: **delegations pending
in the file are not resumed while the feature is off**, so their activities time out. The file can
stay; if the feature is turned on again later, records whose activities are gone are dropped on the
first pass.

## What remains of "no bridge restart during consensus"

With the feature off, all of it: see `docs/epic-grants.md`.

With the feature on, a bridge restart no longer fails in-flight reviews or delegate steps, provided
the bridge is back within the heartbeat window above. What still applies:

- the restart that enables or disables the feature;
- an outage longer than the heartbeat timeout;
- anything else that rule protects that is not a delegation. Search attributes register at bridge
  start, and a relayed workflow signal that arrives while the bridge is down waits in its queue.

## Verification status

Covered by tests against a real local Temporal server (`DurableDelegationLocalTests`): a UWE run
whose delegation is in flight across a bridge restart completes with the agent's answer and its
history replays; the same restart with the feature off fails; passes keep an activity alive past
its heartbeat timeout; an outage longer than the timeout loses the delegation and drops the record;
a workflow cancel reaches the agent; a budget fails the activity the same way on both paths.

Not covered by any test: a real RabbitMQ broker (publishing and the listener's ack and requeue are
exercised only with substitutes), a real bridge container restart, and several delegations in
flight across a restart under load. Treat the first enable as the acceptance run.
