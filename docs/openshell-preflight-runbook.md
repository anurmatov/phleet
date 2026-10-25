# OpenShell preparation preflight — operator runbook

The preparation preflight (`OpenShellPreparationPreflight`) is the concrete external qualification
that runs before any mutation in the OpenShell cold-preparation chain. It reads; it never writes,
installs, starts or pulls. This runbook covers configuration, the qualification-root custody
contract, and refusal behavior.

Nothing here enables anything. The preparation authority stays default-deny
(`DenyOpenShellPreparationAuthority`), the runtime-evidence reader and the authorized
current-policy reader have no production implementation, and even a fully configured preflight
refuses until the reviewed upstream ownership/capabilities contracts ship.

## Configuration — the default is disabled

Nothing registers unless every key below is set exactly; the orchestrator otherwise starts with no
preflight and the preparation chain answers `compatibility_blocked: preparation peers unqualified`.

```json
{
  "OpenShellBootstrapPreflight": {
    "Enabled": "true",
    "HookQualificationRoot": "/opt/fleet/openshell-qualification",
    "HookPluginName": "identity"
  },
  "Docker": { "SocketPath": "/var/run/docker.sock" }
}
```

- `Enabled` must be the exact string `"true"`; absent, `false`, `"1"` or any other value keeps the
  preflight unregistered.
- `HookQualificationRoot` must be an absolute path; `HookPluginName` (letters/digits/`-`/`_`,
  ≤128) must equal the middleware registration the approved plan selects — the plan's single
  `NetworkMiddlewares` entry — otherwise every qualification refuses with
  `bootstrap_hook_registration_mismatch`. Configured-but-incomplete (either missing) stays
  unregistered.
- `Docker:SocketPath` is the engine socket used only for the exact-ID legacy stop (defaults to
  `/var/run/docker.sock`).

## Qualification root — custody and file contract

The root is operator-owned material installed outside the repository and outside any agent mount.
Trust root = the read-only operator custody; the digest file below is corruption/mix-up and
substitution detection, not authentication.

| file | content |
|---|---|
| `hook` | pinned hook binary bytes (mode 0400) |
| `hook-sha256` | 64 lowercase hex of `hook` |
| `describe.json` | the hook's actual `MiddlewareManifest` Describe reply, proto3 JSON |
| `validate-request.json` | the `ValidateConfigRequest` actually sent: registration name + the approved plan's middleware config |
| `validate-config.json` | the `ValidateConfigResponse` reply (`valid: true`, empty `reason`) |
| `config.json` | the qualified current-policy acknowledgement (`v1:{…}`) |
| `records-sha256` | 64 lowercase hex over the canonical framing of the `hook` binary plus the four record files (sorted name + NUL + byte length + NUL + bytes) |

Rotation replaces every file in one step. The seal covers the pinned binary itself, so replacing
`hook` and `hook-sha256` as a fresh self-consistent pair while keeping the old replies is caught;
each recorded reply is further bound to the registration the approved plan selected (three-way:
configured name = plan-selected registration = request `middleware_name`) and to the approved plan
(request `config` must equal the middleware config inside the plan). A record set that is
internally valid for another hook, another config, or an older binary generation is refused.

Capture procedure: send `Describe` and `ValidateConfig` to the hook with the exact registration
name and the plan's middleware config, serialize the generated replies with proto3 JSON, write all
seven files, set `records-sha256`, chmod the tree read-only, and rotate the whole directory as a
unit.

## Refusal behavior

Every refusal is an `OpenShellStateConflictException` whose message carries the snake_case code;
the coordinator reports `preparation_refused_before_launch` with no reservation, stop or create
performed. Zero engine calls happen before the exact-ID stop step.

| code | meaning |
|---|---|
| `bootstrap_preflight_plan_changed` | scope plan hash does not equal the plan's own hash |
| `bootstrap_preflight_unconfigured` | bad registration name or relative qualification root |
| `bootstrap_static_qualification_changed` | a bootstrap invariant fails at the mutation boundary |
| `bootstrap_artifact_missing` | a qualification file is absent or unreadable |
| `bootstrap_hook_artifact_unqualified` | `hook` digest does not match `hook-sha256` |
| `bootstrap_qualification_record_unqualified` | `records-sha256` does not cover the current record set |
| `bootstrap_qualification_record_malformed` | a record is not parseable proto3 JSON of its schema |
| `bootstrap_hook_registration_mismatch` | configured `HookPluginName` is not the registration the approved plan selected |
| `bootstrap_hook_describe_unqualified` | manifest lacks the HTTP-request/`PRE_CREDENTIALS` binding, audience, or protocol declaration |
| `bootstrap_hook_validate_request_config_missing` | the recorded ValidateConfigRequest carries no config |
| `bootstrap_hook_validate_request_unqualified` | request names a registration other than the plan-selected one, or its config differs from the plan's middleware config |
| `bootstrap_hook_validate_config_unqualified` | the hook refused the config |
| `bootstrap_ingress_unqualified` | ingress URL is not exactly https + `/identity/ready`, without query/fragment/userinfo |
| `bootstrap_receiver_binding_unqualified` | receiver disabled, bound to the API port 3600, port out of range, or DNS name mismatch |
| `bootstrap_current_policy_unavailable` | no authorized current-policy reader registered, or capability absent |
| `bootstrap_current_policy_unqualified` | live policy bytes differ from the qualified `config.json` |
| `legacy_ownership_inventory_unqualified` | retained fail-closed: no reviewed upstream ownership inventory exists yet |

The ingress front port and the receiver listen port are intentionally not required to match (a
fronting ingress may publish a different port); the only port the receiver must never bind is the
orchestrator API port 3600.

`legacy_ownership_inventory_unqualified`, the unregistered D2 runtime-evidence reader and the
unregistered current-policy reader are dependencies on the reviewed upstream contracts, not bugs
to delete. Do not substitute local inventories, plugin Settings surfaces, or synthetic replies to
make them pass.
