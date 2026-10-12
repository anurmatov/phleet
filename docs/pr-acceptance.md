# PR acceptance implementation status

**Draft implementation for #434 under #441 revision 3. Not ready for activation or merge.**
This page currently documents the validator and visibility-record foundation only.
The parent/child/cleanup definitions, engine extensions, dashboard stale-click guard,
classification/replay coverage and end-to-end scenario tests are still pending in this PR.
Existing seeded definitions and every running snapshot are unchanged.

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

The PIN delegate must collect **every** check/status/comment page; incomplete
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

The required future PIN lookup selects Completed design runs whose Repo,
IssueNumber and ReviewRef match the exact current issue body. It must fetch
**that row's run ID**, with `followRuns: false`, not the latest run under the ID.
The current result-tool extension and PIN seed wiring are not implemented yet.

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

Pending seed wiring must write W1 before each child, W2 after every child before
branching, W3 Phase/GateRef/Acceptance together before every bound gate and W4
clear before every implementer and invalidation. Writes must be required, never
ignored. Doc-only W3 always uses `not_required;iso=production`. No current seed
writes these attributes, so this draft does not claim those transitions work.

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

## Remaining acceptance and rollout

The complete state-machine/caps/gates/restart/runbook, seed parity command,
classification and scenario coverage are still required before this draft is
ready. Existing #443 pause, #445 generation ownership and #447 wake/state source
remain untouched. A green local test or CI result is not production-path proof.
Runtime deployment, namespace registration, definition activation and exact-head
isolated premerge verification require separate authorization; this PR performs
none of them. Registration may fail at startup or stall an upsert workflow task.
Activation must check it first. Keep registered attributes and rollback data.
