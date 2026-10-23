# Lifecycle operator authentication upgrade

This draft changes the orchestrator authentication boundary before any OpenShell
workload is admitted. Treat it as a compatibility change, not a runtime migration.

The existing operator host `ORCHESTRATOR_AUTH_TOKEN` maps to
`Orchestrator:AuthToken`. Configure a nonblank value before upgrading and ensure
the trusted dashboard, upgrade client and administrative REST clients send exactly
one `Authorization: Bearer <operator-token>` header. Never put real values in a
repository, issue, command transcript or workload. ConfigToken, query/body identity
and forwarded agent attribution do not authorize these routes.

| Request | New requirement |
|---|---|
| GET /api/agents and GET /api/agents/{name} | Operator bearer required, including disabled/stopped or nonexistent names |
| Agent create/delete/start/stop/restart/reprovision and bulk reprovision | Operator bearer for all verbs, including HEAD/OPTIONS, wrong-method and unknown-child requests |
| execution-policy, secret-bindings and openshell route shapes | Same guard even before their handlers exist |
| /api/services/restart | Same guard, no infrastructure restart bypass |
| Blank configured token | 503 openshell_operator_auth_unconfigured, backend calls zero |
| Missing/wrong/malformed/duplicate/comma/oversized header | 401 openshell_operator_unauthorized, backend calls zero |

Existing unrelated routes retain their own authorization. This is not an MCP grant
change, and a valid operator token is not approval to migrate or deploy workloads.
The operator token is startup-only, so separately approved rotation must coordinate
the orchestrator and its trusted administrative clients. Do not work around a 401
or 503 with direct Docker mutation or a different caller identity.

Before merge, verify the exact candidate image on an approved throwaway target:
dashboard list/detail/start/stop, upgrade paths and workflow cancellation, both
positive and negative authentication cases. Those runtime checks are still UNRUN.
Application rollback retains the additive schema and retirement records; do not
run destructive migration Down or restore a retired sandbox ID to get unstuck.
See [the implementation boundary](../openshell.md) and
[the operator runbook](../runbooks/openshell.md).

This revision also centralizes lifecycle callers and closes raw agent restart
bypasses: `FLEET_DOCKER_INFRASTRUCTURE_CONTAINER_IDS` maps to
`Docker:InfrastructureContainerIds` and defaults empty in setup/example/compose/
appsettings, so ordinary infrastructure restart uses DB/driver ownership checks
without an ID allowlist and survives recreate; existing recorded lists are preserved
as an explicit additional restriction that needs updating after recreate.
Agent aliases and OpenShell driver labels cannot be allowlisted around the guard.
Legacy never-launched pending agents retain their existing provider/model/auth
provisioning path; migrated/unknown or previously launched agents remain blocked
until the OpenShell backend and current-supervisor admission are implemented.
Delete failures now retain agent/ACL/registry state rather than report deletion
when the runtime's absence is unknown.
