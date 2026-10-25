# Process qualification source checkpoint (#476)

This tool is a **partial source checkpoint, not a qualified process campaign**.
Application feature defaults are unchanged; no installation or deployment invokes it.

The preserved candidate now builds with no external packages and includes:
- strict target/config/policy/executable hashes, source HEAD/status, image revision/digest and server-version checks;
- absolute command paths and a cleared, small child environment with owned HOME/cache/temp roots;
- a reserved Linux session leader and bounded dual-pipe draining, terminating same-session descendants even after target exit, cancellation, deadline or output overflow;
- allowlisted local Git configuration plus explicit fsmonitor/hooks/submodule/lazy-fetch guards;
- hash-bound Docker config.json and named-context metadata checked against the exact pinned Unix endpoint before any source process or engine request;
- an inference-only loopback Responses fixture, tested using the actual production prompt and strict turn parser;
- named synthetic identity/denial/crash/reconciliation/emission/late-handoff assertion oracles.

`run` always returns BLOCKED before any infrastructure read or subprocess.
Caller-authored observation JSON cannot establish that a process boundary ran.
Compose/SQL/start/stop/cleanup command planning also refuses: the complete immutable
service/container/volume registry, live collectors and exact-owned cleanup are not implemented.
The synthetic scenario evaluator is not evidence of daemon/native CLI/shell/API/DB/broker execution.
Outage and DB/keyring restore oracles remain UNRUN, even if listed as authorized in input.

Run only the owned source tests with an installed .NET 10 SDK:

```sh
python3 -m unittest discover \
  -s tests/scripts \
  -p test_workflow_outcome_process_driver.py
```

`validate` is a live, read-only target check, including native CLI version invocation;
it is not part of the source-test command and requires separate target authorization.
`fixture` serves only loopback inference and is bounded by the manifest deadline.
It never replaces a native executable, daemon, shell, API, database or broker.

Remaining source: connected shipped-daemon/native-CLI scenario collectors, authenticated
API/broker/Temporal observations, immutable nested-target registry and exact-owned cleanup.
Final-head independent review/manual scrub, green CI/current AC24 and separately authorized
image/server/process/AC25 DB-keyring backup/restore qualification still block merge.

The process runner uses an internal helper from this same compiled artifact, not an
unlisted shell/setsid executable. Its separate status pipe is close-on-exec for the
target. The leader remains alive until cleanup so target exit cannot lose session
ownership. Cleanup inventories the reserved session rather than only its original
process group, pins each member with a Linux pidfd, and rechecks membership before
signalling that handle; two consecutive empty live-member inventories precede leader
termination. Same-session setpgrp descendants are covered with inherited and closed
pipes on exit, deadline, cancellation and output-overflow paths.
An unreadable inventory, missing pidfd support or unexpected leader exit refuses
confirmed cleanup; non-Linux execution is unsupported.
This is not containment of hostile children escaping the session,
and it does not replace the still-missing immutable campaign cleanup registry.

Docker context metadata must be present in configFiles under
contexts/meta/<SHA256(context-name)>/meta.json beside config.json. Both files are
hash-checked; currentContext, metadata Name and Docker Host must exactly match.
Default/implicit contexts, unknown metadata/TLS material or mismatched endpoints
refuse rather than falling back to ambient Docker configuration.

The read-only `validate` path additionally requires one hash-pinned
`runtime-bindings.json` in `configFiles`, with schemaVersion 1, ownerNonce equal
to the scenario nonce, an explicit /run/ socketTarget, and exactly the agent,
orchestrator and temporalBridge container pins (full 64-hex id, sha256 imageId,
and 64-hex specSha256).
These are immutable IDs, not container names or a Compose project lookup.
The driver checks engine identity around each exact-ID GET, image/owner/spec,
host isolation and owned bind mounts before the native version probe.
It requires explicit nested Docker, Temporal, broker and local-model settings;
unknown environment defaults or additional namespace entries refuse.
Keys follow .NET environment-provider semantics: case-insensitive comparison and
double-underscore-to-colon normalization precede selection.
Duplicate normalized aliases refuse instead of depending on enumeration order;
additional namespace indices refuse in any casing or separator form.
Run the separate actual-provider source oracle with the installed shared framework:

```sh
dotnet run --project \
  tests/Fleet.WorkflowOutcomeQualification.SourceTests
```

That oracle feeds only owned dictionaries into the actual .NET provider loader,
never mutates global environment, and invokes the actual internal observation
parser without broadening its public API.

The internal spec digest covers Config, HostConfig, Mounts and NetworkSettings,
plus the exact State.Pid/StartedAt generation, using ordinal object-key order and
preserved array order.
It is not a new cross-SDK JCS format or permission to accept arbitrary observations.
Raw environment/configuration is never serialized in the validation plan.
HTTP errors, duplicate JSON, missing identities or over-4-MiB inspection responses
refuse rather than invent an empty target.

This checkpoint binds only the three application containers and their inspected
nested configuration, not the complete infrastructure/helper/volume registry,
effective live configuration, broker principal, SQL schema or native task history.
A volume name, owner label or inspect hash alone is not an immutable cleanup
precondition.
Campaign mutation planning and RunAsync remain BLOCKED until connected collectors,
full resource-generation custody and exact-owned campaign cleanup are implemented
and separately qualified.

The connected `read-receipt` command is a separate live read, not campaign execution.
It requires the exact approvalRef, an explicit `receipt-read` allowed operation and
an owner-private, hash-pinned token file from configFiles; it has no ambient token
or endpoint fallback.
After target validation it performs exactly one GET of the existing coordinator
receipt route for the exact batch/nonce/generation/item/version, then validates
the target again before returning selected state and a response digest.
Non-200/redirect, malformed/duplicate/oversized JSON, foreign membership, conflicting
delivery/ack state, cancellation or changed custody refuses without POST or retry.
Only owned HTTP-handler/process/metadata stand-ins are used by the source tests.

This readback is not proof of the daemon/native CLI/shell path, local fsync,
broker confirmation, effective server configuration, actual worker build ID or
database/keyring restore.
No campaign PASS is generated; RunAsync and all mutation planning remain BLOCKED
until the full collectors, resource registry and exact-owned cleanup are complete.
