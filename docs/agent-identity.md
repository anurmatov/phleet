# Agent identity

Fleet MCP requires the existing signed `fa1.<purpose>.<subject>.<mac>` bearer on every selected
MCP request, including initialization, SSE and messages. There is no unsigned mode or enforcement
switch. All four servers refuse startup with `agent_identity_misconfigured` if the shared
`AgentIdentity:Key` is absent or invalid. Errors never include key material.

## Credentials and authority

`setup.sh` generates a missing durable `FLEET_AGENT_IDENTITY_KEY` once. Existing installations
must preserve their key: `upgrade.sh` validates it before changing files, pulling, building or
recreating anything, and never generates a replacement. All four services need the same key.
Comma-separated base64url keys of at least 32 decoded bytes support rotation: the first signs,
all verify. Rotation and caller reprovisioning require an operator-controlled maintenance window.

Provisioning writes only the agent's own `fa1.agent` bearer to its native MCP configuration.
The signing key cannot be attached as an Env Ref, mapped to a header or delivered to agents.
Server-side discovery and memory proxy calls use service-purpose tokens, never agent files.
A Docker socket or host access is outside this boundary.

Claude, Codex and Gemini have native header support (Gemini through the Antigravity CLI, see
[the transcript](evidence/antigravity-cli-1.5.14.md)). A provider that is not known to send headers
is refused with `mcp_headers_unsupported` before files are written or a working container is
removed. Invalid subjects receive `agent_identity_subject_invalid`.
Subjects are 1–128 ASCII letters, digits, underscores or hyphens. Preview reports the same fault.

Built-in names (`fleet-memory`, `fleet-orchestrator`, `fleet-telegram`, `fleet-temporal`) are
recognized case-insensitively. Explicit and implicit URLs must satisfy the existing trusted-origin
rule (scheme, host and port, no userinfo). An untrusted destination refuses provisioning with
`agent_identity_origin_untrusted`, rather than silently provisioning without identity.
The default origins are the three compose service addresses and configured `FleetMemory:McpUrl`;
`AgentIdentity:TrustedOrigins` replaces the three defaults for other deployments.
Third-party endpoint arguments are unchanged. Built-in URLs lose only the obsolete caller
`agent` query option; unrelated options and Telegram's generated `formatting_mode` are preserved.

## Request and grant checks

No server derives a caller from URL names, tool arguments or an earlier MCP session.
Missing/nonidentity credentials return `401 agent_identity_required`; malformed, forged and
unknown-purpose identity tokens return `401 agent_identity_invalid`. A signed subject wins over
any typed name. Legitimate author, target and search filters are not identity credentials.

Agent `tools/list` exposes only current grants, and `tools/call` checks the same grants before
invocation. A missing or unavailable grant source lists no tools and calls return
`tool_grants_unavailable`. Unknown/deleted agents are known-empty, not an outage.
The orchestrator reads its database per request. Other servers poll every 30 seconds, with a
5-second miss-refresh throttle and a maximum 10-minute successful snapshot age. Failures,
auth refusals and malformed responses never freshen a snapshot. Remote revocation is not immediate.

Exactly `/internal/agent-mcp-grants` and `/internal/agent-project-access` on the orchestrator
require a service-purpose bearer before any database read. Agent tokens get
`403 service_identity_required`; absent/forged tokens get 401; database outages get 503.
Neither bootstrap route consults the map it serves. Each consumer sends its own service identity.
Grant and project-ACL clients validate their configured absolute HTTP(S) base URL, reject
userinfo/query/fragment, use a 5-second bound and disable redirects and cookies. No unsigned retry
or alternate destination is used.

Project ACLs retain their separate existing policy. A cold ACL cache denies reads; a populated
snapshot survives failed refreshes **without an outage expiry**. Do not confuse this with the
10-minute grant-cache limit. Attribution and project access always use the signed agent subject.

Memory `/internal/*` remains service-only. A valid agent token receives
`403 service_identity_required`. The orchestrator's browser/operator-authorized proxy supplies
its service token. Existing anonymous `/health` on all four servers and the bridge's exact
`/workflow-failures` and `/pending-delegations` status routes are unchanged; similar prefixes
are not exclusions.

The orchestrator also accepts its existing verified operator bearer and signed-in browser MCP
path. Phone or config tokens alone are insufficient, and a failed bearer never falls back to
cookies. `PeerConfigClient` continues using its independent config token on `/api/config/values`.
`set_config_values` needs both its grant and the config token in `X-Fleet-Config-Token` alongside
identity Authorization. Journal-purpose tokens retain their separate endpoints and checks.
Reserved human decisions and existing tool/project grants do not change.

## Existing-install migration

1. First run signed-capable commit `2a338668893527017fbaf77781906e9a9bb141ab` (PR610), or a later
   pre-hardening descendant. Strict binaries are not the first migration step.
2. Preserve the shared durable key and captured compatible images/config. Remap config-token
   Authorization mappings to `X-Fleet-Config-Token` **before token-bearing reprovisioning**.
3. Keep an operator-local inventory of active, stopped and external callers, header capability,
   trusted destinations and verified signed subjects. Every eligible active/external caller needs
   a real signed native read receipt on that preceding release. Quiet logs alone prove nothing.
4. Migrate or explicitly stop every running unsupported-provider caller with operator approval.
   Preserve stopped callers as stopped and exclude them from start/reprovision until migrated and
   verified. Do not start an unsupported caller merely to collect a receipt.
5. Only with no unknown/unmigrated active caller, deploy strict binaries in an approved quiet
   window. A hand-managed deployment must wire the key to all four services and configure both
   authenticated bootstrap consumers itself. A bulk job containing unsupported callers fails;
   it must not silently skip them. No automatic provider changes or agent reprovision occur.
6. Verify signed native reads, tool/project denials and revocation, journal/config coexistence and
   captured rollback against the exact installed images. Source tests do not establish this.

A leftover false value cannot re-enable unsigned access. Upgrade removes the retired env line.
To undo, restore captured compatible images, configuration and key together in an approved window;
preserve stores, collections and stopped callers. An older unsigned-capable binary **restores that
exposure**. There is no enforcement-off switch in the strict binary.

## External and operator clients

An external agent client needs an operator-issued **agent-purpose** token for a registered subject
with its existing tool/project grants. Never give it a signing key or service-purpose token:

```bash
scripts/mint-identity-token.sh \
  agent agent1
```

For deliberate operator-controlled server-side MCP use on memory, Telegram or the bridge, the
existing utility remains available:

```bash
scripts/mint-identity-token.sh \
  service operator
```

This credential is not an agent and retains existing service tool/project-ACL semantics.
The script prints the token only; handle stdout as a secret. Do not distribute it to an agent.
`--check-key` checks the configured file without printing a token or key.

## Networks

Identity on the MCP servers is only as good as the absence of a way around them. Two services
take no credential at all, and used to share `fleet-net` with the agents:

| Service | What an agent on its network could do | Now on |
|---|---|---|
| Temporal (`temporal-server`, `temporal-ui`, `temporal-postgresql`) | Signal any gate, cancel or terminate any workflow, without the bridge and its checks | `fleet-temporal-net`, with the orchestrator and the Temporal bridge |
| Qdrant | Read and delete memory vectors without fleet-memory and its project check | `fleet-memory-net` (internal), with fleet-memory |

No agent joins either network: agents are provisioned onto `fleet-net` and whatever networks an
operator adds to them. The host ports (`7233`, `8080`) are unchanged, and loopback by default.

Not closed by this: RabbitMQ is on `fleet-net` because agents use it, with one shared account, so
an agent can still publish to another agent's queue. `fleet-bridge` (port 3300) takes no
credential and prompts the CTO agent by design, for callers outside the fleet. Both are separate
pieces of work.

### Moving an existing install

A stack whose compose file is generated from the example gets this from `./upgrade.sh`, which
writes the new file and recreates the services whose networks changed. A deployment with its own
compose file gets nothing from merging this and makes the same change by hand:

1. Add `fleet-temporal-net` (bridge) and `fleet-memory-net` (bridge, `internal: true`) to the
   top-level `networks:`.
2. Move `temporal-postgresql`, `temporal-server` and `temporal-ui` from `fleet-net` to
   `fleet-temporal-net`, and add `fleet-temporal-net` to the orchestrator and the Temporal bridge,
   which keep `fleet-net`.
3. Move `qdrant` to `fleet-memory-net`, and add `fleet-memory-net` to fleet-memory, which keeps
   `fleet-net`. Qdrant must not publish a host port: a container on an internal network cannot.
4. Recreate those seven services (`docker compose up -d --force-recreate --no-deps <services>`).
   Named volumes and host ports do not change, no data moves, and no agent is reprovisioned.
5. Check the orchestrator's, the bridge's and fleet-memory's `/health`, a memory search, and the
   dashboard's workflow list; from an agent's container, `temporal-server` and `qdrant` no longer
   resolve.

To roll back, put the seven services back on `fleet-net`, remove the networks they were given, and
recreate the same services. Volumes and host ports are untouched either way. Removing the two
networks afterwards (`docker network rm`) is optional once nothing is attached.

### What stops working from inside `fleet-net`

- **Temporal by its Docker name**: an agent browsing `http://temporal-ui:8080` through
  fleet-playwright, or a `temporal` CLI container on `fleet-net`. That is the point: the UI and the
  frontend can signal, cancel and terminate. Both are still on their host ports (`7233`, `8080`,
  loopback by default through `FLEET_INTERNAL_BIND_ADDRESS`). A tool that needs them runs on the
  host or joins `fleet-temporal-net`, an operator's decision and never an agent's.
- **Qdrant by its Docker name**: a debug container or a job that used its HTTP API (snapshots, for
  example) over `fleet-net` now joins `fleet-memory-net` or uses `docker exec qdrant`. A backup of
  the `qdrant_data` volume is unaffected.
- **Temporal's database**: a backup job that connected to `temporal-postgresql` over `fleet-net`
  joins `fleet-temporal-net` or uses `docker exec`.

Network isolation complements mandatory identity and does not replace it. The example's network graph is checked by
`ComposeNetworkTests`; whether a particular deployed stack has moved is for its operator to check,
as above.

## Verification boundary

The focused identity tests use production middleware and the real MCP SDK with synthetic keys.
`tests/secret-proxy/mcp-headers.sh` checks the pinned native Claude/Codex paths when its actual
runtime prerequisites are available; missing prerequisites exit 2, never pass. Neither source
fixtures nor a native header test alone establish a deployed fleet's acceptance.
