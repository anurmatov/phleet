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

Infrastructure restart now requires host-recorded full immutable IDs in
`FLEET_DOCKER_INFRASTRUCTURE_CONTAINER_IDS`; the empty default denies all such
mutations and is not populated automatically by setup or upgrade.
Agent names, aliases and OpenShell-owned labels remain denied even if an ID was
mistakenly listed; read/ownership failures are not successful restarts.
Migrated/unknown metadata or any launch history blocks all legacy lifecycle entry
points, including MCP, version pinning, provisioning facades and deletion.
Do not clear launch history/reset metadata to work around compatibility_blocked.
