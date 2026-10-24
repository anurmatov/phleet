# OpenShell implementation status

This is a **partial, source-first foundation for slice 1 of #474**, not the closed
agent vertical slice and not a deployment or merge recommendation.
OpenShell remains the required target shell. There is no permanent opt-out.
Existing runtime/auth/provider/model selection is not changed by desired metadata.

## Implemented source

| Component | Boundary |
|---|---|
| Shared lifecycle dispatch | REST/MCP start/stop/restart/version/create/delete/provision/preview/reprovision/bulk and compatibility facades use `IAgentRuntimeLifecycle`; only never-launched pending agents may retain legacy execution |
| Legacy bypass guard | Any launch history, migrated/unknown metadata or failed ownership lookup rejects before legacy runtime/config access; infrastructure operations reject agent aliases and pinned OpenShell driver labels; an optional nonempty host ID list adds a restriction |
| Delete | DB/ACL/registry deletion requires confirmed deprovision, including exact-ID absence after removal; read errors do not mean absent |
| Additive EF migration | Desired shell/policy/pending metadata, full launch fields, leases, bindings, sessions, nonces and independent retired-ID table; generated designer and snapshot |
| Launch store | Serialized intent, one active slot, exact-owner non-revivable lease, authoritative reloads, phase-ordered sandbox binding and revocation records; no driver or gateway calls |
| Retire | Revokes before cleanup, retains active slot and cleanup-pending; never claims stopped, deleted, or safe to resume |
| Operator guard | Existing `Orchestrator:AuthToken`, startup-only; blank 503, invalid/duplicate/malformed 401; all lifecycle/new policy route shapes and verbs before legacy exemptions/body/backend |
| Verified memory receiver source | Optional held-off HTTPS/mTLS listener, pinned ES256 request proof, authoritative mapping/consumed-nonce read, bounded local sessions, fresh HttpContext ACL and all six tools; actual TLS/ingress/agent integration UNRUN; SQL reader cases passed prior-head CI |
| Request ACL acquisition | Actual AclCacheService one-fetch immutable snapshot API, five-second deadline, malformed/operator/error rejection and synchronous pending-generation invalidation |
| Tests | Actual relational EF store on isolated SQLite, actual operator middleware over TestServer and actual ACL service through an isolated HTTP handler; none is daemon acceptance |

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
are unchanged. The receiver listener is held off by default and no outside gateway/issuer or
migration API is available, so no OpenShell workload can be admitted through it.

## Remaining slice-1 implementation (not merely untested)

Shared dispatch and caller wiring now exist, but the OpenShell lifecycle backend is
still unimplemented and returns 422 compatibility_blocked without a legacy fallback.
Static endpoint/policy compiler, lifecycle mutation orchestration, post-create
current-supervisor readiness, bootstrap/activation gate and nonroot image/home
changes are **not implemented** here.
Outside supervisor identity hook/issuer/ingress, durable issuer session/replay
lifecycle, secret binding inventory writers and production driver rollback remain
**not implemented** here. Memory receiver/proof/state-reader/six-tool source exists,
but real TLS, DB and upstream/native wire compatibility is unqualified, not admission.
Other receiving services remain unimplemented and blocked.
Full install/config/example lockstep and actual daemon/native CLI/shell/verified
memory/Rabbit-to-matching-workflow fixture entrypoints remain to be written.
No upstream hook, header canonicalization or native compatibility is inferred.
The gateway wire target is the conditional-mutation fork commit
`80d76a5c72d2a752d811775b62c86e974bc2bf19`; a source pin is not runtime qualification.
Unsupported service/SDK/provider combinations stay unadmitted; later slice 2 and
AC1–34 are not claimed complete. No real credential, bot, account, grant or model
change follows a source test or merge.

## Reproducible checks

Run `bash tests/openshell/run.sh unit` for the local store and operator guard.
Run `bash tests/openshell/run.sh memory-identity` for cryptographic receiver,
six-tool boundary and actual MapMcp session/dispatch tests, not socket/agent acceptance.
Run `python3 tests/openshell/config-test.py` for the isolated actual setup block and
twelve-key source/config mapping checks, not setup execution.
Run `bash tests/openshell/run.sh memory-acl` for authoritative ACL acquisition tests;
these use only an owned HTTP handler, never an orchestrator or external network.
Run `dotnet test tests/Fleet.Orchestrator.Tests --filter FullyQualifiedName~ModelSnapshotTests`
for migration target/snapshot consistency, without a database.
After separately approving a disposable MySQL target, supply
`FLEET_ORCHESTRATOR_MIGRATION_CONNECTION` and run
`bash tests/openshell/run.sh migration` for fresh/upgrade/query/tombstone tests.
The MySQL fixtures create/drop only their unique synthetic database; no existing
database is a test target. Missing prerequisite returns BLOCKED (3), never PASS.

CI evidence for the previous head `3500fcd506a4f67ef98ed7f218ae31bddc00d1cf`:
[MySQL migration job](https://github.com/anurmatov/phleet/actions/runs/40015656609/job/120626933022)
passed 53/53, including all four production VerifiedLaunchReader current/revoked/
wrong-epoch/missing-nonce cases against the actual migrated schema.
The [overall test job](https://github.com/anurmatov/phleet/actions/runs/40015656609/job/120626932554)
failed `JournalAttachmentContentTests.DigestMismatchAbortsAndNeverCompletesBytes`
in unchanged Comms code; overall green CI is not claimed.
These results are not this new revision's CI, backup/restore or runtime acceptance.

Earlier CI evidence for head `4d0fa6a243fa7c2383d8996fe37d8cac3029dad6`:
[Orchestrator migrations (MySQL), run 40007094718](https://github.com/anurmatov/phleet/actions/runs/40007094718/job/120597653708)
passed 49/49 tests with zero skipped, including the real-1062 context-reuse regression.
The [Build and Test job](https://github.com/anurmatov/phleet/actions/runs/40007094718/job/120597653961)
also passed for that head.
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
verified listener. No outside issuer is registered; the held-off memory listener reads current launch
and consumed nonce rows, but cannot mint them or activate a launch.
Tests cover storage, key uniqueness, failed-save detachment and receiver reads.
The MySQL suite now also injects an actual duplicate lease during store SaveChanges
to exercise 1062 and reused-context cleanup; it passed prior-head MySQL CI above,
but is still UNRUN locally and does not prove backup/restore.
The regenerated designer/snapshot match the model; new-head MySQL execution,
restore/query and full-runtime acceptance are still required, not inherited from CI.

This draft migration has not been released. Disposable databases that applied an
older draft need a fresh database for qualification, not a misleading no-op reapply
of the same migration ID. No production migration or destructive reset is authorized.
The shared delete endpoint retains rows on blocked/unverified cleanup; restrict FKs
are not its absence proof, and no OpenShell cleanup backend is available.
Do not populate launch rows in an actual installation while the backend is missing.

## Authoritative ACL dependency added, verified memory path still closed

`AclCacheService.GetVerifiedSnapshotAsync` fetches the authoritative ACL exactly once
per invocation with a five-second whole-operation deadline, no retry, no redirect
and no read from or write to the legacy populated cache. Missing/invalid payloads,
operator wildcard failure, timeout and transport/read errors reject the observation.
Disabled project ACL also rejects verified acquisition rather than inheriting the
legacy allow-all behavior. No grant or configured project access changes here.
The snapshot freezes the ACL and public/no-project policy and uses the same project
policy evaluator as legacy reads, retaining wildcard/case/filtering semantics.

`TriggerRefresh`, including calls from `PeerConfigHostedService` on `config.changed`,
synchronously advances the generation before scheduling the existing legacy refresh.
A generation change during acquisition rejects the result; the next invocation always
fetches authority, even if an event was lost. The authorization boundary is the final
locked generation check; an already authorized in-flight request may finish after it.
Legacy cache refresh and stale-on-failure behavior are intentionally not strengthened
or represented as verified authorization by this change.

This API is not an HTTP identity override or readiness flag. The receiver now
attaches its snapshot to HttpContext after certificate/proof/state/session checks,
and all six tools consume it instead of legacy attribution/cache on that path.
The listener defaults off and no actual listener, endpoint migration or deployment
was enabled by source work. See [the receiver protocol and configuration boundary](openshell-request-identity.md).
Actual TLS/DB/upstream/native integration and the full slice-1 blocker remain open;
unit certificate/proof tests do not establish daemon admission or runtime rollback.

## Latest receiver corrections, not completed slice 1

The verified port is configurable and the staged protected-agent set gates only
those legacy MCP callers, not every legacy agent. The absent-session proof claim
is the fixed SHA256-of-zero-bytes digest shared by the receiver and signer vector.
The real TestServer MapMcp initialize/session/memory_get test now checks allowed
content then revoked access using fresh request ACL while the old cache is warm.
Four real-MySQL reader cases use the actual migration for current, revoked,
wrong-epoch and missing-nonce mappings; all four passed prior-head MySQL CI above and remain locally UNRUN.
Configuration examples/setup and host-only custody restrictions move together;
none enables a listener, changes a grant or runs a production migration here.
The OpenShell lifecycle backend, compiler/current-supervisor readiness, outside hook/issuer/
ingress, production stop/absence reader and actual native callback/rollback path
remain missing, and separately approved exact-head runtime/restore evidence and
green CI are still required before merge.

## Shared lifecycle boundary in this revision

This is connected dispatch for existing production callers, not an implemented
OpenShell launch/activation path or a completion claim for slice 1.
`AgentRuntimeLifecycle` reads current DB ownership on every request, not a registry
heartbeat or a retained EF instance; missing DB/ownership is 503, missing agent 404,
and migrated/unknown or previously launched agents are 422 compatibility_blocked.
A retired launch still blocks fallback when desired metadata is reset to pending.
Version pinning checks admission before changing the instruction pointer.
Bulk operations reject a mixed backend snapshot rather than infer sandbox state
from Docker running names; no sandbox backend or migration writer is enabled.

Legacy implementation stays internal to the provisioning facade and adapter.
REST delete preserves agent/ACL/registry/recovery state on failed deprovision;
legacy inspect 404 is absence, while transport/status/parse errors remain unknown.
Deletion pins the observed immutable ID and checks absence after removal.
This is not OpenShell cleanup, drained-consumer/poller proof, or a serialized live
migration protocol; migration remains closed until that backend is implemented.

Raw infrastructure start/stop/restart requires fresh DB/driver ownership checks.
The optional host-owned comma-separated `FLEET_DOCKER_INFRASTRUCTURE_CONTAINER_IDS`
-> `Docker:InfrastructureContainerIds` list adds a restriction only when nonempty;
empty preserves ordinary infrastructure restart, including after recreate.
Current DB agent names/aliases plus all `openshell*` driver labels are rejected
before mutation, regardless of that optional restriction.
A configured list must contain the inspected full lowercase 64-hex immutable ID;
a name/short ID in that list does not grant authority.
The mutation uses the inspected full ID so a concurrent name replacement cannot
redirect it; operator authentication does not replace ownership checks.
Setup preserves any recorded list and never discovers/adds IDs automatically.
The allowlist does not authorize an agent/supervisor/gateway rollout or host change.

Tests exercise actual facades, all eight MCP lifecycle tool methods, in-process
REST start/stop/restart/delete, externally changed DB state, retired history,
infrastructure name/ID/driver-label guards and legacy read-error/404 distinctions.
Only isolated SQLite, TestServer, temporary generated files and owned HTTP handlers
are used; actual OpenShell daemon/CLI/shell/callback/rollback remains unimplemented.

## Pinned gateway observations and conditional mutation checkpoint

The unmodified conditional-mutation fork protobuf closure and Apache-2.0 license are
vendored under `src/Fleet.Orchestrator/OpenShell`; UPSTREAM records exact commit
`80d76a5c72d2a752d811775b62c86e974bc2bf19` and SHA256 for all four source files.
Build generation uses Grpc.Tools 2.67.0, with Grpc.Net.Client 2.67.0 for native
protobuf/gRPC and Google.Protobuf 3.26.1 matching the existing transitive pin.
No generated C# or runtime executable/image is committed or installed.

`OpenShellGatewayClient` implements authenticated named-workspace inventory and
exact-ID observations through that generated client, not a fabricated JSON route.
It exhausts all pages without a label selector and rejects repeated tokens,
duplicate names/IDs, foreign workspaces, malformed records, size/page bounds,
read failures and cancellation rather than returning partial absence evidence.
A whole-operation five-second bound covers inventory plus refreshed GetSandbox;
snapshots are immutable copies and a changed name-to-ID mapping is rejected.
The pinned empty-workspace/default normalization is applied explicitly.

The client is **not registered for runtime admission**; its outside
HTTPS handler/credential custody and actual gateway wire acceptance remain UNRUN.
A workspace-scoped authorized inventory is not a global launch-owned inventory,
consumer/poller-drain proof, current-supervisor readiness or cleanup authorization.
A Ready phase or nonempty supervisor instance ID cannot activate an agent.

The pinned fork [Stop/Delete contract](https://github.com/anurmatov/openshell/blob/80d76a5c72d2a752d811775b62c86e974bc2bf19/proto/openshell.proto#L1464-L1499)
adds optional expected immutable ID/version fields and the explicit
`sandbox-mutation-preconditions-v1` Health capability. The generated client now
implements conditional Stop/Delete, not a read/check followed by an unguarded call.
Each call requires a nonzero caller-owned request UUID and exact configured gateway,
workspace, ID, name and observed resource version. The caller must durably retain
that UUID and the complete payload before sending; this transport does not create
or persist operation intent, retry, fall back, or change a lease.

Health is checked for every call within the same whole-operation five-second
cancellation budget. Missing/unknown capability or a Health error sends zero
mutations. A separate Health request is not atomic downgrade protection: a mixed
or downgraded endpoint remains unqualified. Replaced response IDs/names/workspaces
and unknown deletion outcomes fail closed without retry. Even Accepted, Completed
or AlreadyAbsent is only an acknowledgement, never intake or external cleanup proof.
Ambiguous results require reconciliation of the original request, not a new UUID.

This is a bounded gateway transport checkpoint, **not a completed lifecycle backend**.
Shared lifecycle callers still reject migrated operations before any legacy fallback.
The production stop/inventory/intake evidence reader, compiler/startup activation,
outside identity hook/issuer/ingress/durable replay/session handling, actual agent
callback path and rollback remain **missing code**, not merely unrun tests.
No feature, lifecycle mutation route or workload admission is enabled by this pin.

Owned HTTP handlers exercise the actual generated gRPC request/response framing,
pagination, bearer metadata, mapping replacement, cancellation, immutable copies
and descriptor/source-hash contracts, plus actual generated guarded mutation RPCs,
capability refusal, cancellation and no-retry outcomes, without sockets, TLS or a real gateway.
Exact-head CI results belong in the PR evidence, not a moving prior-head status.
Approved actual daemon/native CLI/shell/memory/Rabbit/denial/revocation/rollback
and MySQL backup/restore-query acceptance remain required before merge.
