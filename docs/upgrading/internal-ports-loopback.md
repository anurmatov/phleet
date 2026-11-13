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

### Reaching the Temporal UI from another machine

Prefer a tunnel, which leaves the port closed to the network:

```bash
ssh -L 8080:127.0.0.1:8080 <user>@<host>   # then open http://localhost:8080
```

A dashboard built with `VITE_TEMPORAL_UI_URL` links to that address from the browser, so the link
only opens where the address is reachable.

### Reopening the ports

Add this to `fleet/.env` and recreate the stack:

```bash
FLEET_INTERNAL_BIND_ADDRESS=0.0.0.0   # or the address of one interface
```

One value moves all four ports. Do this only on a network where every machine is trusted: the
services still take no credential. `setup.sh` and `upgrade.sh` ignore a value exported in the
shell, so `.env` is the only place that decides it.

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

### ⚠️ Qdrant: check the running release before you upgrade

A pin moves a host to the pinned release on the next start, in whichever direction that is.

```bash
docker inspect qdrant --format '{{ index .Config.Labels "org.opencontainers.image.version" }}'
```

- **The same minor as the pin, or one below it** (v1.19.x or v1.20.x): nothing to do.
- **Older**: Qdrant supports upgrading one minor version at a time. Set `FLEET_QDRANT_VERSION` in
  `fleet/.env` to the release you run, upgrade Fleet, then raise it one minor at a time (v1.18.x,
  v1.19.x, …), recreating `qdrant` at each step, until it matches the pin; then remove the key.
- **Newer than the pin**: do not downgrade. Set `FLEET_QDRANT_VERSION` to the release you run and
  keep it until the pin passes it.

Qdrant holds an index, not the memories. The memory files under `fleet/memories` are the source of
truth and fleet-memory indexes all of them again on every start. If Qdrant will not start on its old
storage, stop the stack, remove the `qdrant_data` volume (`fleet_qdrant_data` under the project
name `setup.sh` uses) and start again; the index is rebuilt.

Kokoro keeps no state, and the two dashboard images are build inputs. They need no step.
