# Session-continuity scenarios

The authoritative scoping table for the real-adapter spike: which scenario runs at which layer,
through which per-provider seam, and which scenarios are runtime-only.

**Anchor commit: `ca90b205`.** Every source reference below is read at that commit, which is also
the commit this work branches from.

## Scenario → layer → seam

### Runtime-only — recorded ONCE, not per provider

These exercise `TaskManager` / `ConversationEventBus` behaviour that is identical regardless of
which executor produced the progress. Running them three times produces three identical rows and a
false impression of per-provider coverage, so they live here and are asserted exactly once each in
`RuntimeScenarioTests`. They deliberately have **no row in the capability matrix**: a per-provider
table is the wrong home for a result that has no provider in it.

| ID | Scenario | Layer | Driver | Observed |
|---|---|---|---|---|
| S7 | Cancel with a running task | L2 | `ScriptedExecutor` blocking until cancelled | `turn.started → submission.accepted(ran)`, then `control.ack(cancel)` and `turn.canceled(user)` **in either order** |
| S8 | Cancel with no running task | L2 | none (intake only) | `control.ack(cancel)` with `hadRunningTask: false`, and **nothing follows** |
| S11 | Turn leaves its loop without a terminal | L2 | `ScriptedExecutor` yielding nothing | `turn.final(completed)` — see the finding below |
| S12 | Terminal exceeds the 128 KiB serialized cap | L2 | `ScriptedExecutor` with non-ASCII final text | `turn.outcome_unknown(terminal_event_oversize)` |
| S15 | Telegram / relay / bridge conversation | L2 | `ScriptedExecutor`, runtime-owned channel id | **zero** adapter events; counted `not_routed` |

### Per-provider

`L1+L2` means the L1 output is captured and fed to L2 in the same test, so the assertion chain is
frame → `AgentProgress` → `ConversationEvent`. Results are in
`real-adapter-capability-matrix.md`.

| ID | Scenario | Layer | Claude seam | Codex seam | Gemini seam |
|---|---|---|---|---|---|
| S1 | Ordinary turn: start → tool → final | L1+L2 | `ExecuteAsync` via stand-in process + event channel | `StreamTurnForTests` + notification channel | `MapEvent` + accumulator; start/terminal **inferred** |
| S2 | Tool-only turn, no assistant text | L1+L2 | same | same | same; terminal **inferred** |
| S3 | Same-chat steer while turn running | L1+L2 | `TryInjectMessageAsync` over the stdin seam | `WaitAndCompleteNextPendingSteerForTests` | **unsupported** — no injection primitive |
| S4 | Steer after final-answer commitment | L1+L2 | `TurnCommittedToFinalAnswerForTests` | `TurnHasFinalAnswerPhaseForTests` | **unsupported** (as S3) |
| S5 | Queued coalescing, 2..10 parts | **L2 only** | `ScriptedExecutor` — **inferred** | ” | ” |
| S6 | 11th part (`MaxParts` = 10) | **L2 only** | `ScriptedExecutor` — **inferred** | ” | ” |
| S9 | Executor failure surfaced mid-turn | L1+L2 | `result` with `is_error` | `turn/completed` with `status: failed` | non-zero `ExitCode` branch — **inferred** |
| S10 | Process exit mid-turn, injections pending | **L2 only** | `ScriptedExecutor` emitting `IsProcessExit` — **inferred** | ” | ” |
| S13 | Nested subagent events | L1+L2 | `ParentToolUseId` fixtures | **unsupported** | **unsupported** |
| S14 | Stale answer preserved across drain | L1+L2 | `DrainStaleTurnEventsForTests` | **unsupported** | **unsupported** |
| S16 | Explicit restart between turns | L1+L2 | `RequestRestart` | `RequestRestart` | `RequestRestart`; terminal **inferred** |

An `unsupported` cell is **asserted, not skipped**: the test asserts the absence of the capability,
so a provider silently gaining it turns the test red and forces a matrix update.

## Findings this table produced

### S9 does not mean what the design assumed, and the spec's seam was unusable

The design named the Claude S9 seam as "event channel faulted". Faulting the channel is **not
reachable from a public test**: `ClaudeExecutor`'s read loop treats a closed channel as "the process
died mid-response", kills the process and retries — and the retry calls `EnsureProcessReady`, which
would start a real `claude` binary. MUST NOT #1 forbids that, and a test that quietly depends on a
local provider CLI passes on the author's machine and fails, or worse is skipped, everywhere else.

S9 therefore replays each provider's real **failure frame** instead: Claude's `result` with
`is_error`, Codex's `turn/completed` with `status: failed`, and Gemini's non-zero exit branch. That
is the failure a client actually meets, and it produced the more valuable finding:

> **An executor-reported failure surfaces as `turn.final` with `completion: incomplete` — never as
> `turn.error`.** A client that watches for `turn.error` to render failure will never see the most
> common provider failure there is. `turn.error` is reserved for a thrown executor and for the
> `internal` catch-all.

### S11 does not reach the reaper, and that is correct

An executor that yields nothing still reaches `TaskManager`'s "no text output" branch, which
publishes `turn.final(completed)` and therefore **disarms** the reaper. `turn_reaped` fires only on
a path that publishes nothing at all — which is exactly the design intent, because the reaper
covering an ordinary no-output turn would manufacture a failure signal on the single most common
no-op path in the runtime.

The scenario is still worth having: it pins the fact that "the executor produced nothing" is
reported to a client as a **completed** turn with empty text, not as an indeterminate one.

### `control.ack` and `turn.canceled` race, and neither order is a contract

S7 was written pinning `control.ack` ahead of `turn.canceled` and turned out to be flaky. The cause
is real, not a harness artifact: the ack is published by `ConversationIntake` on the caller's thread
after `HandleCancel` returns, while the terminal is published by the turn's own catch block on the
turn's thread. Either can win.

> A client must read `control.ack` as "the cancel REQUEST was accepted" — never as "the terminal has
> not arrived yet". `ControlAckPayload.HadRunningTask` is the field that says whether to expect a
> `turn.canceled` at all.

### Terminal events can overtake queued progress

The pump drains every terminal outbox before the shared progress channel on every pass, by design.
Across a multi-turn scenario a second turn's `turn.final` was observed arriving **ahead of** that
same turn's `turn.started`. `seq` is what makes this detectable. The matrix records emission order
and asserts delivery order is lawful; a client must not assume delivery order equals emission order.

## What is deliberately not here

- No `turn.delta` / incremental assistant text. The spike **recommends** it (see
  `voice-feasibility-protocol.md`); adding it is a separate protocol change.
- No seam on `GeminiExecutor`. Opening one is a `src/` change, tracked as a follow-up.
- No fix for tool completion, the Codex `ToolName` leak, or the missing incremental-text event.
  Each is pinned by a test so a later fix is a visible, deliberate change.
