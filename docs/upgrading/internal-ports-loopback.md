# Upgrading: four host ports move to loopback, and four images are pinned

Two changes in `docker-compose.example.yml` take effect the next time the stack is recreated
(`./upgrade.sh` regenerates `fleet/docker-compose.yml` from it).

## Ports that are no longer reachable from other machines

| Port | Service | What an unauthenticated caller could do |
|---|---|---|
| 7233 | Temporal frontend | Start, signal and terminate workflows, including the approval gates |
| 8080 | Temporal UI | The same, in a browser |
| 3100 | fleet-memory | List, read, change and delete memories over its REST API |
| 3300 | fleet-bridge | Send a prompt to the CTO agent |

None of these services checks who is calling. They used to be published on every interface of the
host; they are now published on `127.0.0.1` only, like RabbitMQ, MySQL and MinIO already were.

Nothing inside Fleet changes. Agents, the orchestrator, the Temporal bridge and the dashboard reach
these services over the Docker network (`temporal-server:7233`, `fleet-memory:3100` and so on) and
never used the host ports.

What does change: a browser or tool on **another machine** can no longer open
`http://<host>:8080`, connect a Temporal client to `<host>:7233`, or call fleet-bridge's MCP endpoint
at `<host>:3300`. On the host itself, `http://localhost:8080` works as before.

Not changed: the dashboard (3700), the orchestrator (3600) and fleet-playwright (3200) stay
published on every interface.

### Reaching one of them from another machine

Prefer a tunnel, which leaves the port closed to the network and opens only the one you need:

```bash
ssh -L 8080:127.0.0.1:8080 <user>@<host>   # Temporal UI: then open http://localhost:8080
ssh -L 3300:127.0.0.1:3300 <user>@<host>   # fleet-bridge: point the MCP client at localhost:3300
```

A dashboard built with `VITE_TEMPORAL_UI_URL` links to that address from the browser, so the link
only opens where the address is reachable.

### Reopening the ports

Add this to `fleet/.env` and recreate the stack:

```bash
FLEET_INTERNAL_BIND_ADDRESS=0.0.0.0   # or the address of one interface
```

One value moves all four ports: there is no per-service setting, so opening fleet-bridge this way
opens Temporal and fleet-memory too. Do this only on a network where every machine is trusted: the
services still take no credential. `setup.sh` and `upgrade.sh` ignore a value exported in the
shell, so `.env` is the only place that decides it. `upgrade.sh` reminds you of this until `.env`
records a choice.

To confirm what the running stack publishes:

```bash
docker ps --format '{{.Names}}\t{{.Ports}}' | grep -E 'temporal|fleet-memory|fleet-bridge'
```

## Pinned images

These images used a floating tag, so each host ran whichever release it happened to pull first and
never moved. Each is now pinned to the release its floating tag resolved to when the pin was written.

| Image | Was | Now |
|---|---|---|
| Qdrant | `qdrant/qdrant:latest` | `qdrant/qdrant:v1.20.0` (`FLEET_QDRANT_VERSION` to override) |
| Kokoro TTS | `ghcr.io/remsky/kokoro-fastapi-cpu:latest` | `…:v0.10.0` |
| Dashboard build stage | `node:20-slim` | `node:20.20.2-slim` |
| Dashboard runtime | `nginx:alpine` | `nginx:1.31.8-alpine` |

`minio/minio` and `minio/mc` are **not** pinned. MinIO no longer publishes them: the registry refuses
an anonymous pull of every tag. A host that still holds the images keeps running them under the
unpinned name, and a pinned tag would be an image it cannot fetch.

### ⚠️ Qdrant: the release you run decides what you may start

A pin moves a host to the pinned release on the next start, in whichever direction that is. Qdrant
supports moving one minor release at a time and never backwards, so compare the **full** release
you run with the one the next start would run (the pin, or `FLEET_QDRANT_VERSION` in `fleet/.env`):

```bash
docker inspect qdrant --format '{{ index .Config.Labels "org.opencontainers.image.version" }}'
```

| You run | Next start would run v1.20.0 | What to do |
|---|---|---|
| v1.20.0, or any v1.19.x | allowed | Nothing |
| v1.20.1 or later, v1.21.x, … | refused: it would downgrade | Set `FLEET_QDRANT_VERSION` to the release you run and keep it until the pin passes it |
| v1.18.x or older | refused: it skips a minor | Step up, below |

The same rule holds for any target: the release you run must be the target's minor at the same or
an older patch, or exactly one minor below it.

**Stepping up**, from v1.17.x as the example: set `FLEET_QDRANT_VERSION=v1.18.<latest patch>` in
`fleet/.env` and run `./upgrade.sh`; then `v1.19.<latest patch>` and run it again; then remove the
key and run it once more to reach the pin.

`upgrade.sh` enforces this before it stops anything, and `setup.sh` before it starts anything. Both
read the release from the existing `qdrant` container and refuse a move the table does not allow.
Setting `FLEET_QDRANT_VERSION` is not a way past the check: it changes the target, and the target
is what is checked. Both scripts take the value from `fleet/.env` only and ignore one exported in
the shell, so a leftover `export` cannot replace the step you saved. Two cases cannot be decided
and are refused as well:

- a `qdrant` container whose image carries no release label;
- Qdrant storage (the `fleet_qdrant_data` volume) with no `qdrant` container to read.

In both, start the container on the release that wrote the storage, or drop the index as described
next. A host with neither a container nor the volume is a fresh install and is not checked.

Qdrant holds an index, not the memories. The memory files under `fleet/memories` are the source of
truth and fleet-memory indexes all of them again on every start. To drop the index, stop the stack,
remove the `qdrant` container and the `fleet_qdrant_data` volume, and start again; the index is
rebuilt.

### Kokoro TTS: a version change

A host that pulled an older `latest` (v0.9.x, for example) moves to v0.10.0 on its next start.
Kokoro keeps no state, so there is nothing to migrate, but the agents' voice replies call its
`/v1/audio/speech` route and that route was **not** exercised against v0.10.0 for this change.
Request one voice reply after upgrading. A failed synthesis is logged by the agent as
`TTS synthesis failed` and the text reply is still delivered.

The two dashboard images are build inputs and need no step.
