# Voice feasibility protocol

The experiment design for the Claude voice half of the spike. **Thresholds are fixed by this
document, before any measurement is taken**, so a result cannot be graded against a threshold chosen
after seeing it.

**Anchor commit: `ca90b205`.**

The experiment itself is executed privately, in Fleet. Nothing in this repository runs it: there is
no voice component in `src/`, no audio pipeline, and no test here touches a microphone, a network
socket or a credential. This file is the method and the pass/fail bar; the aggregate results land in
`results-public-summary.md`.

## Topology

```
utterance → STT → text → ConversationIntake.SubmitAsync
                       → Claude executor turn
                       → turn.final
                       → TTS → playback
```

The existing services are the ones measured: `VoiceTranscriptionService` (batch `POST /transcribe`)
and `TtsService` (batch `POST /v1/audio/speech`). **Both are whole-utterance, non-streaming.** The
experiment measures the stack as it exists; it does not build a streaming one.

## Structural constraint, measured and confirmed

The capability matrix's `G2` rows confirm it on all three providers:

> **There is no incremental assistant-text event in v1.** `ConversationEventKind` has no delta kind;
> top-level assistant text is buffered and only the matching terminal `result` owns `turn.final`.

A voice client therefore **cannot begin speaking before the entire answer exists**. This is an input
to the stop/go decision, not a tuning problem, and it is why the `CONDITIONAL` verdict below exists
at all.

## Timing points

Monotonic clock, one source, recorded as offsets from `t0`.

| Point | Definition |
|---|---|
| `t0` | Last audio frame of the user utterance handed to STT (VAD-declared end of speech) |
| `t1` | STT returns a final transcript |
| `t2` | `submission.accepted` observed by the adapter |
| `t3` | `turn.started` observed by the adapter |
| `t5` | `turn.final` observed by the adapter |
| `t6` | First audio byte returned by TTS |
| `t7` | First audible sample played on the device |

There is deliberately **no `t4`** ("first assistant token"). With no incremental text event,
first-token and final are the same instant. The gap is named here rather than silently collapsed.

Derived: `stt = t1−t0`, `agent = t5−t2`, `tts_first_audio = t6−t5`, **`end_to_end = t7−t0`**.

⚠️ `t2` precedes `t3` in this table by convention, but the runtime delivers `turn.started` BEFORE
`submission.accepted` (see the capability matrix). Record both as observed; do not reorder them to
match the table.

## Barge-in points

| Point | Definition |
|---|---|
| `t8` | Onset of new user speech detected during playback |
| `t9` | Local playback actually stopped |
| `t10` | `control.ack` for the cancel |
| `t11` | `turn.canceled` |

Derived: **`local_stop = t9−t8`** — the number the user feels — and `cancel_ack = t10−t8`. Reported
separately: local stop must not depend on a server round trip.

## Trial plan

≥ 30 trials per cell. Cells are {short ≈ 2 s, medium ≈ 6 s, long ≈ 15 s utterance} × {short answer,
tool-using answer}. Report p50 / p95 / max per cell, plus failure counts.

Fewer than 30 completed trials in a cell ⇒ that cell is `insufficient-data`. Never an average of
whatever happened to finish, and never a percentile computed after dropping failures out of the
failure count.

## Stop/go thresholds

**Evaluation is ordered: GO, then CONDITIONAL, then NO-GO.** The first verdict whose conditions hold
is the verdict.

### Comfort gate — short utterance, short answer

| Verdict | `end_to_end` |
|---|---|
| **GO** | p50 ≤ 2500 ms **and** p95 ≤ 5000 ms |
| **CONDITIONAL** | p50 ≤ 4000 ms — voice is viable only *after* an incremental-text protocol addition lands; recorded as a dependency, not a pass |
| **NO-GO** | p50 > 4000 ms — voice deferred |

A run with p50 = 2000 ms and p95 = 9000 ms is `CONDITIONAL`: it fails GO's p95 bound and satisfies
CONDITIONAL's p50 bound. CONDITIONAL carries no p95 bound of its own — a wide tail is precisely what
the incremental-text dependency would address.

### Hard gates — any failure defers voice regardless of the comfort gate

| Gate | Threshold |
|---|---|
| `local_stop` (`t9−t8`) | p95 ≤ 150 ms |
| Stale-epoch audio played | **exactly 0** across all trials |
| Post-hangup spoken answers | **exactly 0** |
| Submissions that terminate (any outcome, including `turn.outcome_unknown`) | **100%** |
| Answer spoken for a submission the user cancelled | **exactly 0** |

A hard-gate failure is recorded as `voice deferred — <gate>` and closes the voice half. It has **no
effect** on the text slice, which is the point of scoping the halves separately.

## Epoch fencing — the correctness half of barge-in

Latency is the comfort question; playing the **wrong** audio is the correctness question.

Two monotonically increasing counters: `utteranceEpoch`, incremented on every accepted user
utterance, and `playbackEpoch`, incremented on every synthesis start. Every STT result, `turn.final`
and audio buffer is tagged with the epoch current when it was initiated. Anything arriving with a
stale epoch is **dropped and counted** — never played, never submitted.

The rule is expressed as a pure, clock-injectable state machine,
`tests/Fleet.Agent.Tests/Harness/UtteranceEpochFence.cs`, with exhaustive synthetic tests: late STT,
late final, late audio chunk, interleaved double barge-in, hangup during synthesis. **It lives in
the test project.** The spike's job is to prove the rule is expressible and sufficient, not to ship
the component; productionising it is a follow-up.

### Post-hangup ownership

A turn still running when the call ends must (a) still terminate its submission, and (b) have its
text delivered to the text transcript **only**. Speaking a late answer into a room after the call
ended is a hard failure, not a latency nuisance. The fence models this as a distinct outcome —
`TranscriptOnly` — rather than as a drop, so the submission still terminates.

## External dependency failure paths

| Dependency | Failure path |
|---|---|
| `fleet-whisper` HTTP | Already returns `null` on any failure. A `null` transcript is a **failed trial**, counted in the failure column; never retried into the latency sample, never silently excluded. |
| `fleet-kokoro-tts` HTTP | Already returns `null` on failure. Failed trial, counted; playback does not occur; the epoch is still advanced so nothing stale can play later. |

## Publication boundary

**Public** (`results-public-summary.md`): methodology, the timing-point table, the threshold table,
aggregate millisecond percentiles per cell, and the per-gate verdict.

**Private Fleet only:** raw traces, audio files, transcripts, per-trial rows, real agent/user/chat
identifiers, hostnames, container names, deployment topology, provider account or session
identifiers, any unredacted provider frame, and any wall-clock timestamp that ties a measurement to
a real person's activity.

The public summary is populated **only** by transcribing aggregate rows from the private results
file. A private artifact is never linked, attached or quoted here.
