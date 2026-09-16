# UWE signal-delivery mutation procedure

The verification plan for the parked-driver work requires that every load-bearing rule has a
mutation which turns the green suite red, shipped as a documented procedure with recorded results.
This file is that procedure.

The defect this work fixes was invisible: the signal was in workflow history, so it looked
delivered, and the workflow simply never woke up. Tests for that class have to assert on what the
engine *did* — the recorded command sequence — rather than on an outcome that a broken engine can
produce by accident. These mutations are how we know they do.

## Running it

One edit at a time. Apply, run the named filter, confirm **RED**, restore:

```bash
# apply the edit from the table, then:
dotnet test tests/Fleet.Temporal.Tests/Fleet.Temporal.Tests.csproj -c Release --filter "<filter>"
# restore the file
```

Never two at once — mutations can cancel each other out, and a pair that does reads as a survivor.
A mutation that fails to COMPILE has not been demonstrated: it tests the compiler, not the suite.

## The table

| # | Mutation | Test that must fail | Result |
|---|---|---|---|
| M1 | Remove the `else` in `HandleSignalAsync` (drop signals with no waiter) | T3 | RED |
| M2 | Discard the `TrySetResult` return value | — | **survives — see below** |
| M3 | Consume a buffered entry without removing it | buffer-removal test | RED |
| M4 | Write the sibling variable when `bindTo` is omitted | T13 | RED |
| M5 | Treat a non-object payload as a match | T14 | RED |
| M6 | Compare `bindTo` before the engine-timeout short-circuit | T7, T27 | RED |
| M7 | Treat `bindTo` present-but-empty as omitted | T15 | RED |
| M8 | Omit the buffer check at the escalation call site | T19 | RED |
| M9 | Snapshot the escalation epoch inside the `catch` | T19 | RED |
| M10 | Change the epoch comparison to `>=` | T22 | RED |
| M11 | Increment the arrival counter only on buffered writes | T22 | **survives — see below** |
| M12 | Delete a stale escalation entry when declining it | T20 | RED |
| M13 | Remove the `Workflow.Patched` gate | T17 (non-determinism) | RED |
| M14 | Make the buffer unbounded | T10 eviction half | RED |
| M15 | Evict the newest instead of the oldest | T10 retained half | RED |
| M16 | Throw instead of skipping on an empty `workflowId` | T24 fatal-failure variant | RED |
| M17 | Default `ignoreFailure` to `false` on the wakeup step | T11 | RED |
| M18 | Drop the definition-load validation call | loader validation test | RED |
| M19 | Let `set_variable` shadow the `workflow` scope | T25 | RED |
| M20 | Send a wakeup from a `changes_requested` branch case | T28 | RED |
| M21 | Drop the waiter id from the chain workflow's child args | T28 chain test | RED |
| M22 | Remove the load-time approver-gate check | reserved-signal load test | RED |
| M23 | Remove the execution-time approver-gate check | templated-gate refusal test | RED |
| M24 | Let `ignoreFailure` suppress the approver-gate refusal | not-suppressed test | RED |
| M25 | Make the correlation comparison always report match | T12, driver stale-rejection test | RED |
| M26 | Send the park notification again on resume | T1 | RED |

## Recorded run

Every mutation above was applied, tested and reverted, one at a time. **24 of 26 turn the suite
red**, and the two survivors are documented below rather than papered over.

The five newest rows (M22–M26) cover the approver-only-gate guard and the driver fixture, and all
five are red. M24 is the one worth singling out: it flips `ignoreFailure` back to suppressing the
refusal, and the step type defaults that flag to TRUE — so without the exclusion the authorization
check would be off by default and only a log line would be left behind. Baseline before and after: `dotnet test Fleet.sln -c Release` — all green, 0 failed,
0 skipped.

**Five mutations survived the first pass, and three of them were missing tests rather than missing
guards.** They are worth reading, because each one is a place where an obvious-looking test proves
less than it appears to:

- **M3** — consuming an entry without removing it is invisible unless a LATER wait asks for the
  same signal name. The test now parks twice on one buffered signal: the first consumes it, the
  second must actually park.
- **M15** — eviction chooses among entries already in the buffer, so an evict-newest policy still
  retains the incoming 17th. A test that parks on the newest name passes under both policies. It
  now parks on a middle entry, which only evict-oldest retains.
- **M16** — an empty target id throwing instead of skipping is hidden by this step type's
  `ignoreFailure: true` default: the dispatch layer swallows the throw and no signal is emitted
  either way. The difference only appears with `ignoreFailure: false`, which is also the case that
  pins the rule that the skip is independent of it. Worth knowing for the next test of this shape:
  under the mutation the .NET SDK turns the unhandled exception into a failed workflow **task** and
  retries it forever, so the test HUNG until its wait was bounded. Bound every await whose failure
  mode is "never completes", or the regression shows up as a stuck pipeline instead of a red test.

### M2 is a real guard with no test, and the reason is mechanical

Discarding the `TrySetResult` return value only loses a payload when a second signal with the SAME
name is delivered in the SAME workflow task as the first, while the waiter is registered but
already completed. Three signals sent at a parked workflow do not reproduce it: each arrives as its
own task, and by the second the waiter is gone, so the payload takes the ordinary no-waiter path
whether or not the return value is honoured.

The only way to force two deliveries into one task from a test client is to send both while no
worker is polling and then start one. That was implemented and **hung the time-skipping
environment** — the test server advances logical time while nothing polls, and the stop/start dance
wedged. A test that hangs is worse than no test: it converts a fast failure into a stuck pipeline,
so it was removed rather than left in with a timeout papering over it.

So this line is defense-in-depth backed by reading rather than by a red-then-green cycle, and it is
recorded that way. The loss it prevents is real and documented in the design; what is missing is a
deterministic way to stage it, not an argument that it cannot happen. Anyone who finds one should
add the test and update this row.

### M11 survives by design, and the design says why

"Increment the arrival counter only on buffered writes" leaves every behavioural test green, and no
test was added for it, because **the property it breaks is not behavioural**.

The counter orders arrivals so an escalation can tell a reply sent during the current attempt from
one left over from an earlier round. Delivered signals advancing it too changes the absolute values
but not any comparison: entries buffered before an epoch stay before it, entries after stay after,
and eviction order between buffered entries is unchanged. What a buffer-only counter actually
destroys is the ordinal's meaning in a log line — which is exactly what the issue's own MUST NOT
says it protects, and which no assertion on workflow behaviour can observe.

Recorded as a deliberate survivor rather than papered over with a test that asserts an internal
field. The alternative — reaching into private state to assert a number — would pass whatever the
code did, which is worse than an honest gap.

## What to do when a mutation survives

Ask which mechanism made it harmless, then ask whether the test would notice if that mechanism were
the only thing left. Three of the five above were passing for the right reason by accident, and the
mutation is the specification of the assertion nobody wrote. The other two are honest gaps, stated
as gaps — a survivor that gets explained is worth more than one that gets a test written around it
until it goes green.
