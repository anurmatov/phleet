# Runbook: workflow failure accountability

Operator procedures for the feature described in [docs/workflow-outcomes.md](../workflow-outcomes.md).
Every real value — namespaces, coordinator name, project map, tokens, URLs, rollout files and
evidence — is operator-owned and stays out of the repository. Examples use placeholders
(`coordinator`, `namespace-a`, `owner/repo-a`).

> The feature ships **off**. Source merge is not deployment. Each stage below (enrollment, delivery,
> late capture, recovery, qualification) needs its own approval; this runbook does not grant one.
> Host qualification (AC25) is UNRUN until an approved `verify` run produces its receipt.

## 0. Ground rules

- **Never** edit or delete `workflow_outcome_*` / `workflow_recovery_operations` rows by hand,
  never `docker compose down -v`, never run a migration Down or restore over unresolved evidence
  without explicit approval.
- **Never** use the legacy admin restart endpoint to "retry" a failed run; it can ignore a
  terminate failure and mint a new timestamped id. Recovery goes through plan → reserve → guarded
  existing tool (section 10).
- **Never** paste the admin or coordinator token into chat, issues, logs or an agent Env Ref.
- Successful exits have no receipts. Do not create a memory, notification workflow or approver DM
  per failure; record verified results and approved decisions through your existing authorized
  audit procedure.
- The separate delegate-recovery capability and the human-gate DM delivery (#345) are unchanged;
  their exclusions stay in force.

Operator session (host shell; the header file keeps the bearer out of argv and history):

```bash
cd fleet                                   # the runtime dir created by setup.sh
export ORCH=http://127.0.0.1:3600          # orchestrator origin as the host reaches it
umask 077
printf 'Authorization: Bearer %s\n' "$(grep '^ORCHESTRATOR_AUTH_TOKEN=' .env | cut -d= -f2-)" > "$HOME/.wo-auth"
wo() { curl -sS --max-redirs 0 -H @"$HOME/.wo-auth" -H 'Content-Type: application/json' "$ORCH/api/workflow-outcomes$1" "${@:2}"; }
wo /coverage; wo /health; wo '?state=all_attention&limit=50'
# when done: rm -f "$HOME/.wo-auth"
```

Read-only database access (any MySQL client against `fleet-mysql`, read-only account preferred):

```bash
docker exec -i fleet-mysql mysql -ufleet -p fleet_orchestrator -e "<query>"
```

## 1. Enrollment and retention-gap inventory

1. Confirm the off rollout is complete: the reviewed commit is deployed with
   `FLEET_WORKFLOW_OUTCOMES_ENABLED=false`, the migration `20261024050000_AddWorkflowOutcomeAccountability`
   is applied (`SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId DESC LIMIT 3;`),
   and a DB dump plus keyring backup exist (section 12).
2. Run the read-only plan for each namespace and keep its output with the approval:
   `bash scripts/verify-workflow-outcomes.sh plan --namespace namespace-a` (exit 3 = static
   checklist only; 1 = gaps; 0 = no coverage gap).
3. Record per namespace: retention and archival (Temporal `DescribeNamespace`, e.g.
   `temporal operator namespace describe --namespace namespace-a`), the number of Running runs and
   the oldest retained close time. Retention starts at **closure**, so old Running runs and old
   definitions are in scope.
4. Check `FULL_SCAN_SECONDS`: retention minus one full sweep must leave at least two sweep
   intervals; otherwise lower the sweep target or accept a declared `retention_margin` gap.
5. Write `fleet/workflow-outcomes/ownership.json` and `classifiers.json` from
   `docs/examples/workflow-outcomes/`, pinning every placeholder (section 4). Keep the directory
   owner-only (`chmod 700 fleet/workflow-outcomes; chmod 600 fleet/workflow-outcomes/*.json`).
6. With the enrollment approval: set `FLEET_WORKFLOW_OUTCOMES_NAMESPACES`,
   `…_COORDINATOR_BASE_URL` (validated at startup even before delivery) and `…_ENABLED=true`; leave
   the coordinator token blank while delivery is not approved — provisioning then writes no client
   section and nothing is delivered. Restart only the orchestrator
   (`docker compose up -d fleet-orchestrator`) and check `wo /coverage` reports `enabled: true`.
7. Catch-up is store-only: the observer classifies retained runs, enqueues proven failures (they
   wait as pending batches until delivery is enabled) and records gaps. Inventory them:

```sql
SELECT Namespace, Component, Reason, GapStart, GapEnd, KnownRunCount, UnknownRunCount, State
  FROM workflow_outcome_health WHERE ResolvedAt IS NULL ORDER BY FirstSeenAt;
SELECT Namespace, WindowKind, WindowKey, Complete, PassCompletedAt, RetentionSeconds, LastError
  FROM workflow_outcome_checkpoints WHERE WindowKind <> 'lease' ORDER BY Namespace, WindowKind, WindowKey;
```

A gap before the retention horizon is permanent and stays reported as unknown; never declare
coverage for it. Archival is read only where it is already configured and authorized.

## 2. Backfill

There is no separate backfill command: enrollment **is** the backfill. To re-inspect after a policy
fix (for example a newly pinned classifier), apply the change and restart the orchestrator; the
next full sweep re-reads history for `classification_pending` runs. Do not delete checkpoints to
force a rescan — an incomplete window already restarts from its committed token, and deleting rows
loses the gap record.

## 3. Image provenance

Before each stage record, outside the repository:

```bash
git -C <repo> rev-parse HEAD; git -C <repo> status --porcelain        # exact reviewed commit, clean
for i in fleet:orchestrator fleet:agent fleet:temporal-bridge; do docker image inspect --format "$i {{.Id}} {{.Created}}" "$i"; done
docker inspect --format '{{.Name}} {{.Image}}' fleet-orchestrator fleet-temporal-bridge
```

Keep the previous image ids tagged for rollback. The orchestrator and agent builds fail if the
outcome services are missing (`ERROR: Fleet.Orchestrator.dll lacks …`), so a successful build of
the reviewed commit is the minimum; the running container's image id is the proof. Source on
`main` or an agent's reported version alone proves nothing about the deployment. The bridge image
must also carry that commit as `FLEET_BUILD_COMMIT` (a clean-checkout `setup.sh`/`upgrade.sh` build
does); `unknown` leaves every compiled run source-unverified (§4).

## 4. Cause review and classifier maintenance

**Explicit cause vs intentional human disposition.** A Canceled/Terminated run whose cause is
absent or conflicting is `cause_unresolved` and shows in the dashboard's unresolved-cause review
(`wo '?state=unresolved_cause'`). It never enters the inbox by itself.

1. Read the run (`wo /<id>`) and the exact history (Temporal UI or `temporal workflow show
   --workflow-id … --run-id …`). Identify the terminal event id.
2. If a failure or deadline branch caused the closure, or you hold authenticated evidence of the
   failure cause, promote it:

```bash
wo /<id>/classification -X POST -d '{
  "expectedVersion": 1,
  "classification": "confirmed_failure",
  "reasonCode": "failure_caused_terminate",
  "terminalEventRef": "event:<terminalEventId>",
  "causalEvidenceRefs": ["event:<failureEventId>"],
  "authorityRef": "https://<your-approval-record>"
}'
```

3. If an authenticated human decision deliberately closed it, record `intentional_nonfailure` with
   `reasonCode: deliberate_disposition` and the decision as evidence. A prior real failed run of the
   same chain stays recorded separately.
4. Generic MCP/dashboard terminate reasons, caller identity or "looks intentional" are not
   evidence; leave it unresolved. 409 = someone changed it (re-read); 422 = evidence insufficient.

**Unknown abort / null output.** `insufficient_evidence` (null, empty or unparseable Completed
output, `needs_human_review`, rejection text) is not a failure and needs no action unless you hold
typed proof. Do not "fix" it by writing a catch-all classifier.

**Uncovered types.** An open `type_uncovered:<ns>:<type>` coverage incident means Completed runs of
that type are not checked for business failures (Failed/TimedOut are still always failures). The
per-run `classifier_uncovered_type` rows expire like other nonfailure metadata; the incident does
not. It closes only after an approved policy change adds Completed rules for the type, the
orchestrator restarts, and none of the type's runs is still `classification_pending`
(`classifier_missing` or `classifier_uncovered_type`). For a compiled type (its runs schedule no
`LoadWorkflowDefinition`), add a compiled rule pinned by `workflowType` + the reviewed `sourceCommit`
with no `definitionHash`, and evidence you confirmed in a recorded history of that source; for a UWE
type pin the loaded hash as below. Until then the incident is an accepted, visible gap.

**Compiled source unverified.** An open `source_unverified:<ns>` incident means compiled runs whose
workflow tasks did not all record a compiled rule's exact `sourceCommit` as the worker build id.
Read the build ids from the run's history (`WorkflowTaskCompleted` → worker version build id):

1. None, or not a 40-hex commit: the bridge image was built without a verified commit (startup
   warning `FLEET_BUILD_COMMIT is not a 40-hex commit`; at build time `source commit unknown: …`
   from `scripts/source-commit.sh`). Rebuild from a clean checkout with `./upgrade.sh`, or pass
   `--build-arg GIT_COMMIT=$(bash scripts/source-commit.sh)`; runs already recorded stay pending.
   If the image does carry a 40-hex `FLEET_BUILD_COMMIT` but new runs still record none, your
   Temporal server does not keep the unversioned build id: compiled rules cannot match there.
2. Another commit: the bridge runs code no rule was reviewed for. Review that commit's compiled
   workflow and add a rule pinned to it (a rule for the old commit may stay for older runs), or
   leave the gap open.
3. More than one build in one run: it spanned a bridge deploy and no single reviewed source
   produced it. It stays pending; record an operator review decision if needed.

Never pin a commit you did not verify the running image was built from; the gap closes only when no
run in the namespace is pending on a classifier gap. After any bridge rollout, check one new
compiled run: `temporal workflow show --workflow-id <id> --output json` must show
`workerVersion.buildId` equal to the image's `FLEET_BUILD_COMMIT`
(`docker inspect fleet:temporal-bridge --format '{{range .Config.Env}}{{println .}}{{end}}'`) on
every `WorkflowTaskCompleted` event.

**Classifier evidence and hash maintenance.** A `classification_pending` row with
`classifier_missing` means a Completed run of a type/definition hash with no pinned rule:

1. Get the loaded definition snapshot hash and version the run was started with (the row's
   `DefinitionHash`/`DefinitionVersion`, or the definition version table), never the current
   definition.
2. Confirm the failure branch or typed delegate result in a recorded fixture of **that** hash.
3. Add a rule with that exact `workflowType`, `sourceCommit`, `definitionHash`, terminal event
   types and evidence; keep ids unique; restart. Saving a new definition version requires a new
   rule for the new hash; old runs keep using the old one.
4. Never pin a hash you did not observe in a loaded snapshot; an unsupported coverage claim is
   worse than a visible gap.

## 5. Namespace reconnect and page-token failure

Symptoms: coverage `state: degraded|pending`, health incident `component=observer` with a namespace,
or `LastError` on a checkpoint.

- Temporal unreachable / namespace missing: fix connectivity or the namespace list; the observer
  reconnects with 5–60 s backoff and keeps the namespace registered. Nothing to reset.
- Repeated or invalid page token: the window restarts from its committed position with dedup; the
  cursor never advances past an uncommitted page. Persistent errors in one window: capture the
  checkpoint row and the orchestrator log line (ids and reason only), then escalate. Do not edit
  `PageToken`.
- Lost lease (`component=lease`): another orchestrator instance or the DB fenced this one. Run a
  single orchestrator; a stale instance stops advancing pages and claiming batches by itself.
- Full sweep slower than `FULL_SCAN_SECONDS` (`capacity_degraded`): set an explicit adjusted budget
  (raise the target) with approval rather than claiming coverage.

## 6. Coordinator delivery, credential rotation and local inbox restore

**Provision (delivery approval):**

1. Generate the token on the host: `openssl rand -hex 32`; write it to `.env` as
   `FLEET_WORKFLOW_OUTCOMES_COORDINATOR_TOKEN=` (edit the file directly; do not echo it to a
   terminal log). Keep `…_COORDINATOR_TOKEN_KEY` at its default name.
2. Set `FLEET_WORKFLOW_OUTCOMES_COORDINATOR_BASE_URL` to the private origin the coordinator
   reaches (`http://fleet-orchestrator:3600` in the reference stack) and confirm `FLEET_CTO_AGENT`.
3. Restart the orchestrator, then reprovision **only** the coordinator agent. Other agents'
   generated configs must not change (compare their `appsettings.json` hashes before and after).
4. Synthetic check: the coordinator's log shows the inbox client polling;
   `wo /health` shows no `coordinator` incident; a pending batch reaches `delivered` then
   `acknowledged`.

**Rotate the credential** (suspected exposure or schedule): write a new token, increment
`FLEET_WORKFLOW_OUTCOMES_COORDINATOR_CREDENTIAL_VERSION`, restart the orchestrator, reprovision the
coordinator. The old token gets 401 immediately; ownership, batches and `CoordinatorGeneration` are
preserved, and the new token reconciles existing nonces and items without rerunning a model turn.

**Change the coordinator (owner change):** approval first. Set the new `FLEET_CTO_AGENT`,
increment `FLEET_WORKFLOW_OUTCOMES_COORDINATOR_GENERATION`, restart, reprovision both agents
(the old one loses its section). Outstanding items need an explicit operator transfer and a fresh
ack; acks from the old generation are 409.

**Local inbox restore.** The coordinator's `{WorkDir}/.fleet/workflow-outcome-inbox.json` lives in
its workspace bind mount and survives container recreation. If it is lost or corrupt, stop the
coordinator, move the damaged file aside (do not edit it), start the coordinator: it re-fetches its
outstanding batches from the server inbox and reconciles each by receipt readback. Items whose turn
was admitted before the loss show `turn_unknown` (section 7).

## 7. fsync, unknown turn and receipt reconciliation

| Observation | Meaning | Action |
|---|---|---|
| Batch `enqueued`, never `delivered` | Coordinator offline, busy, misconfigured, or its local fsync failed | Check coordinator health and disk; nothing is lost, the batch stays pending |
| `deliveryState: unknown` | A delivered POST's response was lost | The client resolves it with `GET /batches/{id}/receipt?nonce=`; to check by hand: `wo '/batches/<id>/receipt?nonce=<nonce>'` (404 = not delivered, 409 = superseded generation) |
| `turnAmbiguous: true` / `turn_unknown` | A failure turn was admitted and the runtime crashed or timed out before a valid result | Never rerun automatically. Read the batch members, review them, and ack/decide as operator (below) |
| Malformed turn output | Parser refused it; nothing was acked | Same as above; the coordinator gets the items again only through a new decision path |
| Unacked > 15 min | Attention flag + health escalation | Find the cause above; no human spam is sent |

Operator acknowledgement of exact reviewed members (never a wildcard):

```bash
wo /batches/<batchId>/ack -X POST -d '{"generation": 1, "nonce": "<nonce>", "items": [{"id": "<outcomeId>", "version": 2}], "taskId": null}'
wo /<outcomeId>/decisions -X POST -d '{"expectedVersion": 2, "operation": "hold", "owner": "coordinator",
  "reason": "Turn outcome unknown after a runtime crash; reviewing manually.", "evidenceRefs": ["outcome:<outcomeId>"],
  "dueAt": "<future ISO-8601 UTC>", "taskRef": null, "artifactRef": null, "batchId": "<batchId>", "generation": 1}'
```

## 8. Late results: hold queue, delayed handoff, quarantine and consumer pause

Enable capture only after the orchestrator intake is healthy (`FLEET_WORKFLOW_OUTCOMES_LATE_CAPTURE_ENABLED=true`,
restart `fleet-temporal-bridge` — **never during an active consensus or delegate turn**).

- Depth on `fleet.workflow-outcomes.hold` means late-queue handoffs were refused (late queue at its
  quota or missing, or an unknown confirm); the originals were acknowledged only after the confirmed
  hold copy and the consumer drains it. Find why the late queue refused (depth, disk/memory alarm).
- Bridge log `not captured ... held`: the broker confirmed neither copy. The reply stays
  unacknowledged and is retried every 5–60 s; once 64 are held, relay deliveries stall (Error log
  `Late-result hold is full`) until the broker accepts publishes again. Fix the broker; do not purge.
- Queue depth on `fleet.workflow-outcomes.late` that drains slowly is normal during an API outage
  (5/30/60 s delayed attempts, head-of-line ≤ 60 s). Check `wo /health/public` and the orchestrator.
- **Consumer paused** (bridge log shows the consumer cancelled after a publish nack, unknown
  confirm or quota error): the broker kept the message; fix the broker condition (disk/memory
  alarm, `max-length-bytes` reached) and the consumer reconnects within 5–60 s. Do not purge or
  requeue by hand.
- **Quarantine** (`fleet.workflow-outcomes.quarantine` depth > 0, deduplicated health alarm): the
  message failed three delayed stores, was oversize, or had an unknown/wrong-run correlation.
  There is no automatic consumer or replay. Inspect with the RabbitMQ management UI or
  `rabbitmqctl list_queues name messages` (counts only; do not print bodies to shared logs).
  Recovery is operator-only and exact: identify the message id, task id and cause; fix the cause;
  re-submit that one message through the authenticated late-results route or record it as an
  accepted loss through your audit procedure.
- **Decryption:** stored late results and evidence are encrypted with the keyring. A
  `CryptographicException`/keyring health incident means the keyring is missing or wrong —
  restore it (section 12). The ciphertext row is retained; there is no plaintext fallback.
- `accept_late` records the exact ResultId/hash/status and artifact review. It never signals a
  closed run and never approves an artifact.

## 9. Stop-proof checklist (before any retry, reassign or replacement)

All items must be verified **for the exact ids**, with references recorded in the reservation's
`stopProofRefs`:

- [ ] Source run is terminal (Describe of the exact RunId) and no successor or retry run is active
      in its chain.
- [ ] Every delegate task of that run (`<workflowId>/<step>` task ids, all attempts) is stopped or
      safely tombstoned on its agent — a terminated workflow or a missing activity waiter is **not**
      proof the agent stopped.
- [ ] Late replies for those task ids inspected (`late-result:` refs) and either accepted or
      rejected by decision.
- [ ] Issue, PR, head SHA, branch and checks read now; no newer commit or comment since the failure
      that changes the picture; the PR (if any) is not being written by another author.
- [ ] No other running workflow targets the same issue/PR (no duplicate author, PR or reviewer).
- [ ] Production or financial scope: an existing approval covers this exact action.
- [ ] Unavailable, degraded or timed-out probes are listed as missing proof — never assumed fine.

## 10. Reserve and ambiguous dispatch readback

1. The coordinator (or operator) records the decision (`retry`, `reassign`, `resume`, `cancel`,
   `accept_late`).
2. Operator plans, read-only: `wo /<outcomeId>/recovery-plan -X POST -d '{"decisionId":"<id>"}'`.
   `allowed:false` lists `missingProofs`; stop there and keep or set a `hold`.
3. Operator reserves with the exact target and proofs:

```bash
wo /recovery/<operationId>/reserve -X POST -d '{
  "decisionId": "<decisionId>", "expectedVersion": 2,
  "targetNamespace": "namespace-a", "targetWorkflowType": "UwePrImplementationWorkflow",
  "targetTaskQueue": "<task-queue>", "targetAgent": "agent1",
  "inputSha256": "<sha256 of the exact start input>", "scope": "same-pr",
  "stopProofRefs": ["task:<taskId>", "run:<namespace>/<workflowId>/<runId>"],
  "authorityRef": "https://<your-approval-record>"
}'
```

4. The coordinator calls the existing guarded tool with `recovery_operation_id=<operationId>`; the
   bridge authorizes dispatch once and starts `recovery-<operationId>` with RejectDuplicate.
5. Read back: `wo /recovery/<operationId>`. `dispatched`/`verified`: done, follow the new run like
   any other. `dispatch_unknown`: **do not retry and do not reserve again.** Look up the exact id
   `recovery-<operationId>` in Temporal (`temporal workflow describe --workflow-id
   recovery-<operationId>`) and its history; if it exists it is the replacement; if it provably
   does not, record that evidence in a new decision — the old reservation is never recycled.
   `blocked`: read `blockedReason`.

## 11. Authorized recovery vs missing human approval

If any required approval is missing — production or financial scope, a stop the coordinator is not
authorized to perform, a human gate — the outcome stays an **owned `hold`** with the exact human
request in `reason` and a `dueAt`. Do not route around it with the admin restart, a manual signal or
a new workflow start without `recovery_operation_id`. Epic grants, gate guards and tool grants are
not widened by this feature.

## 12. Backup, keyring restore and key rotation

- **Back up together:** a `fleet-mysql` dump (the existing `mysql-backup` procedure) and the
  `workflow_outcomes_keys` volume:

```bash
docker run --rm --entrypoint tar -v fleet_workflow_outcomes_keys:/k:ro -v "$PWD/mysql-backup":/b \
  fleet:orchestrator -C /k -czf /b/workflow-outcomes-keys-$(date -u +%Y%m%dT%H%M%SZ).tgz .
```

  (The volume is named `<project>_workflow_outcomes_keys`; the project is `fleet` for setup.sh
  installs — check `docker volume ls`.) Store the key archive with the same protection as the DB
  dump: it decrypts the evidence.
- **Restore:** stop the orchestrator, restore the DB dump, restore the key archive into the volume
  (owner-only, mode 0700), start the orchestrator. Pending outcomes, batches and cursors resume;
  catch-up records any gap for the outage. Restoring the DB without the matching keys keeps every
  row but leaves evidence undecryptable (keyring health incident) — restore the keys, never delete
  the rows.
- **Data-protection key rotation:** ASP.NET Core rotates keys inside the keyring automatically and
  keeps old keys for decryption. Never prune old keys while any encrypted row they protect is
  retained (resolved failures 90 days, unresolved indefinitely).
- Rehearse a restore on a throwaway stack before enrollment and record the proof.

## 13. Disable and rollback (unresolved rows retained)

**Disable:** set `FLEET_WORKFLOW_OUTCOMES_ENABLED=false` (and `…_LATE_CAPTURE_ENABLED=false`),
restart the orchestrator (and the bridge). Reprovision the coordinator to drop its client section if
desired. Coverage now reports *disabled*. Every table, cursor, the keyring, the coordinator's local
inbox and the unacked backlog are retained; nothing is marked finished; no workflow is cancelled.
Tell stakeholders failure monitoring is off.

**Rollback:** disable first, then roll back the coordinator client (reprovision on the previous
agent image), the bridge, then the orchestrator to the recorded previous image ids. The additive
tables stay in place and are ignored by older code; keep the keyring volume. A migration Down or a
DB restore that would drop unresolved evidence requires explicit approval and a fresh dump first.

## 14. Nonrecursive monitoring outage recovery

The feature never reports its own problems through Temporal, `notify_cto`, memory workflows or
approver DMs. When monitoring itself fails:

1. External probe of `GET /api/workflow-outcomes/health/public` (anonymous, `{healthy}` only) is the
   independent signal; alert on probe failure as well as `healthy:false`.
2. If the orchestrator DB was down: on recovery the observer resumes from committed checkpoints and
   records the outage as a coverage gap; check `workflow_outcome_health` for it. Delivery during a
   combined DB + monitor outage cannot be guaranteed — report the window as a gap.
3. If Prometheus/Alertmanager were down: replay the condition table from the
   [design doc](../workflow-outcomes.md#monitoring) against `wo /coverage`, `wo /health` and
   `wo '?state=all_attention'` and record the outage window in your monitoring audit.
4. Never restart a notification or memory workflow to "report" a failure; never bulk-ack to clear
   an alert.

## 15. Host qualification (AC25, separately approved)

`scripts/verify-workflow-outcomes.sh verify --manifest <file> --approval <https-url>` refuses
(exit 3) unless the manifest satisfies all of:

```json
{
  "schemaVersion": 1,
  "kind": "workflow-outcomes-qualification",
  "approvalRef": "https://<approval-record-for-this-exact-run>",
  "resourcePrefix": "woq-<runid>-",
  "namespaces": ["woq-<runid>-a", "woq-<runid>-b", "woq-<runid>-c"],
  "taskQueues": ["woq-<runid>-queue"],
  "coordinatorClone": { "name": "woq-<runid>-coordinator", "cloneOf": "coordinator" },
  "database": { "connectionEnv": "FLEET_ORCHESTRATOR_MIGRATION_CONNECTION", "server": "woq-<runid>-mysql" },
  "maxDurationSeconds": 7200,
  "images": { "orchestrator": "fleet:orchestrator", "agent": "fleet:agent", "temporalBridge": "fleet:temporal-bridge" },
  "configFiles": ["ownership.json", "classifiers.json"],
  "cleanup": { "owned": ["woq-<runid>-a", "woq-<runid>-b", "woq-<runid>-c", "woq-<runid>-queue",
                         "woq-<runid>-coordinator", "woq-<runid>-mysql"], "verify": true },
  "requires": [],
  "suites": {}
}
```

- Every created name starts with `resourcePrefix`, is listed in `cleanup.owned`, and none is a
  production name (`fleet`, `default`, the configured namespaces or coordinator, core service names).
- `FLEET_ORCHESTRATOR_MIGRATION_CONNECTION` must point at `database.server` — a throwaway MySQL,
  never `fleet-mysql`.
- The working tree must be clean; image ids and config-file hashes are recorded in
  `provenance.json` before anything runs; the run is killed at `maxDurationSeconds`.
- `requires` / `suites.<name>.{projects,requires}` let the manifest add prerequisites (for example
  the env vars of a real test Temporal and coordinator clone) — it can never remove one.

The script itself never starts workflows, migrates a live database or touches production names; it
runs `tests/integration/workflow-outcomes/run.sh all --manifest <file>`. Creating the throwaway
namespaces and clone before, and cleaning up only `cleanup.owned` after, are operator steps under the
same approval. Actual production workflows, queues, agents and financial flows are untouched, and
no real human messages are sent. Keep the receipt and provenance outside the repository.
