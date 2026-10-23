# OpenShell implementation status

This is a **partial, source-first foundation for slice 1 of #474**, not the closed
agent vertical slice and not a deployment or merge recommendation.
OpenShell remains the required target shell. There is no permanent opt-out.
Existing runtime/auth/provider/model selection is not changed by desired metadata.

## Implemented source

| Component | Boundary |
|---|---|
| Additive EF migration | Required desired shell/policy/pending metadata, launch intents, leases, independent retired-ID table; generated designer and snapshot |
| Launch store | Serialized intent, one active slot, exact-owner non-revivable lease, once-only sandbox binding, durable revocation/tombstone; no driver or gateway calls |
| Retire | Revokes before cleanup, retains active slot and cleanup-pending; never claims stopped, deleted, or safe to resume |
| Operator guard | Existing `Orchestrator:AuthToken`, startup-only; blank 503, invalid/duplicate/malformed 401; all lifecycle/new policy route shapes and verbs before legacy exemptions/body/backend |
| Tests | Actual relational EF store on isolated SQLite and actual middleware over TestServer; no duplicated lifecycle or authorization implementation |

The store requires a non-retrying relational context. It refuses EF execution
strategies that replay mutations. The existing retrying orchestrator context is
not wired to it. Ambiguous writes require future request-ID reconciliation.
A created row is never provider readiness, authenticated supervisor evidence or
activation. Metadata is not a claim that an existing agent already runs in OpenShell.
The nullable unique active-agent slot remains held after revocation until a future
verified stop/delete transition proves consumer/poller absence.
Tombstones have no agent foreign key and must survive agent/workspace cleanup.
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
request-scoped authoritative ACL, nonce/session tables and guards, secret binding
inventory and production driver rollback are **not implemented** here.
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

Real-MySQL execution, backup/restore proof, current-supervisor/native CLI identity
qualification, actual agent path, denial/revocation and runtime rollback are UNRUN.
SQLite/TestServer/source builds are not substitutes for those gates.
