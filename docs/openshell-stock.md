# Stock OpenShell whole-agent enrollment

## Status: source connected, runtime unqualified

This draft connects one manually enrolled, non-root Codex whole-agent profile to
the existing daemon/native CLI/tool path, verified memory and normal broker control/
completion. It is not a command-only sandbox. Source fixtures do not demonstrate
installed-image compatibility, gateway/memory TLS, native AMQP or an actual whole-agent
turn; those require the separately authorized exact-build E2E before merge/deploy.

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
Neither image target was built in the source-only tests.
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
A real stock launch must honor that UID and the approved role policy; no image was
built or runtime started during source tests. Do not mount a Docker socket, host shell,
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
the binary (`roles/.../system.md`, `projects/.../context.md`), and one read-only `/workspace/.mcp.json`:

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
this bounded profile; they are not silently granted or runtime-qualified. Agent HTTP
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

Mandatory exact-build whole-agent/native-tool/memory/completion/revoke/non-destructive
rollback E2E and relevant real storage/restore evidence remain UNRUN/BLOCKED pending
separate target authorization. Full independent final-head review/public scrub,
existing CI and human merge approval remain required. Broad provider/platform/load
permutations are deferred, not passed. This draft is not complete issue delivery.

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
