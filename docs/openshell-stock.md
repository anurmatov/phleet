# Stock OpenShell whole-agent enrollment

## Status: manual opt-in, deployment-specific qualification

This bounded, experimental profile connects one manually enrolled, non-root Codex
whole-agent workload to the existing daemon/native CLI/tool path, verified memory
and normal broker control/completion. It is not a command-only sandbox or automatic
provisioning. Each deployment must qualify its exact images, installed native CLI,
gateway/memory TLS, native AMQP, whole-agent turn, revocation and cleanup on an
explicitly authorized target. Source fixtures alone do not establish those results.

The stock source pin is NVIDIA/OpenShell v0.1.5,
`bdb956f2cd65a635f1294fccf1d138d263bf650f`. The middleware proto import closure is
unmodified and hash-listed in `src/Fleet.StockIdentity/Proto/UPSTREAM`; generated
C# is build output. No fork RPC, lifecycle framework, ownership database, migration,
replacement or automatic recovery is introduced.

## Implemented source paths

| Path | Scope and limits |
|---|---|
| `entrypoint.sh` → validation-only Fleet.Agent → core registration gate | Parsed opt-in enforcement refuses invalid enrollment before intake; the supported profile validates the one outside record, exact stock/policy/agent identity, non-root UID, memory route, shared native settings artifact and broker binding |
| Stock `SupervisorMiddleware` → `Fleet.StockIdentity` | TLS plus pinned Ed25519 gateway JWT authenticates the exact supervisor/sandbox; outside operator enrollment supplies agent/epoch; the outside P-256 signer overwrites the assertion header |
| Dedicated Fleet.Memory receiver → existing `MapMcp("/mcp")` → existing memory tools/ACL | Verifies signature, current enrollment, method/body/session binding, expiry and nonce; injects verified attribution only after rejecting caller query identity |
| Receiving memory session | Receiver-local owner/epoch session map captures actual initialize responses; foreign/replayed assertions refuse; missing/terminated sessions return authenticated 404; clients may initialize anew |
| Native AMQP source | Existing BridgeRelayService → GroupRelayService and RelayCompletionPublisher → BridgeRelayService bind broker-validated user IDs, exact exchange/route/agent/correlation and fresh outside enrollment; heartbeat publishers/receiver use the same epoch vhost |
| Live memory admission | Reloads the operator file and public key each request, fetches fresh ACLs, and refuses failed ACL fetches instead of admitting on the legacy stale cache |

The only accepted tool calls here are `memory_get`, `memory_search` and
`memory_list`; writes and unrelated protocols refuse. Existing ACLs decide project
access. This fixed route restriction does not change or create tool grants: the receiver
also reads the existing orchestrator agent-config endpoint on each request and
requires the enabled agent, exact memory endpoint and enabled tool-name grant.
The old validator was reused individually for stock gateway JWT verification,
not its old launch database, ingress proxy or lifecycle architecture.

The dedicated memory mode rejects legacy/internal paths, query attribution and
plaintext requests. The ordinary receiver's behavior remains unchanged when the
startup-only `StockOpenShell:AuthorityFile` setting is absent. Never expose the
separate legacy receiver to the workload: its network exclusion is a required
operator policy condition, not something these unit tests prove.

## Operator-owned authority and trust inputs

`Fleet.StockIdentity` and the dedicated memory receiver consume the same outside
operator-owned authority file via `StockOpenShell:AuthorityFile`. The receiver
additionally requires existing `Acl:EnableProjectScopedAcl=true`. These are
startup-only source seams, not automatic enrollment/setup/deployment switches.
Do not mount the private signing key, gateway private keys, or writable authority
state into the workload. Mount the shared authority directory read-only in its
outside consumers so an atomic operator-file replacement remains visible.

`Describe` and `ValidateConfig` authenticate TLS and the pinned issuer/audience/JWKS
for both stock callers: gateway-kind has issuer-as-subject and no sandbox claim;
supervisor-kind has a nonempty signed sandbox claim and the exact subject
`spiffe://openshell/sandbox/<sandbox-id>`, also matching the outside sandbox ID
when one is already configured. The [pinned issuer](https://github.com/NVIDIA/OpenShell/blob/bdb956f2cd65a635f1294fccf1d138d263bf650f/crates/openshell-server/src/auth/sandbox_jwt.rs#L422-L448)
constructs these distinct identities; the supervisor negotiates through the same
remote middleware connector as the gateway. These methods work before active
enrollment and validate protocol/configuration only, never sign or grant data access.
`EvaluateHttpRequest`, assertion signing and every receiver retain active,
nonexpired exact workload enrollment checks; a bootstrap response cannot bypass them.

The authority schema uses the exact property names below; all example values are
synthetic and intentionally expired, and no key material is generated by setup:

```json
{
  "State": "active",
  "Agent": "agent1",
  "SandboxId": "synthetic-sandbox",
  "Epoch": "00000000-0000-0000-0000-000000000001",
  "ExpiresAt": "2026-01-01T00:00:00Z",
  "GatewayIssuer": "synthetic-gateway",
  "GatewayAudience": "synthetic-hook",
  "GatewayJwksPath": "/trust/gateway-public.jwks",
  "AssertionPrivateKeyPath": "/trust/outside-private.pem",
  "AssertionPublicKeyPath": "/trust/outside-public.pem",
  "MemoryHost": "memory.example.test",
  "BrokerHost": "broker.example.test",
  "StockCommit": "bdb956f2cd65a635f1294fccf1d138d263bf650f",
  "PolicySha256": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
}
```

The outside signer has a separate `src/Fleet.StockIdentity/Dockerfile` using the
standard non-root ASP.NET runtime user; it is never copied into the workload image.
CI builds the default and opt-in `stock-agent` images and runs networkless identity,
workspace and startup-file checks. It does not build the separate `Fleet.StockIdentity`
image or enroll a runtime; build and qualify that image separately.
TLS serving/trust uses normal ASP.NET Core Kestrel configuration and the caller's
existing trust configuration. The hook and receiving boundary reject plaintext;
this source work has not installed certificates or qualified an actual TLS path.
The stock role policy must fail closed on hook failure and limit exact destinations,
executables and filesystem access. Use the pinned upstream
[policy schema](https://github.com/NVIDIA/OpenShell/blob/bdb956f2cd65a635f1294fccf1d138d263bf650f/docs/how-it-works/policies/schema.mdx),
not a fork policy or guessed installed paths.

## Manual workload profile and prerequisites

This first profile supports the existing Codex cloud executor only, without hosted/
local overrides or harness extras. Use the optional `stock-agent` Dockerfile target;
the default target still inherits the legacy root entrypoint unchanged. The stock
target runs UID/GID 10001, with `/home/agent` and `/workspace` owned by that account.
Its OCI working directory is `/home/agent`, which the stock runtime can validate
as writable without granting write access to `/app`. The opt-in build guard
checks effective-identity workspace writes and application readability/protection.
Only this target sets all three startup scripts (`entrypoint.sh`,
`stock-entrypoint.sh`, `gh-auth.sh`) to `0755`; its UID/GID 10001 build guard
checks read/execute access and Bash parsing for each, even when the build context
has owner-only script read permissions. CI builds that restrictive context and
repeats the checks without network, credentials or product startup.
The stock target also normalizes only the copied public skills directories to
`0755`, their files to `0644`, `/opt/fleet` to `0755` and its public
`acceptance_plan.py` helper to `0644`. Build and networkless CI guards read the
entire skills tree and run the helper's `--help` as UID/GID 10001; CI supplies
skills directories at `0700` and those public files at `0600` before building.
No credential, runtime mount, auth/ACL or confinement policy is changed.
Initial enrollment validation and the final stock daemon both explicitly select
`/app` as the configuration content root, independent of the working directory;
unrelated cwd settings cannot silently disable enrollment. The default image
retains its original `/app` working directory and startup path.
A real stock launch must honor that UID and the approved role policy; the CI image
probes do not start an enrolled runtime. Do not mount a Docker socket, host shell,
private signing keys or unrelated global credentials into the workload; do not pass
through the legacy stack environment wholesale.

The workload's manually prepared `/app/appsettings.json` uses existing agent/broker
settings plus these opt-in inputs (synthetic paths, not an activation command):

```json
{
  "StockOpenShell": {
    "Enabled": true,
    "EnrollmentFile": "/trust/enrollment.json",
    "AuthorityFile": "/trust/enrollment.json",
    "BrokerPasswordFile": "/trust/workload-broker-password"
  },
  "Agent": {
    "Name": "agent1", "ShortName": "agent1", "Role": "synthetic",
    "Provider": "codex", "Model": "sol", "WorkDir": "/workspace",
    "AllowedTools": ["mcp__memory__memory_get"]
  },
  "RabbitMq": { "Host": "broker.example.test" }
}
```

`EnrollmentFile` is a compatibility alias of `AuthorityFile`, not a second identity
record. Mount the public enrollment directory read-only, with no outside private
keys in that directory; the managed workload gate also refuses a visible assertion
private-key path. Only outside consumers receive the private signer. A minimal legacy
intent record cannot self-attest a supported enrollment and continues to refuse.

Provide the existing provider credential reference at
`/home/agent/.codex-host/auth.json`, the exact normal `HarnessDefaults`-generated artifact
at `/app/codex-root.toml`, normal generated read-only instruction/project inputs beside
the binary (`roles/.../system.md`, `projects/.../context.md`), and one read-only `/workspace/.mcp.json`.
A managed staged launch instead uploads the artifact and instruction/project inputs under
`StockOpenShell:AppRoot` (`$HOME/app`), which must equal the launcher's content root
(`stock_enrollment_app_root_mismatch` otherwise); preparation and daemon registration both validate
that same `codex-root.toml`. Without `AppRoot` (the manual profile) the paths above are unchanged:

```json
{"mcpServers":{"memory":{"url":"https://memory.example.test/mcp"}}}
```

The configured URL must exactly match enrollment `MemoryHost`, with no headers,
query attribution or extra MCP server. Existing orchestrator endpoint/tool grants
must independently match the label `memory`; the receiver still fetches them fresh.
Startup checks the root artifact against the shared plan, not another set of mode
defaults. The non-root entrypoint copies the existing credential into its own home,
writes the memory-only native MCP table and executes the existing Fleet.Agent daemon;
it does not run legacy root/cron, file-sharing or host-auth setup. Direct managed starts
also require the exact prepared native MCP configuration and matching credential cache
before registration; an alternate `CODEX_HOME` refuses rather than bypassing that check.

Telegram, journal/files, conversation-client routes and other providers refuse in
this bounded manual profile; they are not silently granted or runtime-qualified (the
managed runtime below routes only the agent's own bot and journal capture). Agent HTTP
control routes return 404 in this profile, leaving only health. Manual enrollment
changes no ordinary provisioning/setup/Compose behavior and creates no runtime,
credential, account, key, grant or lifecycle action.

Stock supports native TCP destination/executable rules, but no request inspection
or credential rewriting on that protocol, as documented in
[Native TCP](https://github.com/NVIDIA/OpenShell/blob/bdb956f2cd65a635f1294fccf1d138d263bf650f/docs/how-it-works/policies/network-rules.mdx#allow-native-tcp).
This is not evidence that AMQP is impossible or permission to replace the normal
transport. These stock primitives still need exact-build runtime qualification;
this source path does not depend on an upstream fork.

## Native broker boundary: source, not activation

The outside stock Bridge uses static `Bridge`/`RabbitMq` configuration and does
not register the ordinary peer-config subscription. Nonempty `PEER_CONFIG_KEYS`
or `PEER_AGENT_DERIVED_KEYS` refuses before service construction/connections,
including whitespace-only values. Its control connection still uses the same
epoch authority, principal and revocation checks; no legacy broker connection
or additional principal is introduced. Ordinary Bridge peer subscriptions remain
unchanged when both stock authority/password settings are absent.

`StockOpenShell:AuthorityFile` and `StockOpenShell:BrokerPasswordFile` are startup-only
inputs for the outside Bridge and separate stock heartbeat observer; the workload relay/heartbeat
receive the same validated binding through core registration after the enrollment gate. Password files reference pre-existing credentials: no user,
permission, certificate or grant is created by this source. No setup/Compose activation
or new live-mutable configuration key is exposed.

`BrokerHost` must match the workload and outside Bridge `RabbitMq:Host` exactly.
The observer selects its host from the enrolled `BrokerHost` independently of the
ordinary orchestrator `RabbitMq:Host`, preserving the exact-host identity check.
The epoch derives the isolated vhost `stock-<epoch-N>` and three distinct principal names ending in `-workload`,
`-control` and `-observer`, without additional identity knobs. All three roles use
plain AMQP on private port `5672`, with AMQP TLS explicitly disabled and no stock
connection retry or automatic recovery. For example, the endpoint is
`amqp://broker.example.test:5672/stock-<epoch-N>`; passwords still come only from
the protected role-specific file, never the URL or logs.
Every publication/delivery reloads active enrollment and refuses changed epoch,
sandbox, agent, policy/source pin or broker; revoked delivery cannot complete a pending request or update
registry state. Already-admitted native work is not represented as canceled.

This profile requires an isolated operator-trusted private network, with the broker
unreachable from public networks and unrelated workloads. Credentials and broker
traffic are unencrypted: epoch authentication and validated user-id do not protect
against a hostile peer, packet capture or network MITM. The stock role policy must
allow only the exact broker host/IP, port `5672` and approved native executable;
do not widen egress or expose a broker host port to make it connect. Memory HTTPS,
gateway/supervisor TLS and assertion verification remain required and unchanged.
This is a fixed stock-only profile, not a global transport option or TLS fallback;
ordinary non-stock connections keep their existing behavior.
The workload, outside Bridge and observer need exact-build operator qualification
with this transport; previously qualified TLS-era images are not evidence for it.

The orchestrator always keeps its ordinary default-vhost heartbeat, registration
and access-request consumer, including its existing recovery behavior and health
status. Stock telemetry uses a distinct hosted observer and connection; enrollment,
connection, delivery and cleanup failures stay inside that observer, log a fixed
manual-reconciliation warning, and never select the ordinary broker as fallback.
Ordinary telemetry/access requests cannot impersonate the known enrolled agent.
The mutation hold snapshots the public identity without requiring an active record,
so revocation does not break ordinary startup or reopen legacy mutation paths.
If stock configuration is present but its public identity cannot be read unambiguously,
all runtime mutations remain held until manual reconciliation; ordinary telemetry
still flows. This conservative hold is not broker authentication or authority to
admit a stock delivery, which still requires the full fresh active binding.

The external broker MUST enforce [validated user-id](https://www.rabbitmq.com/docs/validated-user-id),
with distinct nonshared passwords, no impersonator users or trusted federation/shovel
publisher into this vhost, and narrowly scoped configure/write/read permissions.
No other broker or legacy receiver may be reachable under the stock role policy.
Stock heartbeats carry no container name or HTTP endpoint; the receiver refuses
those workload-supplied selectors. The outside enrollment selects the control
identity, never heartbeat metadata. Legacy local Docker lifecycle, cancellation
and log routes return 409 for that enrolled agent, even after revocation, rather
than falling back to a stale database container or same-named host runtime.
The same immutable enrollment hold applies before shared provision/deprovision/
reprovision and MCP start/stop/restart/version-pin actions, including after
revocation. Bulk reprovision returns a held result for the enrolled agent before
any runtime inspection of it, while other agents retain their normal behavior.
The generic raw service-restart endpoint refuses while stock enrollment is
configured because raw container names do not establish stock ownership.
Named aliases (`restart-with-version`, `cancel_bg`) are also held before dispatch.
Other agents and default-off routing are unchanged; stock lifecycle remains manual.
These are mandatory operator/E2E prerequisites, not properties established by a fake
channel, an enrollment file or a client-selected `UserId`. The source rejects missing
or foreign user-id and denies access-request, broadcast token, peer relay, workflow
signal and uncorrelated status routes instead of falling back to legacy attribution.

Focused owned tests connect the actual Bridge request, workload relay, normal
completion publisher and matching Bridge pending request, plus real heartbeat
publisher/receiver methods. The test includes actual GroupBehavior and TaskManager
intake/completion; only broker channels and inference are synthetic; no AMQP server, native CLI or whole-agent E2E was executed. Separate native
startup fixtures stub UID/dotnet only and exercise real Node/file preparation; managed
profile tests supply UID metadata rather than claiming a non-root deployed process.

## Revocation, failure and rollback

Setting the outside authority state to `revoked` denies subsequent hook and memory
admissions, including assertions issued before revocation. Already-admitted work
is not represented as canceled. Sessions/nonces are receiver-local; restart requires
fresh initialization, not automatic replay. Assertion admission is bounded separately
from MCP execution; requests are not automatically retried.

Ambiguous launch, shutdown or revocation stays held for operator reconciliation.
No Create/Stop/Delete, replacement, takeover or inferred absence is issued. Revoke
and independently verify workload quiescence before resuming legacy intake; preserve
workspaces, journals and credential references. No destructive cleanup is provided.

Operator cleanup evidence must establish that both the mounted test copy at
`/home/agent/.codex-host/auth.json` and the native-home copy written by
`stock-entrypoint.sh` at `/home/agent/.codex/auth.json` are absent, with the exact
owned workload stopped. Removing only the mounted copy is not complete cleanup.
Prove ownership before removing only those temporary test copies, preserve the
approved donor/reference, and never read, hash, archive or export credential contents.
This source does not automate that removal or attest that it occurred.

Reverting the stock broker transport requires the matching TLS broker fixture and
exact application images; there is no silent plaintext/TLS retry or runtime switch.
Both forward qualification and rollback still require independent operator evidence.

## Acceptance

Focused tests cover padded opt-in refusal through core and the actual validation-only
entrypoint, authenticated hook signing → receiver → actual MapMcp/MemoryGetTool with
owned file data, changed/foreign project ACLs, revocation, identity/body/session/expiry
binding, spoof/direct bypass, session reinitialization and replay. Other tests use a
synthetic receiver continuation to observe counts. TestServer is not a real gateway,
TLS connection, installed native CLI, broker or whole-agent E2E.

Record deployment-specific exact-build whole-agent/native-tool/memory/completion,
revocation, non-destructive rollback and applicable storage/restore results separately.
Unrun or blocked checks must stay explicit; a narrowly accepted smoke does not prove
full native acceptance, all providers or a broader rollout. Source changes still
require full independent final-head review/public scrub, CI and human merge approval.
Keep private deployment evidence out of this public guide.

## Canonical execution failure is not enrollment refusal

An observed `/app/entrypoint.sh` exit 126 with `Permission denied` precedes the
enrollment validator and must not authorize binding, credential transfer or Start.
Installed metadata identified a concrete defect: a root-owned `0711` startup
script is executable but unreadable by UID 10001, and `chmod +x` does not add the
missing read bits. The stock-only explicit modes correct that source defect;
they do not establish that the real launch now succeeds or that this was its
only cause. Preserve the original failed attempts and exact image, policy and
effective file/interpreter/mount evidence. Do not assume Landlock from the exit
code, add writable `/app`, switch to root, disable admission or substitute a shell
command. Fresh exact-head review and a separately approved actual-path launch
and whole-agent qualification remain required; source checks are not that pass.

## Maintaining the upstream contract

The integration maintainer owns upstream pin and middleware-schema updates through
reviewed source changes. Never update only the version label or assume a new
runtime accepts the existing middleware contract.

1. Select one exact upstream source commit and matching runtime artifacts, then
   review its policy, lifecycle, gateway-token and middleware compatibility.
2. Refresh the unmodified middleware import closure under
   `src/Fleet.StockIdentity/Proto/`, including its license, and verify the file
   SHA256 values against the selected upstream source before updating `UPSTREAM`.
   Include any newly required imports; do not edit upstream protocol definitions.
3. Keep `StockOpenShellEnrollment.StockCommit`, the documented baseline and the
   external enrollment's source/policy binding consistent in the reviewed change.
   Generated C# remains build output, not another maintained protocol copy.
4. Build the solution and both opt-in artifacts, run the focused Agent, Memory,
   Orchestrator and startup tests, and repeat exact-build runtime qualification on
   an authorized isolated target before changing a deployment.
5. Preserve the previous runtime/application images, policy, authority references
   and workspaces for an explicitly approved rollback. Never rewrite a live
   enrollment or perform an upstream upgrade as a documentation-only action.

The regular agent CI builds `stock-agent`; the outside identity image has a
separate build recipe and still needs its own build and runtime evidence:

```sh
docker build \
  --target stock-agent \
  -t stock-agent:review .
identity_dockerfile=src/\
Fleet.StockIdentity/Dockerfile
docker build \
  -f "$identity_dockerfile" \
  -t stock-identity:review .
```

These commands build artifacts only. They do not create broker users, install
trust, enroll an agent, or authorize runtime activation.

## Saved managed policies (no activation)

The dashboard's **Sandbox policies** page creates a named policy with immutable version 1,
then appends versions. Select an exact version in **Model & runtime**, select API-key auth and
an existing environment reference name, then **Save**. API-key billing is separate from subscription
OAuth. Never paste credentials into YAML, profile hashes or reference fields.

This release stores desired selection only. Applied runtime/binding is read-only and remains
unchanged on save or heartbeat. Existing Docker and operator-managed stock profiles and service
grants are unchanged. Managed apply/reprovision is unavailable (`managed_runtime_unavailable`,
HTTP 409 on apply), without lifecycle actions or fallback. Start/Stop/cancel continue targeting
applied Docker while a new selection is pending. An applied non-Docker binding is held rather
than routed to a same-named Docker container. No managed binding can be created in this slice.

All `/api/sandbox-policies` reads and writes require `Orchestrator:AuthToken`, including in dev
mode. Create with `POST /api/sandbox-policies` (`name`, `yaml`, optional `providerProfileHashes`
object mapping profile names to lowercase SHA256 content hashes). List with GET; append using
`POST /api/sandbox-policies/{id}/versions` (`version`, `yaml`, `providerProfileHashes`). GET the
versions list or `/versions/{version}`. Version IDs, not “latest”, are stored on agents.
Supported YAML is a bounded subset of the pinned [stock authored schema](https://github.com/NVIDIA/OpenShell/blob/bdb956f2cd65a635f1294fccf1d138d263bf650f/crates/openshell-policy-schema/src/lib.rs):
`version: 1`, `filesystem_policy` (absolute read-only/read-write paths and include_workdir),
`process` (named user/group), `landlock`, and `network_policies` (binary paths, host/port,
protocol, automatic TLS, enforcement/access presets and credential_binding).
Advanced rules/middleware, aliases, anchors, tags and unknown fields return 400, never silently
lose fields. Each explicit credential binding requires its pinned profile content hash. These
operator-supplied hashes are persisted immutably; this save-only release does not contact a gateway
or establish that the installed profiles match. Future apply must independently verify them.

400 means invalid/unsupported input; 404 means missing policy/version; 409 means name/version
conflict or unavailable apply; 503 means database/auth configuration unavailable, not an empty
list. Retry a save only after reading the stored versions if its outcome is uncertain.

The additive migration retains old agents/grants with Docker defaults and null selections.
For rollback keep the additive schema and all saved versions; do not delete referenced versions.
A database restore must retain policy bytes/hashes, exact selections and applied bindings.
Run `dotnet test tests/Fleet.Orchestrator.Tests --filter FullyQualifiedName~ManagedStockMySql`
with `FLEET_ORCHESTRATOR_MIGRATION_CONNECTION` pointing only to a disposable MySQL 8 server;
the suite creates/drops uniquely owned databases and uses mysql/mysqldump for restore/query.
Missing MySQL is a failure, not a skip. Runtime integration is a separate slice under [#489](https://github.com/anurmatov/phleet/issues/489).

## Managed API-key bootstrap (partial #492)

Explicit `Agent:NativeAuthMode=api_key` and `Agent:ProviderApiKeyEnvRef` prepare a private `auth.json` containing only the standard `OPENAI_API_KEY` placeholder and force Codex API login without a manual auth cache, using the [official OpenAI login-method setting](https://learn.chatgpt.com/docs/auth#enforce-a-login-method-or-workspace).
The reference is never resolved inside the workload, and placeholder syntax is not proof of stock issuance, acknowledged provider attachment, or secret isolation.
With `StockOpenShell:ManagedEnabled=true` the managed runtime below performs apply; the existing manual profile still requires its original auth input.

## Managed runtime (#492, default off)

`StockOpenShell:ManagedEnabled=false` (`FLEET_STOCK_MANAGED_ENABLED`) keeps every agent on Docker and
returns `managed_runtime_unavailable` as before. Enabled, one adapter (`StockOpenShellRuntime`) sits
behind the existing REST and MCP entry points. It drives the pinned stock CLI (`0.1.5`, argument
arrays, JSON output, TLS-skip/endpoint variables removed) and never falls back to Docker.

| Operation | Behavior |
|---|---|
| Apply (`POST /api/agents/{name}/runtime/apply`, provision, reprovision) | Preflight first, with no external action: codex + API-key ref resolvable, exact policy version, private 0700 `ConfigDir`, authority template, HTTPS broker management with operator CA, CLI version, observable gateway. Then the old runtime is quiesced (Docker stop, or full revoke fanout of the old epoch), the broker epoch is created, a revoked allocation record is written, the provider is created/updated with the key only in the CLI's environment, and the sandbox is created once (`--restart-policy never`, `--no-auto-providers`). After the refused allocation settles, the record is bound to the actual UUID and activated, the active copy is uploaded, and one `sandbox start` runs. |
| OpenShell→Docker | Revoke fanout for every verified binding, then `applied=docker`, and the ordinary Docker path continues. Any unresolved leg holds. |
| Start/Stop/restart/cancel/logs | Act on the persisted applied binding only, with exact-UUID checks before and after each name-based stock call. A pending desired change never redirects them. |
| `POST /api/agents/{name}/revoke` | Admission revoke, provider detach, broker revoke/connection close and Stop, as independent legs (30 s each, 120 s total). All unresolved errors are aggregated and the binding becomes `needs_attention`. |

- **One writer per agent.** A compare-and-set on `agents.AppliedRuntimeBinding` admits one operation; a concurrent request gets 409.
- **Interrupted or unknown outcomes.** An operation left by an earlier orchestrator process becomes `needs_attention` and is never replayed. Unknown outcomes, a changed sandbox UUID, an unobservable quiescence check and a changed gateway generation (container ID plus StartedAt) all hold, with no Start or replacement, until an operator reconciles and revokes. A planned gateway restart is the one exception: when every owned managed record is durably stopped (or revoked) with the same sandbox UUID, Stopped and quiescent, Start or re-apply rebinds the recorded generation after the trust preflight; a changed generation under a running binding still holds and runs the revoke/Stop fanout.
- **Cancel.** The adapter runs `sandbox exec` to POST the workload's loopback `/cancel` or `/cancel/{taskId}`. The agent admits only that route, and only when the actual connection address is loopback and `Agent:NativeAuthMode=api_key`; forwarded headers are ignored.
- **Broker.** Per epoch: vhost `stock-<epoch>` plus `-workload`, `-control` and `-observer` users with random passwords, no tags, and exact anchored permissions built from the source queue/exchange names. Existing resources refuse, partial writes are reported as owned partials, and revoke closes connections and deletes the users while keeping the vhost for custody.

Operator inputs live in the private `${FLEET_BASE_DIR}/openshell-client` directory (0700), mounted
into the orchestrator at `/var/lib/fleet-openshell-client`:
- stock CLI client metadata/trust (`XDG_CONFIG_HOME`); with the example `--profile openshell` gateway the
  client endpoint is `https://openshell-gateway:8443` on `fleet-net`, authenticated with the mTLS
  client cert/key (the gateway cert needs that DNS SAN), never the host loopback, which the
  orchestrator container cannot reach;
- `managed-authority.json` (gateway issuer/audience/JWKS, assertion key paths, memory and broker hosts,
  optional `ServiceHosts` such as `{"temporal": "temporal.example.test", "telegram": "telegram.example.test"}`
  and optional `JournalHost` such as `"journal.example.test"`);
- `broker-ca.pem`.

Gateway reachability (the design's network row) with the example `--profile openshell` unit:

| Caller | Network | Endpoint | Authentication |
|---|---|---|---|
| Orchestrator stock CLI | private `fleet-net` bridge | `https://openshell-gateway:8443` | mTLS client cert/key from `openshell-client`; server cert DNS SAN `openshell-gateway` |
| Trusted native supervisor (`network_mode=host`) | host loopback | `https://127.0.0.1:8443`, the gateway's loopback-only host binding `127.0.0.1:8443:8443` | the stock supervisor TLS/JWT path; server cert IP SAN `127.0.0.1` |

The binding is published on `127.0.0.1` only, never `0.0.0.0`, and only when the dormant
`openshell` profile runs, so a default-off install publishes nothing. Host and container port stay
equal so the supervisor's default loopback callback matches the listener; the gateway health
listener (`127.0.0.1:8081`) stays inside the container. The outside signer, receivers and broker
stay on private networks with no public listener. Whether the pinned images use exactly this
callback is part of each deployment's qualification, not something these source checks prove.

With `FLEET_STOCK_MANAGED_ENABLED=true`, `setup.sh` and `upgrade.sh` refuse before replacing anything
unless `FLEET_STOCK_BROKER_MANAGEMENT_URL` is `https://`, `broker-ca.pem` is readable, both
`FLEET_STOCK_BROKER_ADMIN_*_REF` keys name non-empty `.env` keys, the stock CLI's mTLS client
`openshell/gateways/<name>/mtls/tls.crt` and `tls.key` are readable here, and `python3 -c 'import sqlite3'`
succeeds (`scripts/lib/stock-managed-preflight.sh`). Default-off installs skip these checks.

The adapter writes per-agent records and control/observer broker passwords under
`managed/authority/` (0600). Outside consumers select records by sandbox: set `StockOpenShell:AuthorityDirectory` to that
`managed/authority` directory on `Fleet.StockIdentity` and each receiver (memory, Temporal, Telegram), keeping
`AuthorityFile` for gateway trust. The gateway-verified sandbox claim (hook) or the assertion's
sandbox (receiver, still signature-verified against the selected record) is only an index hint:
exactly one active, non-symlink record must claim it, else the request refuses.

**Broker control and observer lanes.** Set the same startup-only `StockOpenShell:AuthorityDirectory` on the outside
Fleet.Bridge and on the orchestrator to open one epoch-bound broker lane per managed agent instead of
one per process:
- **Bridge (control).** The ordinary connection, peer config and `ask_acto` behaviour stay as they are
  for ordinary targets. When the target (`Bridge:TargetAgent` / `FLEET_CTO_AGENT`) has a record
  `<agent>.json` in the directory, the request goes only through that agent's own lane: the record's
  `BrokerHost`, vhost `stock-<epoch-N>`, principal `stock-<epoch-N>-control`, password
  `<agent>.control.password`. The lane is opened on first use, with the single-file principal, route
  and correlation checks. A managed target never falls back to the ordinary connection. These all
  refuse: an inactive, expired, ambiguous or linked record; a record removed after use; an unreadable
  directory. A lane is replaced when the agent's record binding changes, for example a new epoch. A
  lane that fails to connect stays refused until then, because there is no stock reconnect loop.
  `check_acto_status` keeps its stock refusal for a managed target. To return an agent to the ordinary
  connection, apply Docker: the verified OpenShell-to-Docker handoff marks the record `retired`, which
  releases the lane and the ordinary-traffic hold. Never delete or edit authority records by hand; a
  failed retirement stays `needs_attention` until an operator reconciles and revokes.
- **Observer.** The orchestrator's stock observer keeps one `-observer` receiver for each active
  record. It uses that record's `BrokerHost` and `<agent>.observer.password` and reconciles every 30 s:
  new or re-enrolled records get a receiver, and vanished, revoked or changed ones are closed. A failed
  or dropped receiver is not retried until its record changes. One lane never affects other lanes or
  the ordinary consumer, and warnings carry no exception text or credentials.

The single-file `AuthorityFile`/`BrokerPasswordFile` mode is unchanged. On the Bridge it cannot be
combined with `AuthorityDirectory`: that combination refuses at startup. Without the directory key
nothing changes. The orchestrator image carries
`LABEL org.phleet.managed-stock-runtime=1`; the stock CLI is installed only with the
`OPENSHELL_CLI_URL` and `OPENSHELL_CLI_SHA256` build args (digest and version checked).

**First-party MCP routes.** Besides memory, managed apply routes the existing `fleet-temporal` and
`fleet-telegram` HTTP MCP endpoints when `managed-authority.json` names a `ServiceHosts` entry for
that service. The per-agent record then carries `Services.<service>` with:
- that host;
- exactly the agent's enabled DB tool grants on the endpoint;
- for Telegram, the agent's formatting mode.

Implicit provisioning grants, such as the Codex `notify_cto` auto-grant, are not added: grant them
explicitly. The workspace `.mcp.json` and native MCP tables list memory plus each routed endpoint
under its ordinary name, with no query identity. Grant changes apply on the next apply/reprovision,
which also refreshes `.mcp.json`. As with memory, the authored sandbox policy must list each
receiving host as an exact destination; nothing is generated. The hook accepts
`{"service":"memory"|"temporal"|"telegram"}` and signs a `tools/call` only for a name in that
service's grants. Each proof binds its receiving service, so it fails at any other receiver. Set
`StockOpenShell:AuthorityFile` (plus `AuthorityDirectory`) on a dedicated Fleet.Temporal or
Fleet.Telegram receiver exactly as on the memory receiver. It then serves only `/mcp` and `/health`,
refuses caller query or `X-Fleet-*` identity, and supplies `?agent=` from the verified record, so the
services' existing caller checks and bot selection run unchanged. Without those settings nothing
changes.

**Own Telegram bot and journal capture.** The real values stay outside, in stock providers attached
to the sandbox; the workload holds only placeholders that stock issues and the gateway resolves on
egress ([URL-path and header substitution](https://github.com/NVIDIA/OpenShell/blob/bdb956f2cd65a635f1294fccf1d138d263bf650f/docs/how-it-works/providers/profiles.mdx#L140-L143)).

| Route | Outside (orchestrator) | Inside (workload) |
|---|---|---|
| Own bot (`TELEGRAM_*_BOT_TOKEN` env ref) | The token is read from the host `.env` by that reference. It is submitted as provider `fleet-<agent>-telegram` (`--type telegram --credential TELEGRAM_BOT_TOKEN`), with the value only in the CLI's environment. | `StockOpenShell:TelegramBotId`, the public numeric id before `:`, plus the placeholder stock issues in `TELEGRAM_BOT_TOKEN`. Telegram.Bot is built with `<botId>:<placeholder>`, which passes its token validation. `StockTelegramHttpHandler` strips only `<botId>:`, so requests leave as exactly `https://api.telegram.org/bot<placeholder>/<method>` or `/file/bot<placeholder>/<path>`. Any other scheme, host, port, query or shape is refused, with no fallback. |
| Journal capture (journal on and own bot, exactly when ordinary provisioning mints an ingest token) | The same derived per-agent ingest token is submitted as provider `fleet-<agent>-journal` (`--type fleet-journal --credential FLEET_JOURNAL_INGEST_TOKEN`). | `StockOpenShell:JournalCapture=true` and `Journal:BaseUrl=https://<JournalHost>`. The existing bearer header carries the placeholder from `FLEET_JOURNAL_INGEST_TOKEN`. |
| `fleet-comms-journal` read MCP (the agent has that endpoint) | The same derived per-agent read token ordinary provisioning writes into the generated MCP header is submitted as provider `fleet-<agent>-journal-read` (`--type fleet-journal-read --credential FLEET_JOURNAL_READ_TOKEN`). The outside record gains `JournalRead` (`Host` = `JournalHost`, `Path` = the ordinary endpoint's path, `Tools` = the agent's enabled existing grants). | `.mcp.json` names `https://<JournalHost><path>` with no header. The agent writes the native `[mcp_servers.fleet-comms-journal]` table with `http_headers = { Authorization = "Bearer <placeholder>" }`, taking only the placeholder stock issued in `FLEET_JOURNAL_READ_TOKEN`; a literal, foreign or missing value refuses with `stock_enrollment_journal_read_placeholder_invalid`. |
| Journal files/send/cross-chat (capture on plus the files or send grant; cross-chat also needs the agent's cross-chat switch, exactly as ordinary provisioning) | The same derived read token goes to `fleet-<agent>-journal-read` (one value even when the read MCP also exists; a mismatch refuses). The derived cross-chat token goes to `fleet-<agent>-journal-cross-chat` (`--type fleet-journal-cross-chat --credential FLEET_JOURNAL_CROSS_CHAT_TOKEN`). | The nonsecret `Journal:FilesEnabled`/`SendEnabled`/`CrossChatEnabled` switches stay; `Journal:ReadToken`/`CrossChatToken` are filled only from the placeholders in `FLEET_JOURNAL_READ_TOKEN`/`FLEET_JOURNAL_CROSS_CHAT_TOKEN`. `.mcp.json` and the native config name the agent-local `fleet-journal-files` listener (`http://127.0.0.1:8091/journal-files/v1/mcp`). The agent's existing requester-only destination and cross-chat checks are unchanged. |

Comms still verifies the same token subject, so receiver ownership and grants do not change. Journal
off, or journal without a bot, stays capture-off. The existing spool, drainer and SDK retries are
reused, with no extra poller. The agent's startup gate accepts only placeholders. It refuses a
literal or foreign value, a missing variable, a `Journal:ReadToken`/`CrossChatToken` other than its
placeholder, files/send without managed capture, and these selectors on the manual profile or an
ordinary agent. The native Codex child does not inherit these variables. The workload appsettings carry no credential slot value. If a submitted value would
still reach them, apply holds with `managed_files_failed` before any old-runtime action.

Operator inputs (nothing is generated; the policy YAML is stored as supplied):
- Import two custom stock profiles: `telegram` (credential `env_vars: [TELEGRAM_BOT_TOKEN]`, endpoint
  `api.telegram.org:443`, `protocol: rest`) and `fleet-journal` (credential
  `env_vars: [FLEET_JOURNAL_INGEST_TOKEN]`, endpoint your journal host on `443`). Name the agent's
  `dotnet` host binary in `binaries`, then pin both profiles' hashes in the policy version. An agent
  with the `fleet-comms-journal` endpoint or files/send also needs `fleet-journal-read` (credential
  `env_vars: [FLEET_JOURNAL_READ_TOKEN]`, the same journal host on `443`; `binaries`: the native
  Codex binary for the read MCP, the `dotnet` host for files/send), and cross-chat needs
  `fleet-journal-cross-chat` (`env_vars: [FLEET_JOURNAL_CROSS_CHAT_TOKEN]`, same host, `dotnet`),
  pinned the same way.
- Add `"JournalHost": "journal.example.test"` to `managed-authority.json`. This is a TLS front for the
  Comms journal listener, an exact DNS name distinct from the memory and service hosts.
- The authored sandbox policy must allow `api.telegram.org:443` and the journal host `:443` as
  inspected REST endpoints for that binary only. `tls: skip` and raw tunnels disable substitution.

Placeholders are not secret values, but anything that can read the daemon's environment can present
them to those endpoints. Binary scoping and endpoint binding are the boundary.

Refusal codes, all raised before the old runtime stops:
- `telegram_bot_token_unresolved` and `telegram_bot_token_invalid`;
- `journal_token_unresolved`, `journal_key_missing` and `journal_key_invalid`;
- `managed_route_unsupported:journal`, when there is no `JournalHost`;
- `journal_token_unresolved` also when the derived read or cross-chat token is missing.

**Current limits, refused rather than dropped.** The agent's own bot, journal capture, the
`fleet-comms-journal` read MCP and journal files/send/cross-chat (the agent-local
`fleet-journal-files` listener) are routed as above. Every MCP endpoint other than memory, the
first-party receivers and `fleet-comms-journal` refuses apply with `managed_route_unsupported:mcp:<name>`,
as does a supported endpoint without a configured receiving host (for `fleet-comms-journal`, no
`JournalHost`). Tool-send receipts (#394) use the epoch's existing control lane (#492 existing-role
routing): when Fleet.Telegram verifies a managed request it pins the receipt context (agent, sandbox,
epoch, MCP session, destination chat) from the verified record, re-reads the record before publishing,
and publishes the typed receipt as that epoch's `control` principal to `fleet.group` with the agent's
routing key. It reads only `<agent>.control.password` and the current record from
`StockOpenShell:AuthorityDirectory`, never admin or observer credentials, declares nothing and closes
the lane when the binding changes. A stale, revoked or foreign context is dropped and counted, never
retargeted or sent to the ordinary `fleet.journal.tool-sends` exchange. The workload admits the receipt
from its own queue only with the control user-id, exchange `fleet.group`, its own routing key and a
pinned agent/sandbox/epoch that is its current enrollment, then applies the usual own-bot, privacy and
capture rules, writes the spool before the ack and ignores a duplicate in the same life. Broker
users, permissions, queues and consumers are unchanged. Workflow directives and signals are not
routed either: a managed agent's epoch identity reads only bridge control requests, and its relay
publishes only bridge responses, so Temporal `DelegateToAgent` (and any workflow signal it would
send back) has no lane. With `TemporalBridge:OrchestratorUrl` set, `DelegateToAgent` reads the
target's applied runtime and fails non-retryably with `managed_route_unsupported:workflow_delegation`
(error type `ManagedAgentWorkflowUnsupported`) instead of publishing a directive nobody reads; after a
verified handoff back to Docker, delegation works again. The check fails closed: when the
orchestrator is unreachable, answers non-success, or returns no valid `appliedRuntimeKind`
(`docker`/`openshell`), the attempt fails retryably with `managed_route_unknown:workflow_delegation`
(error type `ManagedAgentRuntimeUnknown`) and nothing is published on the ordinary broker. The
directive leg alone would fit the control role (configure/write `fleet.group`), but the correlated
reply does not: it is routed `temporal-bridge` to `fleet.agent.temporal-bridge`, which control may
neither configure nor read, and the only control-readable queue, `fleet.agent.bridge`, is already
consumed by the bridge's managed lane, so a Temporal reader there is the extra consumer §6 rules
out. Routing workflow replies needs a §6 decision. A dedicated Fleet.Temporal receiver
(`StockOpenShell:AuthorityFile` set) is receiver-only: it serves the authenticated `/mcp` route but
starts no namespace workers and no relay consumer on the fixed `fleet.agent.temporal-bridge` queue,
so it never competes with the ordinary bridge for replies on a shared broker. None of the following is
qualified:
- that topology;
- the stock gateway Compose unit;
- the gateway's real bot/journal substitution and TLS path;
- dashboard revoke/state controls;
- the upload-into-stopped-sandbox step.

#493 owns the exact-build trial, with no installation or activation implied.

**Rollout guard and rollback.** `scripts/check-stock-runtime-rollout.sh` runs in `upgrade.sh`
before anything is stopped, and it is the operator rollback entrypoint (`--image IMAGE`). A target
without the label needs the operator-authenticated `GET /api/managed-runtime/rollback-preflight` to
report zero desired, applied and unresolved managed bindings. A nonzero, malformed or unavailable
answer refuses. Ordered rollback:
1. revoke;
2. confirm quiescence and no unresolved errors;
3. apply Docker;
4. roll back the orchestrator image;
5. Start if needed.

`setup.sh` re-runs call the same guard before rebuilding the orchestrator; with `--skip-build` they
check `--image fleet:orchestrator`, the existing local image they will start, instead of the Dockerfile. **CI jobs and any external
deploy integration that replace the orchestrator must run
`scripts/check-stock-runtime-rollout.sh --image <exact orchestrator image>` first and stop on a nonzero
exit.** Administrator Docker commands that bypass this procedure are not protected.

**Backup.** Run `scripts/backup-stock-gateway.sh BACKUP_DIR GATEWAY_TOML [/var/lib/openshell]` from the
operator's daily host job and before gateway changes. It refuses incomplete coverage (database, gateway TOML, TLS cert/key/client CA, JWT keys and kid), then takes a stdlib SQLite online backup with
an integrity check, copies config and trust files, and checks their inode/mtime before and after.
Directories are 0700 and files 0600, and seven complete snapshots are kept. A failure, a
changed trust file or an interruption (SIGTERM/SIGINT/SIGHUP) marks the partial snapshot `INVALID` and
deletes it (it may already hold keys), stamps `BACKUP_DIR/last-failure` and exits nonzero; `last-success`
is replaced atomically and only after the snapshot is `COMPLETE`. A run writes into
`BACKUP_DIR/.partial-gateway-*` and renames it to `gateway-*` only once `COMPLETE`, so a SIGKILLed run
leaves no `gateway-*` snapshot; the next run deletes such partial dirs older than an hour and any
incomplete `gateway-*` dir. Linked OS ancestors above `STATE_DIR` (macOS `/var` -> `/private/var`) are resolved; a
linked `STATE_DIR` or anything under it still refuses. Staleness is surfaced by
`scripts/check-stock-gateway-backup.sh BACKUP_DIR [MAX_AGE_HOURS]` (default 26): it exits nonzero when
`last-success` is missing, malformed, older than the limit or not newer than `last-failure`, or when no
complete `gateway-*` snapshot exists (partial and incomplete dirs never count). With
`FLEET_STOCK_MANAGED_ENABLED=true`, `setup.sh` and `upgrade.sh` run it against `FLEET_STOCK_BACKUP_DIR` (limit
`FLEET_STOCK_BACKUP_MAX_AGE_HOURS`) before the rollout guard and refuses on a missing, failed or stale
backup; wire the same check into host monitoring. The example compose mounts `FLEET_STOCK_BACKUP_DIR`
read-only into the orchestrator for the status view, which re-reads the stamps at most every 30 s on
status reads (a configured dir that is not mounted reports `unmounted`); the periodic status refresh
likewise re-observes applied sandbox containers at most every 30 s per agent, display-only. Snapshots contain keys: keep
them host-local unless an operator-approved encrypted transfer exists. To restore, stop the gateway
and every owned workload first, verify integrity and bindings before any admission, and keep the
unexpected-resume hold.
