# Running Fleet.Comms — chat for the dashboard and the phone

`Fleet.Comms` is the chat service: conversations between a signed-in person and any chat-enabled
agent, from the dashboard's Chat view and the phleet-ios app. It also hosts the optional
[conversation journal](comms-journal.md). This document covers running it.

## What it does, and what it does not

**It does:** one conversation per person and agent — open or resume, send, steer, cancel, catch up,
and a WebSocket stream — with optional image attachments, and a phone notification for a reply that
arrives while no client is attached.

**It does not sign anyone in.** The orchestrator owns users, devices and credentials
([sign-in](sign-in.md)); Comms asks it who each caller is, on every request, and keeps no credential
store, no volume and no operator commands for devices.

## Enabling it

It is **off by default** and starts only when you ask for it.

```bash
./setup.sh          # answer "y" to "Enable Fleet.Comms?" and to durable conversations
```

To opt in on an existing install, including one that previously answered no:

```bash
./setup.sh --comms
./upgrade.sh
```

`--comms` runs only the Comms question chain, revisits recorded false answers and asks about media
while its endpoint is blank. It never changes a recorded true, store, bucket or credential. It builds
and starts nothing, and may be combined only with `--dry-run`. The image is built only when
`FLEET_COMMS_ENABLED=true`, so declining costs nothing.

The service sits behind a Compose profile. `setup.sh` and `upgrade.sh` pass `--profile comms`
explicitly when you have enabled it. Driving it by hand:

```bash
docker compose --profile comms up -d fleet-comms
```

## One address

Clients reach Comms through **the dashboard's address**: the dashboard's nginx proxies `/v1/` to it,
WebSocket upgrade included, next to `/api/` and `/ws` for the orchestrator. The phone is given that
one address in its setup code, and the browser chats same-origin.

`FLEET_COMMS_BIND` (default `127.0.0.1:3500`) still publishes Comms on the host, on loopback, but no
client needs it. Leave it on loopback.

## The routes

| Method | Path | Purpose |
|---|---|---|
| `GET` | `/v1/session` | who is signed in, and the server's limits |
| `GET` | `/v1/conversations` | the caller's conversations, most recently active first |
| `POST` | `/v1/conversations` | open or resume a conversation with an agent (`agent`, `externalRef`) |
| `GET` | `/v1/conversations/{id}/events` | catch-up |
| `POST` | `/v1/conversations/{id}/submissions` | create **or** steer |
| `POST` | `/v1/conversations/{id}:cancel` | stop the current turn (`current`) or all of it (`all`) |
| `POST` | `/v1/conversations/{id}/cursor` | advance the durable cursor |
| `GET` | `/v1/conversations/{id}/stream` | WebSocket upgrade |

With an attachment root configured, `POST /v1/conversations/{id}/attachments` and
`PUT`/`GET /v1/attachments/{id}/content` are added. With conversations off, **nothing** is mapped:
every path is a `404` from the router. Full shapes: [`docs/first-party-api.md`](first-party-api.md).

**Credentials.** A browser presents its `fleet_session` cookie, and a write must also carry
`X-Fleet-Request: 1`; an app presents its access token as a bearer. Comms forwards the credential to
the orchestrator's `POST /internal/auth/check` under a `cj1.auth-check.fleet-comms` token, remembers a
definite answer for at most 10 seconds, and asks again, fresh, on every ping of an open stream (every
20 seconds), so a revoked device loses its socket within that bound. **An answer it could not get is
`503`, never a pass.**

## Durable conversations

Off unless configured: with no `FLEET_COMMS_CONVERSATION_DB` there is no route, no database
connection and no background loop.

### What it needs

A MySQL 8.0 database and a broker. The example compose ships a `comms-mysql` service under the same
`comms` profile, with **no published host port** and its own named volume.

**Two database accounts, and the split is the point.** The service runs as an account holding
`SELECT`, `INSERT`, `UPDATE`, `DELETE` and **no DDL grants**. Migrations use a separate account that
has them, and the running container is never given it.

| Key | What it is |
|---|---|
| `FLEET_COMMS_CONVERSATION_DB` | runtime connection string. **Its presence enables the feature** |
| `FLEET_COMMS_CONVERSATION_MIGRATION_DB` | DDL connection string, for `conversations migrate` only |
| `FLEET_COMMS_JOURNAL_KEY` | the key Comms shares with the orchestrator. **Required with conversations on**, journal or not: it signs Comms's sign-in questions and every agent's chat token. `setup.sh` and `upgrade.sh` generate it |
| `FLEET_COMMS_SOUTH_BIND` | the agent-facing listener. Default `http://0.0.0.0:8082` |
| `FLEET_COMMS_BROKER` | AMQP connection for the two outboxes |
| `FLEET_COMMS_CLAIM_RETENTION` | ⚠️ see below |

### ⚠️ The south listener is never published

It carries an administrative surface: a caller who reached `/turns:commit` could write a terminal for
someone else's turn. It is reached by agent containers on the Docker network and by nothing else —
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
migration. Add a new forward-only script instead.

### Chat with every agent

- **A conversation belongs to one person and one agent.** Both clients keep one thread per agent and
  open it with `externalRef: "main"`, so the phone and the browser share it.
- **Chat is on by default for every agent.** Turn it off per agent in the dashboard (agent settings →
  Chat). It applies only where it can: Comms runs with the shared key, and the agent's unique name is
  usable as a routing key (`[A-Za-z0-9_-]{1,128}`). Anywhere else the agent simply has no chat — it is
  never a reason provisioning fails. **Changes take effect on reprovision.**
- **Provisioning configures the agent.** A chat agent's generated settings get a `Conversations`
  section: the listener address (`Chat:SouthUrl` on the orchestrator, default
  `http://fleet-comms:8082`), the agent's own token `cj1.south.<agent name>`, and
  `PrimaryHumanPrincipalId` when `FLEET_PRIMARY_HUMAN_USER_ID` is set. Do not attach `Conversations__*`
  Env Refs by hand.
- **Routing.** A command is published to `fleet.conversations` with its conversation's agent as the
  routing key. The queue `fleet.conversations.inbound.<agent name>` is declared by Comms before the
  first publish to it and by the agent when it starts, with the same arguments.
- **One agent's token acts only on its own conversations.** A south call naming another agent's
  command, submission, attempt, conversation or attachment is answered as an unknown conversation.
- **Chat turned off** for an agent: sending, cancelling or attaching to its conversations answers
  `409 agent_unavailable`; reading history still works.
- **Conversations from before #530** have no agent. They are not listed and never routed; their
  pending commands were retired by migration 0007, and they age out under retention.

#### ⚠️ Heartbeat and lease

The store's lease is **120 seconds** and the agent renews every **30** by default — four renewals per
lease, so a lost renewal is survivable. The agent refuses to start with a heartbeat above **half**
the lease.

**The gap that constant cannot close:** the agent cannot read *your* configured lease at runtime. If
you lower `LeaseDuration` on the service below twice the agent's heartbeat, the agent will start
happily and the store will abandon attempts on healthy turns — reported as
`turn.outcome_unknown { attempt_abandoned }`, which reads as a store fault. **If you shorten the
lease, shorten the agent's `Conversations__HeartbeatInterval` in the same change.**

#### Priority and cancel

A message from the deployment's primary human gets what their Telegram message gets: the priority
lane and eligibility to steer a running turn. `POST /v1/conversations/{id}:cancel` stops that
conversation's current turn (`scope: "current"`) or all of its work (`"all"`); the client receives
`control.ack`, then the stopped turn's `turn.canceled`. A cancel reaches only its own conversation —
never a Telegram turn or another conversation's.

#### Reply notifications

When an agent's answer commits and no client has a stream open on that conversation, Comms asks the
orchestrator (`POST /internal/push/chat-reply`, its own `cj1.chat-push.fleet-comms` token) to notify
the phone: "<agent> · New reply", identifiers only, never the text. It needs push notifications to be
on ([push notifications](push-notifications.md)); otherwise nothing is sent. A notification that
cannot be delivered is simply lost — the reply itself is durable.

### The conversation journal (opt-in)

A durable, text-only record of Telegram messages, written by your own agent runtimes to an internal
listener. It needs durable conversations and lives in the same database. Full contract:
[comms-journal.md](comms-journal.md).

1. In `.env`: `FLEET_COMMS_JOURNAL_ENABLED=true` and a `FLEET_COMMS_JOURNAL_KEY`
   (`openssl rand 48 | base64 | tr '+/' '-_' | tr -d '='`). `setup.sh` asks once and generates the
   key; `upgrade.sh` never changes the decision.
2. `docker compose run --rm fleet-comms-ops conversations migrate` applies 0004. It is additive and
   runs whether or not the journal is on.
3. Recreate `fleet-comms`. The listener binds `http://0.0.0.0:8083` on the Docker network. ⚠️ It is
   **never** published and never proxied; publishers are containers on the same network.
4. Mint one token per publishing runtime:
   `docker compose run --rm fleet-comms-ops journal token --purpose ingest --subject <runtime-name>`.

The workflow-activity group (`FLEET_GROUP_CHAT_ID`) is always excluded; setup's "no group" value
`0` is ignored. To turn the journal off, set the flag to `false` and recreate: the tables and rows
stay.

**Tool sends and read tools (slice 5).** No migration.

- **Rollout order:** Comms first (so `agent_tool` records are accepted), then `fleet-telegram`
  (recreated with `Journal__ToolSendReceipts`, which compose sets from `FLEET_COMMS_JOURNAL_ENABLED`),
  then agents. Any other order is harmless: receipts wait in the agent's queue or are unroutable,
  and `agent_tool` records an older Comms refuses go to the agent's `dead/`; move them back to
  `pending/` after the upgrade.
- **Read tools, per agent:** set the agent's `fleet-comms-journal` endpoint URL to
  `http://fleet-comms:8083/journal/v1/mcp` and grant it the tools
  `mcp__fleet-comms-journal__search_messages`, `mcp__fleet-comms-journal__get_message` and
  `mcp__fleet-comms-journal__get_conversation`, then reprovision. Provisioning already mints the
  `read` bearer for any agent with that endpoint; providers without header support fail closed. Never add
  these to a template. To revoke, remove the endpoint and tools and reprovision.
- **Scope `all`:** list the subject in `FLEET_COMMS_JOURNAL_READ_ALL_SUBJECTS` in `.env` and recreate
  `fleet-comms`. The config API and the dashboard deny this key on purpose. Everyone else reads only
  messages their own bot observed.
- **Rollback:** revert and redeploy; rows already written stay valid. Delete leftover
  `fleet.journal.tool-sends.*` queues (journal-off agents delete their own at startup).
- **Delivery-based capture (#439):** agent image only — no Comms, `fleet-telegram`, schema, config,
  grant or wire change. Once its image updates, a journal-enabled agent journals every own-bot
  `send_message`/`send_to_ceo` delivery whatever turn made the call, including receipts already
  waiting in its queue; sends before the deploy are never backfilled. Schedule the normal agent
  restart outside in-flight review or delegate steps. Per-agent mitigation: turn that agent's
  journaling off and reprovision. Global rollback: revert and redeploy agents; the old consumer again
  excludes non-human, edge and post-restart receipts, and rows already written stay. Removal of rows
  is only through the existing two-step `journal purge`, on an operator decision.

### Retention is a garbage-collection horizon, not deletion

Nothing here serves a request to erase anything. Rows age out; ephemeral events are pruned without
announcing a gap, because their absence is not a loss, and durable events are pruned with the
retained floor advanced in the same transaction so a reader is told exactly what it can no longer
have.

## Journal media (slice 4)

The journal's object store, for installs that archive attachment bytes. Off unless
`FLEET_COMMS_MEDIA_ENDPOINT` is set to a non-blank value, and off means the bucket is not deployed,
fresh installs generate no credentials. Disabling removes containers, not saved keys or volumes.

⚠️ **There is no `FLEET_COMMS_MEDIA_ENABLED`.** An earlier version of this page named a boolean
that no script, compose file or setting ever implemented, and an operator who set it would have
gotten nothing. `FLEET_COMMS_MEDIA_ENDPOINT` is the switch: a blank or absent value is off, exactly
as it is for Comms and for `upgrade.sh`'s profile decision.

### What it adds

Fresh installs use `comms-seaweedfs` and `comms-seaweedfs-init` under
`comms-media-seaweedfs`. Existing MinIO installs keep `comms-minio` and `comms-minio-init`
under `comms-media`, with their names, volumes and service bodies unchanged.
Both use the **internal** `comms-media` network with **no published ports**.
The runtime pair reaches Comms, its ops CLI and the selected store's identity provisioner;
it is not an administrative credential.

### SeaweedFS: fresh media

Apache-2.0 image, pinned to the same image tested by CI:
`chrislusf/seaweedfs:4.48@sha256:4e61d15fd35994cb1e43e1e553dff106794841fd9a99ade2fc8c8bfce4d7872d`.
`start.sh` writes exactly one runtime identity, with `Read:<bucket>`, `Write:<bucket>` and
`List:<bucket>` only, before starting any listener. No anonymous or Admin identity exists.
Bucket names must match `^[a-z0-9][a-z0-9-]{1,61}[a-z0-9]$`; credential pairs use
`^[A-Za-z0-9+/=_-]{8,128}$`, and the signing key uses `^[A-Za-z0-9]{32,128}$`.

`FLEET_COMMS_SEAWEEDFS_SIGNING_KEY` is generated with `openssl rand -hex 32` and passed as
`WEED_JWT_FILER_SIGNING_KEY` **only** to the server. Without it the S3 administrative gRPC
handlers would accept unauthenticated mutations; the entrypoint refuses to start without it.
It is never sent to clients/init and is hidden from the config API.

| Listener | Port(s) | Bind / protection |
|---|---|---|
| master HTTP / gRPC | 9333 / 19333 | loopback only |
| volume HTTP / gRPC | 8080 / 18080 | loopback only |
| filer HTTP / gRPC | 8888 / 18888 | loopback only |
| S3 HTTP | 8333 | internal network, SigV4 runtime identity |
| S3 gRPC | 18333 | internal network, signing-key-authenticated mutations |
| HTTPS, Iceberg, Lance, metrics, pprof, SFTP, WebDAV, IAM, MQ | none | explicitly disabled |

The init shares the server's network namespace, **not** its filesystem, and holds no keys.
It checks the bucket through `weed shell`, creates it only when absent, and propagates errors.
`service_healthy` orders it after the server; `on-failure` retries a failed init.

```bash
cd <checkout>/fleet
docker compose --profile comms --profile comms-media-seaweedfs up -d
```

### MinIO: legacy media only

Keep `FLEET_COMMS_MEDIA_STORE=minio`; no migration is supported. Its images are no longer
published, so setup/upgrade check the local images **before** stopping anything and never pull them.
A legacy install without a store key gets a one-time `minio` record when its endpoint or root user
is present, including one that previously disabled media. Existing objects are never copied,
re-pointed or deleted automatically.

The internal network is not belt-and-braces. It is what makes "the bucket is not reachable from
outside the compose project" a property of the network rather than of nobody having remembered a
`ports:` line — and `DeploymentKeyLockstepTests` fails the build if a host port ever appears on the
media service or a service without credentials is given them.

`comms-minio-init` creates the bucket and a **scoped runtime policy**
(`s3:GetObject,PutObject,DeleteObject,ListBucket,AbortMultipartUpload` on that one bucket) and
writes the two keys `fleet-comms` reads. It reads its own credentials from the environment; the
policy document keeps the bucket name as a placeholder that `init.sh` substitutes, so the file in
the repo contains no deployment-specific value.

⚠️ **`comms-minio-init` runs on `minio/mc`, pinned to
`minio/mc:RELEASE.2025-08-13T08-35-41Z`.** The published release image is built from upstream
`Dockerfile.release` on `ubi9/ubi-micro`. It **does** carry a shell (`/bin/sh`, `/bin/bash`) and
coreutils-single — the "no shell at all" story that used to be written here came from reading the
dev `Dockerfile` (`FROM scratch`), not the release one. What it does **not** carry is `sed`, `awk`,
`grep` or `envsubst`.

That is the constraint the script is written against: the substitution is done by bash
(`${var//pattern/replacement}`), the shebang is `#!/bin/bash`, and the compose entrypoint invokes
`/bin/bash` — a POSIX `sh` rejects that substitution with "Bad substitution" (measured on dash
0.5.12), so both places say bash rather than relying on the base image's `/bin/sh` happening to be
bash. The pin matters because the `mc` behaviour `init.sh` depends on (alias credentials on stdin,
the `mc admin policy` verbs) is release-specific; an untagged `minio/mc` is a different client on
every pull.

`scripts/check-minio-init-deps.sh` (CI job "MinIO init script dependencies") fails the build if the
script ever calls a binary other than `mc`. That is stricter than the image requires, on purpose:
the old `sed` call exited 127 *after* the bucket and the runtime user existed, leaving a scoped user
with no policy and every call answered `Access Denied`, and no check we had could see it.

`scripts/verify-minio-init-in-image.sh` is the **host-side** proof, and it can only run on the deploy
host: it prints the pinned image's identity (image id, `mc` version, and which of `sed`/`awk`/`grep`/
`envsubst` are actually present), runs the repo's `init.sh` inside the pinned image using the same
`:/init:ro` mount and `/bin/bash` entrypoint as compose, then lists the bucket with **only** the
scoped runtime credentials — which is what proves the policy is *attached*, not merely created. Run it
before enabling media on a new host; CI cannot, because MinIO publishes no pullable image.

It creates **its own network and its own MinIO server** and never names the live bucket. That is
load-bearing: `init.sh` hardcodes the policy name `comms-journal-runtime`, so a verifier pointed at
the live server would create that policy over the live one and then remove it during cleanup — the
exact `Access Denied` failure the provisioner exists to prevent. The compose service name is aliased
inside the container to the disposable server, which is what lets `init.sh` run byte-for-byte
unmodified. It needs `FLEET_COMMS_VERIFY_SERVER_IMAGE` because `minio/mc` ships the client only.

⚠️ Every docker flag goes **before** the image name. `docker run … "$IMAGE" --entrypoint /bin/bash -c …`
reads correctly and does nothing of the kind: everything after the image is argv for the container's
`ENTRYPOINT`, and this image's is `["mc"]`, so the flags are handed to `mc`.
`MinioVerifyHelperInvocationTests` fails the build on that, on a live network or bucket name, on a
missing `--entrypoint`, and on a swallowed cleanup error — the four defects the first version had.

### Keys

| Key | What it is |
|---|---|
| `FLEET_COMMS_MEDIA_STORE` | scripts-only choice: `seaweedfs` or legacy `minio`; preserve once recorded |
| `FLEET_COMMS_SEAWEEDFS_SIGNING_KEY` | SeaweedFS administrative signing key; server only |
| `FLEET_COMMS_MEDIA_ENDPOINT` | **the switch.** the bucket URL on the internal network; also derives each agent's `Journal:MediaEnabled` |
| `FLEET_COMMS_MEDIA_BUCKET` | default `comms-journal`; `setup.sh` records it, compose defaults it |
| `FLEET_COMMS_MEDIA_ACCESS_KEY` / `_SECRET_KEY` | the **scoped** runtime credentials, not the root ones |
| `FLEET_COMMS_MINIO_ROOT_USER` / `_PASSWORD` | root credentials, used only by the init container and the sidecar |
| `FLEET_COMMS_MEDIA_BACKUP_DIR` | host directory the ops container writes `media backup` output to |

Because the agent-side flag is **derived at provisioning**, changing `FLEET_COMMS_MEDIA_ENDPOINT`
means **reprovisioning** the agents that journal media — restarting a container does not pick it up.
`upgrade.sh` prints that reminder when media is on.

Only the documented runtime/store consumers receive the scoped pair. The config API cannot read
or write `FLEET_COMMS_MEDIA_*`, `FLEET_COMMS_MINIO_*` or `FLEET_COMMS_SEAWEEDFS_*` — they are on the orchestrator's denylist,
because a settings endpoint that can rewrite a bucket credential is a way to move the journal's
archive without ever touching a shell.

### Enabling and upgrading

`setup.sh` prompts for media only with the journal on; setup/upgrade select the store profile
from `FLEET_COMMS_MEDIA_STORE`, but only when Comms is enabled and the endpoint is non-blank.
SeaweedFS is pulled before any startup/shutdown. A missing legacy image or invalid store stops
before any lifecycle change. Both scripts print `Media store: seaweedfs|minio|off`.
The shell `unset` clears **process exports**, never `.env` keys. Disabling removes all inactive
Comms/media containers without deleting volumes. Re-enabling restores the recorded store and bucket.

The dashboard reports a missing journal key as `disabled`, not a fault. It blocks only enabling an
unchecked journal checkbox; a checked box can always be turned off. REST/MCP reject false-to-true
with `journal_not_configured` when the key is missing/invalid, leaving the row unchanged.

### Rollback and acceptance boundary

Revert the change to roll back. For a fresh SeaweedFS install, first set
`FLEET_COMMS_MEDIA_ENDPOINT=`; older scripts otherwise select MinIO while retaining a SeaweedFS URL.
Keep all saved keys and `comms_seaweedfs_data`. Legacy installs are unchanged by the revert.

[#399](https://github.com/anurmatov/phleet/issues/399) owns the core share-store replacement.
Full unmodified cold-host acceptance (AC4b, AC6b, AC11b of #397) is gated on that issue;
#397 stays open until those pass. Cold-host AC4a explicitly excludes only `fleet-minio` and
`fleet-minio-init`; cached-image runs are existing-install evidence, never cold-host evidence.
The deployment smoke tests the pinned image (M1–M9), not a different convenient fixture.

### Backing it up

`media backup --out DIR` from the ops container, into the bind-mounted `FLEET_COMMS_MEDIA_BACKUP_DIR`.
It writes a manifest and re-hashes every object it copies. The acceptance is the round trip, not the
manifest: **`backup`, wipe, `restore`, `journal verify-media` exits 0** — see
`docs/comms-journal.md`.

## TLS

**Comms never terminates TLS**, and neither does the dashboard container. Put TLS in front of the
dashboard's address — the phone connects only over HTTPS — and proxy only that address. Never proxy
the Comms ops listener or its south listener, and do not publish Comms on its own hostname: since
#530 nothing needs it.

### Rate limits

Every Comms route is authenticated and admits 300 requests per 60 seconds **per credential** — a hash
of the bearer or of the session cookie — refusing the rest with `429` and an integer `Retry-After`.
Behind the dashboard's proxy every caller arrives from one address, which is why the key is the
credential. A request with no credential falls back to its address; `FLEET_COMMS_TRUST_PROXY`
(default `false`) decides where that comes from, and should stay `false` unless a proxy in front
**replaces** `X-Forwarded-For`. Sign-in's own limits are the orchestrator's.

## Readiness, and what a `503` means

There is a second listener, bound to loopback **inside the container only**. It is not published and
**must never be proxied**.

| Path | Meaning |
|---|---|
| `GET /health` | the process is running |
| `GET /ready` | with conversations on, one real read against the conversation database succeeded and the applied schema version matches the binary's |

The container's healthcheck targets `/ready`. It does **not** ask the orchestrator: sign-in being
briefly unreachable is answered `503` per request, and a readiness failure would restart Comms over a
dependency it does not own.

- **Container exits non-zero immediately** — a configuration that cannot work, named in the log:
  `chat_key_missing` or `chat_key_invalid` (the shared key), `Comms__OrchestratorUrl`, or a
  `journal_…` code. Nothing is retried, because missing configuration cannot fix itself.
- **`/ready` is `503`** — the conversation database is unreachable, or a migration has not been run
  (or was rolled back), and the body says which.
- **Every chat request is `503`** while `/ready` is fine — the orchestrator cannot be reached from
  Comms, or it refused Comms's own token (the log says `FLEET_COMMS_JOURNAL_KEY differs`).

## Backups

Comms holds no credentials to back up: users, devices and sessions live in the orchestrator's
database and are backed up with it. The conversation database is backed up with ordinary MySQL
tooling (`mysqldump`, a filesystem snapshot of its volume, or your provider's mechanism). Journal
media has its own `media backup` (above).

## Upgrading to one sign-in (#530)

1. `./upgrade.sh`. It removes `FLEET_COMMS_AGENT_NAME`, `FLEET_COMMS_AGENT_LABEL`,
   `FLEET_COMMS_SOUTH_TOKEN` and `FLEET_COMMS_STORE_PROVISIONED` from `.env`, adds
   `FLEET_COMMS_JOURNAL_KEY` when conversations are on and it is missing, and runs
   `conversations migrate` (0007).
2. Sign in to the dashboard once with `ORCHESTRATOR_AUTH_TOKEN`.
3. Reprovision the agents: chat is on for every agent by default. Turn it off where you do not want
   it, and remove any hand-attached `Conversations__*` Env Refs.
4. Connect the phone again from **Credentials → Devices → Connect a phone**. Comms device
   registrations do not carry over.
5. The old `fleet_comms_auth` volume is no longer used. Remove it when you are satisfied:
   `docker volume rm <project>_fleet_comms_auth`.

App chat history from before the upgrade is not carried over. Telegram chats and the journal are
untouched.

## Upgrades

`./upgrade.sh` runs `docker compose down`, regenerates the compose file from the example, rebuilds
and restarts. `fleet:comms` is rebuilt only when `FLEET_COMMS_ENABLED=true`. The profile is resolved
before shutdown, so disabling the service actually stops it. On an enabled upgrade the script waits
for the container to report healthy before declaring success.

## Troubleshooting

| Symptom | Cause |
|---|---|
| Container exits non-zero at startup with `chat_key_missing` or `chat_key_invalid` | `FLEET_COMMS_JOURNAL_KEY` is missing or not a usable key while conversations are on. Run `./upgrade.sh`, or set it to the orchestrator's value |
| Every chat request is `503`, log says `FLEET_COMMS_JOURNAL_KEY differs` | Comms and the orchestrator hold different keys. Recreate both from the same `.env` |
| Every chat request is `503` | the orchestrator is unreachable from Comms (`Comms__OrchestratorUrl`) |
| `409 agent_unavailable` | chat is off for that agent, or that agent's name cannot be a routing key |
| An agent never answers chat | it has not been reprovisioned since chat was turned on: its generated `appsettings.json` has no `Conversations` section |
| `401` on everything | signed out, or an app's access token expired and was not renewed; a browser write without `X-Fleet-Request` is also `401` |
| `429` | 300 requests per 60 seconds per credential |
| Port already allocated | another service holds `FLEET_COMMS_BIND` |
| `comms-seaweedfs-init` restarting | inspect its restart count and bucket creation error; do not weaken credentials |
| `start.sh: <KEY>` and exit 1 | missing/invalid runtime credential, bucket or SeaweedFS signing key; fix the named key |
| Legacy MinIO images missing at upgrade | restore local images or set `FLEET_COMMS_MEDIA_ENDPOINT=`; nothing has been stopped |

Rotate the SeaweedFS signing key: change `FLEET_COMMS_SEAWEEDFS_SIGNING_KEY` in `.env`, then
`docker compose --profile comms-media-seaweedfs up -d --force-recreate comms-seaweedfs`.
Do not change the runtime pair or bucket as part of that rotation.

## Journal attachment fetch rollout and rollback

Upgrade Comms first, then agent images, then grant
`mcp__fleet-journal-files__fetch_attachment` individually with authorized
reprovisioning. No templates or automatic grants are changed. Before merge,
verify the exact head in a throwaway stack and an independently identified
canary: control health/cancel remain on 8080, MCP is loopback-only on 8091,
Comms 8083 is unpublished, and agents have neither byte-store credentials nor
`comms-media` membership. Pinned-provider photo/PDF/20 MiB file and timeout
proof is separate acceptance, not inferred from unit tests.

The rollback lever is removing the grant and reprovisioning; the loopback
listener, server entry and runtime read token disappear. After an image rollback
to an older agent, remove only `{AttachmentDir}/journal/` while the agent is
stopped: its old attachment sweeper does not recurse into that directory. Leave
the journal database and private bucket untouched. Old agents ignore the new
Comms route; new agents with old Comms return `comms_refused`, without a retry
storm.

Healthy fetches grow the fixed `ok` counters. Investigate `integrity_failed` or
`object_missing` with `journal verify-media` on the Comms operator CLI, not by
giving agents bucket access. Rising `comms_refused` indicates rollout order or
read-token problems. Startup, hourly and per-fetch cleanup enforce local TTL and
quota; cleanup failures log only their exception type and the next fetch tries
again. No migration, environment key or compose change is needed for fetch.

## Attachment send rollout (operator-gated)

The send runtime, schema-6 storage, cross-chat configuration and live Comms
routes are implemented. Operator-owned exact-image and real-provider acceptance
(AC13) remains UNRUN; source merge and CI are not live rollout evidence.
Use an explicitly authorized operator rollout plan for the sequence below.
Do not grant `mcp__fleet-journal-files__send_attachment` or enable cross-chat
before that plan permits it.

1. Before migration `0006`, take a conversation-schema dump:

   ```bash
   mysqldump \
     --defaults-extra-file=<client> \
     --single-transaction \
     --skip-lock-tables \
     --no-tablespaces \
     <schema> > <dump>
   ```
   Keep credentials in that protected client file, not command arguments or logs.
   Restore the dump into an isolated schema and compare its base-table count
   with `information_schema.tables` in the source schema. Stop if the counts
   differ or any restore/query fails; dump size alone is not verification.
2. Deploy Comms with the complete parser, migration and new authenticated routes
   first. The older binary refuses a database ahead of its supported schema;
   reverting an image alone is not a schema rollback.
3. Deploy the orchestrator and dashboard, applying the additive EF migration,
   then the complete agent image. Keep grants and switches off during this step.
4. Explicitly grant send to approved agents only. Reprovision and verify same-chat
   acceptance before saving the default-off cross-chat switch. Reprovision again
   for switch-on, then verify live authorized group-to-private delivery.

Comms' **first** `Comms__Journal__TokenKeys` key signs its internal
`cross-chat-authz` credential. That key must remain in the orchestrator's
`Journal__TokenKey` verification set throughout rotation. Stage the verification
set first, then switch Comms' first key, then retire the old key after every
consumer has rotated. If they do not match, internal authorization gets 401 and
Comms must return **503 `authorization_unavailable`**, never relax authorization.
Comms' orchestrator URL is a static service route, not an agent-controlled URL.
A blank URL, timeout or non-200 authorization response also fails closed.

The orchestrator needs outbound HTTPS to Telegram for `getMe` and
`getChatMember`; Comms must never receive a bot token. A cached `getMe` is only
bot identity, not group membership: membership is checked on each authorization
request with a shared four-second budget and at most eight concurrent requests.
No Telegram URL, token, chat id or user id belongs in authorization logs.

Rollback starts by clearing the cross-chat switch and removing the send grant,
then reprovisioning. Stop writers before restoring the verified pre-migration
conversation dump into a clean schema and starting the old Comms binary.
Records captured after that dump are lost on schema rollback; obtain operator
approval for that loss. Restore the prior orchestrator version only after the
new EF column is no longer used, following its migration rollback procedure.

Exact-image and real-provider acceptance is operator-owned and **UNRUN** until
recorded: isolated schema-6 conversation storage and media credentials, the
matching orchestrator route and EF schema, one test bot, a test group with two
allowed private requesters plus an unrelated account, and an exact-image
real-provider agent with send grant off then on. Verify same-chat, authorized
group-to-private, no caption, group/workflow refusal, member leave, switch-off
and copied-token revocation. Unit tests and CI do not replace this check.
