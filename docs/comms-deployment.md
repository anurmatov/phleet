# Running Fleet.Comms — the first-party client API

`Fleet.Comms` is the HTTPS boundary a first-party client talks to. This document covers running it
on your own machine, behind your own TLS, with your own backups.

## What this ships, and what it does not

**It ships:** device enrollment, access-token mint and refresh, device self-revocation, and session
discovery. Four routes, durable auth state, an operator path for issuing codes and revoking a lost
device, a readiness probe that actually checks the store, and a backup command.

**It does not ship:** chat, message submission, WebSocket streaming, conversation resume, durable
conversation history, admission or outbox, push notifications, or working iOS connectivity.

Read that second list literally. **After you enable this, an enrolled device holds a valid access
token and has nothing to talk to yet.** That is the intended state of this slice. If you are here
to run a chat server, this is not yet one.

## Enabling it

It is **off by default** and starts only when you ask for it. Most phleet installations run Telegram
agents and want no public client API at all; an auth boundary is not something to acquire by
accident.

```bash
./setup.sh          # answer "y" to "Enable Fleet.Comms?"
```

Or on an existing install, in `.env`:

```bash
FLEET_COMMS_ENABLED=true
FLEET_COMMS_BIND=127.0.0.1:3500
FLEET_COMMS_TRUST_PROXY=false
```

then `./upgrade.sh`. The image is built only when `FLEET_COMMS_ENABLED=true`, so leaving it off
costs nothing.

The service sits behind a Compose profile. `setup.sh` and `upgrade.sh` pass `--profile comms`
explicitly when you have enabled it. Driving it by hand:

```bash
docker compose --profile comms up -d fleet-comms
```

### Why the port is loopback by default

`FLEET_COMMS_BIND` defaults to `127.0.0.1:3500`. Other phleet services publish on all interfaces —
but `rabbitmq`, `fleet-mysql` and `fleet-minio` already bind loopback, and this service belongs with
them rather than with the dashboards. It accepts credentials. A reverse proxy on the host reaches it
at the loopback address and terminates TLS; nothing on your LAN or the internet reaches it directly.

**A half-finished install must not leave an internet-reachable auth boundary behind.** That is the
whole reason for the default. Widen it only when TLS is genuinely in front.

## The routes

Four with conversations off, ten with them on. The surface grows deliberately and visibly: the
"exactly four routes" statement was true of the auth-only slice and is not a permanent contract.

| Method | Path | Purpose | Needs conversations |
|---|---|---|---|
| `POST` | `/v1/auth/devices` | register a device with an enrollment code | |
| `POST` | `/v1/auth/token` | mint or refresh an access token | |
| `POST` | `/v1/auth/devices/{deviceId}:revoke` | self-revoke, authenticated as that device | |
| `GET` | `/v1/session` | session and server limits | |
| `POST` | `/v1/conversations` | open or resume by `externalRef` | yes |
| `GET` | `/v1/conversations/{id}/events` | catch-up | yes |
| `POST` | `/v1/conversations/{id}/submissions` | create **or** steer | yes |
| `POST` | `/v1/conversations/{id}:cancel` | request cancellation | yes |
| `POST` | `/v1/conversations/{id}/cursor` | advance the durable cursor | yes |
| `GET` | `/v1/conversations/{id}/stream` | WebSocket upgrade | yes |

With conversations off, the six are **not mapped** — a `404` from the router, not a handler that
authenticated and then refused. `/health`, `/ready`, `/metrics` and `/` all return `404` here too;
see [readiness](#readiness-and-what-a-503-means) for where the probes actually live.

Full request and response shapes: [`docs/first-party-api.md`](first-party-api.md).

## Durable conversations

Off unless configured, and an install that leaves it off is byte-identical to the auth-only one: no
conversation route, no database connection attempted, no background loop.

### What it needs

A MySQL 8.0 database and a broker. The example compose ships a `comms-mysql` service under the same
`comms` profile, with **no published host port** and its own named volume.

**Two database accounts, and the split is the point.** The service runs as an account holding
`SELECT`, `INSERT`, `UPDATE`, `DELETE` and **no DDL grants**. Migrations use a separate account that
has them, and the running container is never given it. That is what makes "the service never
migrates on startup" a property of the deployment rather than of the code being careful — a process
that tried would be refused by the database.

| Key | What it is |
|---|---|
| `FLEET_COMMS_CONVERSATION_DB` | runtime connection string. **Its presence enables the feature** |
| `FLEET_COMMS_CONVERSATION_MIGRATION_DB` | DDL connection string, for `conversations migrate` only |
| `FLEET_COMMS_SOUTH_BIND` | the agent-facing listener. Default `http://0.0.0.0:8082` |
| `FLEET_COMMS_SOUTH_TOKEN` | bearer credential for that listener. **No default; startup fails without it** |
| `FLEET_COMMS_AGENT_NAME` | routing key and queue-name segment, `[A-Za-z0-9_-]{1,128}` |
| `FLEET_COMMS_BROKER` | AMQP connection for the two outboxes |
| `FLEET_COMMS_CLAIM_RETENTION` | ⚠️ see below |

### ⚠️ The south listener is never published

It carries an administrative surface: a caller who reached `/turns:commit` could write a terminal for
someone else's turn. It is reached by a peer container on the Docker network and by nothing else —
**never in a `ports:` block, never behind the public proxy.**

Its binding rule is the *opposite* of the ops listener's, and the difference is deliberate. Ops is
loopback-bound because only this container's own healthcheck calls it. The south caller is a
different container, so a loopback bind would make the surface unreachable by construction.

### ⚠️ Claim retention is a guard, not housekeeping

`FLEET_COMMS_CLAIM_RETENTION` must exceed **both** the broker's redelivery horizon **and** the
longest a submission can legitimately wait behind a running turn.

A `done` delivery claim inside its retention is the **only** thing standing between a redelivered
command and a duplicate turn. The attempt state machine refuses a *second* start of a running
attempt; it does not refuse a *first* one, and a queued attempt is pending for as long as it waits.
Shortening this removes the guard rather than tightening it.

### Migrations

`conversations migrate` is an operator command, run through the ops service, and it is the **only**
thing that applies a migration:

```bash
docker compose run --rm fleet-comms-ops conversations migrate
docker compose run --rm fleet-comms-ops conversations status
```

`status` prints the applied version, the version the binary expects, and whether they agree; it exits
non-zero when they do not. An applied version **ahead** of the binary is as unhealthy as one behind
— that is the rollback-after-migration case — and the two produce distinguishable messages.

An edited migration is refused by name rather than re-applied: an edited script is a different
migration, and applying the difference silently is how two deployments end up at the same version
number with different schemas. Add a new forward-only script instead.

### The agent side, and the one relationship no process can check for you

The consumer of the queue this service publishes to is the agent, and it should need **nothing said
about it**. `Conversations__SouthBearerToken` is the enabling key and the only value an agent must be
given; `Conversations__SouthBaseUrl` defaults to `http://fleet-comms:8082` — this service's container
name and south port in the compose file that deploys it — so an ordinary deployment sets no address
at all. Set it only if you renamed the service or moved the port. **It carries no agent name and no broker connection string** — the agent already has
both, and a second copy of either is a value that can drift or a credential a rotation can miss. The
queue segment is the agent's own `Agent__ShortName`, and the inbound queue is consumed on the
RabbitMQ connection the agent already holds for the task, relay and orchestrator exchanges
(`RabbitMq__Host`). A half-configured agent **fails to start** rather than coming up healthy beside a
queue nobody drains, and that includes a `ShortName` outside the pattern or a missing broker host.

**The agent's `Agent__ShortName` must match `FLEET_COMMS_AGENT_NAME` exactly** — including case. It
is the routing key on one side and the queue-name segment on the other, and it is used verbatim here
even though the relay path lowercases its own copy. A value the two sides read differently produces
no error anywhere: the agent binds and drains a queue nobody publishes to, while the real one grows.
Both sides validate the same `^[A-Za-z0-9_-]{1,128}$` pattern, which catches a malformed name but
cannot catch a well-formed *different* one.

#### ⚠️ Heartbeat and lease

The store's lease is **120 seconds** and the agent renews every **30** by default — four renewals per
lease, so a lost renewal is survivable. The agent refuses to start with a heartbeat above **half**
the lease, and it validates against a constant both assemblies compile from rather than a literal
each side keeps its own copy of.

**The gap that constant cannot close:** the agent cannot read *your* configured lease at runtime.
There is no endpoint that reports it, and adding one would make a configuration value a network
dependency. So if you lower `LeaseDuration` on the service below twice the agent's heartbeat, the
agent will start happily and the store will abandon attempts on healthy turns.

The symptom is the misleading part. It arrives as `turn.outcome_unknown { attempt_abandoned }` on
turns that actually answered, which reads as a store fault or a broker problem — and the thing to
check is neither. **If you shorten the lease, shorten the agent's `Conversations__HeartbeatInterval`
in the same change.**

#### Cancel is accepted, not executed

`POST /v1/conversations/{id}:cancel` returns `200` and the command reaches the agent, which claims it,
completes it as `dropped`, and acknowledges. It does **not** stop a running turn, and the client
receives no `control.ack`. Nothing here is broken; cancel execution is a later slice. Do not enable a
cancel affordance in a client against this build.

### Retention is a garbage-collection horizon, not deletion

Nothing here serves a request to erase anything. Rows age out; ephemeral events are pruned without
announcing a gap, because their absence is not a loss, and durable events are pruned with the
retained floor advanced in the same transaction so a reader is told exactly what it can no longer
have.

## TLS and the reverse proxy

**This service never terminates TLS.** Without it, enrollment codes, device secrets and access
tokens travel in clear text. An HTTP-only setup is a local-development configuration and nothing
else — do not put one on a network you do not control.

### Caddy

Certificates are automatic.

```caddyfile
comms.example.com {
    reverse_proxy 127.0.0.1:3500 {
        # Replace, never append. See the warning below.
        header_up X-Forwarded-For {remote_host}
    }
}
```

### nginx

Use an ACME client (certbot, acme.sh) for the certificate.

```nginx
server {
    listen 443 ssl;
    http2 on;
    server_name comms.example.com;

    ssl_certificate     /etc/letsencrypt/live/comms.example.com/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/comms.example.com/privkey.pem;

    location / {
        proxy_pass http://127.0.0.1:3500;
        proxy_set_header Host              $host;
        proxy_set_header X-Forwarded-Proto $scheme;

        # REPLACE the chain with the real peer. Do NOT use
        # $proxy_add_x_forwarded_for here: it appends to whatever the client
        # sent, which hands the caller the rate limiter's partition key.
        proxy_set_header X-Forwarded-For   $remote_addr;
    }
}
```

**Only the four routes above belong on the public hostname.** Do not proxy anything else, and never
proxy the operations listener.

### Client IP and the rate limiter

The auth routes admit 30 requests per 60 seconds per caller, refusing the rest with `429` and an
integer `Retry-After`. The partition key is the caller's address.

`FLEET_COMMS_TRUST_PROXY` decides where that address comes from, and **it defaults to `false`**:

| Setting | Behind a proxy | Directly reachable |
|---|---|---|
| `false` (default) | one shared bucket for all callers — **degraded but bounded** | correct per-caller budgets |
| `true` | correct per-caller budgets, **if** the proxy replaces `X-Forwarded-For` | **broken**: any caller picks their own partition and mints a fresh budget by changing a header |

Set it to `true` only when a proxy is actually in front and uses the replace form above. The failure
mode of getting this wrong is not a smaller limit — it is no limit at all, for anyone who reads this
document. Exactly one hop is consumed, and that is not configurable: each additional hop is another
position a caller can forge from.

**The conversation routes partition differently, on purpose.** They are all authenticated, so their
key is a hash of the presented bearer rather than the address — two devices behind one NAT, one
corporate egress or a proxy that does not forward would otherwise share a budget, and one client's
catch-up storm would throttle the other's. The unauthenticated auth routes keep the address
partition described above. Their budget is 300 requests per 60 seconds, higher because catch-up
after a reconnect issues a page request per 200 events and because these routes do not spend an
Argon2id evaluation per call.

## The operator path — enrollment and a lost device

Neither of these is an HTTP route, and neither ever will be. Issuing a code over HTTP would let
anyone who reached the port mint a registration; revoking a device without authenticating as it
would be a one-request lockout against the owner's only way in. They are one-shot commands against
the store, and their output belongs in your terminal.

### Issue an enrollment code

```bash
cd <checkout>/fleet        # where setup.sh generated docker-compose.yml and .env
docker compose -p fleet -f docker-compose.yml --env-file .env \
  run --rm fleet-comms-ops enroll issue --principal owner
```

No `--profile` flag is needed: `docker compose run` enables the profile of the service it targets.
`fleet-comms-ops` sits on its own `comms-ops` profile precisely so that `up` never starts it — under
the API service's profile it would run as a second long-lived process writing to the same database.

**`fleet-comms-ops`, not `fleet-comms`** — and that is not cosmetic. `compose run` builds the
one-shot container from the service definition including its logging driver, so running this on the
API service would write the code into the engine's `json-file` log on the host *before* `--rm`
removed the container. `docker logs` would show it and removal does not un-write the file. The
operator service is the same image and entrypoint with `logging: none`; the API service keeps its
rotation.

The code prints on stdout. It is valid for **15 minutes**, registers exactly **one** device, and is
never written to a log, a file or an environment variable. Treat it like a password, because for the
next fifteen minutes it is one.

### List devices

```bash
docker compose -p fleet -f docker-compose.yml --env-file .env \
  run --rm fleet-comms-ops devices list
docker compose -p fleet -f docker-compose.yml --env-file .env \
  run --rm fleet-comms-ops devices list --principal owner
```

Device id, principal, status and registration time. No hashes, no salts, no token material.
Revoked devices are listed too — otherwise "did my revocation work?" would be answered by silence.

### Revoke a lost or stolen device

One active device per principal, so a lost phone must be revoked before a replacement can enroll.
The phone cannot do it — it is gone.

```bash
OPS=(docker compose -p fleet -f docker-compose.yml --env-file .env run --rm fleet-comms-ops)
"${OPS[@]}" devices list
"${OPS[@]}" devices revoke --device-id <id>
"${OPS[@]}" enroll issue --principal owner
```

`enroll issue` **refuses while an active device exists** — it exits non-zero telling you to revoke
first, and writes no enrollment row. That is checked and inserted in one transaction, so a code
cannot slip past a concurrent registration.

The device and every token it holds are revoked in one transaction. A device marked revoked whose
tokens still authenticate is not a revocation.

These commands run safely while the service is live: SQLite serialises the writers and both use the
same busy timeout.

## Readiness, and what a `503` means

There is a second listener, bound to loopback **inside the container only**. It is not published, it
is not reachable from another container, and **it must never be proxied** — `/ready` reports whether
your auth store is answering, which on a public address is an availability oracle.

| Path | Meaning |
|---|---|
| `GET /health` | the process is running |
| `GET /ready` | one real transaction against the auth store succeeded — **and, with conversations enabled, against the conversation database, plus a schema-version check** |

With conversations on, `/ready` also fails when the applied schema version does not match the one
the binary expects. Both directions: **behind** means a migration has not been run, **ahead** means
a binary was rolled back after one, and the body says which. A database that is up but carrying the
wrong schema would otherwise report ready while every conversation route answered `503`.

The container's healthcheck targets `/ready`, not `/health`, and the difference matters. The store
initialises lazily, so a broken store path — unwritable, wrong owner, read-only mount, volume not
mounted — produces a process that **starts cleanly and then `503`s every single request**. A
liveness probe calls that healthy. `/ready` does not.

So:

- **Container never becomes healthy, `/ready` returns `503`** — the store is unreachable. Check the
  volume is mounted, the path is writable, and that `store init` has been run. Auth routes that
  reach the store return `503` too — though the checks that run *before* it still answer as they
  always do: a bad protocol or content type is still `400`, a missing bearer still `401`, and a
  throttled caller still `429`. A store outage does not turn those into `503`.
- **Container exits non-zero immediately** — `Comms__AuthStorePath` is unset or blank. The log names
  the key. This is deliberately a different failure from the one above: missing configuration cannot
  fix itself, so the process refuses to start rather than serving errors.

Recovery from the first needs no recreation: fix the permission or mount, and `/ready` goes green on
its next probe.

## The two stores are independent

⚠️ **They are separate engines with separate backups, and they are not transactionally linked.**
Restoring one without the other has a defined outcome, and an operator under pressure will otherwise
assume the two restores are one:

| Restored | Not restored | What a client sees |
|---|---|---|
| auth store | conversation database | a device that authenticates and a conversation that answers `conversation_not_found` |
| conversation database | auth store | a conversation whose principal no longer has a registered device — also `conversation_not_found`, since the token cannot be minted |
| conversation database rolled back | auth store current | a lower `nextSeq` with an explicit gap rather than silent truncation |

The SQLite auth store keeps `store backup` and the procedure below. The conversation database is
backed up with ordinary MySQL tooling (`mysqldump`, a filesystem snapshot of its volume, or your
provider's mechanism) — nothing in this repository wraps it, because nothing in this repository
would add anything to it.

## Backup and restore

The auth store is a SQLite database in the named volume `fleet_comms_auth`. **If you lose it, the
owner is locked out of their own client** and the only recovery is a restore — there is no
self-service re-enrollment.

`docker compose down` without `-v` keeps the volume, and so does `./upgrade.sh`. Never make
`down -v` a routine step.

**A deleted volume does not silently re-enroll.** The service opens the database read-write and will
not create it, so an empty or missing store makes `/ready` return `503` and the container never
reports healthy — it does not quietly start over with no devices. Creation is a deliberate, one-time
`store init`, which `setup.sh` runs once when you enable the service. Recovery from a deleted volume
is the restore procedure above, not a fresh enrollment you did not intend — and `store init` will
say so: it records a marker inside the volume the first time it runs, so a **missing database in a
volume that has held one** is reported as storage loss and refused. Starting over with no devices is
possible but must be asked for explicitly, with `store init --recover`.

Two records make that work, because each catches a case the other cannot. A marker inside the volume
catches a **deleted database**. `FLEET_COMMS_STORE_PROVISIONED` in `.env`, on the host and outside
the volume, catches a **deleted volume** — which takes the marker with it and would otherwise look
exactly like a first install. `setup.sh` and `upgrade.sh` set it after the first successful init; do
not set it by hand.

A second `store init` **validates** what is already there rather than trusting that bytes exist: a
zero-byte leftover, a truncated restore or an unrelated database is refused, not reported as
"already initialised". The service applies the same rule on every open — it verifies the
`enrollments`, `devices` and `tokens` tables and returns `503` if any is missing, instead of
creating them.

### Taking a backup

```bash
cd <checkout>/fleet
docker compose -p fleet -f docker-compose.yml --env-file .env run --rm \
  -v "$PWD/backups:/backups" \
  fleet-comms-ops store backup --out /backups/auth-$(date +%F).db
```

This uses SQLite's `VACUUM INTO`, which is safe while the service is serving. **Do not back this up
with `cp`, `tar` or a volume snapshot**: those capture a WAL database mid-checkpoint and produce a
file that restores into a plausible-looking database missing its most recent writes — a backup that
fails only when you finally need it.

The command claims a sibling lock file, writes to a uniquely named temporary on the destination
filesystem, **validates that what was written is a usable auth store**, and only then renames it
into place. So the destination never exists until a complete, verified database is ready: an
interrupted backup leaves a temporary and a claim, never a backup. Exactly one of two concurrent
backups to the same path wins; the other refuses without touching the winner's file. It refuses to
overwrite an existing file, and refuses the live database as a destination.

If a backup is interrupted, a `.claim` file **and a `.tmp-*` file** are left beside the destination,
and the next attempt at that exact path names both. The temporary is the full size of the database,
so on a real store it is worth removing rather than leaving:

```bash
rm -f backups/auth-2026-01-01.db.claim backups/auth-2026-01-01.db.tmp-*
```

A later successful backup to the same destination sweeps any stale temporaries for that path
itself.

A cron line, daily at 03:30, keeping 14 days:

```cron
30 3 * * * cd /path/to/checkout/fleet && docker compose -p fleet -f docker-compose.yml --env-file .env run --rm -v "$PWD/backups:/backups" fleet-comms-ops store backup --out /backups/auth-$(date +\%F).db && find "$PWD/backups" -name 'auth-*.db' -mtime +14 -delete
```

### Restoring

**Verify before replacing.** A truncated or non-database file must be rejected while the working
store is still intact.

```bash
#!/usr/bin/env bash
# Disaster restore. Copy this WHOLE block — it is written to run as one script, and the guards are
# what make it safe. Running the steps by hand, one at a time, is how a failed revocation gets
# followed by a start.
set -euo pipefail

CHECKOUT=/path/to/checkout     # setup.sh generated docker-compose.yml and .env under $CHECKOUT/fleet
BACKUP=auth-2026-01-01.db
cd "$CHECKOUT/fleet"

COMPOSE=(docker compose -p fleet -f docker-compose.yml --env-file .env)
OPS=("${COMPOSE[@]}" run --rm -v "$PWD/backups:/backups" fleet-comms-ops)

# 1. Verify the backup BEFORE it replaces a working store. `store verify` asks the question the
#    restore depends on — "is this an auth store?" — using the same check the service applies on
#    every open, plus a full row-by-row integrity check. A non-zero exit stops here, with the
#    current store untouched.
"${OPS[@]}" store verify --in "/backups/$BACKUP"

# 2. Stop the service so nothing is writing.
"${COMPOSE[@]}" --profile comms stop fleet-comms

# 3. Resolve the ACTUAL volume, and refuse to continue without one.
#    Compose prefixes the logical name with the project, so `fleet_comms_auth` is really
#    `fleet_fleet_comms_auth`. Writing to the guessed name creates a DIFFERENT empty volume: the
#    service then starts on its untouched database and reports ready — a restore that appears to
#    have worked and changed nothing.
VOL=$(docker inspect -f '{{range .Mounts}}{{if eq .Destination "/var/lib/fleet-comms"}}{{.Name}}{{end}}{{end}}' fleet-comms)
if [ -z "$VOL" ]; then
  echo "Could not resolve the auth store volume; refusing to continue." >&2
  exit 1
fi
echo "restoring into $VOL"

# 4. Replace, keeping the current store until the copy succeeds, and remove stale WAL sidecars.
#    A -wal or -shm left from the old database beside a restored file is how a restore silently
#    resurrects the data it was supposed to replace.
docker run --rm -v "$VOL:/store" -v "$PWD/backups:/backups" alpine sh -c "
  set -e
  cp /backups/$BACKUP /store/auth.db.incoming
  # Keeps the current store until the new one is in place. Delete auth.db.previous once the
  # restored service has been verified — they accumulate across restores, each a full copy.
  mv /store/auth.db /store/auth.db.previous 2>/dev/null || true
  mv /store/auth.db.incoming /store/auth.db
  rm -f /store/auth.db-wal /store/auth.db-shm"

# 5. Invalidate everything the snapshot brought back — BEFORE the service starts.
#    A restore reinstates whatever credentials the snapshot held, including devices revoked after
#    it was taken, and a device secret is long-lived: expiring the restored access tokens does not
#    help, because the restored device can mint more. If any of these fails, the script stops and
#    the service stays down.
"${OPS[@]}" devices list          # what did the snapshot bring back?
"${OPS[@]}" devices revoke --all  # every device, token, and unconsumed enrollment code
"${OPS[@]}" enroll issue --principal owner   # the code for the device you actually trust

# 6. Only now start the service, and confirm readiness before trusting it.
"${COMPOSE[@]}" --profile comms up -d fleet-comms
"${COMPOSE[@]}" --profile comms exec fleet-comms curl -fsS http://127.0.0.1:8081/ready
```

**Fail closed — and it is `set -euo pipefail` plus the empty-volume check doing that, not this
paragraph.** An earlier version had `set -e` only inside the helper container's `sh -c`, so the
surrounding sequence ran on regardless: a rejected backup still reached the replacement, and a
failed `devices revoke --all` still reached `up -d`. `RestoreProcedureTests` now executes this exact
block with a recording Docker substitute and injects a failure at each step, asserting that nothing
downstream is attempted.

Nothing before step 6 starts a listener — the operator subcommands are one-shot containers that bind
no port — so a failure at any step leaves the service down. A restore that half-succeeded must not
be reachable.

`devices revoke --all` takes devices, tokens **and unconsumed enrollment codes** in one transaction.
A snapshot restores unconsumed codes too, and an unconsumed code registers a device for the rest of
its TTL — closing the front door while restoring a key to the back one is not invalidation. Doing it
device by device, or after the service is already up, leaves a window in which a restored credential
works and is easy to get wrong under pressure.

This credential invalidation applies to **disaster restore only**. An ordinary upgrade or container
recreation keeps the same volume and needs none of it.

A `VACUUM INTO` backup contains no WAL of its own, which is why step 3 removes the sidecars rather
than copying any.

**A backup you have never restored is not a backup.** Restore one into a throwaway volume and check
that `devices list` shows what you expect, before you need it under pressure.

## Upgrades

`./upgrade.sh` runs `docker compose down`, regenerates the compose file from the example, rebuilds
and restarts. Named volumes survive that, so **enrolled devices stay enrolled** across an upgrade
and a token that had life left keeps the life it had — an upgrade does not extend a token's original
15-minute expiry, so a client mid-upgrade may still need to refresh, which it does with the device
secret it already holds.

`fleet:comms` is rebuilt only when `FLEET_COMMS_ENABLED=true`. The profile is resolved before
shutdown, so disabling the service actually stops it, and its volume is preserved either way. On an
enabled upgrade the script waits for the container to report healthy before declaring success.

The `--profile comms` flag and the generated compose file are both required on every command above:
`setup.sh` writes them to `<checkout>/fleet/`, not to the repository root.

## Troubleshooting

| Symptom | Cause |
|---|---|
| Container exits non-zero at startup | `Comms__AuthStorePath` unset or blank — the log names it |
| Never becomes healthy, `/ready` is `503` | store path unwritable, or `fleet_comms_auth` not mounted |
| Port already allocated | another service holds `FLEET_COMMS_BIND`; the default `3500` is chosen to avoid the ports the example compose file already publishes |
| Every auth request is `401` | expected for an unknown or expired credential — all auth failures are deliberately identical, so the response cannot tell you which one |
| `429` on a valid request | the 30/60s limiter; behind a proxy with trust off, all callers share one bucket |
| Enrollment code rejected | codes last 15 minutes and register exactly one device |
