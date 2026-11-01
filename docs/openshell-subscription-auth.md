# Codex subscription authentication qualification

**UNQUALIFIED — documentation and an operator-imported profile, not a supported production mode.**
Refs [#498](https://github.com/anurmatov/phleet/issues/498).
Current approved contract:
`36ab8279672befe3d022095b55ce5f2762d7e5decc53971f3e8add9216b91af2`.
Approval history, not current trial instructions: HTTP-descriptor baseline
`30994a396679bae842c3a6cf16793ff650f514feb7a4a2493068ad264cc53c89`
and earlier docs/profile body
`e960764c036d6767a6c0538272f4af79ddf562c284851da8ec11aeef8904f4d4`.
This adopts the prospective suppression/denial contract in the guide shipped in
[PR #499](https://github.com/anurmatov/phleet/pull/499), without reopening qualification.
Historical native HTTP/catalog success remains an observation, not qualified A3
or acceptance of these denials; this amendment cannot retroactively relabel it.
All prospective A0–A9 qualification outcomes remain **UNRUN** in this source patch.
UNRUN does not erase historical observations or turn a failed attempt into a pass.
A source review, profile check or merge cannot qualify native credential isolation.
**Transport candidate: source-supported stock HTTP descriptor only.**
Prospective loaded-configuration and native HTTP checks remain UNRUN here, with no
credential/native action authorized by this documentation change.
Later amendment: [#502 native-memory compatibility path](#502-native-memory-compatibility-amendment)
adds a managed subscription branch; the history below is retained unchanged and still UNQUALIFIED.

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

### Native TOML / stock HTTP descriptor

Retain the forced login/workspace selection and select a non-reserved descriptor in the unmodified pinned native client, no built-in `openai` override and no new provider code

```toml
forced_login_method = "chatgpt"
forced_chatgpt_workspace_id = "<selected-account-id>"
model_provider = "openai-http"

[model_providers.openai-http]
name = "OpenAI"
requires_openai_auth = true
supports_websockets = false
supports_standalone_web_search = true
http_headers = { version = "0.164.0" }
```

For the prospective trial, append the separate native tables in
[Pinned suppression and per-family bounds](#pinned-suppression-and-per-family-bounds)
to this same config after the descriptor, never as descriptor fields.

Descriptor fields `base_url`, `env_key`, `experimental_bearer_token`, `auth` and `aws` **ABSENT**, not blank, null-like or supplied by an inherited override, preserving the cached subscription-auth path rather than introducing another auth source
`wire_api` omitted deliberately, pinned default `Responses`, public version header and standalone-web-search capability copied from the built-in descriptor, capability not permission to make additional requests or widen the three-endpoint policy

| pinned source | source-supported consequence / remaining limit |
|---|---|
| [Descriptor merge](https://github.com/openai/codex/blob/6aaac694803004a63c6f393d153c96d22521a6b9/codex-rs/model-provider-info/src/lib.rs#L696-L732) and [selection](https://github.com/openai/codex/blob/6aaac694803004a63c6f393d153c96d22521a6b9/codex-rs/core/src/config/mod.rs#L3972-L3990) | Non-reserved `openai-http` inserted by stock `or_insert`, selected by `model_provider`, unlike an ignored built-in OpenAI table override |
| [AuthManager and cached auth](https://github.com/openai/codex/blob/6aaac694803004a63c6f393d153c96d22521a6b9/codex-rs/model-provider/src/auth.rs#L236-L271) | Absent `auth` retains the base `AuthManager`, absent bearer/env key plus `requires_openai_auth=true` selects cached `CodexAuth`, existing placeholder-bearing `ChatgptAuthTokens` cache and single outside stock refresh owner retained, no OAuth-to-API-key conversion |
| [First-party auth/account path](https://github.com/openai/codex/blob/6aaac694803004a63c6f393d153c96d22521a6b9/codex-rs/model-provider/src/provider.rs#L335-L341) and [account state](https://github.com/openai/codex/blob/6aaac694803004a63c6f393d153c96d22521a6b9/codex-rs/model-provider/src/provider.rs#L519) | Descriptor follows the existing first-party/account state path in source, real selected-account discovery/authority still unrun and mandatory |
| [Cached mode and backend](https://github.com/openai/codex/blob/6aaac694803004a63c6f393d153c96d22521a6b9/codex-rs/model-provider/src/provider.rs#L215) plus [base-URL selection](https://github.com/openai/codex/blob/6aaac694803004a63c6f393d153c96d22521a6b9/codex-rs/model-provider-info/src/lib.rs#L429-L447) | `ChatgptAuthTokens` and absent `base_url` select `https://chatgpt.com/backend-api/codex`, not the usage-based API endpoint, no billing-mode change authorized |
| [OpenAI name predicate](https://github.com/openai/codex/blob/6aaac694803004a63c6f393d153c96d22521a6b9/codex-rs/model-provider-info/src/lib.rs#L618-L629) and [model catalog](https://github.com/openai/codex/blob/6aaac694803004a63c6f393d153c96d22521a6b9/codex-rs/model-provider/src/models_endpoint.rs#L214-L217) | `is_openai()` keys on exact `name="OpenAI"`, not descriptor ID, preserving the Codex-backend catalog branch in source, actual catalog resolution under a stock handle still unrun |
| [Internal metadata](https://github.com/openai/codex/blob/6aaac694803004a63c6f393d153c96d22521a6b9/codex-rs/model-provider/src/provider.rs#L121-L128) and [built-in parity](https://github.com/openai/codex/blob/6aaac694803004a63c6f393d153c96d22521a6b9/codex-rs/model-provider-info/src/lib.rs#L541-L564) | HTTPS chatgpt.com origin enables internal metadata in source, no authored `include_internal_metadata` field because it is `serde(skip)`, pinned version header/web-search capability copied, no new endpoint authority |
| [WebSocket guard](https://github.com/openai/codex/blob/6aaac694803004a63c6f393d153c96d22521a6b9/codex-rs/core/src/client.rs#L1146-L1154), [stream selection](https://github.com/openai/codex/blob/6aaac694803004a63c6f393d153c96d22521a6b9/codex-rs/core/src/client.rs#L2403-L2445) and [Responses path](https://github.com/openai/codex/blob/6aaac694803004a63c6f393d153c96d22521a6b9/codex-rs/codex-api/src/endpoint/responses.rs#L67) | Selected descriptor `supports_websockets=false` takes `stream_responses_http`, POST `/responses` fits existing `POST /backend-api/codex/responses`, no WSS GET upgrade admitted, actual effective selection/HTTP turn still unrun |
| [API-key cyber-program ID exception](https://github.com/openai/codex/blob/6aaac694803004a63c6f393d153c96d22521a6b9/codex-rs/core/src/cyber_access_program.rs#L22-L36) | Non-`openai` descriptor ID makes `ApiKeyCyberAccessPrograms` report `UnsupportedProvider`, API-key-program behavior not subscription-turn parity, not in this candidate's scope |

Source proof only, not an effective-config dump, native initialization/HTTP turn, stock-handle account/catalog/refresh test or isolation pass
No real OAuth token in the proposed workload cache remains a design requirement, prior native observations do not establish this amendment's qualification, no new A0–A9 result or historical passing relabel is claimed and UNQUALIFIED status retained
The approved amendment is adopted here as documentation only; independent PR review and human merge approval remain required, with a separate exact operator scope before any credential or native action

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

Chosen candidate only: the stock example's **native cache route** with the stock `openai-http` configuration descriptor above, all usable OAuth material outside the workload and the stock gateway as sole refresh owner, not a provider implementation, external-token RPC route or OAuth-to-API-key conversion

| handoff stage | exact contract / stop condition |
|---|---|
| Pins | Unmodified OpenShell CLI/gateway/supervisor/runtime 0.1.5, source `bdb956f2cd65a635f1294fccf1d138d263bf650f`; Codex 0.164.0, annotated release resolved to `6aaac694803004a63c6f393d153c96d22521a6b9`; operator-recorded platform binary SHA-256 and image digests before any run; missing/mismatched identity = stop |
| Outside grant | One separately authorized human login into a fresh outside-owned cache for primary fixture P only, no copying an existing agent grant and no second native client sharing it, private operator custody with directory 0700/files 0600, synthetic-only fixture F with no real login or grant |
| Provider import | Separate operator authorization may create/configure/import/delete only the disposable stock providers in P/F, never an external account or an existing provider, access material supplied through explicitly selected environment-only discovery rather than credential-valued arguments, enrollment client stopped before gateway ownership, refresh material only through stock `--secret-material-env`, no shell tracing/history/value output |
| Refresh owner | Stock gateway alone, `oauth2-refresh-token`, `refresh_before_seconds: 300`, `max_lifetime_seconds: 3600`, `https://auth.openai.com/oauth/token`, native OAuth client ID `app_EMoamEEZ73f0CkXaXp7hrann` from [pinned CLIENT_ID](https://github.com/openai/codex/blob/6aaac694803004a63c6f393d153c96d22521a6b9/codex-rs/login/src/auth/manager.rs#L1783-L1790), no client-ID override, native refresh token or parallel refresh owner |
| Workload cache | Private non-root native home, cache 0600, `auth_mode="chatgptAuthTokens"`, `OPENAI_API_KEY=null`, existing forced ChatGPT/workspace config plus exact stock `openai-http` descriptor above, access token equal to the exact supervisor-issued handle, empty refresh token, no real ID token and no credential source mount |
| Local parsing | Non-secret ID-token-shaped metadata stub following the stock example, used only for native parsing, never sent as authentication or trusted as proof of issuer identity/plan/entitlements; unaccepted stub or a required real access-token JWT = stop |
| Account metadata | Authorized operator writes `{ "account_id": "<selected-account-id>" }` to `<trial-root>/inputs/account-metadata.json` as 0600 under a 0700 directory, uploads only this non-secret file with `openshell sandbox upload <sandbox> <local-file> <native-home>/.codex/account-metadata.json`, verifies workload UID ownership/mode 0600, then invokes a non-root one-shot cache writer to read that file and the supervisor-issued access handle, atomically write `auth.json` 0600 with `tokens.account_id` from that metadata and a separate native `config.toml` with the same `forced_chatgpt_workspace_id` before native startup, no secret/cache source upload or ID in public evidence |
| Account authority | Outside-owned grant and policy binding to that one account, with mandatory cross-account refusal test; native `forced_chatgpt_workspace_id` alone not a security boundary because workload configuration is writable; absence of a stock/issuer-enforced boundary = stop |
| Native client | Existing Codex app-server over private stdio, no exposed WebSocket listener or remote client; no `account/login/start` injection of opaque access handles and no auth-refresh callback returning real tokens |
| Endpoint boundary | Stock-inspected HTTPS only to `chatgpt.com:443`: `GET /backend-api/wham/accounts/check`, `GET /backend-api/codex/models`, `POST /backend-api/codex/responses`; no wildcard provider endpoint, API-key endpoint, direct egress or TLS-skip route |
| Transport/routing | Source-supported stock `openai-http` descriptor above selects HTTP Responses/SSE in pinned source, prior native observations do not qualify this amendment and remaining checks stay UNRUN, no adoption before reviewed human-approved amendment and exact operator authority, extra bootstrap paths, WebSocket-only behavior, redirects or another origin remain blocked, no WSS GET/endpoint widening, optional non-credential denials only under the explicit rule below |
| Long-lived use | Stable handle resolves to the currently committed gateway token after refresh without restarting the workload; a changed account, refresh owner, provider configuration epoch or endpoint set requires detach and a new authorization, not silent rebinding |

Source evidence supports descriptor merge/auth/routing/HTTP branches plus the existing cache/refresh primitives, not an observed effective configuration, native discovery/catalog/HTTP turn, account isolation, refresh, cancellation or restore pass at the combined pins


## Trial isolation and planned outcomes

| fixture | ownership / allowed use |
|---|---|
| P — primary | Dedicated throwaway gateway, supervisor instances, network, credential store and state directory, shared with no existing gateway/provider/workload, one fresh outside-owned real grant only |
| F — refusal | A second independently authorized throwaway gateway/supervisor/state/network and synthetic provider values only, never a copy/restore of P or an existing grant, never a real model turn |
| Target identity | Separate explicit absent `<trial-root>` per fixture, no shared volume, DB, key, trust file, port or provider, exact owned IDs recorded before mutations, unknown/pre-existing target = stop |
| Expected refusal | A predeclared negative input plus its exact refusal, timeout bound and no-forwarding/no-secret assertions all satisfied = **passing negative probe**, nonzero command status alone neither pass nor unexpected failure |
| Unexpected failure | Any nonmatching refusal/status, missing assertion/receipt, timeout outside the declared outcome, possible credential forwarding or positive-step failure = stop the whole qualification, remaining checks unrun, no repair/retry or passing qualification |
| Optional egress | Deny `ab.chatgpt.com` and all other non-allowlisted telemetry/flag/feedback/update origins, record only sanitized destination/count and absence of credential/handle forwarding, non-blocking only if A3/A4 native work still succeeds and the denied request is proven non-credential, otherwise blocked |
| Same-origin denied paths | All seven exact method/path families stay denied, suppress six with the pinned native controls below and require zero positive-phase emissions, only the bounded settings family is eligible for source-backed expected-denial classification, missing proof or suppression drift blocks |

## Prospective same-origin denied-path contract

This changes prospective qualification only, never the three allowed routes, no WSS GET, wildcard or endpoint/profile expansion
Prefer native suppression over classifying traffic as harmless, all seven families remain denied by stock policy and historical observations remain unqualified
The complete design inputs below replace operator-chosen optionality, field and size limits, an operator selects owned resource identities only, not new bounds or exceptions

### Pinned suppression and per-family bounds

For a future separately authorized trial only, set top-level native `analytics.enabled=false`, `features.plugins=false` and `features.apps=false` outside the unchanged `model_providers.openai-http` descriptor table
Use separate native tables after the unchanged descriptor, never nested descriptor fields

```toml
[analytics]
enabled = false

[features]
plugins = false
apps = false
```

Use existing private initialized `config/read` with `includeLayers:true` to compare those three loaded values as Boolean false and check active layer/origin precedence, require loaded `mcp_servers` absent, JSON null or empty object and no selected-plugin, MCP or thread-level override in the owned native request inputs, any registration/unknown input or unavailable/mismatched value blocks before the positive live phase, no new RPC or capture tool
These native trial settings do not change the descriptor, authentication/refresh owner, stock profile, endpoints or any existing agent, and are not applied by this design task
Six suppressed families have an accepted native-positive request count of zero and therefore zero emitted payload bytes, any occurrence is blocked suppression drift rather than an optional denial, unexecuted suppressed paths are not negative-test passes

| denied method/path family | pinned construction and incoming-auth evidence | fixed payload/query bounds and selected suppression |
|---|---|---|
| GET `/backend-api/ps/plugins/installed` | [`codex-rs/core-plugins/src/remote.rs:2272–2301`](https://github.com/openai/codex/blob/6aaac694803004a63c6f393d153c96d22521a6b9/codex-rs/core-plugins/src/remote.rs#L2272-L2301) plus common auth trace below | Source GET body 0 bytes, query keys only `scope`, `limit`, `includeDownloadUrls`, `pageToken`, limit `200`, scope `GLOBAL/USER/WORKSPACE` or absent, pagination is server-derived so no classified retry allowed, mandatory `features.plugins=false`, native requests 0 |
| GET `/backend-api/ps/plugins/suggested/codex` | [`codex-rs/core-plugins/src/remote.rs:1040–1057`](https://github.com/openai/codex/blob/6aaac694803004a63c6f393d153c96d22521a6b9/codex-rs/core-plugins/src/remote.rs#L1040-L1057) plus common auth | Source GET body 0 bytes, sole query `scope=GLOBAL` (12 ASCII bytes), mandatory `features.plugins=false`, native requests 0 |
| GET `/backend-api/ps/plugins/list` | [`codex-rs/core-plugins/src/remote.rs:2222–2247`](https://github.com/openai/codex/blob/6aaac694803004a63c6f393d153c96d22521a6b9/codex-rs/core-plugins/src/remote.rs#L2222-L2247) plus common auth | Source GET body 0 bytes, query keys only `scope`, `limit`, optional `collection/pageToken`, limit `200`, dynamic collection/pageToken not approved for classification, mandatory `features.plugins=false`, native requests 0 |
| GET `/backend-api/plugins/featured` | [`codex-rs/core-plugins/src/remote_legacy.rs:125–161`](https://github.com/openai/codex/blob/6aaac694803004a63c6f393d153c96d22521a6b9/codex-rs/core-plugins/src/remote_legacy.rs#L125-L161) attaches first-party auth when present | Source GET body 0 bytes, sole `platform` query from pinned Product mapping, mandatory `features.plugins=false`, native requests 0, no operator-selected platform variant |
| GET `/backend-api/wham/settings/user` | [`codex-rs/backend-client/src/client.rs:547–563`](https://github.com/openai/codex/blob/6aaac694803004a63c6f393d153c96d22521a6b9/codex-rs/backend-client/src/client.rs#L547-L563) and [`codex-rs/backend-client/src/client.rs:269–292`](https://github.com/openai/codex/blob/6aaac694803004a63c6f393d153c96d22521a6b9/codex-rs/backend-client/src/client.rs#L269-L292) | Eligible bounded denial only, body 0 bytes, query absent (0 bytes), at most one request per A3 and one per A4 native-positive phase, auth/metadata bounds below, no proven suppressing config key in selected attribution registration/policy, do not guess one |
| POST `/backend-api/codex/analytics-events/events` | [`codex-rs/analytics/src/client.rs:1035–1088`](https://github.com/openai/codex/blob/6aaac694803004a63c6f393d153c96d22521a6b9/codex-rs/analytics/src/client.rs#L1035-L1088) constructs `{events}` and first-party auth | Dynamic event fields/content have no approved nonzero safe bound in this trial, mandatory `analytics.enabled=false`, native requests/body bytes 0, an emitted event is blocked, no capture-file/debug workaround |
| POST `/backend-api/ps/mcp` | [`codex-rs/codex-mcp/src/mcp/mod.rs:636–706`](https://github.com/openai/codex/blob/6aaac694803004a63c6f393d153c96d22521a6b9/codex-rs/codex-mcp/src/mcp/mod.rs#L636-L706) selects ChatGPT-authenticated StreamableHttp | MCP protocol messages/tool payloads have no approved nonzero safe bound in this trial, mandatory `features.apps=false` with plugins disabled and no configured/selected MCP servers, native requests/body bytes 0, any emission blocks, no tools/call exception |

Canonical path excludes the query, the table's named query construction is source context only for suppressed families, not permission to send them, the eligible settings request allows no query key or value, encoded/unknown variants block

Suppression source: plugin configuration reads `Feature::Plugins` in [`codex-rs/core/src/config/mod.rs:1788–1799`](https://github.com/openai/codex/blob/6aaac694803004a63c6f393d153c96d22521a6b9/codex-rs/core/src/config/mod.rs#L1788-L1799), plugin loading and inventory/catalog scheduling return early in [`codex-rs/core-plugins/src/manager.rs:822–825`](https://github.com/openai/codex/blob/6aaac694803004a63c6f393d153c96d22521a6b9/codex-rs/core-plugins/src/manager.rs#L822-L825), [`codex-rs/core-plugins/src/manager.rs:1575–1585`](https://github.com/openai/codex/blob/6aaac694803004a63c6f393d153c96d22521a6b9/codex-rs/core-plugins/src/manager.rs#L1575-L1585) and [`codex-rs/core-plugins/src/manager.rs:1790–1799`](https://github.com/openai/codex/blob/6aaac694803004a63c6f393d153c96d22521a6b9/codex-rs/core-plugins/src/manager.rs#L1790-L1799), suggestions/featured guards in [`codex-rs/core-plugins/src/manager.rs:1883–1924`](https://github.com/openai/codex/blob/6aaac694803004a63c6f393d153c96d22521a6b9/codex-rs/core-plugins/src/manager.rs#L1883-L1924)
Apps suppression maps config to `apps_enabled` in [`codex-rs/core/src/config/mod.rs:1927`](https://github.com/openai/codex/blob/6aaac694803004a63c6f393d153c96d22521a6b9/codex-rs/core/src/config/mod.rs#L1927) and removes the implicit Apps registration in [`codex-rs/core/src/mcp.rs:458–479`](https://github.com/openai/codex/blob/6aaac694803004a63c6f393d153c96d22521a6b9/codex-rs/core/src/mcp.rs#L458-L479), the isolated trial has no installed/selected plugin or manually configured MCP registration that could reintroduce it
Analytics suppression: [`codex-rs/config/src/types.rs:244–252`](https://github.com/openai/codex/blob/6aaac694803004a63c6f393d153c96d22521a6b9/codex-rs/config/src/types.rs#L244-L252), [`codex-rs/core/src/config/mod.rs:4664`](https://github.com/openai/codex/blob/6aaac694803004a63c6f393d153c96d22521a6b9/codex-rs/core/src/config/mod.rs#L4664) and [`codex-rs/analytics/src/client.rs:314–328`](https://github.com/openai/codex/blob/6aaac694803004a63c6f393d153c96d22521a6b9/codex-rs/analytics/src/client.rs#L314-L328) disable queue creation, with the per-session guard in [`codex-rs/core/src/session/session.rs:1672–1685`](https://github.com/openai/codex/blob/6aaac694803004a63c6f393d153c96d22521a6b9/codex-rs/core/src/session/session.rs#L1672-L1685), config suppression is source-supported, effective/live suppression remains unproven until the prospective check
Settings optionality: [`codex-rs/ext/git-attribution/src/policy.rs:70–98`](https://github.com/openai/codex/blob/6aaac694803004a63c6f393d153c96d22521a6b9/codex-rs/ext/git-attribution/src/policy.rs#L70-L98) maps a non-401 failure to no attribution policy, [`codex-rs/ext/git-attribution/src/lib.rs:52–85`](https://github.com/openai/codex/blob/6aaac694803004a63c6f393d153c96d22521a6b9/codex-rs/ext/git-attribution/src/lib.rs#L52-L85) contributes attribution disabled, the selected [`codex-rs/ext/git-attribution/src/lib.rs:97–109`](https://github.com/openai/codex/blob/6aaac694803004a63c6f393d153c96d22521a6b9/codex-rs/ext/git-attribution/src/lib.rs#L97-L109) registration supplies no native config disable, no claim that `analytics=false` suppresses this separate path

### Accepted stock evidence, not header/body capture

Stock supervisor OCSF HTTP Activity / `L7_REQUEST` receipts contain route/verdict, not headers or bodies, do not infer auth absence or measured payload sizes from them
Per-family pinned construction/header-attachment traces in this body are explicitly accepted as source-derived evidence of incoming shape under the verified isolated inputs, never described as a wire capture
The already required private cache/account/forced-workspace equality check must show only the current supervisor handle in `ChatgptAuthTokens`, empty refresh token, non-secret parsing stub and selected account metadata, no browser cookies, ambient auth/header helper or additional custom auth/header input beyond the unchanged public descriptor version and pinned non-secret client defaults, unknown extra input blocks
Common auth trace: [`codex-rs/core-plugins/src/remote.rs:2375–2390`](https://github.com/openai/codex/blob/6aaac694803004a63c6f393d153c96d22521a6b9/codex-rs/core-plugins/src/remote.rs#L2375-L2390) attaches [`codex-rs/model-provider/src/auth.rs:355–373`](https://github.com/openai/codex/blob/6aaac694803004a63c6f393d153c96d22521a6b9/codex-rs/model-provider/src/auth.rs#L355-L373) first-party auth, [`codex-rs/model-provider/src/bearer_auth_provider.rs:31–46`](https://github.com/openai/codex/blob/6aaac694803004a63c6f393d153c96d22521a6b9/codex-rs/model-provider/src/bearer_auth_provider.rs#L31-L46) builds `Authorization: Bearer <current-handle>` and optional selected `ChatGPT-Account-ID`, settings uses this through [`codex-rs/backend-client/src/client.rs:233–241`](https://github.com/openai/codex/blob/6aaac694803004a63c6f393d153c96d22521a6b9/codex-rs/backend-client/src/client.rs#L233-L241) and its headers builder above
The eligible settings request therefore has no body/query/prompt/file field, auth value length exactly `7 + current_handle_byte_length`, account metadata value at most the already verified selected-account-ID byte length, public non-secret client headers only, no additional private field, raw value, content hash or arbitrary operator bound
Record `source_shape_match`, `cache_handle_match`, `account_metadata_match` and `bounds_match` booleans only, the existing private checks provide input equality without printing values, if that source/input conjunction cannot establish shape or isolation, stop rather than add a capture tool
For the six suppressed families, the accepted observation is the verified loaded suppression values plus a complete owned-supervisor interval with zero such native request events, not a fabricated per-request header/body measurement, any emitted request remains blocked regardless of apparent safety

### Accepted denial-before-substitution/forwarding proof

For the eligible settings family require the conjunction of (1) a stock OCSF HTTP Activity event whose `L7_REQUEST deny GET chatgpt.com:443/backend-api/wham/settings/user` message has reason `no L7 endpoint path matched request`, Denied/Blocked disposition, and (2) the exact-pinned unmatched-route return in [`crates/openshell-supervisor-network/src/l7/relay.rs:1219–1232`](https://github.com/NVIDIA/OpenShell/blob/bdb956f2cd65a635f1294fccf1d138d263bf650f/crates/openshell-supervisor-network/src/l7/relay.rs#L1219-L1232) with no matching configured route, and (3) complete stock supervisor route receipts for the owned positive-phase interval with zero matching allow/audit events or contrary upstream/forward outcomes
The stock event producer is [`crates/openshell-supervisor-network/src/l7/relay.rs:1649–1715`](https://github.com/NVIDIA/OpenShell/blob/bdb956f2cd65a635f1294fccf1d138d263bf650f/crates/openshell-supervisor-network/src/l7/relay.rs#L1649-L1715), the local deny response is 403 via [`crates/openshell-supervisor-network/src/l7/rest.rs:2802–2868`](https://github.com/NVIDIA/OpenShell/blob/bdb956f2cd65a635f1294fccf1d138d263bf650f/crates/openshell-supervisor-network/src/l7/rest.rs#L2802-L2868), not an issuer 401/403, the forwarding path is later at [`crates/openshell-supervisor-network/src/l7/relay.rs:1522–1539`](https://github.com/NVIDIA/OpenShell/blob/bdb956f2cd65a635f1294fccf1d138d263bf650f/crates/openshell-supervisor-network/src/l7/relay.rs#L1522-L1539)
This source-plus-enforced-event conjunction is the accepted proof that the denied request never reaches forwarding/substitution, not a claim of a nonexistent stock per-request byte counter, any available stock upstream accounting must also show zero matching forwarded requests, unavailable route coverage or contradictory evidence blocks
Use only the already available complete supervisor stdout/OCSF receipt from startup through owned cleanup, no logging-level change, new trace/capture tool, raw HTTP dump or upstream credential inspection, absent/rotated/unreadable receipts block rather than count as zero

A3 evaluates its own positive step and eligible denial within 120 seconds before A4 starts, A4 independently applies the same proof to its post-commit request under its existing 60-second bound, A9 needs both, no future A4 result is a prerequisite for finishing A3
Per-family source refs, counts and equality/shape/bounds booleans are the only redacted evidence, any needed real account ID/private input receipt stays under existing 0700/0600 seven-day custody, no public payload, header, raw query, handle or evidence upload
Only the settings family may be an expected same-origin denial in this amendment, absent requests are UNRUN observations rather than tested denials, suppressed-family traffic, source/config/input drift, wrong reason/status, missing proof or any other unexpected live failure stops immediately with remaining checks UNRUN
The zero-native-emission rule concerns positive A3/A4 work, it does not silently rewrite separately predeclared A5 negative probes, no automatic issuer/auth/model retry, endpoint widening, workaround framework or retrospective passing label

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
| HTTPS/TLS failure, redirect, outside origin/path, unknown routing or unsupported transport | Required auth/model/bootstrap traffic: no forwarding, stop on unexpected failure, no downgrade/broadening, A5 intentional refusals, the existing optional-origin rule and only prospectively predeclared same-origin denials may pass their full stated assertions, any missing optionality, incoming-auth/payload classification or no-forward/substitution proof blocks |
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
| MUST NOT set descriptor `base_url`, `env_key`, `experimental_bearer_token`, `auth` or `aws`, change exact `name="OpenAI"` or replace the descriptor ID with built-in `openai` | Those changes break the traced cache/auth/backend/catalog or merge selection, requiring fresh design rather than implied equivalence |
| MUST NOT treat the stock descriptor table as a new provider implementation or source proof as observed isolation | Unmodified stock parsing is the only selected path, real effective-config/native/account/catalog/refresh evidence remains required |
| MUST NOT enable API-key cyber programs or add WSS GET/web-search endpoints through this descriptor | The provider-ID API-key exception and capability flags grant no subscription or network scope beyond the existing three routes |
| MUST NOT allow any of the seven denied families, classify traffic required to be suppressed as expected, or infer safe headers/bodies from stock route logs | Native suppression is preferred, only the settings exception has fixed source-backed bounds and an accepted enforced-denial proof |
| MUST NOT relabel a historical run, an unknown request or a missing receipt as an expected optional denial | Independent exact-body review/human approval and a separately scoped prospective qualification cannot be replaced by observed native success |

## Acceptance ledger (A0–A9)

All commands below planned only after the design and exact isolated operator scope are approved, with secrets supplied by protected references rather than arguments or public fixtures

| id | command / explicit manual check | expected result |
|---|---|---|
| A0 | `openshell --version`, `codex --version`, `sqlite3 --version`, binary SHA-256 and gateway/supervisor/runtime image inspections, compare authored native TOML with the exact descriptor above before any trial | Exact selected pins and descriptor fields match, the reported Codex version component must equal the descriptor `http_headers.version` literal and both must be `0.164.0`, all five forbidden fields absent, any mismatch/drift/missing identity blocks before trial, not proof of effective native behavior |
| A1 | `openshell provider profile lint --file deploy/openshell/codex-subscription.yaml` | Exit 0, gateway-only refresh material, only access handle published, exact endpoints and actual native binary allowlist |
| A2 | With fresh synthetic cache and network denied, complete `initialize` / `initialized` over authorized private stdio, then call stock `config/read` with `includeLayers:true` and the owned `cwd`, using the exact observation below | Returned loaded `model_provider`, required descriptor fields and forced login/workspace keys match, analytics/plugins/apps suppression values Boolean false with owned user-file origins and active layer/origin precedence, `mcp_servers` absent/null/empty object and no selected-plugin/MCP/thread-level override, five optional base-URL/alternate-auth fields unset as missing keys or JSON `null`, layer/origin conflicts or missing required fields block, ID-token stub accepted without real token and no login/model call, full response stays private with public match booleans only, loaded configuration not proof of HTTP selection, A3 must observe actual POST and no WSS GET |
| A3 | Under separately authorized P grant and approved descriptor, verify private account metadata/cache values, initialize/account-read, actual catalog resolution and one native HTTP Responses turn within 120 seconds | Correct selected account/catalog and exact handle cache, HTTP POST selected with only existing GET account/GET models/POST responses routes and no WSS GET, source name/catalog/internal-metadata routing verified by secret-free observations, loaded native suppression values false, six suppressed families have zero native request events, existing optional-origin denials and only the bounded settings denial meet the exact source/input/enforced-event conjunction above, unknown or missing evidence blocks at first unexpected failure, no real token in workload files/env/logs/process/RPC and no workload refresh, historical observations not retroactively qualified |
| A4 | P: one `openshell provider refresh rotate <provider> --credential-key CODEX_AUTH_ACCESS_TOKEN`, observe `provider refresh status` within 60 seconds, native process unchanged; F: synthetic invalid refresh material through the same command, then expire its synthetic access credential using stock `provider update --credential-expires-at` | P: committed new generation, empty refresh error and next native request succeeds through the same opaque handle, queued/exit-0 alone not pass, P post-commit requests apply the same prospectively predeclared denial evidence as A3 without new paths or issuer retries; F: predeclared failed-refresh state and expired-handle refusal with zero credential/model forwarding = passing negative probes, no real grant consumed, any real invalid_grant needs separately approved re-login for a new trial |
| A5 | P: foreign-sandbox, wrong-account, foreign origin/path, redirect and TLS-skip/direct-egress probes; F: retain a handle then explicitly reconfigure refresh once to create a new authorization epoch | Every predeclared unauthorized probe refused within 30 seconds without credential forwarding/model turn, old F handle rejected at stock boundary rather than merely upstream 401, unknown outside account enforcement or an unexpected outcome blocks the whole qualification |
| A6 | Cancel an active native turn, then detach provider while the process/connection is retained | Cancel acknowledgement within 30 seconds, detach acknowledgement within 30 seconds and verified stopped/quiescent state within 120 seconds overall, cancellation distinct from credential revoke, no later authenticated request through detached/old handles, issuer grant not falsely reported revoked |
| A7 | P after A6: perform the exact stopped snapshot/restore table in the stopped snapshot/restore section and verify authenticated gateway readiness and old-handle refusal | Dedicated state only, required DB/KEK/trust coverage complete, hashes match and integrity `ok`, latest credential-generation/owner reconciled, detached state not resurrected and no blind issuer-token replay |
| A8 | After P/F owned cleanup, compare fresh/default-off and existing API-key/manual configuration, gateway/provider/state mappings and runtime identity baselines | No P/F gateway, supervisor, state directory, volume, key, DB, client trust or provider shared with existing agents/providers, no existing config/credential/refresh/workflow/runtime/grant/billing change |
| A9 | Independent security/ops/code review of exact pins/descriptor/P-F ledger in the declared order, distinguishing static source trace from observed effective-config/native/account/catalog/refresh results | All required positive and matching expected-negative assertions pass before qualification, independent review of the in-body per-family source/bounds/suppression ledger, zero suppressed-family native events and any settings denial against its exact accepted source/input/enforced-event proof, no source-only isolation/HTTP/catalog success claim or historical reclassification, missing evidence or first unexpected result blocks with remaining checks unrun, no fallback/managed implementation or automatic adoption |


### A2 executable loaded-config observation

Only within the separately authorized network-denied fixture, after the native private-stdio session completes `initialize` and the `initialized` notification, send this stock request using the verified owned workspace path

```json
{
  "id": "a2-config",
  "method": "config/read",
  "params": {
    "cwd": "<owned-workspace>",
    "includeLayers": true
  }
}
```

| returned configuration / comparison | required loaded value |
|---|---|
| `config.model_provider` | Exact `openai-http` |
| `config.model_providers.openai-http.name` | Exact `OpenAI` |
| `config.model_providers.openai-http.requires_openai_auth` | Boolean `true` |
| `config.model_providers.openai-http.supports_websockets` | Boolean `false` |
| `config.model_providers.openai-http.supports_standalone_web_search` | Boolean `true` |
| `config.model_providers.openai-http.http_headers.version` | Exact `0.164.0`, equal to A0's reported binary version component |
| `config.forced_login_method` | Exact `chatgpt` |
| `config.forced_chatgpt_workspace_id` | Exact private account-metadata selection, compare privately rather than publishing its value |
| Descriptor `base_url`, `env_key`, `experimental_bearer_token`, `auth`, `aws` | In the returned JSON, each key missing or JSON `null` means unset, reject every non-null value including `""`, `false`, `0`, `{}` or `[]`, A0 still requires literal omission from authored TOML |
| `config.analytics.enabled` | Boolean `false`, owned user-file origin with active layer/origin precedence, unavailable/conflicting evidence blocks |
| `config.features.plugins` | Boolean `false`, same owned user-file/layer check, not a descriptor field |
| `config.features.apps` | Boolean `false`, same owned user-file/layer check, not a descriptor field |
| `config.mcp_servers` and owned native request inputs | Key absent, JSON `null` or empty object only, no selected-plugin, MCP or thread-level override, any registration/unknown input blocks before the positive phase |
| Returned `layers` and `origins` | Inspect active layer/origin precedence for the selected descriptor, forced login/workspace and three suppression fields, no conflicting/disabled-only layer offered as proof of the loaded configuration |

Missing/errored response, unexpected schema/type/value, unavailable layer/origin evidence, missing required positive descriptor field or any mismatch = stop A2 as blocked, the five optional descriptor fields may be missing or JSON `null`, `mcp_servers` may also be missing/null/empty object as specified above, no guessed defaults or runtime/transport success claim
A0 remains the literal-absence proof for authored TOML, A2 accepts the pinned serializer's normal JSON representation of unset optional fields without weakening that authored-config requirement
Full `config/read` response including `config`, `layers` and `origins` contains the real workspace ID and remains only in `<trial-root>/receipts/a2-config-read.json`, directory 0700/file 0600 under the existing private custody/retention contract, public evidence only per-field match yes/no booleans, no raw response, IDs, paths or layer contents
This observation proves loaded configuration only, not application of built-in merge semantics, actual provider/runtime behavior or HTTP selection, A3 must independently observe the permitted HTTP POST with no WSS GET and retain its account/catalog/isolation checks
No `config/read` or native process is run by this source patch, all A0–A9 outcomes remain UNRUN/UNQUALIFIED pending the existing review, human approval and exact operator gates

### A4 exact refresh/failure recipe

P profile uses `refresh_before_seconds=300` / `max_lifetime_seconds=3600`, initial recorded expiry in the future, one manual stock rotate trigger before the due window, a 60-second status/commit bound and one post-commit request from the unchanged native process
F uses only deliberately synthetic non-grant values, including refresh token `qualification-invalid-not-a-grant`, valid pinned public client ID and the same real issuer token URL, no real account login or grant copy
In F, seed future expiry, configure refresh and retain H1, reconfigure once for the A5 epoch-refusal probe, obtain the supervisor-issued current H2 only after acknowledgement and prove H1 refused at stock before forwarding, then request one manual rotate and require the recorded failed-refresh/reauthorization outcome, not a success inferred from CLI exit 0
Finally set `CODEX_AUTH_ACCESS_TOKEN` expiry to an explicit past RFC3339 timestamp with stock `provider update --credential-expires-at`, require current-generation H2 to be refused before forwarding rather than reusing already-revoked H1 as the expiry probe, then close F without repair/re-login/reuse
No production/real grant is deliberately invalidated, P needs no re-login solely because F's synthetic test failed as expected, any real invalid_grant or ambiguous token rotation ends its fixture and requires a separately approved fresh human login for any new trial

## #502 native-memory compatibility amendment

**UNQUALIFIED — compatibility only, not production account isolation.** Refs
[#502](https://github.com/anurmatov/phleet/issues/502) (approved body
`fdfbae2d7361934bd8aadfbd24829d199c24086aa0639bd217288df6bea8d5b3`). The #498 history, A0–A9 ledger and every
UNRUN/UNQUALIFIED label above are unchanged; this section does not retroactively qualify anything.

**Explicit empty-MCP amendment, this path only.** #498 requires loaded `mcp_servers` absent, null or empty. For the
managed subscription compatibility path only, that requirement is replaced by exactly one table:

```toml
[mcp_servers.memory]
url = "https://<memory-host>/mcp"
enabled_tools = ["memory_get"]
```

The URL is derived from the operator's enrollment authority, the workspace `.mcp.json` holds exactly the same
memory route, and the table carries no bearer credential: the existing stock-authenticated signer and the
dedicated memory receiver authenticate the request and re-check a fresh ACL, endpoint/tool/project grants and
session/epoch on every call. This native registration is not the cloud Apps endpoint: `POST /backend-api/ps/mcp`
stays denied and suppressed at zero requests, and all six suppressed families plus the bounded settings denial
above still apply. No callback, dynamic-tool bridge or custom model client is added.

**Selected-name amendment ([#505](https://github.com/anurmatov/phleet/issues/505)).** The table is named after the
workload's one memory endpoint, `memory` or the ordinary `fleet-memory`, taken from the same `.mcp.json`; for
`fleet-memory` the table above reads `[mcp_servers.fleet-memory]` with the same URL and `enabled_tools = ["memory_get"]`.
Apply requires that name's own `memory_get` grant; a grant under the other name never counts. Both names, neither or
another spelling refuse. Nothing else in this amendment changes, the path stays **UNQUALIFIED**, and no A0–A9 or #501
result is claimed.

**Approved native config composition.** One deterministic builder, shared by preparation and registration
checks, writes, in order: `forced_login_method = "chatgpt"`, `forced_chatgpt_workspace_id = "<selected-account-id>"`
and `model_provider = "openai-http"`; then the generated defaults unchanged, including their own
`[analytics] enabled = false` and `[history]` tables; then the exact descriptor from
[Native TOML / stock HTTP descriptor](#native-toml--stock-http-descriptor); then one
`[features] plugins = false, apps = false` block; then the memory table above. No second analytics table, no root
key below a table, and no duplicate or shadowing key. The cache keeps the stock example shape with the
supervisor-issued `CODEX_AUTH_ACCESS_TOKEN` handle, an empty refresh token, the parsing-only ID-token stub and the
selected account. The outside binding, scoped provider checks, expiry, cleanup and rollback contracts are in
[the stock runbook](openshell-stock.md#managed-codex-subscription-branch-502-unqualified-compatibility-only).

**Compatibility proof versus production isolation.** A later, separately authorized trial may run one positive
synthetic subscription turn with exactly one `memory_get` for an approved non-sensitive record before issuer
account proof: at most 120 seconds for the turn and 10 minutes for the owned task including cleanup, with current
source, permissions and readiness verified first. Its private inputs (host, image, model, caller, enrollment
UUID/epoch, policy/receiver endpoint, memory ID and expected output, login owner, refresh custody/window, receipt
and rollback locations) are **ABSENT** here and must not be guessed from retained fixtures. Any unexpected denial,
timeout, duplicate tool call, input drift, generation/custody uncertainty or missing receiver/native evidence stops
without retry, remaining live checks UNRUN.

**Remaining #501 gates.** Production account-isolation qualification still requires the #501 issuer
route/header/refresh/refusal contract, a meaningful existing same-principal B and timely authenticated correlated
receipts, plus the full applicable #493/#498 closure. Account metadata, cache/JWT and forced-workspace equality are
workload-side input checks, not issuer singleton enforcement. No production subscription workload is permitted by
this amendment.

## #510 ordinary-service amendment

**Configured first-party MCP is not issuer Apps MCP.** A managed subscription workload may now keep the agent's
existing first-party routes: memory, Temporal and Telegram receivers, the comms-journal read route, and the
agent-local journal files/send listener, with its own bot and journal capture through stock service providers. They
appear as further `[mcp_servers.<name>]` tables after the memory table. Each table holds the agent's enabled grants
that the outside record permits, and memory stays `memory_get` only. These are workload-configured receivers that
verify the outside assertion or derived token. They are not the cloud Apps route: `[features] plugins = false,
apps = false` stays, so `POST /backend-api/ps/mcp` is still never registered or allowed. No issuer endpoint,
WebSocket permission or subscription endpoint is widened. #498's empty-MCP requirement remains the contract of its
isolated auth trial, not of an ordinary agent.

**Auth custody is unchanged.** The forced ChatGPT login and workspace, the `openai-http` descriptor, the selected
account, the outside binding and its existing provider, the empty refresh token, the parsing-only ID-token stub and
the gateway as sole refresh owner are all unchanged. Service credentials live in their own scoped stock providers,
never in the OAuth provider, the cache or workload inputs. The workload sees only stock-issued handles. Routes,
refusals, service-provider custody, cleanup and the capability-aware rollback are in
[the stock runbook](openshell-stock.md#ordinary-services-510).

**Limits.** This adds source support only. Status stays **UNQUALIFIED**: it is not issuer isolation (#501), CONTROL
restore qualification or a #493/#498 pass. One useful real-work, cancel and revoke check on an exact build needs
separate human and operator authority with named candidate, images, custody, policy and profile hashes, grants,
refresh window, a current eligible stopped recovery checkpoint and cleanup authority. While any applicable
#501/#498 positive-work gate or that CONTROL prerequisite is open, the check is **BLOCKED** before native work.
Until it runs, delivery is source-only and every live row remains UNRUN.
