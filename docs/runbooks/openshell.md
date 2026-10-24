# OpenShell draft operator runbook

No OpenShell workload is admitted by this draft. Do not invoke production migration,
configure a real credential source or restart a runtime based on source tests.
Shared lifecycle dispatch is wired, but its OpenShell backend and the identity/compiler/native agent path remain incomplete.

For the authentication-only boundary, use a trusted outside-workload client with
the operator host `ORCHESTRATOR_AUTH_TOKEN`. The existing mapping is
`Orchestrator:AuthToken`; do not use `ORCHESTRATOR_CONFIG_TOKEN` or agent identity.
GET agent list/detail now require the token, as do all lifecycle/policy route shapes
and methods. Blank configuration means 503, never an unauthenticated development
mode. Missing, invalid or duplicate authorization means 401 before backend access.

For an independently approved candidate-image check, supply the token through the
trusted client's protected configuration without printing it. Check list/detail
reads, start/stop, bulk/upgrade, wrong-method/unknown-child handling and workflow
cancellation. A successful response from a different image is not qualification.
Retain exact head/image/request class/status and backend-call evidence, without
credentials, request bodies, private messages or token-bearing URLs.

The source store requires current transaction reloads, phase order, lease ownership
and an authenticated runtime evidence reader. The production reader is not yet
implemented: synthetic reader tests are not proof that a runtime is gone.
Retirement alone keeps cleanup pending; only verified absence/drained intake may
release a slot, and the next launch must use a fresh sandbox ID and identity epoch.
Without a bound sandbox ID, the reader must exhaust the authoritative launch/epoch
ownership inventory and prove zero owned sandboxes before releasing the slot; a
missing mapping or incomplete inventory is not absence evidence.
Unknown state remains blocked, not success or permission for direct Docker cleanup.

The database contains tracked-write immutability guards, not a DB-admin boundary.
Bulk/raw SQL and destructive Down can bypass them and must not be used as an
operational lifecycle substitute. Preserve tombstones across agent deletion and
application rollback, with separately approved backup/restore/query evidence.

See [the upgrade notes](../upgrading/openshell-operator-auth.md) for the route/status
matrix and [current source/CI limits](../openshell.md) before any execution decision.

Infrastructure restart always checks DB/driver ownership and mutates the inspected
full immutable ID, not a replaceable name; the empty default optional
`FLEET_DOCKER_INFRASTRUCTURE_CONTAINER_IDS` does not break infrastructure restarts.
A nonempty host-recorded list is an explicit additional restriction and must be
updated after recreate; setup/upgrade never discovers or populates it automatically.
Agent names, aliases and OpenShell-owned labels remain denied even if an ID was
mistakenly listed; read/ownership failures are not successful restarts.
Migrated/unknown metadata or any launch history blocks all legacy lifecycle entry
points, including MCP, version pinning, provisioning facades and deletion.
Do not clear launch history/reset metadata to work around compatibility_blocked.

## Pending retirement reconciliation (source-only, admission still off)

`POST /api/agents/{name}/openshell/reconcile` uses the same outside operator token
and requires `expectedLaunchId` and `expectedRequestId` from the persisted pending
operation. It accepts `replay` (default `false`); ordinary Stop/Deprovision never
replays a pending operation automatically. This route is not runtime authorization.

Wait for the original bounded operation lease to expire; a live lease or another
owner returns 409 without mutation. The route transaction reloads authoritative
ownership, revocation, tombstone and the complete recorded intent before renewing
only that original operation's lease. Missing trusted peers returns 503 before
storage access. Missing/empty IDs returns 400 after operator authentication.

With `replay=false`, only fresh exact stop/absence and intake-drain proof can finish
the original pending transition; no gateway mutation is sent. With `replay=true`,
only the identical pinned conditional RPC is sent, retaining its name, workspace,
sandbox ID, observed version, request ID and Delete allow-missing semantics. There
is no fresh name lookup, new UUID, unguarded fallback or automatic retry. Lost
responses, capability downgrade/refusal or failed proof preserve recovery state;
409/503 includes the expected request ID, never a 2xx failure. A renewed lease
must expire before another explicit attempt. Gateway replay retention/version
refusal is not authority to rewrite the recorded intent or select a replacement.

A confirmed Stop keeps config and the active slot, while confirmed cleanup releases
the slot only after absence and drain verification. Reconciliation does not create
or resume a sandbox, activate intake, complete migration or implement rollback.

## Agent deletion (source-only)

`DELETE /api/agents/{name}` retains the existing outside-operator authentication.
It first requires shared deprovision success, then rechecks all retained launches
under a fresh serializable transaction before deleting configuration. Active or
pending launches, any lifecycle lease, missing tombstones, inconsistent sessions,
incomplete launch inventory or stale/failed absence/drain proof block deletion.
Failure before commit retains configuration and recovery rows, not partial cleanup.

A successful response removes agent-owned references and assignment-derived ACL
rows only. Shared credential records/files, workspace bytes, manual/wildcard ACL
rows and retired-ID tombstones are retained. Registry removal follows commit.
`503 agent deleted; ACL notification unconfirmed` means database cleanup committed
but the bounded notification failed; inspect authoritative state instead of replaying
a delete or interpreting the response as rollback. The whole deprovision/cleanup
request budget is 120 seconds, with no new runtime or rollout authority.

Deletion proof acquisition has at most eight concurrent reads and a shared
five-second cohort deadline, including queued history. It does not launch one task
per history row or share the transaction context with concurrent readers. Every
proof must still be fresh before save/commit; cohort timeout, cancellation, stale
or inconsistent proof preserves configuration rather than weakening the gate.
The reader implementation must support concurrent immutable-target requests with
independent I/O/context ownership and cancellation; the production reader remains
unimplemented and unregistered, not qualified by synthetic cohort tests.

## Outside identity source checkpoint

No installer or default image starts `Fleet.Identity`; `Identity:Enabled` remains
false. Enabling the standalone service is **not authorized by these source checks**.
Missing custody values/files, invalid keys/certificates or unavailable database
schema refuse enabled startup. No keys, CA, endpoints or launch grants are generated.
The deployment must separately isolate ingress/hook from workload direct receiver
access and configure the receiver's existing protected-agent set; source defaults
are not network enforcement or permission to migrate an agent.

Required startup-only `Identity` configuration (source shape, not new live env keys):

| Keys | Custody contract |
|---|---|
| Enabled, HookPort, IngressPort | Off by default; distinct TLS listeners, defaults 17441/17443 |
| UpstreamTimeoutSeconds | Startup-only admitted execution/header and finite-body timeout, default 120 seconds, valid 1–600; independent of two-second admission |
| ServerCertificatePath, ServerKeyPath | Operator-owned TLS server certificate/private key |
| GatewayId, GatewayIssuer, GatewayAudience, GatewayJwksPath | Exact immutable gateway and pretrusted public Ed25519 JWKS; no network JWKS discovery/TOFU |
| Issuer, KeyId, AssertionPrivateKeyPath | Outside-only P-256 private signing custody matching existing receiver public verification |
| ConnectionString | Authoritative existing launch/session/nonce tables; enabled startup checks schema reachability |
| IngressHost | Exact admitted HTTPS host/port, only `/mcp` without query |
| MemoryReceiver, ReceiverCaPath | One immutable HTTPS `/mcp` receiver URL and pinned public server CA; DNS name still verified |
| ClientCertificatePath, ClientKeyPath | Outside-only mTLS certificate/private key accepted by the receiver |

Only static `service=memory` hook configuration is valid. Use the pinned generated
manifest, protocol 1.0, `openshell.supervisor-middleware.contract`, pre_credentials,
256KiB and a two-second fail-closed hook timeout. No caller `agent` parameter,
identity header, Authorization override, arbitrary upstream or query is admitted.
The issuer accepts only activated/committed mappings, never a bootstrap grant.
`/identity/ready` remains an explicitly unresolved later-package boundary.

Local source commands, with no gateway, socket, host or real credentials:

```sh
dotnet build src/Fleet.Identity
dotnet test tests/Fleet.Identity.Tests
dotnet test tests/Fleet.Memory.Tests
```

Run `dotnet test tests/Fleet.Orchestrator.Tests --filter 'FullyQualifiedName!~Migrations'`
for the preserved lifecycle/store regressions. Counts belong to exact-head receipts;
prior-head CI does not qualify a successor. Tests use owned keys, TestServer and
SQLite, with an explicit certificate/raw-target transport seam and an owned receiver
SQL seam. They do not prove real TLS, pinned gateway compatibility or MySQL isolation.
Native daemon/CLI/shell/Rabbit, MySQL restore and production rollout remain UNRUN.

Troubleshooting uses fixed refusal codes only: `gateway_token` means pinned trust or
caller kind/context failed; `launch_inactive`/`launch_changed` mean authoritative
mapping refused; `session_foreign` means missing/expired/mismatched durable ownership;
`identity_override`/`request_shape` mean forbidden protocol/headers/route.
An authenticated current identity with an exactly owned expired session gets 404
`session_expired`; a receiver binding lost on restart gets 404 only after fresh
durable ownership is proven. Neither invokes a tool; reinitialize without the old
session ID. Unknown/foreign sessions, invalid assertions or revoked mappings retain
401/403 denials and do not become a session-recovery fallback. Generic
verification/dependency refusal is fail-closed, not absence or fallback permission.
Do not log payloads, queries, tokens, assertions, certificates or connection strings.
Nonce uniqueness or uncertain commit failures must not resend the same operation;
request admission can remain denied even if the outside nonce was committed.
A receiver restart invalidates its local sessions; initialize a fresh session rather
than rebinding a retained/foreign session. The two-second authorization/body/nonce
budget ends before dispatch; admitted tools use the independent configured upstream
bound. A 504 `upstream_unconfirmed` may mean a write already committed: never
blindly replay it, and no proxy retry occurs. SSE continues after response headers
until caller cancellation; finite bodies remain bounded. Session persistence uses
its own fresh two-second bound with authoritative ownership checks.
Known nonce replays return 403 `replay`; ambiguous DB errors remain 503 and cannot
be retried as if absent. Strict gateway clock-skew rejection is unchanged and its
qualification decision remains held before runtime. Source rollback is a revert while admission
is off, not authority for a runtime rollback, DB cleanup, key rotation or feature enablement.

## Unqualified cold preparation

The preparation authority defaults to deny; no operator selection route or installer is available.
`preparation_scope_conflict` (409) rejects wrong immutable identity/hash/intent/deadline or overrides.
`compatibility_blocked: preparation peers unqualified` (503) means no reserve/stop/create occurred.
`preparation_verified_activation_held` (409) is a preparation receipt followed by verified retirement,
not a running agent. `preparation_unconfirmed` retains the launch ID for authoritative inspection.
Never repair this by deleting its slot/tombstone, replaying Create, guessing an unbound ID from labels,
starting a retained sandbox, enabling grants, or restarting the old runtime.
The preparation-specific reconcile/operator workflow, concrete evidence/bootstrap peers and live
acceptance are not implemented; preserve pending state for the separately scoped recovery work.
