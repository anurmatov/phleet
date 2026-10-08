# Epic grants (#436) — isolated acceptance run

**Result: 25/25 checks passed** on 2026-10-08, against the code at `2b6f51b` (PR #437), with every
process started from an empty environment (`env -i`). An earlier recorded run at `53a830d` also
passed 25/25; see "Broker isolation" for what it inherited. The only code change between the two is
a comment moved in `EpicGrantService.cs`. Before those, two runs exposed harness bugs (a null-result
check, then result decoding), not product defects. Each recorded run started from an empty Temporal
database and an empty orchestrator database and used the committed harness in
[`epic-grants-acceptance/`](epic-grants-acceptance/).

## What was real and what was simulated

| piece | in this run |
|---|---|
| Temporal | real: `temporal server start-dev` 1.9.1 (Server 1.32.0), a separate server instance on 127.0.0.1, SQLite file deleted afterwards |
| orchestrator database | real: MySQL 8.0.46 on 127.0.0.1, database `orch_accept436` created by the orchestrator's own migrations (`AddEpicGrants` applied) |
| broker | real: RabbitMQ 3.12.1 on 127.0.0.1 (distribution port pinned to loopback) |
| orchestrator, bridge | real: `Fleet.Orchestrator.dll` and `Fleet.Temporal.dll`, Release build of `2b6f51b`, `EpicGrants__Enabled=true` |
| definitions | real: the seed definitions as the orchestrator seeded them (version 1), pinned by the SHA-256 of the stored text |
| CTO decisions | real path: the bridge MCP tool `temporal_signal_workflow` with `?agent=cto-agent` → `POST /api/epic-grants/{id}/decisions` → signal |
| human decisions | real path: the dashboard's REST route `POST /api/workflows/fleet/signal/{id}` |
| agents | **simulated**: `fake_agent.py` answers every delegation by step name over RabbitMQ |
| GitHub | **simulated**: `github_stub.py` (the configurable `GitHubApiBaseUrl`) serves repo visibility; PR heads, merges and issue bodies live in the agent's `state.json`, and `--match-head-commit` is enforced by the simulated merge |

The Temporal namespace is `fleet` because the seed definitions start their children on task queue
`fleet`; the separate server instance is the isolation. No production service, repository or
credential was used. Disposable GitHub repositories were not used: creating them needs the
owner's approval, so real `gh pr merge --match-head-commit` behaviour is simulated, not observed.

## Steps (spec "Before merge")

| step | outcome |
|---|---|
| 1 create a grant | validate report valid; a denied repo with `allowPublic` refused (`target … is on the deny list`), nothing stored; grant created `active`; exactly one grant stored |
| 2 design approved by delegation | gate parked at `design-approval:1` with `ReviewRef` = the attested body hash; CTO decision `sent`; `verify_approved_body` ran; run result `{"IssueNumber": 101}` |
| 3 PR approved, merged pinned | gate parked with `ReviewRef` = PR head; decision `sent`; `phase4_merge_pinned` ran `--match-head-commit <reviewed head>` and merged; doc maintenance started and was prepared by `PrepAgent` |
| 4 push + re-review | human `changes_requested` → new head re-reviewed → new visit `merge-approval:2`, new `ReviewRef`; old ref refused `stale_artifact`, old visit refused `stale_visit`; human rejection, nothing merged |
| 4 push, no re-review | decision `sent` (ref still the reviewed head); pinned merge refused ("Head branch was modified"), `notify_merge_failed`, no merge, no doc run |
| 4b body edit after review | decision `sent`; `verify_approved_body` saw a different hash → `notify_approved_body_changed`, no approval emitted, null result, driver told `cancelled` |
| 5 revoke | grant `revoked`; the next decision refused `grant_inactive`, nothing sent |
| 6 no-grant gate | human approval merged unpinned (`phase4_merge`, no `--match-head-commit`); CTO `approved` without `GrantId` still refused as CEO-only |

Also checked: the decisions table holds exactly the four `sent` decisions and no row for any
refusal, and every gated run reported its terminal decision to the driver
(`approved`/`cancelled`/`approved`/`rejected`/`approved`).

Orchestrator decision log (one line per decision, evidence never logged):

```
accept436-design-101 design-approval sent
accept436-design-102 design-approval sent
accept436-pr-201 merge-approval sent
accept436-pr-202 merge-approval refused stale_artifact
accept436-pr-202 merge-approval refused stale_visit
accept436-pr-203 merge-approval sent
accept436-pr-204 merge-approval refused grant_inactive
```

The bridge logged the same seven forwards with `caller=cto-agent`. GitHub visibility was read 8
times, all unauthenticated (`auth=no`), each a 404 for the private target.

## Broker isolation

**Recorded run (`2b6f51b`).** `stack.sh` launches every process with `env -i` plus its env file
(`RabbitMq__Host` and `RABBITMQ_HOST` = `127.0.0.1`) and, before declaring the stack ready, reads each
.NET process's `/proc/<pid>/environ`:

```
Fleet.Orchestrator.dll RabbitMq__Host=127.0.0.1 inherited_names=none
Fleet.Temporal.dll RabbitMq__Host=127.0.0.1 inherited_names=none
```

While the run's workflows were finished, the local RabbitMQ listed four client connections, all
from `127.0.0.1`: `fleet-orchestrator`, `fleet-temporal-bridge`, `fleet-peer-config-sub` (the
bridge's config subscriber) and the scripted agent. The scripted agent, which only connects to
127.0.0.1, answered all 43 delegations.

**Earlier run (`53a830d`).** That `stack.sh` sourced the env file into the launching shell's
environment instead of an empty one. The launching container carries a broker host for the
production broker (`RabbitMq__Host`) and other agent variables. Reproducing that launch with a
harmless child process shows the env file's `RabbitMq__Host=127.0.0.1` replaced the inherited
value. The inherited `Telegram__BotToken`, `Tts__ServiceUrl`, `Whisper__ServiceUrl`,
`ASPNETCORE_HTTP_PORTS` and `FLEET_BUILD_COMMIT` were present but unused: the orchestrator and
bridge read none of them (the orchestrator only writes the first three into agents it provisions,
and none was provisioned). The container sets no `PEER_CONFIG_KEYS` or `ORCHESTRATOR_URL`. Every
directive in that run was answered by the scripted agent, which only listens on 127.0.0.1, so the
bridge published and consumed on the local broker. That run's full process logs were deleted at
cleanup, so it has no broker connection listing; the recorded run above has one.

## Reproduce

On a disposable Ubuntu 24.04 host (root, no Docker needed):

```
apt-get install -y mysql-server-8.0 rabbitmq-server python3-venv
# MySQL: initialise a datadir under $ACCEPT_DIR, mysqld --daemonize --bind-address=127.0.0.1,
#   CREATE USER 'accept'@'127.0.0.1' IDENTIFIED BY 'accept-only'; GRANT ALL ON *.* TO it.
# RabbitMQ, from an empty environment: env -i PATH=... RABBITMQ_NODE_IP_ADDRESS=127.0.0.1
#   ERL_EPMD_ADDRESS=127.0.0.1 RABBITMQ_SERVER_ADDITIONAL_ERL_ARGS="-kernel inet_dist_use_interface {127,0,0,1}"
#   rabbitmq-server -detached
# $ACCEPT_DIR: the `temporal` CLI binary and `python3 -m venv venv && venv/bin/pip install pika==1.3.2`
dotnet build Fleet.sln -c Release
ACCEPT_DIR=... docs/evidence/epic-grants-acceptance/stack.sh start
cd $ACCEPT_DIR && env -i PATH=/usr/bin:/bin HOME=$PWD ACCEPT_DIR=$PWD python3 accept.py   # non-zero on any FAIL
ACCEPT_DIR=... docs/evidence/epic-grants-acceptance/stack.sh stop
```

The bridge's MCP port 3001 is hard-coded to all interfaces; everything else binds 127.0.0.1.

## Cleanup

After each recorded run: every service stopped; `mysql-server`, `mysql-client`, `mysql-common`,
`rabbitmq-server` and `python3-venv` purged with `autoremove`; the MySQL datadir, `/etc/mysql`,
`/var/run/mysqld`, the Temporal SQLite file and the work directory deleted. Ports 3001, 3306, 3600,
3901, 4369, 5672, 7233, 8233, 15672 and 25672 were confirmed closed afterwards.

## Not covered

- Real agents and real GitHub (repositories, branch protection, `gh pr merge --match-head-commit`
  refusing a moved head). That acceptance is owned by the maintainer's operator before merge.
- Exact-image containers, the deployment compose file, and a production-like network.
- The dashboard view in a browser.
