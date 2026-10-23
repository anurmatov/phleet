# Request assertion receiver contract (draft)

This is Phleet's receiving contract, not an OpenShell gateway JWT or a claim of
qualified supervisor header mutation. The outside hook/issuer/ingress, current
supervisor readiness and actual daemon path are still missing, so do not enable
this draft in an installation or change agent endpoints based on source tests.

## Memory receiving boundary

The startup-only `FleetIdentity:Enabled` setting defaults to false. When enabled,
Memory adds the configured `FleetIdentity:VerifiedPort` (default 3101) with a
required client certificate. It exposes only `/mcp`, never internal/admin routes.
`ProtectedAgents` is the explicit comma-separated staged set; those names are
refused on legacy MCP, while a single named unprotected agent keeps its legacy
path. Missing/duplicate legacy attribution is denied. Verified admission requires
membership in the staged set, so it cannot silently migrate an unlisted agent.
Legacy query attribution is not authenticated identity: workload policy must still
block every direct legacy route before any protected workload is admitted.
The existing internal listener remains outside workload policy and is not proxied.
No compose host port, production config or grant is added by this source change.

Startup requires a nonempty valid protected-agent set, a verified port distinct
from legacy port 3100, project ACL enforcement, fixed audience `memory:/mcp`, issuer,
key ID, public P-256 assertion key, public ingress CA, exact ingress DNS SAN,
server certificate/key paths and an outside-workload MySQL connection string.
Blank/missing trust fails startup; keys are never generated, fetched or trusted on
first use. Assertion-key and CA files containing private key PEM are rejected.
The client chain uses only the configured root, client-auth EKU and exact DNS SAN,
without certificate downloads or system-root fallback; this is not CRL/OCSP support.
All settings live under `FleetIdentity` in Memory configuration and are startup-only.
Use protected operator configuration for values, never repository files or CLI logs.
The receiver's database credential must remain outside the workload with only the
required mapping/nonce reads; this draft does not provision a user or widen grants.
Default host `.env` and optional 1Password source selection are unchanged.

## Phleet-owned proof format

The single visible `X-Fleet-Assertion` header is a compact JWS with canonical
unpadded base64url segments and a 64-byte P1363 ES256 signature. Its protected
header has exactly `alg=ES256`, `typ=fleet-request+jwt`, and the configured `kid`.
Duplicate fields, alternate algorithms, supplied JWKs and malformed segments fail.

Required payload fields are `iss`, string `aud`, `sub`, `gateway_id`, `sandbox_id`,
`launch_id`, `identity_epoch`, integer `iat`/`exp`, `jti`, `method`, `target`,
`body_sha256` and `session_hash`. Issuer/audience/key are pinned; lifetime is at
most 15 seconds with no future-issued or expired acceptance and no clock leeway.
Opaque names/IDs are ASCII letters, digits, hyphen and underscore; launch/epoch
are nonempty D-format UUIDs. No caller query/header/body attribution is identity.

`target` is the exact received origin-form raw target, including encoded query
bytes, without decoding/reordering. `body_sha256` is lowercase SHA256 of the exact
body bytes and `session_hash` is lowercase SHA256 of the exact UTF-8 MCP session
header. With no session header during initialization, it is exactly
`e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855`
(the SHA256 digest of zero bytes), never an empty claim, null or a sentinel word.
The signer/receiver initialization vector accepts this digest and rejects an empty
claim. A present-but-empty session header is not an initialization escape. No intermediary may mutate these
bytes after signing; real hook/proxy/native compatibility remains UNRUN.
POST must be JSON and body size is limited to 256 KiB; GET/DELETE bodies and
Upgrade/unsupported methods are denied. Response streaming is left unchanged.
Caller identity headers, legacy Authorization and attribution queries are denied.

## Current state, sessions and request authorization

Every verified request performs a bounded authoritative MySQL read for exact
agent/gateway/sandbox/launch/epoch, active slot, nonrevocation and activated or
committed phase. The JTI must already exist for this launch and remain unexpired:
only the missing outside ingress may atomically consume it before forwarding.
The receiver does not mint assertions, consume that nonce a second time, or cache
a positive mapping. SQL failure is unavailable, not an empty/authorized result.

MCP initialization without a session is restricted to the initialize method.
Response session IDs bind to immutable agent/launch/epoch/service/route tuples;
foreign or unknown IDs cannot rebind. Receiver restart invalidates local sessions.
There are at most 100 local sessions and 64 concurrent requests per launch, with
no eviction into acceptance; DB-backed issuer session/replay lifecycle is still
missing. DELETE/GET and every subsequent POST repeat proof/state/ACL checks.

After identity and session checks, middleware calls `GetVerifiedSnapshotAsync`
and attaches its immutable result to this HttpContext before MCP dispatch.
All six existing memory tools select that snapshot and signed agent, never the
legacy warm cache or a query identity; existing project/record filtering remains.
Without the request marker, every tool refuses execution on port 3101 even if
legacy project ACL is disabled. Store attribution is the verified agent, not the
submitted metadata author. Authority failure produces 503 before tool dispatch,
invalid identity 401, and scope/session/mapping denial 403, without content.

## Evidence boundary

`bash tests/openshell/run.sh memory-identity` runs actual proof, certificate,
middleware, request ACL and tool code with owned fake keys, a stub state reader
and an isolated ACL HTTP handler. No actual TLS socket, upstream hook, MySQL query,
Qdrant search/list, daemon/native CLI, Rabbit callback or runtime rollback is
qualified by these tests. The TestServer MapMcp test now exercises a real MCP
initialize/session/tools-call sequence through the production middleware and
memory_get, proving that revoking its authoritative ACL changes the next call
even while a legacy wildcard cache remains warm; its TLS/state/ACL transport seams
remain isolated fixtures, not an outside hook or actual TLS socket. The production SQL reader and Kestrel setup are source
only until separately approved exact-head integration and restore/query evidence.
The complete slice-1 and merge blockers in [openshell.md](openshell.md) remain open.

## Installation/configuration lockstep and database fixture

`.env.example`, `setup.sh` and `docker-compose.example.yml` carry the twelve
`FLEET_MEMORY_IDENTITY_*` keys for the options above. Setup clears exported shell
overrides, adds only missing defaults and never changes recorded values, enables
the path or generates/imports keys. It creates an empty 0700 `identity/memory`
directory for the example's read-only `/run/fleet-identity` bind; no verified host
port is published. Existing installs keep the same default-off behavior through
compose fallbacks; host `.env` and optional 1Password choices are unchanged.
The reserved prefix is excluded from config/setup API values and writes, cannot
be saved as a workload Env Ref and is refused by provisioning even if a privileged
writer bypasses the tracked-write guard. These are source restrictions, not a
live grant or credential migration.

Run `python3 tests/openshell/config-test.py` for actual setup-block tests on owned
temporary files, not a setup or Docker run. Run the migration suite from
[openshell.md](openshell.md) on a separately approved MySQL target to exercise
VerifiedLaunchReader against the actual migration in current/revoked/wrong-epoch/
missing-nonce states; those four new cases are authored and locally UNRUN here.
Neither migration/query fixtures nor TestServer prove backup/restore or the
actual daemon→CLI→shell→memory/Rabbit callback and rollback path.
