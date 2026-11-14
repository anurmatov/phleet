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
| tell the escalation target about the timeout | once, after Temporal has been told | yes, but it no longer comes first |
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

## A pass never waits for the relay

A heartbeat has a deadline and a publish does not, so the two are kept apart.

- **The pass** talks to Temporal only: heartbeat, fail on a budget, report a cancellation, deliver
  an answer Temporal could not be told earlier. What it learns it writes to the record.
- **The relay lane** sends what a record owes the outside: the directive (a continuation, the
  five-minute re-send, one that was recorded but never published), a cancel to the agent, a timeout
  notice. It reads what is owed from the record, sends, and writes back what was sent.

Neither a pass nor a reply waits for a send, its own record's included. A relay that is slow or
does not return delays relay effects and nothing else. What that means in numbers:

- at most 4 relay calls are unfinished at any moment, and one per record. Nothing waits in a
  queue: a call is made or it is not, and what is not made stays owed in its record and is looked
  at afresh when a call ends;
- the first directive of a delegation, which its activity publishes itself, counts as one of the
  4. If there is no room, the activity does not wait: the record is written, Temporal is told the
  activity completes later, the record is heartbeated from the next pass, and the directive is
  owed and sent by the lane when a call ends;
- a directive that has not been published after 10 seconds is told to stop; a cancel or a timeout
  notice after 30. The call is not abandoned. Its place, its record's next effect and the hold on
  its task id (below) are given back when the call has really ended, because until then it may
  still deliver. The relay honours the stop, so in practice that is at once;
- if the relay does not return at all, 4 hung calls stop every further send. What is owed stays in
  the state file and a restart sends it. Heartbeats are not affected;
- a cancel or a timeout notice is one best-effort attempt, as it was inside the activity: once its
  call has ended it is not tried again. A directive that failed is tried again after the next pass;
- a call that ends late changes nothing for a record that has since been completed, removed, or
  replaced by a later attempt under the same task id;
- a call cut short because the bridge is stopping does not count as sent.

### A cancel and the execution it was meant for

A later attempt of a workflow step gets the task id its predecessor had, so a task id cannot say
which attempt a cancel was for. Each durable delegation therefore has an **execution id**:

- minted once, when the record is created, and kept in the state file (`ExecutionId`), so it is the
  same after a bridge restart;
- written on every directive published for the record, as the optional field `ExecutionId` of the
  relay message: the first directive, a re-send, and a continuation. A continuation is a new task
  on the agent (`…/incomplete-retry-N`) and the same delegation, so it carries the same id;
- sent with every cancel issued for the record, as the optional query parameter `executionId` of
  `POST /api/agents/{name}/cancel/{taskId}`. The orchestrator passes it on to the agent's
  `POST /cancel/{taskId}` unchanged and returns the agent's answer as it came.

The agent keeps the id with the task for as long as the task is queued or running, and applies a
cancel that names an execution only to the task that carries the same id. Its answer echoes the id
and says what happened, and every answer is final:

| `outcome` | meaning |
|---|---|
| `cancelled` | the task was held under that id: running and told to stop, queued and removed, or not yet admitted and now never will be |
| `not_found` | nothing is held under the task id: it finished, or never arrived |
| `other_execution` | the task id now belongs to another execution; it was left alone |
| `no_execution_id` | the task id is held by a task whose directive carried no id; it was left alone |

A directive holds its task id a moment before it is queued or started. A cancel for its execution
that arrives in that moment is not answered `not_found`: the cancel and the admission are decided
under the same gate, so either the cancel is first, answers `cancelled`, and the directive is never
queued or run (nothing is published for it, and the task id is free at once), or the admission is
first and the cancel stops the task where it finds it.

A task without an id was started by the in-process path or by a bridge from before the id. It is
not the execution a cancel with an id was issued for, so that cancel leaves it; a cancel without an
id still stops it.

The bridge also keeps the order it kept before: **a directive is not published while a cancel for
the same task id is owed, still out, or may still arrive.**

- A record that replaces another under its task id carries any cancel the predecessor still owed
  (`PriorCancels` in the state file, with the predecessor's execution id) and sends it, to the end,
  before its own directive.
- While any cancel call for the task id is unfinished, or any call made for the predecessor, the
  directive waits. The activity is not kept waiting for this.
- The cancel goes bridge → orchestrator → agent over HTTP, and each waits for the next to answer.
  A 2xx therefore means the agent has dealt with the cancel, and the directive follows at once.
  Any other end leaves open whether it will still arrive, and directives for that task id are held
  for 30 seconds (`PublishNotBefore` in the state file).

With an agent that compares ids the order is not what protects a successor; it is what makes the
predecessor's work stop before the successor's begins. With an agent that does not, it is the only
protection there is.

A delegation that has an execution id is never cancelled by the `/cancel all` broadcast. That
fallback stops every task the agent has, whenever the agent reads it, which is the opposite of
naming one execution. If the orchestrator cannot be reached (or `TemporalBridge__OrchestratorUrl`
is not set) the cancel is not delivered: the agent finishes the work and its answer finds nothing
waiting.

#### An answer and the execution it came from

The same holds in the other direction: an answer names its task id, and a predecessor's answer that
arrives late has the task id its successor is waiting under. So the agent puts the execution id it
keeps with a task on every answer it sends for that task (final, partial, failed, incomplete,
idle), as the same optional `ExecutionId` field, written only when the task has one. The bridge
accepts an answer for a durable record only when that id is the record's:

| the record | the answer | what happens |
|---|---|---|
| has an id | names that id | accepted |
| has an id | names another id | not accepted. It is a predecessor's answer. The message is acknowledged and logged with identifiers only; the record is not completed or continued, nothing is added to a partial answer and nothing is kept for Temporal |
| has an id | names none | not accepted, the same way. See below |
| has none (written before the id) | anything | accepted by task id, as before |

An answer without an id comes from an agent on an image from before the id, or from a task that was
started by a directive without one. The bridge cannot tell which from an answer, so it does not
guess: by default (`DurableDelegation__RequireAnswerExecutionId=true`) the answer is refused. An
answer is then never matched by task id alone.

**This sets the rollout order: upgrade every agent image first, then turn durable delegation on.**
A delegation to an agent on an older image is never answered in a way the bridge accepts. It does
not complete, and ends when its budget runs out, as it does when an agent never answers. The log
names the task and the agent each time an answer is refused for this reason.

Setting `DurableDelegation__RequireAnswerExecutionId=false` restores the lenient behaviour: an
answer without an id is accepted by task id, with a warning each time. That keeps older agents
working, and for them it reopens the hole this closes: a predecessor's late answer can be taken for
its successor's.

The in-memory path (switch off, or a delegation that is not a durable record) matches an answer by
task id and ignores the field.

#### Services deploy separately

Every part is optional on the wire, nothing was renamed, and nothing is sent while the switch is
off.

| bridge | orchestrator | agent | what happens |
|---|---|---|---|
| new, switch on | new | new | the id is compared; a cancel reaches only its own execution, however late |
| new, switch on | new | old | **not supported by default.** The agent's answers name no execution and are refused, so its delegations end by timeout. Cancels are matched by task id, with the bridge's order and 30-second hold as the only protection. With `RequireAnswerExecutionId=false` the answers are accepted by task id and the stale-answer hole is open for this agent |
| new, switch on | old | new | answers are accepted (the agent names the execution). Cancels lose the parameter at the orchestrator and are matched by task id, protected by the order and the hold only |
| old, or switch off (in-process path) | any | new | no id is sent. Directives and cancels are handled exactly as before, by task id |
| new, record written before the id | any | any | the record has no id and is not given one; it sends what it always sent, broadcast fallback included |

The bridge cannot tell which row it is in when a cancel call fails, so it always keeps the hold. It
can tell on a 2xx: an answer without `executionId` means the id was not compared, and the bridge
logs that.

What this still does not cover:

- A directive that is dropped as a repeat does not change whose task it is. If a second delegation's
  directive arrives while the first one's task is still running, the task keeps the first id, and
  the second delegation's cancel does not stop it.
- For the same reason the second delegation does not get the first one's answer: it names the first
  execution. The second delegation's directive is sent again every five minutes, and is taken up
  once the first task has ended.
- The in-process wait (switch off) is unchanged. It sends its cancel by task id with no ordering,
  so the same late cancel can stop a successor there. The agent-side check makes fixing that a
  matter of sending an id from the activity.

### At start-up

Heartbeats start with the process. Relay sends do not: they are held until the bridge has fetched
its configuration from the orchestrator (the peer-config bootstrap), because until then it does not
know who the escalation target is or which chat its directives belong to. The bootstrap retries for
up to five minutes, which is longer than any heartbeat timeout, so the two cannot wait for each
other. A record that ran out of budget while the bridge was down is failed in Temporal at once and
keeps its timeout notice until the escalation target is known; the notice is then sent once. The
log says `relay sends are held` and `relay sends resumed`, once each.

## The switch

| setting | default | meaning |
|---|---|---|
| `FLEET_DURABLE_DELEGATION_ENABLED` → `DurableDelegation__Enabled` | `false` | off: the in-process wait, exactly as before |
| `DurableDelegation__StateFile` | `/var/lib/fleet-temporal-bridge/pending-delegations.json` | the records; empty keeps them in memory, which survives nothing |
| `DurableDelegation__TickSeconds` | `15` | seconds between passes; keep it well under the shortest heartbeat timeout |
| `DurableDelegation__RequireAnswerExecutionId` | `true` | an answer that names no execution is refused for a delegation that has one, so every agent must be on an image that names it. `false` accepts such answers by task id and reopens the stale-answer hole for older agents |

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
- **Temporal being away** costs a pass one bounded call (10 seconds) per namespace, not one per
  delegation. The broker or the orchestrator being away costs a pass nothing: see above.
- Not covered: local activities (none delegate today) keep the in-process wait.

Differences from the in-process wait, all deliberate:

- A continuation is recorded when the incomplete answer arrives and sent by the relay lane. One
  that cannot be published is tried again after the next pass. The activity used to fail there,
  discarding the partial answer.
- A cancelled or timed-out record stays in `/pending-delegations` (`cancelling`, `timed-out`) until
  its cancel or notice has gone out, although Temporal has already been told.
- A new attempt of a step whose previous attempt is being cancelled has its directive held until
  that cancel has ended. The activity used to publish at once, and the old cancel could stop the
  new attempt's work.
- A cancel after a continuation names the continuation task (`…/incomplete-retry-N`), which is the
  one the agent is running. It used to name the original task.
- A budget is checked on a pass, so a timeout is reported up to a pass late when Temporal or the
  loop is slow; the message still carries the time measured when it was noticed.

## Rollout

1. Merge and deploy with the key `false`. Nothing changes.
2. **Upgrade every agent image, and the orchestrator, before going further.** An agent on an image
   from before execution ids answers without one, and its delegations then end by timeout (see "An
   answer and the execution it came from"). Reprovision the agents and check each is on the new
   image. If an agent cannot be upgraded yet, either leave the feature off or set
   `DurableDelegation__RequireAnswerExecutionId=false` and accept what that reopens.
3. Pick a moment with no delegation in flight: no `ConsensusReviewWorkflow` running and no UWE run
   inside a delegate step. **The restart that turns the feature on loses delegations in flight**,
   because they were started by the old process and exist only in its memory. This is the last
   restart the old rule applies to.
4. Set `FLEET_DURABLE_DELEGATION_ENABLED=true` in `.env` and recreate `fleet-temporal-bridge`.
5. Check the log line `Durable delegation is on: state …` and that `/pending-delegations` reports
   `enabled: true, durable: true`.
6. Start a workflow that delegates, confirm it appears in `/pending-delegations`, restart the
   bridge, and confirm it is still there and completes when the agent answers. A delegation that
   stays pending while the log says an answer `names no execution and is not accepted` is an agent
   that was not upgraded.

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
