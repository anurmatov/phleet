# Process qualification source checkpoint (#476)

This tool is a **partial source checkpoint, not a qualified process campaign**.
Application feature defaults are unchanged; no installation or deployment invokes it.

The preserved candidate now builds with no external packages and includes:
- strict target/config/policy/executable hashes, source HEAD/status, image revision/digest and server-version checks;
- absolute command paths and a cleared, small child environment with owned HOME/cache/temp roots;
- bounded concurrent output draining and owned-child termination on deadline or cancellation;
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
