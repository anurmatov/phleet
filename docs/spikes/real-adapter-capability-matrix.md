# Real-adapter capability matrix

What a first-party client actually receives today, per provider, per scenario — measured, not
assumed.

**Anchor commit: `ca90b205`** (`feat(uwe): park a driver on a human gate and wake it when the gate
clears (#281)`). Every source reference in this document and its siblings is read at that commit,
which is also the commit this work branches from.

Every row here is produced by a test. `ProviderCapabilityMatrixTests` parses this file and asserts
row set ↔ asserted set in **both** directions, and each scenario asserts its own observation
against the row parsed here — so the document cannot drift from the code in either direction.

## The honesty rule

L1 (parse-layer replay) and L2 (runtime projection) prove the **mapping given a frame shape**. They
do not prove the frame shape is what the pinned CLI emits. Only an L3 live capture does, and L3 is
out of scope for this issue and runs in private Fleet.

So **every `evidence` cell in this file reads `fixture-only`**. A green CI run is not provider
evidence. A cell may only become `verified@<YYYY-MM-DD>/<cli-version>` after a dated L3 pass naming
the exact CLI version, and the parser rejects any other shape.

## Cell grammar

`scenario | provider frames | agent progress | client events | verdict | evidence | note`

- **scenario** — an `S`-id from `session-continuity-scenarios.md`, or a `G`-id from the
  cross-cutting capability rows below. Bare, e.g. `S1`.
- **provider frames** — `+`-joined frame discriminators in emission order, backtick-quoted. Claude
  uses `type` or `type/subtype`; Codex uses the notification `method`, or the `item.type` for
  `item/*` methods (so a started/completed pair appears as the same discriminator twice); Gemini
  uses the stream `type`, refined by `role` or `status`. `—` means no frame was involved.
- **agent progress** — `+`-joined `AgentProgress.EventType` values in emission order, each
  optionally suffixed with a parenthesised discriminator from a closed set: `(sig)`, `(tool=<name>)`,
  `(final)`, `(err)`, `(exit)`. No free text.
- **client events** — TWO independently-ordered groups separated by ` ‖ `: the dispatch
  dispositions, then the turn's own events. Within each group, `→`-joined `ConversationEventKind`
  wire values with payload discriminators drawn from the enum wire values. **Order within a group is
  part of the value; order ACROSS the separator is explicitly not.**
- **verdict** — exactly one of `supported` / `inferred` / `unsupported` / `leaky`.
- **evidence** — `fixture-only`, or `verified@<YYYY-MM-DD>/<cli-version>`.
- **note** — required whenever the verdict is not `supported`.

### Two measured corrections to the cell grammar

Both were found by running the harness, and both are recorded here rather than quietly absorbed.

**1. `client events` is TWO independently-ordered groups, not one sequence.** The design specified
delivery order. That turned out to be undeliverable, and it took two CI failures to find the
bottom of it.

*Terminals overtake progress.* The pump drains every terminal outbox **before** the shared
progress channel on every pass, by design (`ConversationEventPump.DrainOnceAsync`). A second
turn's `turn.final` was observed arriving ahead of that same turn's `turn.started`.

*Concurrent publishers interleave.* `seq` is assigned inside `Publish` and the channel write
happens afterwards, and the two are not one atomic step. The caller thread, the turn thread and
the four-second typing loop publish independently, so they can take `seq` 5 and 6 and then write
6 before 5. Weakening the cell from delivery order to emission order did not fix this, and an
assertion that delivered non-terminals ascend among themselves — and terminals likewise — passed
four local runs and then failed on the CI runner. That assertion has been removed; it described a
guarantee the bus does not make.

*A disposition is not ordered against the turn it dispatched.* CI failed a second time, on
`codex/S9`, with `turn.final` holding a **lower** `seq` than the `submission.accepted` for the
very submission it answered. `TaskManager.StartTaskCore` publishes `turn.started`, hands the turn
to `Task.Run`, and only **then** calls `ReportDisposition`, so a short turn can finish before the
caller thread reports its own dispatch. Ordering by `seq` cannot repair that, because the `seq`
values themselves are assigned in that order.

> **A client must not use `submission.accepted` as a checkpoint.** It can arrive after the
> `turn.final` that answers it. Correlate on `submissionId` and treat the disposition as metadata
> about dispatch, not as a position in the stream.

So the cell stops pretending there is one sequence. Dispositions and turn events are separated by
` ‖ `; each group is internally ordered and genuinely deterministic — dispositions by sequential
dispatch, turn events by the single turn thread, with `turn.started` ahead of them because it is
published before `Task.Run` — and the separator marks where ordering stops being a claim.

Each scenario then asserts exactly the three properties the bus does guarantee, and nothing more:

1. **`seq` is unique per conversation.** Assignment goes through an atomic per-conversation
   counter, so a duplicate would mean two events claiming one slot.
2. **Every event is delivered at most once.** A repeated `eventId` would be a double delivery,
   which no client dedupe could tell apart from a redelivery.
3. **A turn's terminal is sequenced after that turn's own `turn.started`.** Per turn id, not
   globally. This one is causal rather than racy, because the turn thread only begins after
   registration has published `turn.started`, and it is the only cross-kind ordering fact the
   runtime really does promise.

`MatrixCells.AssertDeliveryOrderIsLawful` is those three properties and nothing else.

**2. The periodic typing heartbeat is excluded from the cell.** `turn.progress(typing)` is emitted
at turn start and then every four seconds for as long as the turn runs
(`TaskManager.RunTypingLoopAsync`), so its multiplicity is a function of elapsed time, not of
provider behaviour. Including it would make every ordered cell a timing assertion. Each scenario
asserts it separately.

## Ordering finding that applies to every row

**`turn.started` is emitted before `submission.accepted`, and the disposition can arrive after the
turn has already finished.** `TaskManager` publishes `turn.started` at registration, dispatches the
turn, and reports the dispatch disposition last. A client therefore learns a turn began — and may
learn it ENDED — before it learns its own submission was accepted. That is why the `client events`
cell separates the two groups with ` ‖ `; a client that waits for `submission.accepted` before
rendering turn state renders late, and one that treats it as a stream position is simply wrong.

## claude

| scenario | provider frames | agent progress | client events | verdict | evidence | note |
|---|---|---|---|---|---|---|
| S1 | `system/init` + `assistant/tool_use` + `user/tool_result` + `assistant/text` + `result` | `prompt_accepted` + `system` + `assistant`(sig,tool=Read) + `user` + `assistant`(sig) + `result`(sig,final) | submission.accepted(ran) ‖ turn.started → turn.progress(tool) → turn.final(completed) | supported | fixture-only |  |
| S2 | `system/init` + `assistant/tool_use` + `user/tool_result` + `result` | `prompt_accepted` + `system` + `assistant`(sig,tool=Read) + `user` + `result`(sig) | submission.accepted(ran) ‖ turn.started → turn.progress(tool) → turn.final(completed) | supported | fixture-only |  |
| S3 | — | — | submission.accepted(ran) → submission.accepted(injected) ‖ turn.started → turn.final(completed) | inferred | fixture-only | no provider acknowledgement frame exists — the steer is a stdin write and nothing comes back. |
| S4 | `assistant/text` | — | submission.accepted(ran) → submission.accepted(queued) ‖ turn.started → turn.final(completed) → turn.started → turn.final(completed) | supported | fixture-only | the refusal is caused by a real frame: an assistant TEXT frame commits the turn to its final answer, and the injection is then refused with `NoActiveTurn`. |
| S5 | — | — | submission.accepted(ran) → submission.accepted(queued) → submission.accepted(queued) ‖ turn.started → turn.final(completed) → turn.started → turn.final(completed) | inferred | fixture-only | scripted progress, no frame replay |
| S6 | — | — | submission.accepted(ran) → submission.accepted(queued) → submission.accepted(queued) → submission.accepted(queued) → submission.accepted(queued) → submission.accepted(queued) → submission.accepted(queued) → submission.accepted(queued) → submission.accepted(queued) → submission.accepted(queued) → submission.accepted(queued) → submission.accepted(queued) ‖ turn.started → turn.final(completed) → turn.started → turn.final(completed) → turn.started → turn.final(completed) | inferred | fixture-only | scripted progress, no frame replay; the 11th part exceeds `MaxParts` = 10 and forms a second continuation turn. |
| S9 | `system/init` + `result(is_error)` | `prompt_accepted` + `system` + `result`(sig,final,err) | submission.accepted(ran) ‖ turn.started → turn.final(incomplete) | supported | fixture-only | an executor-reported failure surfaces as `turn.final` with `completion: incomplete`, NOT as `turn.error`. A client watching for `turn.error` never sees the most common provider failure. |
| S10 | — | — | submission.accepted(ran) → submission.accepted(injected) ‖ turn.started → turn.started → turn.final(completed) | inferred | fixture-only | scripted progress, no frame replay; the redelivered submission runs under a new turn id and `attempt` 2. |
| S13 | `assistant/tool_use` + `system/task_started` + `assistant/text(nested)` + `system/task_notification` + `assistant/text` + `result` | `prompt_accepted` + `assistant`(sig,tool=Agent) + `system` + `assistant` + `system` + `assistant`(sig) + `result`(sig,final) | submission.accepted(ran) ‖ turn.started → turn.progress(tool) → turn.final(completed) | supported | fixture-only | nested assistant text is mapped insignificant and never becomes the parent answer; the client sees only the parent tool progress. |
| S14 | `assistant/text` + `assistant/text` + `assistant/text` + `result` | `recovered_answer`(sig) + `result`(sig,final) | submission.accepted(ran) ‖ turn.started → turn.recovered_answer → turn.final(completed) | supported | fixture-only | the stale answer survives the drain and reaches the client as `turn.recovered_answer`, which arrives with no visible question attached. |
| S16 | — | — | submission.accepted(ran) → submission.accepted(ran) ‖ turn.started → turn.final(completed) → turn.started → turn.final(completed) | inferred | fixture-only | restart is a local process action; no provider frame acknowledges it |
| G1 | `tool completion` | `user` | turn.progress(tool) | unsupported | fixture-only | the provider DOES emit a completion frame; the runtime maps it with `IsSignificant = false`, and `TaskManager` publishes `turn.progress` only when `IsSignificant && ToolName is not null`. A client sees a tool START and never a tool FINISH, so a per-tool spinner has no event that clears it. |
| G2 | `assistant/text` + `assistant/text` + `assistant/text` + `result` | `assistant`(sig) + `assistant`(sig) + `assistant`(sig) | turn.final(completed) | unsupported | fixture-only | `ConversationEventKind` has no delta kind. Three assistant chunks in, exactly one `turn.final` out — a client cannot begin rendering, or speaking, before the whole answer exists. |

## codex

| scenario | provider frames | agent progress | client events | verdict | evidence | note |
|---|---|---|---|---|---|---|
| S1 | `turn/started` + `mcpToolCall` + `mcpToolCall` + `agentMessage` + `turn/completed` | `system` + `tool_use`(sig,tool=read_file) + `tool_result` + `result`(sig,final) | submission.accepted(ran) ‖ turn.started → turn.progress(tool) → turn.final(completed) | supported | fixture-only |  |
| S2 | `turn/started` + `mcpToolCall` + `mcpToolCall` + `turn/completed` | `system` + `tool_use`(sig,tool=read_file) + `tool_result` + `result`(sig,final) | submission.accepted(ran) ‖ turn.started → turn.progress(tool) → turn.final(completed) | supported | fixture-only |  |
| S3 | `turn/steer` | — | submission.accepted(ran) → submission.accepted(injected) ‖ turn.started → turn.final(completed) | supported | fixture-only |  |
| S4 | `agentMessage` | — | submission.accepted(ran) → submission.accepted(queued) ‖ turn.started → turn.final(completed) → turn.started → turn.final(completed) | supported | fixture-only | the refusal is caused by a real frame: an `agentMessage` with `phase: final_answer`. |
| S5 | — | — | submission.accepted(ran) → submission.accepted(queued) → submission.accepted(queued) ‖ turn.started → turn.final(completed) → turn.started → turn.final(completed) | inferred | fixture-only | scripted progress, no frame replay |
| S6 | — | — | submission.accepted(ran) → submission.accepted(queued) → submission.accepted(queued) → submission.accepted(queued) → submission.accepted(queued) → submission.accepted(queued) → submission.accepted(queued) → submission.accepted(queued) → submission.accepted(queued) → submission.accepted(queued) → submission.accepted(queued) → submission.accepted(queued) ‖ turn.started → turn.final(completed) → turn.started → turn.final(completed) → turn.started → turn.final(completed) | inferred | fixture-only | scripted progress, no frame replay; the 11th part exceeds `MaxParts` = 10 and forms a second continuation turn. |
| S9 | `turn/started` + `turn/completed` | `system` + `result`(sig,final,err) | submission.accepted(ran) ‖ turn.started → turn.final(incomplete) | supported | fixture-only | an executor-reported failure surfaces as `turn.final` with `completion: incomplete`, NOT as `turn.error`. A client watching for `turn.error` never sees the most common provider failure. |
| S10 | — | — | submission.accepted(ran) → submission.accepted(injected) ‖ turn.started → turn.started → turn.final(completed) | inferred | fixture-only | scripted progress, no frame replay; the redelivered submission runs under a new turn id and `attempt` 2. |
| S13 | — | — | — | unsupported | fixture-only | no nested-turn concept: the executor exposes no parent/nested/subagent member, so there is nothing a client could be told. |
| S14 | — | — | — | unsupported | fixture-only | a drain exists (`DrainInterruptedTurnAsync`) but it preserves no answer text, so nothing can be recovered. |
| S16 | — | — | submission.accepted(ran) → submission.accepted(ran) ‖ turn.started → turn.final(completed) → turn.started → turn.final(completed) | inferred | fixture-only | restart is a local process action; no provider frame acknowledges it |
| G1 | `tool completion` | `tool_result` | turn.progress(tool) | unsupported | fixture-only | the provider DOES emit a completion frame; the runtime maps it with `IsSignificant = false`, and `TaskManager` publishes `turn.progress` only when `IsSignificant && ToolName is not null`. A client sees a tool START and never a tool FINISH, so a per-tool spinner has no event that clears it. |
| G2 | `turn/started` + `item/agentMessage/delta` + `item/agentMessage/delta` + `item/agentMessage/delta` + `agentMessage` + `turn/completed` | `assistant`(sig) + `assistant`(sig) + `assistant`(sig) | turn.final(completed) | unsupported | fixture-only | `ConversationEventKind` has no delta kind. Three assistant chunks in, exactly one `turn.final` out — a client cannot begin rendering, or speaking, before the whole answer exists. |
| G3 | `turn/started` + `commandExecution` + `commandExecution` + `agentMessage` + `turn/completed` | `tool_use`(sig,tool=grep -rn --include=*.cs "synthetic-marker" /workspace/example/src /workspace/example/tests) | turn.progress(tool) | leaky | fixture-only | `ToolName` is the executed shell command, not a tool name. `BoundToolName` only BOUNDS it to 64 UTF-16 units — it does not classify it — so the client receives the first 64 characters of a real command, which routinely contains absolute paths and can contain secret-shaped values. Pinned, deliberately NOT fixed here. |

## gemini

| scenario | provider frames | agent progress | client events | verdict | evidence | note |
|---|---|---|---|---|---|---|
| S1 | `init` + `message/user` + `tool_call` + `tool_result` + `message/assistant` + `result/success` | `system` + `tool_use`(sig,tool=read_file) + `tool_result` + `assistant` + `result`(sig,final) | submission.accepted(ran) ‖ turn.started → turn.progress(tool) → turn.final(completed) | inferred | fixture-only | terminal constructed in ExecuteAsync; no seam below ExecuteAsync at `ca90b205` |
| S2 | `init` + `tool_call` + `tool_result` + `result/success` | `system` + `tool_use`(sig,tool=read_file) + `tool_result` + `result`(sig,final) | submission.accepted(ran) ‖ turn.started → turn.progress(tool) → turn.final(completed) | inferred | fixture-only | terminal constructed in ExecuteAsync; no seam below ExecuteAsync at `ca90b205`; the terminal carries empty text. |
| S3 | — | — | submission.accepted(ran) → submission.accepted(queued) ‖ turn.started → turn.final(completed) → turn.started → turn.final(completed) | unsupported | fixture-only | no injection primitive at all: `GeminiExecutor` declares no `TryInjectMessageAsync`, so the interface default returns `Unsupported` and the submission is queued. |
| S4 | — | — | submission.accepted(ran) → submission.accepted(queued) ‖ turn.started → turn.final(completed) → turn.started → turn.final(completed) | unsupported | fixture-only | same absence as S3 — there is no turn to steer, before or after a final answer. |
| S5 | — | — | submission.accepted(ran) → submission.accepted(queued) → submission.accepted(queued) ‖ turn.started → turn.final(completed) → turn.started → turn.final(completed) | inferred | fixture-only | scripted progress, no frame replay |
| S6 | — | — | submission.accepted(ran) → submission.accepted(queued) → submission.accepted(queued) → submission.accepted(queued) → submission.accepted(queued) → submission.accepted(queued) → submission.accepted(queued) → submission.accepted(queued) → submission.accepted(queued) → submission.accepted(queued) → submission.accepted(queued) → submission.accepted(queued) ‖ turn.started → turn.final(completed) → turn.started → turn.final(completed) → turn.started → turn.final(completed) | inferred | fixture-only | scripted progress, no frame replay; the 11th part exceeds `MaxParts` = 10 and forms a second continuation turn. |
| S9 | `init` + `message/assistant` | `system` + `assistant` + `result`(sig,final,err) | submission.accepted(ran) ‖ turn.started → turn.final(incomplete) | inferred | fixture-only | terminal constructed in ExecuteAsync; no seam below ExecuteAsync at `ca90b205`; the non-zero `ExitCode` branch is the error terminal. an executor-reported failure surfaces as `turn.final` with `completion: incomplete`, NOT as `turn.error`. A client watching for `turn.error` never sees the most common provider failure. |
| S10 | — | — | submission.accepted(ran) → submission.accepted(injected) ‖ turn.started → turn.started → turn.final(completed) | inferred | fixture-only | scripted progress, no frame replay; the redelivered submission runs under a new turn id and `attempt` 2. |
| S13 | — | — | — | unsupported | fixture-only | no nested-turn concept: the executor exposes no parent/nested/subagent member. |
| S14 | — | — | — | unsupported | fixture-only | no drain-preserve path at all. |
| S16 | — | — | submission.accepted(ran) → submission.accepted(ran) ‖ turn.started → turn.final(completed) → turn.started → turn.final(completed) | inferred | fixture-only | restart is a local process action; no provider frame acknowledges it; terminal constructed in ExecuteAsync; no seam below ExecuteAsync at `ca90b205` |
| G1 | `tool completion` | `tool_result` | turn.progress(tool) | unsupported | fixture-only | the provider DOES emit a completion frame; the runtime maps it with `IsSignificant = false`, and `TaskManager` publishes `turn.progress` only when `IsSignificant && ToolName is not null`. A client sees a tool START and never a tool FINISH, so a per-tool spinner has no event that clears it. |
| G2 | `init` + `message/assistant` + `message/assistant` + `message/assistant` + `result/success` | `assistant` + `assistant` + `assistant` | turn.final(completed) | unsupported | fixture-only | `ConversationEventKind` has no delta kind. Three assistant chunks in, exactly one `turn.final` out — a client cannot begin rendering, or speaking, before the whole answer exists. |
## Reading the `G` rows

`G1`, `G2` and `G3` are not turn scenarios. They record the three per-provider facts the design
required the harness to **confirm or refute** rather than discover by accident. All three were
confirmed:

- **G1 — tool completion is invisible to clients on every provider.** Confirmed. Tracked as #285.
- **G2 — there is no incremental assistant-text event in v1.** Confirmed. This is a structural
  input to the voice stop/go decision, not a tuning problem: a voice client cannot begin speaking
  before the entire answer exists. Tracked as #286.
- **G3 — Codex's `ToolName` is not a tool name.** Confirmed, and pinned byte-for-byte. Tracked as
  #287.

None of the three is fixed here. Each is a behaviour change with its own client-visible
consequences and its own review; the rows exist so a later fix flips a red test rather than
silently changing behaviour. The fourth follow-up — a `GeminiExecutor` seam that would let its
start and terminal rows be observed at L1 instead of `inferred` — is #288.

## What is NOT in this file

Runtime-only scenarios — `S7`, `S8`, `S11`, `S12`, `S15` — exercise `TaskManager` and
`ConversationEventBus` behaviour that is identical regardless of which executor produced the
progress. Running them per provider would produce three identical rows and a false impression of
per-provider coverage. They are recorded **once**, in the scenario table of
`session-continuity-scenarios.md`, and asserted once in `RuntimeScenarioTests`. The bidirectional
doc ↔ harness check in `ProviderCapabilityMatrixTests` therefore scopes to the per-provider rows in
this file.
