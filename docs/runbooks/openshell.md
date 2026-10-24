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
