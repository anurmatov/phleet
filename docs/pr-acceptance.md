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
pending uses the existing 12 × 300-second CI_WAIT. CI success for code or
doc-only goes directly to MERGE_GATE without acceptance or cleanup children.
Only `ci_timeout`, unknown-head `owner_unavailable` and `stale_signals` can
reach BLOCKED. No plan, recovery, owner, harness or child reason can block.

All four PIN instructions carry `REQUIRE_ACCEPTANCE: <off|required>`, including
post-approval repin. In `off`, read only the actual head, every CI page and diff
paths. Skip body hashing, design lookup, plans, receipts, prior rounds, owner
and capability-map checks. Ignore missing/malformed acceptance tokens.
Unchanged head plus successful CI after human approval still routes to MERGE;
otherwise approval is invalidated. The required acceptance subtree is unchanged.
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
| `ci-rollup` | `--checks-file`, `--statuses-file`, complete arrays | `CI`, `CI_FAILING` |
| `change-class` | `--paths-file`, one path per line | `CHANGE_CLASS` |
| `access-check` | `--plan-file`, `--config-file`, `--status-file`, `--target-agent`, CSV `--reviewers`, explicit `--capabilities` | `OWNER` |
| `harness-evidence` | `--comments-file`, complete comment array; `--head` | `HARNESS_RETRY_SPENT` |
| `reserve-harness-retry` | `--repo`, `--pr`, `--head`, `--attempt-hash` | `RESERVATION` |
| `render-comment` | `--receipt`, `--ssh-targets` | sanitized comment or nonzero with empty stdout |

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

The copied validator is 21,584 bytes. The baseline agent build already installs
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
| HUMAN_PAUSE | human-review 180/60/2; approved → fresh REVIEW, changes_requested → revision then IMPLEMENT, rejected/timeout → terminal |
| UNRESOLVED | Report threads only; no push; unresolved threads → IMPLEMENT; none → PIN |
| PIN | Re-read actual head, all CI pages, immutable plan evidence, owner access, prior receipts and counters |
| CI_WAIT | Literal 300-second timer, maximum 12; repeat PIN; still pending → ci_timeout |
| CLEANUP_WAIT | Literal 1800-second timer, maximum 16; repeat PIN; still dirty → cleanup_pending |
| ACCEPT | Enforce batch entry guard, update counters, W1, then one awaited exact-head child |
| CLASSIFY | Code-validated unanimous classification of failed attempt receipts; no synthesizer verdict |
| ROUND_CHECK | batch 5 → CONTINUE_GATE before another revision; otherwise same-PR product fix |
| CONTINUE_GATE | CEO-only acceptance-decision; continue resets batch only, stop → STOPPED |
| BLOCKED | CEO-only acceptance-decision; continue → PIN and full revalidation, stop → STOPPED |
| INVALIDATED | W4 clear; publish sanitized head invalidation; withdraw review ref and fresh delta REVIEW |
| MERGE_GATE | Required Phase/GateRef/Acceptance write then bound CEO merge wait |
| MERGE | Recheck H and CI, then ready/merge with --match-head-commit H; verify actual merged state |
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
`REQUIRE_ACCEPTANCE` on every PIN and post-approval repin. Off-mode PIN emits
only HEAD_OID, CI and CHANGE_CLASS. Required-mode PIN emits
PR, HEAD_OID, IS_DRAFT, CI, CI_FAILING, CHANGE_CLASS, PLAN, PLAN_SHA256,
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

First attempt is round zero. At ACCEPT entry, a different head with batch 5
must enter CONTINUE_GATE before any counter update or child. After five failed
retests, ROUND_CHECK asks before the sixth fix. A harness retry or blocked rerun
on the same head is not a round. Continue returns to its caller (IMPLEMENT or
ACCEPT); BLOCKED continue always repeats PIN and cleanup verification.

Caps are independent: delivery 60; consensus five per review entry; stale signals
five per gate visit; CI 12 × 300 seconds; cleanup 16 × 1800 seconds. Fix total and
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
No new grant authority or grant creation is introduced.

Acceptance waits have timeout/matching-decision/stale routing only. Continue/stop
is CEO dashboard-only, including for the configured CTO. Timeout is 720 minutes,
180-minute reminders, three maximum. Merge remains 600/180/2. Human-review is
180/60/2 and never consents to plans, privileges, production effects or merging.
Notices are 1–3 plain sentences with verified facts, next owner/action and at most
one human ask; no signal-sending instructions or invented progress.

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
production_risk, owner_unavailable, owner_timeout, ci_timeout, cleanup_residue,
cleanup_pending, harness_repeated, harness_evidence_unknown, unclassified,
environment, prior_rounds_unknown and stale_signals. They do not consume rounds.
Attribute-only batch_limit records the continue gate, not a new blocked route.

A blocked continue permits revalidation, not prerequisite creation, tool/network/
key/ACL access, reprovisioning, live definition edits or broader production scope.
Never deploy from this workflow. Owner_timeout needs backstop evidence before
another child. Repeated cleanup blocks require protected receipt review, not
resource deletion by another owner. Unclassified requires human evidence review,
not a synthesizer override. Render failure withholds the public comment, not the
protected result; gate notice reports evidence comment withheld.

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
