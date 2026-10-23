# Request assertion receiver contract (draft)

This is Phleet's receiving contract, not an OpenShell gateway JWT or a claim of
qualified supervisor header mutation. The outside hook/issuer/ingress, current
supervisor readiness and actual daemon path are still missing, so do not enable
this draft in an installation or change agent endpoints based on source tests.

## Memory receiving boundary

The startup-only `FleetIdentity:Enabled` setting defaults to false. When enabled,
Memory adds HTTPS port 3101 with a required client certificate and rejects legacy
MCP on other ports. Port 3101 exposes only `/mcp`, never internal/admin routes.
The existing internal listener remains outside workload policy and is not proxied.
No compose host port, production config or grant is added by this source change.

Startup requires project ACL enforcement, fixed audience `memory:/mcp`, issuer,
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
header, or the empty string when initializing. No intermediary may mutate these
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
qualified by these tests. The production SQL reader and Kestrel setup are source
only until separately approved exact-head integration and restore/query evidence.
The complete slice-1 and merge blockers in [openshell.md](openshell.md) remain open.
