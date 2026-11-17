# PR acceptance runbook

Contract: [#434](https://github.com/anurmatov/phleet/issues/434), overridden by
[#441 revision 3](https://github.com/anurmatov/phleet/issues/441), with opt-in
parent routing from [#453](https://github.com/anurmatov/phleet/issues/453).
This is the required contract, not a record of deployment or acceptance.
The parent, acceptance and cleanup seeds are source definitions with local
definition/scenario tests. CI, independent review and exact-head isolated
operator acceptance remain separate gates. Nothing here activates a live
definition or claims a deployment.

## RequireAcceptance opt-in

`UwePrImplementationWorkflow` and `UweDesignToPrWorkflow` default to `off`.
Both validate before application attributes, delegates or children using the exact
rendered value. The SDK still publishes its existing `TemporalChangeVersion`
patch metadata before seed execution, even for invalid input. JSON booleans render as `True`/`False`; matching is case-sensitive.

| input | mode |
|---|---|
| absent, JSON null, `""` | `off` |
| `"false"`, `"False"`, JSON false | `off` |
| `"true"`, `"True"`, JSON true | `required` |
| anything else, including `"TRUE"`, `"yes"`, `"1"`, whitespace, numbers or objects | `ExplicitFail` |

Invalid input fails with `RequireAcceptance must be true, false or absent`.
The wrapper forwards normalized literal `true` or `false`, never the raw input.
An `AutonomyGrantId` other than exactly `none` or empty requires `required` mode
before design starts. `"None"` and `" none"` count as grant ids.
The failure is `AutonomyGrantId requires RequireAcceptance=true`.
Charter children still require `RequireAcceptance=true`; autonomous merge
still refuses `acceptance_missing`, including absent/false inputs and
`not_required` records. This changes no human or delegated gate authority.

In `off`, IMPLEMENT → REVIEW (five attested rounds) → UNRESOLVED → PIN remains.
Exact head, `review_ref == H`, complete CI rollup, the bound human merge gate
and `--match-head-commit H` still apply. CI failure returns to IMPLEMENT;
pending uses the existing 12 × 300-second CI_WAIT and then the technical
stall (STALLED), in both modes, never a gate. CI success for code or
doc-only goes directly to MERGE_GATE without acceptance or cleanup children.
Only unknown-head `owner_unavailable` and `stale_signals` can reach BLOCKED.
No plan, recovery, owner, harness or child reason can block.

All six PIN instructions carry `REQUIRE_ACCEPTANCE: <off|required>`: PIN, the
CI wait, the cleanup wait, a delegated approval's re-pin, and a human approval's
re-pin in the merge hold and in the stall after it. In `off`, read only the actual head, every
CI page and diff paths. Skip body hashing, design lookup, plans, receipts, prior
rounds, owner and capability-map checks. Ignore missing/malformed acceptance
tokens. After a delegated approval, unchanged head plus successful CI retains its
existing re-pin routing, followed by the additional read-only formal-review
check before merging; anything else invalidates the approval, as before. The
re-pin subtree, its instruction and historical fixture remain unchanged. After a human
approval a re-pin with the CI read tokens feeds the merge check described under
"Holds and the technical stall". The required acceptance subtree is unchanged.
The plan/receipt/lease sections below apply only in `required` mode. Empty JSON
strings have an explicit off case because the engine default filter only covers
empty CLR strings and null JSON values, not empty JSON strings.

The `off` merge record is visibility only, never acceptance evidence:
`v1;head=<H>;result=not_required;iso=production;block=none;fix=0;batch=0`.
Merge notices print `basis require_acceptance_false` for off code PRs,
`basis doc_only` for doc-only in either mode and `basis passed` after acceptance.
The initial basis is `none`; blocked notices also show the mode.

Design clear/publish/withdraw writes of `ReviewRef`/`ReviewScrub` are required.
A bad value or unregistered local name fails non-retryably with
`RequiredSearchAttribute` before the design gate or notice. Empty `ReviewRef`
from `needs_human_review` remains valid and opens the human-only gate.

Monitoring: a new off code run should show `not_required` at merge-approval
with no acceptance child. `plan_missing` for absent/false input, unexpected
`ExplicitFail` or Design `RequiredSearchAttribute` failures require investigation.

This seed-only rollout needs separate human approval. Confirm ReviewRef and
ReviewScrub are Keyword attributes in the target namespace, then update live
Design → PR → wrapper definitions from reviewed source while preserving
custom controls. DbSeeder is create-if-absent; running snapshots do not change.
Rollback restores previous definitions as new versions in reverse order:
wrapper → PR → Design. Keep registered attributes and running snapshots;
no runtime rollback is needed. Old wrapper/new PR still validates raw input;
new wrapper/old PR retains the stricter always-acceptance behavior.

## Validator interface

`/opt/fleet/acceptance_plan.py` uses only the Python standard library.
Except for `reserve-harness-retry`, commands read explicit local files and emit
fixed token lines. They never create prerequisites, deploy, grant access or invoke
providers. A validation failure exits nonzero without echoing input values.

| command | input | output |
|---|---|---|
| `plan-check` | `--plan-file` JSON or `--body-file` | `PLAN`, `ISO`, `PLAN_SHA256` |
| `body-sha` | `--body-file`, exact bytes | `BODY_SHA256` |
| `plan-evidence` | plan/body file and `--design-match yes\|no` | `PLAN_APPROVAL`, `PLAN_SHA256` |
| `design-result` | `--issue`, `--workflow-id`, `--run-id`, `--result-file` | `DESIGN_MATCH`, `DESIGN_REASON` |
| `prior-rounds` | `--fix`, `--batch`, `--receipts yes\|no` | `PRIOR_ROUNDS`, valid counters |
| `prior-clean` | `--receipts-file`, array of protected receipts | `PRIOR_ATTEMPTS_CLEAN` |
| `ci-rollup` | `--checks-file`, `--statuses-file`, complete arrays | `CI`, `CI_FAILING`, `CI_DETAIL` |
| `change-class` | `--paths-file`, one path per line | `CHANGE_CLASS` |
| `access-check` | `--plan-file`, `--config-file`, `--status-file`, `--target-agent`, CSV `--reviewers`, explicit `--capabilities` | `OWNER` |
| `harness-evidence` | `--comments-file`, complete comment array; `--head` | `HARNESS_RETRY_SPENT` |
| `reserve-harness-retry` | `--repo`, `--pr`, `--head`, `--attempt-hash` | `RESERVATION` |
| `render-comment` | `--receipt`, `--ssh-targets` | sanitized comment or nonzero with empty stdout |

`CI_DETAIL` says why a result is pending: `running` (a check or status has not
finished), `none_reported` (nothing but skipped/neutral results, or nothing at
all) or `unexpected_terminal` (everything finished and something is neither
success nor a known failure). It is `none` for `success` and `failure`. The
`CI` and `CI_FAILING` lines are what they were. An unreadable file prints no
token line and exits nonzero; the delegate reports that as `CI_READ: error`.

In required mode the PIN delegate must collect **every** check/status/comment page; incomplete
files are not evidence of absence. The reservation command itself uses `gh api
--paginate --slurp`, never retries a failed/ambiguous write, and re-lists before
confirming its own unique marker. A reservation remains spent after a crash.
Listing errors and malformed/duplicate evidence never permit a harness retry.

## Plan

Under `## Acceptance plan`, exactly one fenced `json` block carries the plan.
Hash canonical JSON with sorted keys, no separators' whitespace, UTF-8 and
`ensure_ascii=False`. The issue body hash is over exact bytes, without adding a
newline. An approval of a different body or run is not approval of this plan.

```json
{
  "owner": "agent3",
  "access": [
    "envref:TEST_CREDENTIAL_A"
  ],
  "isolation": "isolated_network",
  "max_duration": 21600,
  "pre_stages": [
    "setup", "initial check"
  ],
  "wait_seconds": 3660,
  "post_stages": [],
  "prerequisites": [
    "envref:TEST_CREDENTIAL_A"
  ],
  "baseline": "protected baseline",
  "cleanup": "owned resources",
  "evidence": "<protected-path>"
}
```

Pre-stages: 1–4; post-stages: 0–4. Wait menu: 0, 60, 300, 900, 1800,
3660, 7260 or 14460 seconds. Duration buckets: 3600, 7200, 14400 or
21600 seconds. Bound: `(pre + post) * 3720 + wait + 3720 + 300`.
Exceeding the bucket requires a plan amendment, not a shortened test.

Isolation classes `ci_only`, `throwaway_container`, `isolated_network` and
`host_isolated` record `isolated`. `canary` and `production_touching` record
`production`. Missing/invalid/exceeds plans conservatively record `production`.
This label grants no authority, and no route uses it.

The owner must be enabled, running, independent of implementer and reviewers,
and possess every requested capability. Prerequisites must already exist.
The owner still confirms access before creating any resource; config presence
is not runtime-access proof. Production effects require explicit approved text.

## Capabilities

There is no default map path or built-in host/env-ref name. The delegate must
resolve an explicit commit of the deployment's private map repository, fetch
that exact version and record its commit and SHA-256. It must not derive the
map ref from an agent image value. The public instruction placeholder is
`<capability map, see docs/pr-acceptance.md>`.

Map format (synthetic documentation data only):

```json
{
  "version": 1,
  "items": {
    "ssh:host-a": {
      "anyEnvRef": [
        "TEST_CREDENTIAL_A"
      ],
      "sshTarget":
        "user-x@192.0.2.10"
    }
  }
}
```

`ssh:<host>` and `docker:<host>` require a listed `anyEnvRef`; missing/invalid
maps refuse host items. Non-host items still evaluate with a missing map file.
`docker:local` requires `mountDockerSock`; `provider:<claude|codex|gemini>` must
match; `envref:<NAME>` must exist in `envRefs`; `mcp:<server>` matches
`mcpEndpoints[].mcpName`; `tool:<name>` matches an enabled `tools[].toolName`;
`network:<name>` must be listed. No command writes agent configuration.

## Design evidence

The PIN lookup selects Completed design runs whose Repo,
IssueNumber and ReviewRef match the exact current issue body. It must fetch
**that row's run ID**, with `followRuns: false`, not the latest run under the ID.
The result-tool extension binds both `RunId` and `ResultRunId` with
`followRuns: false`: SDK 1.11.1's `GetWorkflowHandle` omits `ResultRunId`, while
`GetResultAsync` uses it. Both properties are required; a bridge returning an unwrapped result is refused.

The validator accepts only a JSON wrapper with exactly `workflowId`, `runId`
and `result`, matching the requested IDs. Result may be an object or one JSON
string encoding of an object with exactly one positive integer `IssueNumber`,
within signed 64-bit range and equal to the requested issue. Duplicate members,
string/boolean/fraction/exponent numbers, extra members, double string encoding,
sentinels, fences, null, a bare result and an older bridge's output fail closed.
Lookup/fetch/validator errors must lead to `plan_unapproved`, never a child start.

## Visibility record

Exactly two new Keyword attributes are registered: `GateRef` and `Acceptance`.
The three existing Int attributes remain unchanged. `AcceptanceAttribute.Pattern`
in `Fleet.Shared` is the single unanchored grammar body; .NET uses `\A(?:…)\z`
and Python readers must use `re.fullmatch`, never `$`-before-newline matching.

`v1;head=<40-hex>;result=<running|passed|failed|blocked|not_required>;iso=<isolated|production>;block=<reason|none>;fix=<n>;batch=<n>`

Fixed order, ASCII, no newline; fix is 0–9999 without leading zeroes; batch is
0–5. `block=none` iff result is not blocked. Empty/invalid means no current
record. This is a visibility record, not authority or a routing variable.

The parent must write W1 before each child, W2 after every child before
branching, W3 Phase/GateRef/Acceptance together before every bound gate and W4
clear before every implementer and invalidation. Writes must be required, never
ignored. Doc-only W3 always uses `not_required;iso=production`.

## Receipt safety and evidence

Keep originals immutable in protected storage. Public comments use only the
renderer, which binds the receipt hash to file bytes, hashes attempt IDs and
substitutes target identities, env-ref tokens, private paths and IP literals.
It re-scans after substitution and withholds all stdout on a deny-pattern hit.
A withheld comment never changes the protected attempt result.

Keep head, source/image/plan/map hashes, stage, commands, exit codes, actual HTTP
counts, timestamps, lease deadline, isolation proof, baseline, owned resources,
cleanup and `harness_retry` in the protected receipt. Public rendering is not
runtime acceptance or proof that every earlier attempt was discovered.

## Agent image size evidence

The copied validator is 21,951 bytes (21,584 when the image sizes below were
measured; `CI_DETAIL` added the rest). The baseline agent build already installs
Python transitively; explicitly naming `python3` makes that prerequisite durable.
This is not a claim that the bare SDK base contains Python.

Historical CI image sizes, measured by `docker image inspect`:

| head | image bytes | evidence |
|---|---|---|
| baseline `7b6b858` | 2,955,284,080 | [job](https://github.com/anurmatov/phleet/actions/runs/38261280020/job/114852116845) |
| foundation `309a82e` | 2,955,306,698 | [job](https://github.com/anurmatov/phleet/actions/runs/38272255309/job/114889112981) |

Observed foundation growth: **22,618 bytes**, below 100 KB. Both builds resolved
SDK base digest `e70cdb7f80b0348f5cb85f19a8f670fca061f033d57eed12fa003d58b0e06317`
and installed Python 3.12.3. This is historical CI evidence, not a controlled
attribution of every filesystem byte or proof for a later revised head. Fresh
exact-head image evidence remains required before readiness; no local Docker
or host operation was performed.

## Delivery states and exact-head readiness

`H` is the end of the latest implementer `HEAD_RANGE`. Every implementer turn
must report `IMPLEMENTATION: complete|incomplete`, a real PR URL, that range and
`HEAD_SHA`; incomplete work never enters acceptance. Implement and revise turns
have a 60-minute budget, push then report, and never wait for CI in the turn.
Recover a missing URL from the one branch's PR list, not by opening a duplicate.

| state | action and next state |
|---|---|
| IMPLEMENT | Clear Acceptance first; implement on the same branch/PR; complete → REVIEW; incomplete → bounded revision |
| REVIEW | Consensus maximum five rounds; reviewers exclude author and CTO, and attest H; unanimous approved at H → UNRESOLVED |
| HUMAN_PAUSE | Entered only after one CI-only probe of H answered success. human-review 180/60/2; approved → fresh REVIEW, changes_requested → revision then IMPLEMENT, rejected/timeout → terminal |
| REVIEW_HOLD | `needs_human_review` at a head that is not green: 300-second timer then probe, 12 passes per head in total; success → fresh REVIEW, failure → IMPLEMENT, head moved → INVALIDATED, budget spent → STALLED(ci_timeout) |
| UNRESOLVED | Report threads only; no push; unresolved threads → IMPLEMENT; none → PIN |
| PIN | Re-read actual head, all CI pages, immutable plan evidence, owner access, prior receipts and counters |
| CI_WAIT | Literal 300-second timer, maximum 12; repeat PIN; a reported different head → INVALIDATED; still not success → STALLED(ci_timeout) |
| STALLED | Owned technical state, no gate: one coordinator notice, then 24 1800-second timers each followed by one read. The read is a probe for `ci_timeout`; for `merge_held` it is one look for late merge feedback, then the merge check (re-pin, then preflight). Clear → resume route (REVIEW, PIN or MERGE), late feedback → MERGE_FEEDBACK, failure → IMPLEMENT, head moved → INVALIDATED, last read not clear → TIMEOUT |
| CLEANUP_WAIT | Literal 1800-second timer, maximum 16; repeat PIN; still dirty → cleanup_pending |
| ACCEPT | Enforce batch entry guard, update counters, W1, then one awaited exact-head child |
| CLASSIFY | Code-validated unanimous classification of failed attempt receipts; no synthesizer verdict |
| ROUND_CHECK | batch 5 → CONTINUE_GATE before another revision; otherwise same-PR product fix |
| CONTINUE_GATE | CEO-only acceptance-decision; continue resets batch only, stop → STOPPED |
| BLOCKED | CEO-only acceptance-decision; continue → PIN and full revalidation, stop → STOPPED. Never entered for `ci_timeout` |
| INVALIDATED | W4 clear; publish sanitized head invalidation; withdraw review ref and fresh delta REVIEW |
| MERGE_GATE | Required Phase/GateRef/Acceptance write then bound CEO merge wait. Delegated approval → re-pin → MERGE or INVALIDATED; human approval → MERGE_HOLD; changes_requested → MERGE_FEEDBACK |
| MERGE_HOLD | Human approvals only, entered once per gate visit. Re-pin, then a read-only preflight; clear → MERGE, CI failure → IMPLEMENT, head moved → INVALIDATED, anything else → a 300-second pause and again, 12 passes, then STALLED(merge_held). The pause listens for late merge feedback → MERGE_FEEDBACK; otherwise the approval is kept |
| MERGE | Entry, by every route: one look for late merge feedback → MERGE_FEEDBACK, else Phase `merging`. Then delegated: pinned merge, one read. Human: the merge command once per gate visit, then up to three reads of the actual merged state |
| MERGE_FEEDBACK | The one feedback route, from the gate, the hold, a held merge's stall and the merge entry: withdraw the review ref, Phase `revision`, review of the feedback; upheld → IMPLEMENT, not upheld → fresh REVIEW, `needs_human_review` → a human-review pause. An approval given before it is void |
| STOPPED | PR remains open, sanitized summary, waiter cancelled; no merge |
| EXHAUSTED | No terminal sentinel after 60 delivery passes; notify, waiter cancelled, no merge |

Review round one reads the full issue body/updatedAt and full diff. Later rounds
read previous blockers, the implementer summary and only HEAD_RANGE commits,
re-reading the issue if updatedAt moved. Before the first approval after round
one, one FINAL AUDIT reads the full PR. A head mismatch is `head_changed`, not
approval. An approved human-review signal is operational feedback, never an
attested consensus result. An unattested approved review consumes another review
round and does not start acceptance. Preserve the post-feedback human pause.

PIN requires `review_ref == H`, complete implementation, CI success, plan ok,
approved plan hash equality, an independent valid owner and every prior attempt
clean. A changed head invalidates consensus, acceptance and any CEO approval
without resetting counters. UNRESOLVED and every post-review step are report-only:
only implementer steps push. `gh pr merge --match-head-commit H` is the final hard
barrier, for human approvals as well as delegated grant approvals. A mismatch
voids the decision and requires fresh review. Never use `--admin`.

Merge verification uses `gh pr view --json state,mergedAt,mergeCommit` and checks
`state == MERGED`. Never request the unsupported `merged` field. Doc maintenance
starts only after verified merge and retains issue/reviewer/preparation inputs.

## PIN evidence and routing

The configured CTO performs a read-only PIN, bounded to 15 minutes, with
`REQUIRE_ACCEPTANCE` on every PIN and post-approval repin. A delegated
approval's re-pin is asked for the tokens it was always asked for (no CI_READ,
no CI_DETAIL); the other five are asked for these. Off-mode PIN emits
only HEAD_OID, CI_READ, CI, CI_DETAIL and CHANGE_CLASS. Required-mode PIN emits
PR, HEAD_OID, IS_DRAFT, CI_READ, CI, CI_FAILING, CI_DETAIL, CHANGE_CLASS, PLAN, PLAN_SHA256,
MAX_DURATION, WAIT_SECONDS, PRE_STAGES, POST_STAGES, OWNER, PRIOR_RECEIPTS,
PRIOR_ATTEMPTS_CLEAN, PRIOR_ROUNDS, PRIOR_LAST_HEAD, MAP_COMMIT, MAP_SHA256,
SSH_TARGETS, PINNED_AT, BODY_SHA256, PLAN_APPROVAL, APPROVED_PLAN_SHA256,
ACCEPTANCE_ISO and HARNESS_RETRY_SPENT. Missing, malformed or failed evidence
never counts as success. The public map instruction remains a placeholder.

Every check-run and commit-status page at H is required. Select the latest check
run per name. Skipped/neutral checks are ignored; failure, cancelled, timed_out,
action_required, stale, startup_failure and status error/failure are failures.
Any remaining incomplete/pending status, or zero nonignored results, is pending.
Only the remaining all-success result is success; branch-protection permissions
are not needed. Pending uses CI_WAIT; failure returns to IMPLEMENT with check
names, never acceptance.

`CI_READ` is `ok` when every check-run and status page was read and `error` when
any GitHub read failed; `CI` is then `pending`. PIN, the CI wait and a human
approval's re-pin read `error` beside `CI: success` as pending; the cleanup wait
and a delegated approval's re-pin parse what they always parsed. A PIN without
the line reads as `ok`, so `CI_READ` never turns a PIN that was success into
something else.

The run records every CI read that held it back as exactly one of `pending`
(answered `CI: pending`, read ok), `unavailable` (`CI_READ: error`) or `silent`
(no head or no parseable `CI:` line). A turn that failed or timed out is silent:
the report variable is cleared before each read in CI_WAIT, in MERGE_HOLD and
before every probe, so an old answer is never read as the new one. The three
counters restart when H changes and appear in the stall notice. None of them is
success.

Doc-only is an allow-list: docs/**, nested README.md or CHANGELOG.md, with a
winning deny-list for projects, roles, seed JSON, deploy, scripts, .github,
CLAUDE.md and src. A seed or script change is code even if named markdown.
Doc-only goes to merge with `not_required`, file evidence and iso=production;
it never invokes an owner or invents a passed test.

On the first PIN, hash the exact issue body, then list Completed UweDesignWorkflow
runs for the same Repo and IssueNumber with ReviewRef equal to that body hash,
using limit 100. Newest startTime first, fetch each row with its workflowId AND
run_id, then run design-result against exactly those IDs and the issue number.
The first strict match approves the canonical plan hash. Errors, missing rows,
old unwrapped bridge output or mismatches mean plan_unapproved with zero children.
The continued-as-new result and a reused workflow ID's newer run never count.

Every PIN compares PLAN_SHA256 with approved_plan_sha256. First-attempt changes
are detected too. In-run plan-gate continue binds to the full hash already in
GateRef, never a later issue read. A restart repeats immutable evidence lookup.
Missing plan → plan_missing; malformed → plan_invalid; exceeds → plan_amendment;
changed → plan_changed. Only `plan_missing` drafts a plan comment before the
bound plan gate. Non-plan BLOCKED reasons without a plan use `00000000` as the
no-plan sha8 in `block:<gate>:<attempt>:00000000`; this is a gate identity, never
an approved plan hash, and continue always repeats PIN. Capability-map/config failures fail owner access closed.
GitHub CI lookup errors are pending, not empty-success. A missing PR is blocked
owner_unavailable (pin_failed), never blind progress.

## Counters, caps and recovery

| value | rule |
|---|---|
| fix_round | Starts at recovered value or zero; increases once per new tested head after initial head; never resets |
| batch_round | Same increments; always 0–5; only CEO continue at CONTINUE_GATE resets to zero |
| last_tested_head | Recovered newest receipt head, then latest tested head |
| attempt_seq | Increases per child, including harness retries and blocked reruns |
| acc_gate_seq | Increases per BLOCKED or CONTINUE_GATE visit |
| merge_gate_seq | Increases per merge visit, including invalidation/feedback returns |
| invalidations | Increases per INVALIDATED entry; never resets counters |
| harness_retry_head | One durably reserved same-head retry; restart does not restore its availability |
| ci_pending_reads, ci_unavailable_reads, ci_silent_reads | One per CI read that was pending, unavailable or silent; all three restart when H changes |
| review_hold_passes | Review-hold passes spent at H, 0–12; restarts only when H changes |
| merge_issued_seq | The merge_gate_seq whose merge command was issued; the command is skipped when it equals the current visit |

First attempt is round zero. At ACCEPT entry, a different head with batch 5
must enter CONTINUE_GATE before any counter update or child. After five failed
retests, ROUND_CHECK asks before the sixth fix. A harness retry or blocked rerun
on the same head is not a round. Continue returns to its caller (IMPLEMENT or
ACCEPT); BLOCKED continue always repeats PIN and cleanup verification.

Caps are independent: delivery 60; consensus five per review entry; stale signals
five per gate visit; CI 12 × 300 seconds; cleanup 16 × 1800 seconds; review hold
12 × 300 seconds per head; merge hold 12 × 300 seconds per gate visit; stall
24 × 1800 seconds; merged-state reads three, 60 seconds apart. Fix total and
merge visits have no separate cap, but remain inside delivery. Exhaustion is not
a synthetic approval. STOPPED is terminal; EXHAUSTED means no sentinel, both wake
the optional waiter with cancelled and leave the PR open.

Restart inputs PriorFixRounds and PriorBatchRounds must be decimal integers,
0 ≤ batch ≤ 5 and batch ≤ fix. When prior receipt comments exist, both are
required. Invalid/absent recovery goes to prior_rounds_unknown. Recover newest
receipt head and ALL per-head harness reservation/receipt pages. Listing failure
or malformed evidence is unknown; never silently reset counters or grant another
retry. Every earlier AttemptId needs verified cleanup or ALREADY_CLEAN before
another attempt, including after an owner timeout.

## Gates and stale clicks

Summaries carry GateRef. The dashboard sends rendered runId and expectedGateRef.
For acceptance-decision/merge-approval, the endpoint checks in this order:
missing run → 400; exact-run describe (10 seconds) error → 503; nonRunning → 409;
both gates absent → acceptance refuses, legacy merge passes unchanged without a
Phase check; only current missing → stale_gate; wrong Phase → stale_phase;
expected missing/mismatched → stale_gate with both values; otherwise overwrite
client Ref and signal that exact run once. Every refusal sends nothing.
A 409 displays `This gate changed. Refresh before deciding.` without retry.

Before EVERY bound wait, one required write publishes Phase equal to the wait,
GateRef and Acceptance. Failure ends visibly, never parks at an unusable gate.
Plan gates: `plan:<seq>:<full64hash>`; other blocked gates:
`block:<seq>:<attempt>:<sha8>`; continue gates: `cont:<seq>:<attempt>:<sha8>`;
merge gates: `merge:<H>:<seq>`. Binding reads vars.gate_ref, not a mutable
search attribute. No signal from a prior gate becomes a current decision.

Merge routing: timeout first; validated delegated GrantId second; Ref-less exact
CTO changes_requested feedback third; matching Ref decision fourth; otherwise
consume stale payload and rewait, up to five then stale_signals. A Ref-carrying
changes_requested needs matching Ref. Grants retain existing VisitId/ArtifactRef
guards, author/CTO exclusions and unanimously published ReviewRef/Scrub evidence.
No new grant authority or grant creation is introduced. A delegated approval
then takes the re-pin and pinned merge it always took; a human approval goes to
MERGE_HOLD; `changes_requested` goes to MERGE_FEEDBACK. Nothing after any of
them changes a gate's timers.

`technical-stalled`, `merge-hold` and `merging` are phases of no gate and are in
no signal registry: there is no button, no push notice and no badge for them,
and a click kept from the merge gate is refused as `stale_phase` because GateRef
is left as published while Phase has moved on. What the run still reads of
`merge-approval` after its gate is described under "Late merge feedback".

Acceptance waits have timeout/matching-decision/stale routing only. Continue/stop
is CEO dashboard-only, including for the configured CTO. Timeout is 720 minutes,
180-minute reminders, three maximum. Merge remains 600/180/2. Human-review is
180/60/2 and never consents to plans, privileges, production effects or merging.
Notices are 1–3 plain sentences with verified facts, next owner/action and at most
one human ask; no signal-sending instructions or invented progress.

## Holds and the technical stall

A technical pending state belongs to the run. It is not a question for a human,
so it never opens a gate.

**CI-only probe.** `ci_probe` is a read-only turn of the configured CTO, 15
minutes, the same text in both modes: the actual head, every check-run and
status page at it, `ci-rollup`, and nothing else. It returns HEAD_OID, CI_READ,
CI, CI_FAILING and CI_DETAIL. Success means all three of: the head is H,
`CI_READ: ok`, `CI: success`. A missing CI_READ line is not success here.

**Human pause.** Every entry to `human_pause` is preceded by a probe, on the
first `needs_human_review` verdict and on every later one. Success opens the
pause as before. `CI: failure` returns to IMPLEMENT with the failing names and
the reviewers' text. A reported different head invalidates. Pending,
unavailable and silent hold: REVIEW_HOLD repeats timer and probe. A green read
during a hold buys one fresh REVIEW at H, not the pause, because the stored
verdict is older than the green result. A hold never approves anything:
`review_ok` stays `no` and ReviewRef stays empty. The review prompt tells
reviewers that the workflow verifies checks after review, so a running check is
no reason to hold a verdict and `needs_human_review` is for a human decision.

**STALLED(reason).** Entered from CI_WAIT (`ci_timeout`, resume PIN), from
REVIEW_HOLD (`ci_timeout`, resume REVIEW) and from MERGE_HOLD (`merge_held`,
resume MERGE). Entry is one required write of Phase `technical-stalled` and
Acceptance `result=blocked;block=<reason>`; GateRef is not written. One notice
goes to the configured CTO and to nobody else: head, reason, the three read
counters, the last CI detail and hold reason, how often the run reads again, and
that a concrete authority, risk, cost or scope question is the CTO's to raise
through its normal channel. Then 24 1800-second timers (one sleep step in a
loop of 24), each followed by one read: 720 minutes, the timeout of the gate
this replaces.

What a read is depends on what stalled. For `ci_timeout` it is the probe, and
green CI at H resumes (a review resumes with a review, never the pause). For
`merge_held` it is the merge check itself, a re-pin and then the preflight when
CI is success, because green CI alone says nothing about why the merge was
held. A clear check restores the gate's Acceptance record under Phase
`merge-hold` and goes to the merge turn; it does not go back to the hold. In
both, a failure goes to IMPLEMENT, a moved head invalidates, and a 24th read
that is not clear ends the run as TIMEOUT, the outcome of a gate timeout:
waiter `cancelled`, PR open, run not Failed. The phase and the blocked record
stay as written for as long as the stall waits. No signal decides anything
here: a signal of any name changes neither the timers nor the outcome, with one
exception, late merge feedback in a `merge_held` stall (below). A human can
cancel the run.

**After a human approval.** MERGE_HOLD first re-pins. Head moved → INVALIDATED.
`CI: failure` → IMPLEMENT. `CI: success` → preflight. Anything else is held.
`merge_preflight` is its own read-only turn (`gh pr view --json
state,mergeStateStatus,mergeable,isDraft,headRefOid`) and the only place a hold
can be reported: `MERGE_PREFLIGHT: clear|held`, `HOLD_REASON:
checks_pending|queue|window_closed|other` and `IS_DRAFT: true|false`. An absent,
failed or unparseable answer is held, never clear. Held writes Phase
`merge-hold` and repeats pause, re-pin and preflight. The approval is kept
throughout, unless late feedback voids it: one consensus round, one gate visit.
When the passes are spent the run stalls with `merge_held`, and the stall's 24
reads are the same check.

The longest a held merge can take, whatever CI does:

| step | count | time |
|---|---|---|
| hold after approval | 12 passes | 60 minutes |
| stall | 24 reads | 720 minutes |
| total | | 780 minutes: 13 hours |

In that time the configured CTO gets one stall notice and makes at most 12 + 24
re-pins after the one at approval, and as many preflights. No merge command is
issued. The run then ends as TIMEOUT: waiter `cancelled`, PR open, run not
Failed. The times do not include the turns themselves.

**Late merge feedback.** An approval is the last word only once the merge
command has been dispatched. Until then, `merge-approval` with `Decision`
exactly `changes_requested`, and either no `Ref` or the `Ref` of the gate that
was approved, voids the approval and sends the PR down the gate's own feedback
route, MERGE_FEEDBACK: review ref withdrawn, Phase `revision`, the review of the
feedback, then IMPLEMENT or a fresh REVIEW. A later merge gate is a new visit
and needs a new approval. A signal that arrives while nothing waits for it is
buffered by the engine, one per signal name, the latest replacing the earlier;
the run reads that buffer in three places:

| where | read |
|---|---|
| MERGE_HOLD | One look without waiting on the pass after the approval. Each later pass's five-minute pause is a wait for the signal, so feedback ends the pause at once |
| STALLED(`merge_held`) | One look without waiting before each read |
| entry to MERGE, by every route (hold cleared, delegated approval, stall resumed) | One look without waiting |

A hold entered before the gate (the preflight was held, so the gate is still
owed) reads the same way: feedback there sends the PR back before the gate
opens, and there is no approval to void.

After the last look the run writes Phase `merging` and issues the merge command
in the same workflow task. Nothing between the look and the command can end the
task, so no signal is delivered between them. That dispatch is the cutoff:
feedback that arrives after it is not read, and belongs on the PR.

Everything else one of these reads meets is dropped and decides nothing, there
or at a later gate: a duplicate or stale approval, a delegated approval, a
rejection, feedback whose `Ref` names another gate, a payload that is not an
object. A payload dropped by a hold's pause ends that pause, so the pass runs
early; the twelve passes are not extended. Two feedback signals behind one
approval are one pass through the route, with the later comment.

The reads are `wait_for_signal` steps that end as a timeout
(`autoCompleteOnTimeout`, zero minutes for a look), with no phase, no notice, no
`visitVar` and a variable of their own, `merge_late_signal`; the gate's payload
in `merge_decision` is never overwritten by them. The route reads its comment
from `merge_feedback_comment`, set by whichever read, or the gate, took the
feedback. They are not gates: the gate's `Ref` binding, its guard and its stale
counting are as they were, and the definition still has exactly one
`merge-approval` gate.

`temporal_signal_workflow` sends the configured CTO's `merge-approval` feedback
only to a run that is Running with Phase `merge-approval` or `merge-hold`.
Anywhere else it is refused with the status and phase, and nothing is sent:
`merging`, `revision`, `implementing`, `technical-stalled`, no phase, a closed
run, a describe that fails or times out. `design-approval` feedback is checked
the same way against its own gate. A dashboard send is checked by the
orchestrator instead, as before: `merge-approval` is accepted in Phase
`merge-approval` only, and a click during the hold is refused as `stale_phase`.
So in the hold, late feedback comes through this tool; a human who wants to
stop a held merge asks the configured CTO to send it, or cancels the run. A
second click made while the gate still shows carries the gate's own `Ref`, which
is why feedback with that `Ref` is accepted.

Known limits:

- A run that loaded an older definition and sits in `merge-hold` has no step
  that reads feedback: the tool lets it through and the run ignores it.
- The tool's describe and its signal are two calls. Feedback sent in the
  instant a run leaves the hold for the merge can be accepted and never read.
- A `merge_held` stall reports Phase `technical-stalled`, so the tool refuses
  feedback there. What the stall's looks take is what was buffered before it.
- Feedback that arrives after an earlier one was taken, during the review of
  that feedback for instance, stays buffered and is the first thing the next
  merge gate takes, as Ref-less feedback sent between gates always was.
- The seed does not change a deployment's stored definition: DbSeeder creates
  definitions that are absent and never updates one that exists. The operator
  installs the new definition, and a run already started keeps the one it
  loaded.

**Merge turn.** `phase4_merge` is a plain delegate with `noRedelivery`, no
continuation retries, a status variable and `ignoreFailure`; no step above it
sets `ignoreFailure`. Its instruction is the pinned command and nothing else
(`gh pr ready` first only when the preflight said the PR is a draft); it reads
nothing before or after. Immediately before it the run records the gate visit
the command is issued for, and the command is skipped for a visit that already
has one. `MERGE_RESULT: merged|head_changed|failed`; a merge turn that answers
`held` is `failed`. After the turn there is only reading: `verify_merge_status`,
repeated up to twice more 60 seconds apart when it answers `ERROR` or nothing
parseable. Reads repeat, the command does not. MERGED ends approved; not merged
with `head_changed` invalidates; anything else ends Failed with
`delivery_failed` and the existing notice. No route leads from the merge turn
back to the preflight, the hold or the turn within the same gate visit; a new
command needs a new review, a new gate visit and a new approval.

A cancelled run sends no merge command. A cancellation that arrives during a
re-pin, a probe or the preflight is swallowed by that step's `ignoreFailure`,
leaves no answer and counts as held; the run then ends Canceled at its next
timer, in the hold or in the stall. A stall's last read has no timer after
it: a cancellation swallowed there ends the run as the TIMEOUT it was one read
away from, Completed and not Canceled, still with no command. At the merge turn
the engine ends the run itself: a `noRedelivery` step is not entered by a
cancelled run and its `ignoreFailure` does not swallow a cancellation, so the
outcome does not depend on the sleep that happens to follow the first
unanswered read.

Not covered:

- Nothing wakes the run when CI finishes. It learns of a result at its next
  timer, up to 300 seconds late in a wait or hold and up to 30 minutes late in
  a stall.
- Each read is still a turn of the configured CTO. A wait costs twelve, a stall
  24, and a stall of a held merge up to 48 (a re-pin and a preflight per
  read).
- Who owns the change after it merged is not decided here.
- `noRedelivery` bounds what the bridge sends, not what the agent executes; see
  `docs/durable-delegation.md`.
- The delegated path is unchanged on purpose: `merge_repin` and
  `phase4_merge_pinned` are the steps that shipped before, instruction text
  included. The pinned merge keeps continuation retries and the five-minute
  re-send, and a re-pin that is not green after a delegated approval still
  invalidates it. Its route to the merge turn passes the same last look for
  late feedback as every other route.
- The pause after merge-gate feedback (`post_feedback_human_review_signal`) is
  not preceded by a probe.
- PIN itself is unchanged: a PIN that reports no head still invalidates, and a
  PIN turn that fails still leaves the previous report in place.

## Child lease, stages and cancellation cleanup

UwePrAcceptanceWorkflow inputs bind Repo, IssueNumber, PrNumber, HeadOid, AttemptId,
Owner, PlanSha256, MaxDurationSeconds, WaitSeconds, PostStages, PinnedAt,
HarnessRetry, SshTargets, CapabilityMapCommit and CapabilityMapSha256. It returns
ACCEPTANCE passed/failed/blocked/head_changed, REASON, RECEIPTS, CLEANUP
verified/residue/deferred, and OWNER_REPORT product_fail/harness_fail/env_blocked/none.

Start the abandoned cleanup backstop BEFORE owner work. Failed backstop start
aborts owner work as owner_unavailable/backstop_failed. Pre-loop is four bounded
stages; each owner delegate has timeout 60, retryOnIncomplete=false and zero
incomplete retries. Every stage first checks the lease: now past PinnedAt plus
MaxDurationSeconds or existing cleanup receipt → attempt_expired, create nothing;
head mismatch → head_changed. Stage one also confirms each runtime capability
and prerequisite, without printing values. Config access is not runtime proof.

One stage per turn; report ATTEMPT, HEAD, STAGE, STAGE_RESULT and protected RECEIPT.
Pass advances, done ends the loop, other results leave it. The wait is the PIN
menu's literal durable timer, never chosen by the owner. Post-loop is four stages
and is skipped for zero PostStages. Cleanup removes only attempt-labelled owned
resources and compares baseline; residue is cleanup_residue. A timeout or delegate
failure means owner_timeout, CLEANUP deferred, and NO more stages or cleanup sent
to that owner. The backstop handles it; late queued work must obey the lease.

Parent child deadlines are literal 60/120/240/360 minutes from the duration bucket.
Child failure/deadline maps to owner_timeout. Backstop waits bucket plus 1800
(5400/9000/16200/23400 seconds), then checks the exact child status. Running means
at most two more 1800-second waits/checks. Still running or failed status lookup
notifies without cleanup. Closed means one idempotent owner cleanup; a verified
receipt returns ALREADY_CLEAN. Residue/failure notifies. It never removes resources
of a live child and survives parent/child cancellation.

## Classification and harness retry

Each reviewer must lead SUMMARY with LABEL product_defect/harness/environment.
Product defect requires changes_requested and a blocker with receipt, expected
versus actual and code path. Harness/environment require approved. Code accepts
only unanimous valid labels and consistent verdicts; mixed/malformed/failed/overflow
returns needs_human_review and null label, with no synthesizer verdict request.
Owner reports are evidence, not routing input.

Product → ROUND_CHECK and verbatim defect blockers; environment → environment;
null → unclassified. Harness retries once per H, only after all prior cleanup.
Reserve with reserve-harness-retry BEFORE dispatch, paginating all comments,
posting a single marker and confirming exactly that marker. reserved dispatches
with HarnessRetry=true; already_spent blocks harness_repeated; conflict/missing/
unknown blocks harness_evidence_unknown. The reservation remains spent after
crash. Harness fixes must preserve plan intent, isolation, resources and checks;
anything broader requires plan_amendment. Never silently retry a failed write.

## Blockers, diagnosis and operational boundaries

Plan blockers: plan_missing, plan_invalid, plan_unapproved, plan_amendment,
plan_changed. Other blockers: prerequisite_missing, privilege_required,
production_risk, owner_unavailable, owner_timeout, cleanup_residue,
cleanup_pending, harness_repeated, harness_evidence_unknown, unclassified,
environment, prior_rounds_unknown and stale_signals. They do not consume rounds.
Attribute-only batch_limit records the continue gate, not a new blocked route.
`ci_timeout` and `merge_held` are stall reasons: they appear in the same
`block=` field with Phase `technical-stalled` and never open a blocked gate.

A blocked continue permits revalidation, not prerequisite creation, tool/network/
key/ACL access, reprovisioning, live definition edits or broader production scope.
Never deploy from this workflow. Owner_timeout needs backstop evidence before
another child. Repeated cleanup blocks require protected receipt review, not
resource deletion by another owner. Unclassified requires human evidence review,
not a synthesizer override. Render failure withholds the public comment, not the
protected result; gate notice reports evidence comment withheld.

A run in `technical-stalled` or `merge-hold` is waiting on a timer, not on a
person; `merging` is the merge command and the reads after it.
`technical-stalled` with `block=ci_timeout` or `block=merge_held` that
ends as a timeout is the signal to look at the PR's checks or merge state.
Monitor Phase, GateRef and Acceptance. A merge gate is healthy only when the
record head equals PR head and result passed (or off/doc-only not_required). Keyword
supports equality only: list by Phase and inspect each run's value. Invalid value
is empty, never authority. Repeated owner_timeout/cleanup_residue/cleanup_pending/
stale_signals or rising fix without implementer activity are broken signals.

## Seed parity (AC29)

Run only against explicit files; no runtime requests or live definition writes.
The command fails if any definition is missing or any named/type/numeric tuple
(including nested steps and literal timer/delegate/child values) differs.

```sh
left=seed.example.json
right=seed.example.json
python3 - "$left" "$right" <<'PY'
import json, sys
names = (
    'UwePrImplementationWorkflow',
    'UwePrAcceptanceWorkflow',
    'UweAcceptanceCleanupWorkflow',
)
def project(path):
    rows = json.load(open(path))
    defs = rows['workflowDefinitions']
    result = {}
    def walk(value, at=''):
        if isinstance(value, dict):
            for key in sorted(value):
                item = value[key]
                here = at + '/' + key
                if key in ('name','type'):
                    yield (here, item)
                elif type(item) in (int,float):
                    yield (here, item)
                else:
                    yield from walk(item, here)
        elif isinstance(value, list):
            for index, item in enumerate(value):
                yield from walk(item,
                    at + '/' + str(index))
    for name in names:
        matches = [d for d in defs
            if d['name'] == name]
        if len(matches) != 1:
            raise ValueError(name)
        result[name] = list(walk(
            matches[0]['definition']))
    return result
try:
    same = project(sys.argv[1]) == \
        project(sys.argv[2])
except (ValueError,KeyError,OSError):
    same = False
print('PARITY: yes' if same else
    'PARITY: no')
sys.exit(0 if same else 1)
PY
```

Compare public seed to itself (zero), then to a copy whose one maxIterations
changes (nonzero). The deployment maintainer compares its private seed locally;
never upload it or make its paths/identities public. This structural check is not
runtime acceptance or proof that live snapshots match source.

## Separately authorized verification, rollout and rollback

Source tests/CI are not production-path acceptance. Before merge, an independent
operator needs explicit scope for throwaway bridge/orchestrator, isolated Temporal
namespace and throwaway agent. Check attributed MCP acceptance-decision refused
for CTO, endpoint Ref injection, synthetic five-defect continue gate, script in
image, cleanup and unchanged pre-existing container/image/start-time identities.
No real credentials, integrations, model calls, live agents or production effects
may be reused to make these checks pass. Retain exact-head evidence and failures.

Deployment is separately authorized: drain active consensus/implementation/doc
runs before bridge restart; deploy runtime AND orchestrator/dashboard together;
confirm GateRef/Acceptance registered and script present; for the acceptance
foundation install cleanup definition,
then acceptance child, then parent LAST. Fresh seeds create absent definitions;
DbSeeder does not update existing definitions. No source PR activates a snapshot.
Keep old parent definition and rollback images/configuration.

Rollback first restores old parent and deactivates new child/backstop definitions.
A human drains/cancels new-definition runs and checks their cleanup. Runtime
rollback requires ZERO remaining new-definition runs. Keep registered attributes
and historical receipts. Legacy merge waits without GateRef retain unchanged
payload behavior, even if their Phase publication failed. Replay evidence is
required for additive engine/classification defaults. Do not treat design approval,
source/seed acceptance, runtime deployment and live-definition acceptance as the
same event.

## Formal review and scope blockers

GitHub formal review is independent of workflow consensus and the human merge
judgment. The read-only preflight includes `reviewDecision` and `reviews` before
offering merge approval and after approval. `GITHUB_REVIEW` is `APPROVED`,
`NOT_REQUIRED`, `REVIEW_REQUIRED`, `CHANGES_REQUESTED` or `UNKNOWN`.
`NOT_REQUIRED` means a successful response explicitly containing null/empty
`reviewDecision`, never a missing field or failed read; contradictory clear
results and non-completed typed preflights remain held.

The existing bounded merge hold and technical stall own missing prerequisites,
with one PR/head/action notice per hold. A pre-gate hold returns to the human
gate, never directly to merge. A same-head post-approval hold may return to its
approved merge turn; moved heads retain existing invalidation. Command-once and
bounded readback guards are unchanged. No formal review is manufactured, and
neither `--admin` nor `--auto` is a fallback.

Every author path records typed status and original output. Textual `complete`
with failed status is not attested. Ordinary incomplete work retains its
five-round bound and concrete report rather than identical generic feedback.
Files to Touch is the implementation map unless explicitly restricted. A
mechanical test fixture/manifest solely registering approved behavior is not a
new permission boundary; changed behavior, access, contracts or an explicit
prohibition still need normal human resolution.

An explicit `IMPLEMENTATION: blocked` with literal one-line `BLOCKER` and
`SCOPE_QUESTION` enters `SCOPE_WAIT` before spending that retry budget. One awaited
`UweHumanInputWorkflow` uses the supported namespace/task queue, approve-reject
mode, actual parent origin and compact artifact/owner context. No artifact is
invented when no PR exists. While waiting there is no author or merge call;
child admission failure, the enriched 1800 UTF-16 bound and cancellation remain
failures/cancellation, never permission to clone a question.

The read-only coordinator executes the seed's Python standard-library validator
against original returned JSON text and a separately fetched **exact-run** result
wrapper, never latest-run. It checks duplicate keys, canonical identity, exact
parent run, original Question/Context, mode, delivery and Status/Value/Comment.
The validator's completed typed result selects only a fixed decision token.
Original JSON, not a model paraphrase of Value/Comment, reaches the author once
as literal data. Rejection remains rejection and expiry remains timeout. Invalid
or unavailable evidence, a repeated blocker at the same head, or a changed/
unreadable artifact stops with a concrete coordinator handoff for fresh-head
verification; it cannot reuse the answer as stale author/merge authority.
A valid same-artifact clarification resumes the existing author once, followed
by fresh review/CI and reserved human gates, not inherited approval.

Causal source evidence: the unmodified seed/engine produced three red cases
(required formal review despite green CI, typed failed and incomplete scope
reports with five unchanged-head author calls); those same causal assertions
pass with the source routing correction. Focused tests also execute the exact
embedded validator, exercise real awaited children on the isolated time-skipping
server and preserve the existing once-command/readback/replay regressions.
These are source/isolated results, not deployed workflow or model acceptance.

Adoption is separate: capture exact current definition bytes/version/hash,
routing metadata, notices, waits/reminders/retries and rollback bytes; prepare
and independently review a candidate preserving custom 43200-minute human
waits. Obtain explicit human adoption approval before writing a new version,
read back its hash/metadata, and leave old running/failed snapshots untouched.
Rollback restores captured definition bytes as a new version before compatible
worker/pin rollback; pending human children still need compatible workers.
`RequireAcceptance` and durable delegation remain default-off throughout.
