# Codex subscription authentication qualification

**UNQUALIFIED — documentation and an operator-imported profile, not a supported production mode.**
Refs [#498](https://github.com/anurmatov/phleet/issues/498), approved body
`e960764c036d6767a6c0538272f4af79ddf562c284851da8ec11aeef8904f4d4`.
All A0–A9 runtime outcomes remain **UNRUN** in this source patch.
A source review, profile check or merge cannot qualify native credential isolation.
**TRANSPORT BLOCKED:** effective HTTP/SSE selection at the pinned native client
has not been established; no native trial startup is authorized by this runbook.

This is one native-cache candidate on unmodified OpenShell 0.1.5 and Codex 0.164.0.
The alternative external-token RPC is not this handoff: it parses an access-token JWT.
Failure means blocked/unsupported at these pins, not permission to add a proxy,
fork, token broker, refresh framework, API billing or a second implementation.

## Authority and support boundary

Design approval permits this documentation/profile slice only.
Before any live action, obtain a separate operator scope naming exact absent P/F
roots, owned resources, account, credential custody, permitted issuer calls and cleanup.
The scope must explicitly cover the fresh P login and disposable-provider operations.
Do not inspect, copy or import any existing agent's auth cache or rotating grant.
Existing refresh owners and ordinary API-key/manual agents stay unchanged.

No runtime enum, executor, bootstrap, setup/upgrade, feature flag, DB/API/MCP,
dashboard, policy selection, grant or image pin changes are shipped here.
The managed adapter remains API-key-only; this example is never auto-loaded.
A qualification pass would still need a newly reviewed design revision before
managed subscription integration or migration.
[#493](https://github.com/anurmatov/phleet/issues/493) whole-agent native,
service/workflow, revoke/quiescence and rollback acceptance remains separate.

## Operator preflight and profile

These are planned checks for a separately authorized isolated trial, not commands
executed by this source patch.

1. Record the exact source pins below, platform binary SHA-256 values and actual
   gateway/supervisor/runtime image digests; tags alone do not prove identity.
2. Record `sqlite3 --version` and the availability of `.backup` and
   `PRAGMA integrity_check` on the operator system before mutating a fixture.
   Record `sha256sum` and mode/owner-preserving `cp -a` availability too; missing
   tools stop preflight, never trigger an automatic package install.
3. Confirm absent private roots and distinct gateway, supervisors, network,
   state/DB/KEK/trust/client context/ports/provider identities for P and F.
   Record existing API-key/manual agents and provider/state mappings as the A8
   baseline, without reading credential values; no shared production resources.
4. Review [the profile](../deploy/openshell/codex-subscription.yaml), authored as
   JSON-compatible YAML so it can also be parsed offline with the Python standard
   library; this does not replace stock semantic validation.
   Its two binary paths describe the reference Linux x64 npm layout only.
   Before import, verify the wrapper and actual native executable paths in the
   selected image and pin those measured paths in the trial ledger/profile hash;
   no wildcard binary directory or broader endpoint on mismatch.
5. The profile exposes only `CODEX_AUTH_ACCESS_TOKEN`; discovery reads only that
   explicitly selected environment slot, never the ordinary auth-cache path.
   Refresh material is separate gateway-owned state, not additional credentials
   named `refresh_token`, `id_token` or `account_id` in the workload environment.
6. Run stock validation only after its isolated target is separately authorized:

```sh
profile=deploy/openshell/\
codex-subscription.yaml
openshell provider profile lint \
  --file "$profile"
```

At this pin, `provider profile lint` is **not offline**: the
[pinned implementation](https://github.com/NVIDIA/OpenShell/blob/bdb956f2cd65a635f1294fccf1d138d263bf650f/crates/openshell-cli/src/commands/provider.rs#L1841-L1865)
loads the file and invokes the authenticated gateway lint RPC.
Offline JSON/schema-field/source checks must not be reported as a stock lint pass.
No gateway is contacted, started or imported into merely to validate this patch.
A1 stock lint and the entire live ledger remain UNRUN until the operator runs them.

7. Only after authority, identity, account-boundary and trust checks pass, import
   the reviewed profile into its dedicated fixture using the existing stock CLI,
   and create/attach only the authorized disposable provider with ambient provider
   discovery disabled; this is not permission to modify an existing provider.
   P access material enters a selected environment-only import in a private
   process, never shell arguments, history, tracing, previews or logs.
   Stop the fresh enrollment client before gateway ownership and retire only its
   obsolete trial cache after a committed handoff; never reuse that refresh grant.
8. Configure the gateway-only refresh material with the pinned public client ID
   and token origin below, using `--secret-material-env refresh_token=<env-name>`
   rather than a credential-valued argument; keep the 300/3600 defaults.
   No native refresh token or refresh callback carrying real tokens is allowed.

## Native cache and account handoff

Use only the stock example's cache shape, not its remote listener or opaque
account-ID environment slot.
The authorized operator writes private non-secret `account-metadata.json` as
specified below, uploads only that file and verifies non-root ownership/mode.
No real ID, email, claims, token or resolver handle belongs in a public fixture.

A non-root one-shot preparation in the owned workload reads that metadata and
the exact supervisor-issued access handle, then atomically writes a 0600 cache
in the owned native home with this shape (angle-bracket strings are schema
placeholders, never literal values to launch):

```json
{
  "auth_mode": "chatgptAuthTokens",
  "OPENAI_API_KEY": null,
  "tokens": {
    "id_token": "<non-secret-local-parsing-stub>",
    "access_token": "<supervisor-issued-handle>",
    "refresh_token": "",
    "account_id": "<selected-account-id>"
  },
  "last_refresh": "<current-RFC3339-time>"
}
```

The ID-token-shaped stub follows the pinned example's base64url header/payload
and non-secret signature segment, never a real ID token or fabricated access JWT.
It is local parsing metadata only, not issuer identity, plan or entitlement proof.
Write a separate native `config.toml` before startup:

```toml
forced_login_method = "chatgpt"
forced_chatgpt_workspace_id = "<selected-account-id>"
```

This config sets authentication intent only, not effective HTTP transport.
At Codex 0.164.0, the built-in OpenAI provider sets `supports_websockets: true`
([provider definition](https://github.com/openai/codex/blob/6aaac694803004a63c6f393d153c96d22521a6b9/codex-rs/model-provider-info/src/lib.rs#L528-L567)).
Its configured-provider merge uses `model_providers.entry(key).or_insert(provider)`
([merge rules](https://github.com/openai/codex/blob/6aaac694803004a63c6f393d153c96d22521a6b9/codex-rs/model-provider-info/src/lib.rs#L696-L740)),
so an `openai` table cannot replace that built-in transport capability.
Do not add an ineffective `supports_websockets = false` override or claim that
the desired HTTP Responses/SSE baseline is selected by the auth configuration.
The coordinator must obtain pinned-supported effective-config/source proof of
HTTP selection before an authorized native trial can start; absent that proof,
record `blocked:transport_unqualified`, with native checks UNRUN.
No endpoint widening, additional provider implementation, proxy or helper
framework is authorized to resolve this prerequisite here.

Only the private stdio app-server is selected, with no remote listener or
`account/login/start` token injection.
Editable native config is not account authorization; prove an outside stock/issuer
boundary before a model turn, otherwise stop as blocked at these pins.
If native parsing needs a real token, a required extra bootstrap path/transport or
another origin, stop instead of widening policy or adding a helper service.

## Evidence and chosen path

| source | verified boundary |
|---|---|
| [Current adapter](https://github.com/anurmatov/phleet/blob/93d6266d00da4060aa1dd924b99d0aef69394b89/src/Fleet.Orchestrator/Services/StockOpenShellRuntime.cs#L567-L578) | API-key-only admission, not a subscription selector |
| [Stock 0.1.5 native example](https://github.com/NVIDIA/OpenShell/blob/bdb956f2cd65a635f1294fccf1d138d263bf650f/examples/codex-app-server/start-codex-app-server#L20-L60) | Placeholder-bearing `auth.json`, `auth_mode="chatgptAuthTokens"`, empty refresh token and a non-secret local ID-token parsing stub |
| [Pinned native cache loader](https://github.com/openai/codex/blob/6aaac694803004a63c6f393d153c96d22521a6b9/codex-rs/login/src/auth/manager.rs#L420-L441) | Codex 0.164.0 loads `ChatgptAuthTokens` from the auth cache without the ordinary native refresh owner |
| [Different native token-RPC path](https://github.com/openai/codex/blob/6aaac694803004a63c6f393d153c96d22521a6b9/codex-rs/login/src/auth/manager.rs#L1801-L1827) | `account/login/start` external-token construction parses the access token as JWT, so an opaque stock handle is not a supported drop-in there |
| [Native workspace discovery](https://github.com/openai/codex/blob/6aaac694803004a63c6f393d153c96d22521a6b9/codex-rs/app-server/src/request_processors/account_processor/workspace_routing.rs#L385-L455) | Mandatory discovery selects an account by exact ID, so the example's opaque account-ID handoff must not be assumed compatible with this pin |
| [Stock refresh/handle contract](https://github.com/NVIDIA/OpenShell/blob/bdb956f2cd65a635f1294fccf1d138d263bf650f/docs/how-it-works/providers/profiles.mdx#L168-L179) | Gateway-managed rotation retains an opaque access handle, while reconfiguration/detach invalidates it |
| [Official OpenAI authentication](https://learn.chatgpt.com/docs/auth#login-caching) | Subscription sign-in distinct from API-key billing, ordinary native credentials cached and refreshed by Codex |

Chosen candidate only: the stock example's **native cache route**, with all usable OAuth material outside the workload and the stock gateway as sole refresh owner, not the external-token RPC route or an OAuth-to-API-key conversion

| handoff stage | exact contract / stop condition |
|---|---|
| Pins | Unmodified OpenShell CLI/gateway/supervisor/runtime 0.1.5, source `bdb956f2cd65a635f1294fccf1d138d263bf650f`; Codex 0.164.0, annotated release resolved to `6aaac694803004a63c6f393d153c96d22521a6b9`; operator-recorded platform binary SHA-256 and image digests before any run; missing/mismatched identity = stop |
| Outside grant | One separately authorized human login into a fresh outside-owned cache for primary fixture P only, no copying an existing agent grant and no second native client sharing it, private operator custody with directory 0700/files 0600, synthetic-only fixture F with no real login or grant |
| Provider import | Separate operator authorization may create/configure/import/delete only the disposable stock providers in P/F, never an external account or an existing provider, access material supplied through explicitly selected environment-only discovery rather than credential-valued arguments, enrollment client stopped before gateway ownership, refresh material only through stock `--secret-material-env`, no shell tracing/history/value output |
| Refresh owner | Stock gateway alone, `oauth2-refresh-token`, `refresh_before_seconds: 300`, `max_lifetime_seconds: 3600`, `https://auth.openai.com/oauth/token`, native OAuth client ID `app_EMoamEEZ73f0CkXaXp7hrann` from [pinned CLIENT_ID](https://github.com/openai/codex/blob/6aaac694803004a63c6f393d153c96d22521a6b9/codex-rs/login/src/auth/manager.rs#L1783-L1790), no client-ID override, native refresh token or parallel refresh owner |
| Workload cache | Private non-root native home, cache 0600, `auth_mode="chatgptAuthTokens"`, `OPENAI_API_KEY=null`, `forced_login_method="chatgpt"`, fixed `forced_chatgpt_workspace_id` from approved non-secret metadata, access token equal to the exact supervisor-issued handle, empty refresh token, no real ID token and no credential source mount |
| Local parsing | Non-secret ID-token-shaped metadata stub following the stock example, used only for native parsing, never sent as authentication or trusted as proof of issuer identity/plan/entitlements; unaccepted stub or a required real access-token JWT = stop |
| Account metadata | Authorized operator writes `{ "account_id": "<selected-account-id>" }` to `<trial-root>/inputs/account-metadata.json` as 0600 under a 0700 directory, uploads only this non-secret file with `openshell sandbox upload <sandbox> <local-file> <native-home>/.codex/account-metadata.json`, verifies workload UID ownership/mode 0600, then invokes a non-root one-shot cache writer to read that file and the supervisor-issued access handle, atomically write `auth.json` 0600 with `tokens.account_id` from that metadata and a separate native `config.toml` with the same `forced_chatgpt_workspace_id` before native startup, no secret/cache source upload or ID in public evidence |
| Account authority | Outside-owned grant and policy binding to that one account, with mandatory cross-account refusal test; native `forced_chatgpt_workspace_id` alone not a security boundary because workload configuration is writable; absence of a stock/issuer-enforced boundary = stop |
| Native client | Existing Codex app-server over private stdio, no exposed WebSocket listener or remote client; no `account/login/start` injection of opaque access handles and no auth-refresh callback returning real tokens |
| Endpoint boundary | Stock-inspected HTTPS only to `chatgpt.com:443`: `GET /backend-api/wham/accounts/check`, `GET /backend-api/codex/models`, `POST /backend-api/codex/responses`; no wildcard provider endpoint, API-key endpoint, direct egress or TLS-skip route |
| Transport/routing | **TRANSPORT BLOCKED** until pinned-supported effective HTTP Responses/SSE selection has offline effective-config/source proof; the built-in OpenAI provider ignores the proposed table override, so no native startup or HTTP baseline claim from that setting; required extra paths, WebSocket-only behavior, redirects or another origin = blocked evidence, no automatic allowlist widening or new helper; optional egress only under the explicit rule below |
| Long-lived use | Stable handle resolves to the currently committed gateway token after refresh without restarting the workload; a changed account, refresh owner, provider configuration epoch or endpoint set requires detach and a new authorization, not silent rebinding |

Source evidence supports this candidate's cache parsing and refresh primitives, not complete native discovery, account isolation, refresh, cancellation or restore compatibility at the combined pins


## Trial isolation and planned outcomes

| fixture | ownership / allowed use |
|---|---|
| P — primary | Dedicated throwaway gateway, supervisor instances, network, credential store and state directory, shared with no existing gateway/provider/workload, one fresh outside-owned real grant only |
| F — refusal | A second independently authorized throwaway gateway/supervisor/state/network and synthetic provider values only, never a copy/restore of P or an existing grant, never a real model turn |
| Target identity | Separate explicit absent `<trial-root>` per fixture, no shared volume, DB, key, trust file, port or provider, exact owned IDs recorded before mutations, unknown/pre-existing target = stop |
| Expected refusal | A predeclared negative input plus its exact refusal, timeout bound and no-forwarding/no-secret assertions all satisfied = **passing negative probe**, nonzero command status alone neither pass nor unexpected failure |
| Unexpected failure | Any nonmatching refusal/status, missing assertion/receipt, timeout outside the declared outcome, possible credential forwarding or positive-step failure = stop the whole qualification, remaining checks unrun, no repair/retry or passing qualification |
| Optional egress | Deny `ab.chatgpt.com` and all other non-allowlisted telemetry/flag/feedback/update origins, record only sanitized destination/count and absence of credential/handle forwarding, non-blocking only if A3/A4 native work still succeeds and the denied request is proven non-credential, otherwise blocked |

Planned order: A0–A2 for P/F → P:A3 → P:A4 positive rotation → P:A5 non-destructive refusals → P:A6 cancel/detach → P:A7 stopped restore → F:A5 epoch refusal → F:A4 failed-refresh/expiry refusals → A8 cleanup/baseline → A9 independent outcome review
Expected negative probes may continue only to these predeclared next steps, never turn an unexpected failure into an expected one after observation

## Failure handling

Unit tests N/A — no runtime code added, stock profile lint and pinned-client behavior required instead of synthetic success being treated as native isolation
Expected negative outcomes below pass only when their predeclared refusal and no-forwarding assertions match, all other errors follow the unchanged first-unexpected-failure stop rule

| dependency / failure | required behavior |
|---|---|
| Missing operator authority or account entitlement | No login, grant read/copy/import, provider mutation, model call, billing change or runtime action |
| File/cache/profile/trust missing, symlinked, wrong owner/mode, unwritable or invalid | Stop before native startup, leave existing caches/workspaces unchanged, secret-free named reason |
| Native/stock process missing, wrong pin, nonzero exit, invalid JSON or timeout | Positive steps: first such failure stops the qualification, sanitized exit/status retained, no alternate command/retry/fallback, negative probes: only predeclared matching refusal plus every security assertion counts as pass |
| Missing/foreign/literal/expired handle or refused provider attachment | No native intake/model request, no credential-valued debugging output |
| HTTPS/TLS failure, redirect, outside origin/path, unknown routing or unsupported transport | Required auth/model/bootstrap traffic: no forwarding, stop on unexpected failure, no downgrade/broadening, A5 intentional refusals and the enumerated non-credential optional-egress denials may pass only their stated assertions |
| Workspace absent/duplicate/changed, wrong account header, issuer-boundary unknown | Positive/discovery steps fail closed with no model turn, A5 intentional wrong-account probe passes only on outside-enforced refusal and no model turn, unknown account authority blocks qualification |
| Gateway refresh timeout, revoked/expired grant, invalid_grant or uncertain rotated-token persistence | P: first unexpected outcome stops qualification, no expired/uncommitted forwarding or blind replay, real invalid_grant/uncertain rotation needs a separately approved fresh human login before a new trial, F: only the explicit synthetic failed-refresh and expiry refusals below may pass without continuation of authenticated work |
| Gateway DB/state or backup unavailable/corrupt, stale grant after restore | No ready/pass state or credential replay, keep the dedicated fixture stopped/detached pending reconciliation, no shared-state restore or claim of issuer grant recovery |
| Cancellation/detach timeout or unobservable quiescence | Hold as unresolved, no replacement workload, account change or claimed revocation success |
| Unexpected first failure | Capture minimal redacted evidence and stop, bounded cleanup only for explicitly owned resources, no opportunistic repair |

## Stopped snapshot, restore and cleanup

No production rollout or shared gateway allowed, P/F each use an explicitly approved absent private trial root with dedicated gateway/supervisor/state, no existing provider sharing

| stopped snapshot/restore stage | exact paths / assertion |
|---|---|
| Explicit trial layout | `STATE=<trial-root>/state`, `CONFIG=<trial-root>/gateway.toml`, `SNAP=<trial-root>/snapshots/<snapshot-id>`, same absolute STATE path visible to the owned host driver/gateway, directory 0700/files 0600 and fixture service UID ownership |
| Pin the storage paths | Owned gateway environment `OPENSHELL_DB_URL=sqlite:<STATE>/gateway.db?mode=rwc`, owned TOML `[openshell.gateway.credential_storage].key_encryption_key_path=<STATE>/key-encryption-key.bin`, server cert/key/client CA/guest CA at `<STATE>/tls/gateway.pem`, `gateway-key.pem`, `client-ca.pem`, `ca.pem`, JWT material at `<STATE>/tls/jwt/signing.pem`, `public.pem`, `kid`, owned CLI client trust kept under `<trial-root>/client` |
| Before mutations | Snapshot empty initialized fixture state/config/trust only, preserve originals, never a production/shared gateway snapshot |
| A7 checkpoint | After P cancellation/detach and before restore, stop native workloads, supervisors and gateway, verify no remaining writer/client/refresh owner, record the latest committed credential generation and all owned IDs, no issuer call while stopped |
| Snapshot | As recorded fixture UID/GID with `umask 077`, SQLite CLI `sqlite3 "$STATE/gateway.db" ".backup '$SNAP/state/gateway.db'"`, require output UID/GID and mode 0600 before hashing; copy complete stopped STATE tree excluding DB/WAL/SHM duplicates, plus CONFIG as `config/gateway.toml` and client trust tree, using mode/owner-preserving `cp -a` below; require DB, KEK and every named/referenced trust file, missing coverage = failure |
| Integrity / hashes | Require backup and newly created `SHA256SUMS` owned by the recorded fixture UID/GID and mode 0600 before hashing, `sqlite3 "$SNAP/state/gateway.db" 'PRAGMA integrity_check;'` exactly `ok`, relative-path manifest covering every payload except itself, `sha256sum -c SHA256SUMS` passes, verify manifest ownership/mode again afterward |
| Restore | Keep all writers stopped, preserve current owned files as originals, verify manifest first, restore DB/config/KEK/trust with original ownership/modes, remove only owned stale WAL/SHM, compare restored payload hashes and run the same integrity command on restored DB |
| Readiness | Restart only the same owned gateway, require authenticated `openshell gateway info` and `openshell sandbox list` success against its private client context, no model/provider use until identity/readiness/current refresh ownership reconciled, old/detached handles remain refused |
| Stale issuer state | Any other owner/issuer rotation or uncertain generation since the checkpoint = no provider replay, remain stopped/detached and require separately approved fresh authorization, filesystem hashes alone not a restored-service pass |

No claim of an upstream stock backup command/doc, this is the explicit stopped-gateway procedure for the dedicated fixture
No restore of shared/host-wide credentials and no claim that stopping/deleting local state revokes the issuer grant

### Mode/owner-preserving stopped copy

Set STATE/CONFIG/SNAP and `<trial-root>/client` only from the approved owned
fixture layout above, never an existing installation.
Before copying, verify every named/referenced payload is a regular non-symlink
file, private roots are 0700, payloads 0600 and ownership matches the fixture UID;
keep the gateway, supervisors, native processes and refresh writers stopped.
Do not follow external symlinks or broaden ownership/modes to make a copy work.

The authorized operator runs the snapshot shell as the recorded numeric
`FIXTURE_UID` and `FIXTURE_GID`, with no default to the operator's own identity.
All destination parents must already have that ownership and exact mode 0700,
without setgid bits, symlinks or pre-existing backup/manifest outputs.
If that identity cannot read the stopped sources or preserve their recorded
ownership, stop; do not broaden permissions or copy shared credentials.
Set the restrictive umask before creating either generated output:

```sh
set -euo pipefail
test "$(id -u)" = "$FIXTURE_UID"
test "$(id -g)" = "$FIXTURE_GID"
umask 077
DB_COPY="$SNAP/state/gateway.db"
test ! -e "$DB_COPY"
test ! -L "$DB_COPY"
sqlite3 "$STATE/gateway.db" \
  ".backup '$DB_COPY'"
chmod 0600 "$DB_COPY"
test "$(stat -c '%u:%g:%a' \
  "$DB_COPY")" = \
  "$FIXTURE_UID:$FIXTURE_GID:600"
```

Run these blocks sequentially in that same private fail-fast Bash shell (`set -euo pipefail`),
with the approved variables already set; a nonzero result stops the procedure.
The backup output has the recorded fixture ownership and mode 0600 before hashing.
Then use `cp -a` for each remaining stopped-state payload/tree, CONFIG and client trust.
Exclude `gateway.db`, `gateway.db-wal` and `gateway.db-shm` from that state-tree
copy, so the `.backup` result is not overwritten by a raw database copy.
For example, once the approved paths and destination parents are verified:

```sh
cp -a "$STATE/tls" "$SNAP/state/"
cp -a "$STATE/key-encryption-key.bin" \
  "$SNAP/state/"
cp -a "$CONFIG" \
  "$SNAP/config/gateway.toml"
cp -a "$TRIAL_ROOT/client" \
  "$SNAP/client"
```

These named files are the minimum, not permission to omit other referenced
configuration/trust or state payloads: enumerate and copy each remaining owned
payload with `cp -a`, preserving relative paths and excluding the DB/WAL/SHM set.
Create the manifest as the same fixture identity with the same `umask 077`,
and verify its UID/GID and 0600 mode before hashing any payload into it:

```sh
cd "$SNAP"
test ! -e SHA256SUMS
test ! -L SHA256SUMS
: > SHA256SUMS
chmod 0600 SHA256SUMS
test "$(stat -c '%u:%g:%a' \
  SHA256SUMS)" = \
  "$FIXTURE_UID:$FIXTURE_GID:600"
find . -type f \
  ! -path ./SHA256SUMS -print0 \
  | sort -z \
  | xargs -0 sha256sum > SHA256SUMS
sha256sum -c SHA256SUMS
test "$(stat -c '%u:%g:%a' \
  SHA256SUMS)" = \
  "$FIXTURE_UID:$FIXTURE_GID:600"
```

This relative-path manifest covers every snapshot payload except itself.
Require integrity result `ok` independently; successful hashing is not that check.
An unsupported `stat`, `sort -z` or other required command stops operator preflight,
not permission to install or substitute a new helper.
On restore, preserve current owned files first and copy each verified snapshot
payload back with `cp -a` to its original absolute path while all writers remain
stopped; remove only the owned stale WAL/SHM before restart.
Compare restored hashes, numeric UID/GID and modes against the recorded ledger;
the restored DB must specifically be `FIXTURE_UID:FIXTURE_GID` and mode 0600,
as must the snapshot DB and manifest before and after verification.
Check every restored config/KEK/trust payload against its recorded UID/GID/mode too;
copy or ownership-preservation failure stops restore, never a successful result.
Restart only the same owned gateway for authenticated readiness checks.
Detached handles must remain refused, with no provider/model use before ownership
and issuer-generation reconciliation; hashes alone are not service readiness.

## Evidence custody and final report

Keep private receipts in `<trial-root>/receipts` and snapshots in
`<trial-root>/snapshots`, directories 0700/files 0600, for seven days after trial
closure, then let the authorized operator delete only recorded owned paths/IDs.
Longer retention requires explicit approval; preserve a redacted outcome report.
Never publish snapshots, JWTs, raw headers, OAuth error bodies, resolver handles,
account IDs or credential values, including in logs/process/RPC diagnostics.
Record versions/digests, generation equality booleans, timestamps/exit codes,
endpoint counts, account-match booleans, refresh commit, cancellation/quiescence,
snapshot integrity and authenticated readiness, not secret-bearing payloads.

The coordinator owns the missing live-trial evidence and exact operator scope.
Before merge, all 11 final-head CI checks must be successful; an in-progress
Build and Test is pending, not failed, and prior-head green checks are not final-head proof.
An unexpected outcome ends qualification with `blocked:<first reason>` and
remaining checks UNRUN; only matching predeclared negative assertions are passes.
No retries, opportunistic repairs, unrelated source rewrites or normal-agent
migration follow a failed trial; real invalid_grant/uncertain rotation requires
separately approved fresh human login for a new trial.
Cancel, detach, local cache deletion and placeholder invalidation are not
issuer-side OAuth grant revocation; record their distinct outcomes and custody.

## Prohibited changes

| constraint | why |
|---|---|
| MUST NOT copy or reuse an existing agent's rotating login cache | A second refresh owner can burn the current grant and disrupt unchanged agents |
| MUST NOT place a usable OAuth access/refresh/ID token or provider-control credential in workload files, env, process arguments or app-server messages | Shell-readable native credentials are not credential isolation |
| MUST NOT feed an opaque stock handle to the native external-token RPC or wrap it in a fabricated access JWT | The pinned constructor parses JWT claims, and changing the token shape breaks stock ownership/substitution |
| MUST NOT treat the local ID-token parsing stub, editable account setting or plan label as authorization | Only the real outside grant and stock/issuer enforcement can establish entitlement and account scope |
| MUST NOT add a custom proxy, fork, token-conversion service or refresh framework if the native cache route fails | Failure at these pins is a design blocker, not authority to redesign the boundary |
| MUST NOT enable automatic host-credential discovery, expose native remote listeners or widen endpoints on failure | Explicit environment-only import into disposable P/F providers is the sole exception, never existing provider/account authority |
| MUST NOT call detach, local logout, cancel or placeholder invalidation issuer-side OAuth revocation | Upstream grant revocation has a separate authority and lifecycle |
| MUST NOT migrate subscription agents, change API billing or rewrite #497/#493 acceptance | This design neither changes existing auth owners nor waives their independent gates |

## Acceptance ledger (A0–A9)

All commands below planned only after the design and exact isolated operator scope are approved, with secrets supplied by protected references rather than arguments or public fixtures

| id | command / explicit manual check | expected result |
|---|---|---|
| A0 | `openshell --version`, `codex --version`, `sqlite3 --version`, binary SHA-256 and gateway/supervisor/runtime image inspections | Exact selected pin ledger, no missing identity or tag-only claim, otherwise immediate blocked outcome |
| A1 | `openshell provider profile lint --file deploy/openshell/codex-subscription.yaml` | Exit 0, gateway-only refresh material, only access handle published, exact endpoints and actual native binary allowlist |
| A2 | Inspect pinned native source/cache schema before the trial, then use a fresh synthetic cache with network denied at the fixture boundary and initialize only, no account/model request | ID-token metadata stub accepted without a real token, API key/refresh token absent, direct opaque external-token RPC recorded as incompatible, no login/refresh/model request from this synthetic check |
| A3 | Under separately authorized P grant, verify operator metadata file/cache ownership and values privately, initialize/account-read and one model turn within 120 seconds | Correct selected account and exact handle cache, required traffic only at admitted paths, non-credential optional telemetry denials recorded without blocking successful work, no real token in workload files/env/logs/process/RPC and no workload refresh |
| A4 | P: one `openshell provider refresh rotate <provider> --credential-key CODEX_AUTH_ACCESS_TOKEN`, observe `provider refresh status` within 60 seconds, native process unchanged; F: synthetic invalid refresh material through the same command, then expire its synthetic access credential using stock `provider update --credential-expires-at` | P: committed new generation, empty refresh error and next native request succeeds through the same opaque handle, queued/exit-0 alone not pass; F: predeclared failed-refresh state and expired-handle refusal with zero credential/model forwarding = passing negative probes, no real grant consumed, any real invalid_grant needs separately approved re-login for a new trial |
| A5 | P: foreign-sandbox, wrong-account, foreign origin/path, redirect and TLS-skip/direct-egress probes; F: retain a handle then explicitly reconfigure refresh once to create a new authorization epoch | Every predeclared unauthorized probe refused within 30 seconds without credential forwarding/model turn, old F handle rejected at stock boundary rather than merely upstream 401, unknown outside account enforcement or an unexpected outcome blocks the whole qualification |
| A6 | Cancel an active native turn, then detach provider while the process/connection is retained | Cancel acknowledgement within 30 seconds, detach acknowledgement within 30 seconds and verified stopped/quiescent state within 120 seconds overall, cancellation distinct from credential revoke, no later authenticated request through detached/old handles, issuer grant not falsely reported revoked |
| A7 | P after A6: perform the exact stopped snapshot/restore table in the stopped snapshot/restore section and verify authenticated gateway readiness and old-handle refusal | Dedicated state only, required DB/KEK/trust coverage complete, hashes match and integrity `ok`, latest credential-generation/owner reconciled, detached state not resurrected and no blind issuer-token replay |
| A8 | After P/F owned cleanup, compare fresh/default-off and existing API-key/manual configuration, gateway/provider/state mappings and runtime identity baselines | No P/F gateway, supervisor, state directory, volume, key, DB, client trust or provider shared with existing agents/providers, no existing config/credential/refresh/workflow/runtime/grant/billing change |
| A9 | Independent security/ops/code review of exact P/F ledger in the declared order, including expected-refusal assertions and final baseline | All required positive steps and matching expected-negative probes pass before qualification, intentional denials not trial failures, first unexpected result blocks and leaves remaining checks unrun, no fallback/managed implementation |


### A4 exact refresh/failure recipe

P profile uses `refresh_before_seconds=300` / `max_lifetime_seconds=3600`, initial recorded expiry in the future, one manual stock rotate trigger before the due window, a 60-second status/commit bound and one post-commit request from the unchanged native process
F uses only deliberately synthetic non-grant values, including refresh token `qualification-invalid-not-a-grant`, valid pinned public client ID and the same real issuer token URL, no real account login or grant copy
In F, seed future expiry, configure refresh and retain H1, reconfigure once for the A5 epoch-refusal probe, obtain the supervisor-issued current H2 only after acknowledgement and prove H1 refused at stock before forwarding, then request one manual rotate and require the recorded failed-refresh/reauthorization outcome, not a success inferred from CLI exit 0
Finally set `CODEX_AUTH_ACCESS_TOKEN` expiry to an explicit past RFC3339 timestamp with stock `provider update --credential-expires-at`, require current-generation H2 to be refused before forwarding rather than reusing already-revoked H1 as the expiry probe, then close F without repair/re-login/reuse
No production/real grant is deliberately invalidated, P needs no re-login solely because F's synthetic test failed as expected, any real invalid_grant or ambiguous token rotation ends its fixture and requires a separately approved fresh human login for any new trial
