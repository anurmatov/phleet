# Epic grants

An epic grant lets the configured CTO agent (`FLEET_CTO_AGENT`) send `approved` on in-scope
`design-approval`, `merge-approval` and `doc-review` gates, for one bounded scope a human approves
in the dashboard. Every delegated approval names the exact artifact that was reviewed and the
current gate visit, and is checked twice: by the orchestrator before it is sent, and by the
workflow when it consumes it.

The feature is off by default (`FLEET_EPIC_GRANTS_ENABLED=false`). With it off, or with no active
grant, every gate behaves exactly as before. Rejections, `advisory-review` and `human-review` are
never delegated.

## Threat model

Trusted, as before, and not hardened by this feature: the `?agent=` attribution on MCP endpoints,
the shared orchestrator admin bearer (the dashboard and agent containers hold it), and host access.
Whoever holds them is trusted. Epic grants are not a defence against a malicious operator or agent.

What the feature does guarantee:

1. A delegated approval names the reviewed commit (PR) or document hash (design, doc) and the
   current gate visit. A stale or buffered one is discarded **when the workflow consumes it**.
2. The review that enables delegation excludes the author and the CTO, and must be unanimous.
   The synthesizer cannot produce an attestation.
3. At most one delegated decision per gate visit, enforced by a permanent unique database key. A
   decision row is never deleted, released or reset, and a send is never retried.
4. The reviewed artifact is never inferred from timestamps or "latest" state.

## How it fits together

| piece | role |
|---|---|
| engine (`wait_for_signal`) | `visitVar` mints `"<signal>:<n>"` per entry and publishes it as the `GateVisit` search attribute while parked; `delegatedGuard` discards a payload carrying `GrantId` unless its `VisitId` and `ArtifactRef` equal the current visit and `vars.review_ref` |
| consensus (`ConsensusReviewWorkflow`) | `ReviewRef` in, `ExcludedAgents` refused (`ReviewerNotIndependent`), `AttestedRef` out only on a unanimous approval where every review ends `REVIEWED_REF: <ref>`; `ScrubAttested` for `PUBLIC_SCRUB: pass` |
| definitions | publish `review_ref` / `ReviewRef` (and `ReviewScrub=pass`) only after an attested approval, clear them at the start of every review round, guard the gate |
| bridge (`temporal_signal_workflow`) | the configured CTO's `approved` + `GrantId` is forwarded to the orchestrator, never signalled directly |
| orchestrator | runs D1–D11, reserves the visit, sends the five-field signal to the exact run id |
| dashboard "Epic Grants" | validate and create a scope, list and revoke grants, show decisions |

### The definitions (seed versions)

| definition | author's ref line | after a grant approval |
|---|---|---|
| `UwePrImplementationWorkflow` | `HEAD_SHA: <40 hex>` from `TargetAgent` (implement and every revise step) | merge with `gh pr merge --squash --match-head-commit <review_ref>`; a push after review that was not re-reviewed cannot merge (failure path + notice). The doc start passes `IssueNumber`, `ConsensusAgents` and `PrepAgent` (from optional input `DocPrepAgent`) |
| `UweDesignWorkflow` | `BODY_SHA256: <64 hex>` = `gh issue view <n> --repo <r> --json body \| jq -j .body \| sha256sum` | `verify_approved_body` recomputes the hash; a mismatch (or no hash) ends the run with `ERROR:approved_artifact_changed`. Human approvals skip it |
| `UweDocMaintenanceWorkflow` | `ARTIFACT_SHA256: <64 hex>` of the artifact body from `PrepAgent` | `apply` writes exactly the reviewed artifact and must return the same `APPLIED_ARTIFACT_SHA256` |

Doc maintenance is prepare → classify → review → publish → gate → apply. **Nothing is written
before the gate.** Only `memory` and `repo_doc` targets are delegable; an `instruction`,
`project_context` or any other target leaves `ReviewRef` empty, so only a human can approve it.
The artifact body is every line strictly between `-----BEGIN DOC ARTIFACT-----` and
`-----END DOC ARTIFACT-----`, each ending in one LF:

```
awk '/^-----BEGIN DOC ARTIFACT-----$/{f=1;next}/^-----END DOC ARTIFACT-----$/{f=0}f' <file> | sha256sum
```

`PrepAgent` empty → the CTO, which makes the run human-only (D7). A doc run started without
`ConsensusAgents` (an older PR definition) skips the review and stays human-only.

⚠️ The new seed versions pass `ExcludedAgents = "<author>,<CTO>"` to every PR and design review. A
panel (`ConsensusAgents`) that contains the author or the CTO now fails that review with
`ReviewerNotIndependent` — keep both out of the panel. In doc maintenance a failed review only
leaves the gate human-only.

## Scope

```json
{
  "driver": {"namespace": "fleet", "workflowId": "<driver-id>", "runId": "<driver-run-id>"},
  "targets": [
    {"repo": "<owner>/<repo>", "issues": [12, 13]},
    {"repo": "<owner>/<public-repo>", "issues": [7], "allowPublic": true}
  ],
  "gates": ["design-approval", "merge-approval", "doc-review"],
  "workflows": [
    {"type": "UwePrImplementationWorkflow", "version": 21, "sha256": "<hex>"},
    {"type": "UweDesignWorkflow", "version": 16, "sha256": "<hex>"},
    {"type": "UweDocMaintenanceWorkflow", "version": 8, "sha256": "<hex>"}
  ],
  "expiresAt": "2026-11-01T00:00:00Z"
}
```

A definition is delegation-capable for a gate only when **every** `wait_for_signal` on that gate
has a literal `visitVar` and a `delegatedGuard` with marker exactly `GrantId`,
`require.VisitId` exactly `{{vars.<that visitVar>}}` and `require.ArtifactRef` exactly
`{{vars.review_ref}}`. One unguarded or differently guarded wait on the gate disqualifies the
gate, and a wait with a templated `signalName` disqualifies the whole definition. Grant creation
and D5 apply the same rule.

Only these fields are allowed. At creation, any failure stores nothing:

- the driver run is Running at that exact run id;
- `expiresAt` is in the future and within `FLEET_EPIC_GRANTS_MAX_DAYS`;
- no target is in `FLEET_EPIC_GRANTS_DENIED_REPOS` — **the deny list wins over `allowPublic`**;
- a public target needs `allowPublic: true`, `allowPublic` on a private repo is refused, and an
  unknown visibility is refused;
- each workflow's `sha256` is the SHA-256 of the stored definition for exactly that (type,
  version) — the current row when its version matches, else the archived version — and the tree
  has a guarded gate (`visitVar` + `delegatedGuard`);
- each gate is served by at least one listed workflow.

The scope is immutable. To change it, revoke the grant and create a new one.
`POST /api/epic-grants/validate` returns the same report without storing anything; the dashboard
uses it before **Create**.

## Making a decision

The CTO agent reads the gate notice (it names the visit and the reviewed ref) or the run's
`GateVisit` and `ReviewRef` attributes, reviews the exact artifact independently, and calls:

```
temporal_signal_workflow
  workflow_id: <gated run>
  signal_name: merge-approval
  args: {"Decision":"approved","GrantId":"<id>",
         "VisitId":"merge-approval:2","ArtifactRef":"<ref>",
         "Evidence":"https://..."}
```

The bridge forwards it to `POST /api/epic-grants/{id}/decisions` (its own HTTP client, 120 s
timeout) and returns `status: delegated` with the orchestrator's `result` (`sent`, `refused`,
`send_failed`) and `reason`. A forward that fails or times out sends nothing from the bridge and is
not retried; if the request may have reached the orchestrator, read the grant's decisions table
before acting. Once D11 starts its insert, the orchestrator finishes the reservation and the send
even if the caller has gone.

The orchestrator stops at the first failing check and sends nothing:

| # | check | refusal |
|---|---|---|
| D1 | feature on; caller == configured CTO == grant `CtoAgent`; `approved`; delegable gate; `VisitId`, `ArtifactRef` and an `https` `Evidence` (≤ 500 chars) | `disabled` / `caller` / `bad_request` |
| D2 | grant active, not expired, scope re-hashes to `ScopeSha256` | `grant_inactive` / `expired` / `scope_tampered` |
| D3 | driver run Running at its exact run id | `driver_not_running` |
| D4 | target run Running, started after the grant, linked: input `WaiterWorkflowId` = driver id, or a parent ≤ 2 levels up is the driver run or linked | `not_linked` |
| D5 | the run's own loaded definition (name, version) is pinned in scope and still hashes to its pin; the gate is in scope and guarded by it | `definition_out_of_scope` / `gate_out_of_scope` |
| D6 | input `Repo` + `IssueNumber` (PR, doc) or `ExistingIssueNumber` (design) in `targets`; not denied; visibility known; public needs `allowPublic` | `target_out_of_scope` / `visibility_unknown` |
| D7 | author (`TargetAgent`, or `PrepAgent` for docs) ≠ CTO | `author_is_decider` |
| D8 | `GateVisit` == `VisitId` with prefix `<gate>:` | `stale_visit` |
| D9 | `ReviewRef` non-empty and == `ArtifactRef` | `stale_artifact` |
| D10 | public target → `ReviewScrub == pass` | `scrub_missing` |
| D11 | insert the `reserved` decision row under `FOR UPDATE` on the grant, re-checking active and unexpired | `already_decided` |

Any Temporal, database or unexpected error is `unknown`. After D11 commits, the signal goes once to
the pinned run id with exactly `Decision`, `GrantId`, `VisitId`, `ArtifactRef`, `Evidence`, and the
row becomes `sent` or `send_failed`. A crash leaves it `reserved` ("delivery unknown"). A new
decision is possible only at a new gate visit.

A payload carrying `GrantId` with any decision other than `approved` is refused by the bridge and
not sent: a guarded gate would treat it as delegated and discard it, losing the feedback. Send
feedback without `GrantId`.

A human `changes_requested` at a PR or design gate withdraws the published ref (`review_ref` and
`ReviewRef` become empty). If the follow-up review finds the concern unwarranted and the gate is
re-entered without a new review, that visit is a human decision only; delegation resumes after the
next attested review.

## Expiry and revocation

- Expiry is decided in D11's transaction (orchestrator clock); D2 is an early exit. There is no
  sweeper — `expired` is computed.
- `POST /api/epic-grants/{id}/revoke` sets `revoked` in one transaction. It stops every decision
  whose D11 commits after it. A decision already reserved is sent once and can only be consumed by
  the visit it names. A consumed approval cannot be recalled; handle it on the PR or workflow.
- A driver that is no longer Running ends the grant's use (D3). A new driver run needs a new grant.
- Dashboard decisions carry no `GrantId`, so the human can always decide.

## Launch checklist

1. Every target repo exists, and has branch protection that requires the review you expect.
2. `FLEET_EPIC_GRANTS_DENIED_REPOS` lists every repo that must never be delegated.
3. The new definition versions are saved (seeds are create-if-absent; an existing deployment saves
   them explicitly) and their (version, sha256) pairs are what the scope pins.
4. Review panels contain neither the author nor the CTO.
5. When `doc-review` is in scope, PR runs are started with `DocPrepAgent` (an agent other than the
   CTO), or doc maintenance stays human-only.
6. The driver run is Running, and the PR and design runs it starts pass `WaiterWorkflowId`.
7. First grant on a throwaway epic.

## Deploy and rollout

- Merge with the feature off. The migration only adds `epic_grants` and `epic_grant_decisions`.
- **Restart the Temporal bridge only when no `ConsensusReviewWorkflow` is Running and no UWE run is
  inside a delegate step.** Consensus delegates run with a single attempt, so a bridge restart
  fails in-flight reviews. Search attributes `GateVisit`, `ReviewRef`, `ReviewScrub` register at
  bridge start.
- To enable: set `FLEET_EPIC_GRANTS_ENABLED=true`, restart the orchestrator, **then restart every
  service that bootstraps peer configuration from the orchestrator only at startup** (in the
  reference compose, the Telegram service).
- Running workflows keep their definition snapshot; only runs started on the new versions are
  delegation-capable.

`setup.sh` writes the three keys with their defaults on a fresh install, and `upgrade.sh` adds any
that are missing; neither prompts or changes a present value.

## Monitoring

- Orchestrator: `EpicGrant decision grant={id} workflow={wf} gate={gate} result={sent|send_failed|refused} reason={code}`;
  `epic_grants_misconfigured` when `ENABLED=true` with `MAX_DAYS ≤ 0` (treated as disabled).
- Engine: `delegated signal discarded: stale visit or artifact (signal=… visit=…)`.
- Bridge: `Delegated <gate> approval for workflow …; outcome=…; reason=…`.
- Dashboard decisions table: `sent` rows should follow reviews. `send_failed`, and `reserved`
  older than 60 s ("delivery unknown"), need a human look.
- **Refusals live only in the orchestrator log** (`result=refused reason=<code>`); the decisions
  table holds reservations only.

## Troubleshooting by refusal

| reason | usual cause |
|---|---|
| `disabled` | feature off, or `MAX_DAYS ≤ 0` |
| `caller` | not the configured CTO, or the CTO changed since the grant was created |
| `grant_inactive` / `expired` | revoked, unknown id (ids are the 36-character form), or past `expiresAt` |
| `driver_not_running` | the driver run ended or was reset; create a new grant |
| `not_linked` | run started before the grant, or no `WaiterWorkflowId` / parent link to the driver |
| `definition_out_of_scope` | the run uses a definition version or content the scope does not pin |
| `gate_out_of_scope` | gate not in `gates`, or the pinned definition does not guard it |
| `target_out_of_scope` | repo or issue not listed, repo denied, or public without `allowPublic` |
| `visibility_unknown` | the unauthenticated GitHub read failed: rate limit (60 requests per hour per IP), 5xx or timeout. A 404 means "not public" — also for a missing or renamed repo, which can never match because the repo must be listed |
| `author_is_decider` | `TargetAgent`/`PrepAgent` is the CTO, or `PrepAgent` was empty |
| `stale_visit` | the gate visit moved on (re-entered, timed out, or not parked) |
| `stale_artifact` | a new push was re-reviewed, or no attested review was published |
| `scrub_missing` | public target without an approving `PUBLIC_SCRUB: pass` |
| `already_decided` | this visit already has a decision row; wait for the next visit |
| `unknown` | Temporal, GitHub or database error; check the orchestrator log |

## Rollback

`FLEET_EPIC_GRANTS_ENABLED=false` and an orchestrator restart (plus the startup-config restarts
above) refuse every decision with `disabled` at once. Re-save the previous definition content as a
new version to return a definition to the human-only shape. Reverting the code uses the same
no-running-consensus rule for the bridge; the migration's Down drops only the two tables.
