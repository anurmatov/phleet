# Spike results — public summary

**Anchor commit: `ca90b205`.**

This file is a template. Every result cell reads `pending — private run not yet executed`, and it
stays that way until the private run in Fleet produces aggregate rows to transcribe. **A fabricated
or placeholder number here is prohibited**: a plausible-looking figure in a results table is worse
than an empty one, because nobody re-checks a table that already looks finished.

## Half 1 — real-adapter output

**Status: complete at L1+L2. `fixture-only` for every row; no L3 capture has been run.**

The full result is `real-adapter-capability-matrix.md`, which is generated from, and enforced by,
the test suite. The headline findings:

| Finding | Status |
|---|---|
| Tool completion is invisible to clients on every provider | Confirmed — follow-up #285 |
| There is no incremental assistant-text event in v1 | Confirmed — follow-up #286 |
| Codex's `ToolName` for `commandExecution` is the shell command | Confirmed, pinned byte-for-byte — follow-up #287 |
| Gemini's turn-start and terminal cannot be observed below `ExecuteAsync` | Confirmed; all such rows `inferred` — follow-up #288 |
| An executor-reported failure arrives as `turn.final(incomplete)`, never `turn.error` | New — found by this spike |
| A thrown executor is `turn.error(internal)`; a reported error is `turn.error(executor_error)` | New — found by this spike |
| `turn.started` is delivered before `submission.accepted` | New — found by this spike |
| A terminal event can overtake queued progress | New — found by this spike |
| Concurrent publishers interleave, so arrival order is not emission order even within one kind | New — found by this spike, on CI |
| `control.ack` and `turn.canceled` race; neither order is a contract | New — found by this spike |

### L3 capture

| Provider | Pinned CLI | L3 capture date | Shape diff vs public corpus |
|---|---|---|---|
| claude | `claude-code@2.1.259` | pending — private run not yet executed | pending — private run not yet executed |
| codex | `@openai/codex@0.153.4` | pending — private run not yet executed | pending — private run not yet executed |
| gemini | `@google/gemini-cli@0.40.1` | pending — private run not yet executed | pending — private run not yet executed |

Until a dated L3 pass lands, no matrix cell may read `verified@…`. The pinned CLI versions above are
already public in `Dockerfile`; the capture dates and diffs are not yet measured.

## Half 2 — Claude voice feasibility

**Status: not started. No measurement has been taken.**

Method, timing-point definitions and the fixed thresholds are in `voice-feasibility-protocol.md`.

### Comfort gate — end-to-end latency

| Cell | p50 | p95 | max | failures |
|---|---|---|---|---|
| short utterance / short answer | pending — private run not yet executed | pending — private run not yet executed | pending — private run not yet executed | pending — private run not yet executed |
| short utterance / tool-using answer | pending — private run not yet executed | pending — private run not yet executed | pending — private run not yet executed | pending — private run not yet executed |
| medium utterance / short answer | pending — private run not yet executed | pending — private run not yet executed | pending — private run not yet executed | pending — private run not yet executed |
| medium utterance / tool-using answer | pending — private run not yet executed | pending — private run not yet executed | pending — private run not yet executed | pending — private run not yet executed |
| long utterance / short answer | pending — private run not yet executed | pending — private run not yet executed | pending — private run not yet executed | pending — private run not yet executed |
| long utterance / tool-using answer | pending — private run not yet executed | pending — private run not yet executed | pending — private run not yet executed | pending — private run not yet executed |

### Stage breakdown

| Stage | p50 | p95 |
|---|---|---|
| `stt = t1−t0` | pending — private run not yet executed | pending — private run not yet executed |
| `agent = t5−t2` | pending — private run not yet executed | pending — private run not yet executed |
| `tts_first_audio = t6−t5` | pending — private run not yet executed | pending — private run not yet executed |

### Hard gates

| Gate | Threshold | Result |
|---|---|---|
| `local_stop` p95 | ≤ 150 ms | pending — private run not yet executed |
| Stale-epoch audio played | exactly 0 | pending — private run not yet executed |
| Post-hangup spoken answers | exactly 0 | pending — private run not yet executed |
| Submissions that terminate | 100% | pending — private run not yet executed |
| Answer spoken for a cancelled submission | exactly 0 | pending — private run not yet executed |

### Verdict

**pending — private run not yet executed**

Evaluated in order GO → CONDITIONAL → NO-GO against the thresholds fixed in
`voice-feasibility-protocol.md`. A hard-gate failure defers the voice half regardless of the comfort
gate, and has no effect on the text slice.
