# OpenShell implementation status

This is a **partial, source-first foundation for slice 1 of #474**, not the closed
agent vertical slice and not a deployment or merge recommendation.
OpenShell remains the required target shell. There is no permanent opt-out.
Existing runtime/auth/provider/model selection is not changed by desired metadata.

## Implemented source

| Component | Boundary |
|---|---|
| Additive EF migration | Desired shell/policy/pending metadata, full launch fields, leases, bindings, sessions, nonces and independent retired-ID table; generated designer and snapshot |
| Launch store | Serialized intent, one active slot, exact-owner non-revivable lease, authoritative reloads, phase-ordered sandbox binding and revocation records; no driver or gateway calls |
| Retire | Revokes before cleanup, retains active slot and cleanup-pending; never claims stopped, deleted, or safe to resume |
| Operator guard | Existing `Orchestrator:AuthToken`, startup-only; blank 503, invalid/duplicate/malformed 401; all lifecycle/new policy route shapes and verbs before legacy exemptions/body/backend |
| Tests | Actual relational EF store on isolated SQLite and actual middleware over TestServer; no duplicated lifecycle or authorization implementation |

The store requires a non-retrying relational context. It refuses EF execution
strategies that replay mutations. The existing retrying orchestrator context is
not wired to it. Ambiguous writes require future request-ID reconciliation.
A created row is never provider readiness, authenticated supervisor evidence or
activation. Metadata is not a claim that an existing agent already runs in OpenShell.
The nullable unique active-agent slot remains held after revocation. The store can
record cleanup completion only after fresh exact-launch/epoch/runtime evidence
reports absence and zero consumers/pollers, and the retirement tombstone exists.
If no sandbox ID was bound, absence of that ID is insufficient: cleanup requires
fresh, complete authoritative inventory for the launch and epoch with zero owned
sandboxes, including ambiguous create results across the applicable gateways.
Incomplete inventory, a negative/unknown count or any owned sandbox retains the slot.
There is no display-name fallback and no inference from a missing DB mapping.
It then releases only that launch's lease and active slot, allowing a fresh UUID.
Failed reservation writes detach only their launch and lease entries, including the
1062 conflict path, so a reused context cannot silently replay the reservation.
Unrelated tracked changes are retained; an ambiguous commit still needs reconciliation.
`prepared -> legacy_stopped -> created` is enforced through store methods.

`IOpenShellRuntimeEvidenceReader` is an application adapter boundary, not an invented
upstream hook. Its production authenticated driver/consumer/poller reader is still
**unimplemented and unregistered**. Tests use owned synthetic observations to test
these production transitions, not to claim live verified stop/delete or admission.
Without a reader these transitions fail; no API accepts client-supplied evidence.

Tombstones have no agent foreign key and survive ordinary agent/launch deletion.
The append-only/mapping/revocation guards cover this store and tracked SaveChanges
only. `ExecuteUpdate`, `ExecuteDelete`, raw SQL, destructive Down and privileged
writers bypass them: **DB-enforced permanence is not implemented or claimed**.
Such writes are outside the claimed boundary, and the existing persistence cleanup
fixtures use them only on owned test databases, not as a production cleanup path.
Application rollback retains the additive schema; do not execute destructive Down
without separately approved backup, restore/query proof and tombstone preservation.

## Configuration and installation boundary

The operator guard uses the existing `ORCHESTRATOR_AUTH_TOKEN` mapping to
`Orchestrator:AuthToken`; no new credential, hot reload, permission or MCP tool is
introduced. Installations without that token can no longer access lifecycle REST
routes, including their GET/HEAD/OPTIONS shapes. Existing ConfigToken and caller
query/header attribution are not operator authority.
No installer enables OpenShell, starts services, changes running/stopped sets or
imports credentials. Default operator host `.env` sources and optional 1Password
are unchanged. No gateway, identity listener or migration API is available in this
partial implementation, so no OpenShell workload can be admitted through it.

## Remaining slice-1 implementation (not merely untested)

The shared lifecycle adapter and all REST/MCP/facade/Docker bypass callers,
static endpoint/policy compiler, pinned gateway protobuf client, post-create
current-supervisor readiness, bootstrap/activation gate and nonroot image/home
changes are **not implemented** here.
Outside identity middleware/issuer/ingress, receiving memory verified listener,
request-scoped authoritative ACL, nonce/session issuance and limit guards, secret binding
inventory writers and production driver rollback are **not implemented** here.
Full install/config/example lockstep and actual daemon/native CLI/shell/verified
memory/Rabbit-to-matching-workflow fixture entrypoints remain to be written.
No upstream hook, header canonicalization or native compatibility is inferred.
The upstream target remains OpenShell v0.1.5 at
`bdb956f2cd65a635f1294fccf1d138d263bf650f`; this change does not implement its wire protocol.
Unsupported service/SDK/provider combinations stay unadmitted; later slice 2 and
AC1–34 are not claimed complete. No real credential, bot, account, grant or model
change follows a source test or merge.

## Reproducible checks

Run `bash tests/openshell/run.sh unit` for the local store and operator guard.
Run `dotnet test tests/Fleet.Orchestrator.Tests --filter FullyQualifiedName~ModelSnapshotTests`
for migration target/snapshot consistency, without a database.
After separately approving a disposable MySQL target, supply
`FLEET_ORCHESTRATOR_MIGRATION_CONNECTION` and run
`bash tests/openshell/run.sh migration` for fresh/upgrade/query/tombstone tests.
The MySQL fixtures create/drop only their unique synthetic database; no existing
database is a test target. Missing prerequisite returns BLOCKED (3), never PASS.

CI evidence for the previous head `a7bc3feaa4752a98c72fb409c235a1fdee74040a`:
[Orchestrator migrations (MySQL), run 40005153372](https://github.com/anurmatov/phleet/actions/runs/40005153372/job/120591076531)
passed 48/48 tests with zero skipped, including both new migration fixtures.
This is real-MySQL fresh/upgrade/query evidence for that head only, not this revision's
CI result, backup/restore proof, authenticated cleanup proof or agent acceptance.
New-head CI is not assumed passed and must be checked independently after push.
Backup/restore, current-supervisor/native identity qualification, the actual agent
path, denial/revocation and runtime rollback remain UNRUN.
SQLite/TestServer/source builds are not substitutes for those gates.

## Storage added in this revision, not admission

The same unmerged `20261023180657_AddOpenShellExecution` migration now includes:

| Storage | Shape / still-required runtime consumer |
|---|---|
| Launch | Nullable workspace, policy/image/destination hashes, control URL, capability reference, bounded error, updated timestamp, poller lease owner/expiry and offset; compiler and lifecycle validation still missing |
| agent_secret_bindings | Unique ordinal agent/reference, unclassified default, source kind, provider/alias/profile/endpoint hashes, expiry, risk reference and optimistic version; no values or inventory API |
| agent_identity_sessions | Ordinal hash/service/route key, agent/launch/epoch/expiry and expiry index per launch; verified session acquisition and 100-session bound still missing |
| identity_request_nonces | Ordinal globally unique JTI, launch foreign key and expiry index; atomic request consumption, expiry+60s cleanup and 64-request bound still missing |

Nullable launch fields allow prepared intent without fabricated compiler/readiness
receipts. Tables alone do not authorize sessions, prevent twin pollers or supply a
verified listener. No issuer/listener is registered and no request uses these rows
as authority. Tests cover storage, key uniqueness and failed-save detachment only.
The MySQL suite now also injects an actual duplicate lease during store SaveChanges
to exercise 1062 and reused-context cleanup; that new case is UNRUN locally.
The regenerated designer/snapshot match the model; new-head MySQL execution,
restore/query and full-runtime acceptance are still required, not inherited from CI.

This draft migration has not been released. Disposable databases that applied an
older draft need a fresh database for qualification, not a misleading no-op reapply
of the same migration ID. No production migration or destructive reset is authorized.
The remaining lifecycle caller/delete path must retain rows until verified cleanup
and map pending/conflict explicitly; restrict FKs are not that endpoint handling.
Do not populate launch rows in an actual installation while caller migration is missing.
