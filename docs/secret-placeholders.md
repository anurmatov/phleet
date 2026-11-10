# Secret placeholders

Off by default (`FLEET_SECRETS_ENABLED=false`). When on, a `.env` key that has a **secret binding**
is never written into an agent container. The agent gets a placeholder (`fleet_placeholder_`
followed by 43 random characters), and the `fleet-secrets` service swaps it for the real value only
on an HTTPS request to one of the binding's hosts, in the binding's header.

The prefix says what the value is and matches no vendor's key format, so a placeholder that ends up
in a commit, a log or a chat does not trip secret scanning, and a real key is never taken for one.

A key without a binding is delivered exactly as before: its value goes into the container
environment. An agent that references no bound key is created with byte-identical Docker input,
whether the feature is on or off.

All examples below use synthetic names and values.

## How it works

```
agent container                            fleet-secrets                     api.example.test
EXAMPLE_API_KEY=fleet_placeholder_…  HTTPS  checks the grant, the source      receives
/etc/hosts: api.example.test ──────────▶  address, the host and the header, ──────▶  Authorization:
            → 10.213.96.2                 swaps the placeholder                       Bearer <real value>
```

1. **Binding.** The operator names a `.env` key, the exact hosts it may be sent to and the request
   header that carries it (default `Authorization`). Stored in `secret_bindings`.
2. **Grant.** An agent that has the key as an Env Ref holds a grant. Nothing else is needed.
3. **Provision.** The orchestrator mints a new placeholder for every bound Env Ref on every
   provision. The container gets the placeholder in its environment, an `/etc/hosts` entry pointing
   each bound host at `fleet-secrets`, the forwarder's CA certificate (with `NODE_EXTRA_CA_CERTS`,
   `REQUESTS_CA_BUNDLE` and `SSL_CERT_FILE` set so common runtimes find it), and a fixed address on
   the internal network `fleet-secrets-net`. Only the placeholder's SHA-256 is stored.
4. **Request.** A tool in the agent calls the bound host as usual. The request reaches
   `fleet-secrets`, which answers TLS with a certificate from its local CA, validates the
   placeholder and relays the request to the real host with the real value.
5. **State.** The orchestrator writes one file, `secret-proxy/state/state.json` under the fleet
   base directory: the bindings with their current `.env` values, and the grant hashes. It rewrites
   the file every 10 seconds, changed or not. The forwarder reads it on every request and refuses
   it once it is older than 120 seconds. It has no database access and does not read `.env`.

The forwarder honours a placeholder only when all of these hold:

| Check | Refusal |
|---|---|
| It is a grant in the current state | 403 `secret_grant_unknown` |
| The request comes from that agent's fixed address | 403 `secret_grant_unknown` (same answer, on purpose) |
| The host is one of the binding's hosts | 403 `secret_destination_mismatch` |
| It sits in the binding's header, and nowhere else in the request head | 403 `secret_slot_mismatch` |
| `.env` has a usable value (for a GitHub App: a token could be minted) | 503 `secret_unavailable` |

Every refusal happens before any upstream call and carries the header `X-Fleet-Secrets-Error`, so an
agent can tell it from the bound host's own answer. A request with no placeholder is relayed
unchanged.

## Enable it

1. Set `FLEET_SECRETS_ENABLED=true` in the fleet `.env`.
2. Run `./upgrade.sh` (or `./setup.sh`). It builds `fleet:secrets`, starts the `secrets` compose
   profile, creates `fleet-secrets-net` and waits for the forwarder to create its CA.
3. Create bindings (below), attach the keys to agents as Env Refs, and reprovision those agents.

The network uses `10.213.96.0/22` with the forwarder at `10.213.96.2`. If that overlaps a network on
the host, set `FLEET_SECRETS_SUBNET` and `FLEET_SECRETS_ADDRESS` before the first start. An agent's
address is the subnet base plus 16 plus its database id, so a /22 serves agent ids up to 1006.

To turn it off again: remove the bound Env Refs or the bindings first, set
`FLEET_SECRETS_ENABLED=false`, run `./upgrade.sh`, then remove the leftover container with
`docker compose --profile secrets rm -sf fleet-secrets` in the fleet directory. While a bound Env
Ref remains, its agent refuses to provision (`secret_proxy_disabled`) rather than receive the value.

## Bind a secret

In the dashboard: **Credentials → Secret bindings → Bind a key**. Over REST, with the operator
token:

```sh
curl -X PUT http://localhost:3600/api/secret-bindings/EXAMPLE_API_KEY \
  -H "Authorization: Bearer $ORCHESTRATOR_AUTH_TOKEN" -H "Content-Type: application/json" \
  -d '{"hosts": ["api.example.test"]}'
```

| Field | Default | Meaning |
|---|---|---|
| `hosts` | required | Up to 8 exact lowercase DNS names. No scheme, port, path, wildcard or IP address. |
| `headerName` | `Authorization` | The one request header the value may be injected into. |
| `exposeAs` | the key itself | Up to 4 environment variable names that carry the placeholder in the container. |
| `kind` | `static` | `static`, or `github_app` (below). |
| `appIdEnvKey` | none | GitHub App only: the `.env` key holding the App id. |
| `agentAttachable` | `false` | Whether the `manage_agent_env_refs` MCP tool may attach the key. |

`GET /api/secret-bindings` lists the bindings, which agents reference each key, and
`pendingReprovision`: agents whose container predates the binding and so still holds the value
itself. It never returns a value or a placeholder.

In `Authorization` the placeholder may be a Bearer or `token` credential or a Basic password; the
scheme and user name are kept. In any other header the placeholder is replaced where it stands.

A binding is the trust decision, so only the operator can write one: REST behind
`ORCHESTRATOR_AUTH_TOKEN`, or the dashboard. No MCP tool creates, changes or deletes a binding, and
`manage_agent_env_refs` refuses to attach a bound key unless its binding says `agentAttachable`.
Removing an Env Ref is always allowed, by anyone who could remove it before.

Not bindable: a `TELEGRAM_*_BOT_TOKEN` (the agent process uses it in a URL path), a hosted-model
key (already kept out of the agent by its own forwarder), and any name the agent process reads as
its own configuration (`Section__Key`).

## GitHub App

Bind the App's private key and no agent holds it, or any token minted from it:

```sh
curl -X PUT http://localhost:3600/api/secret-bindings/GITHUB_APP_PEM \
  -H "Authorization: Bearer $ORCHESTRATOR_AUTH_TOKEN" -H "Content-Type: application/json" \
  -d '{"kind": "github_app", "appIdEnvKey": "GITHUB_APP_ID",
       "hosts": ["api.github.com", "github.com", "uploads.github.com"],
       "exposeAs": ["GH_TOKEN", "GITHUB_TOKEN"]}'
```

Then attach `GITHUB_APP_PEM` as an Env Ref to each agent that should use GitHub, and reprovision.

- `fleet-secrets` signs the App JWT, lists the installations and mints installation tokens itself.
  Tokens stay in its memory and are re-minted five minutes before they expire.
- The agent's `GH_TOKEN` and `GITHUB_TOKEN` are the placeholder. `entrypoint.sh` sees that and runs
  `gh-auth.sh --placeholder`: the `gh` wrapper and the git credential helper are installed, and no
  key file, token file or cron refresh exists in the container.
- Which installation: the account that owns the repository, organisation or user in the request
  URL. A request that names none uses the primary installation, the last organisation GitHub lists
  or else the first installation. The `gh` wrapper appends the owner it resolved to the placeholder
  (`fleet_placeholder_….owner`), because most `gh` commands call GraphQL, whose URL names no owner.
  This is the same routing `gh-auth.sh` has always applied.
- Once `GITHUB_APP_PEM` is bound, the orchestrator no longer injects it into every agent. An agent
  without the Env Ref has no GitHub credential at all.
- A second App is a second binding (for example `GITHUB_APP_PEM_OVERRIDE` with
  `appIdEnvKey: GITHUB_APP_ID_OVERRIDE`). An agent may hold only one binding per exposed name.

## Revocation and rotation

| Action | Takes effect |
|---|---|
| Remove the Env Ref from an agent (dashboard, REST or MCP) | When the call returns |
| Change or delete a binding | When the call returns |
| Reprovision or deprovision an agent | The old placeholder dies when the old container is removed |
| Delete an agent | When the call returns |
| Change the value in `.env` | Within 10 seconds, with no agent restart |
| Remove the value from `.env` | Within 10 seconds; requests then get 503 `secret_unavailable` |
| Any of the above while the state cannot be written | The call fails; in force at once if the old state could be withdrawn, else within 120 seconds |

The save itself rewrites the state file, and the forwarder reads the file on every request, including
on a connection that was already open. A background pass every 10 seconds keeps the file fresh and
is what picks up `.env` edits. A request already in flight completes.

### When the state cannot be written

The file is the only way a revocation reaches the forwarder, so a failed write is never reported as
success, and a state that stops being refreshed stops being honoured:

1. **The call fails.** The change is in the database, but REST answers 503 with
   `fault: secret_proxy_state_write_failed`, `saved: true`, `enforced: false`; the MCP tool says the
   change was "NOT published"; a deprovision reports that the grants were not revoked.
2. **The old state is withdrawn.** The orchestrator deletes the state file, and with no state the
   forwarder refuses every request to every bound host. The grant that was just removed stops
   working at once, and so does everything else until the next write succeeds (the background pass
   retries every 10 seconds).
3. **If it cannot even delete the file,** the forwarder's age bound ends it: a state older than
   120 seconds is refused whole. The same bound applies when the orchestrator is down.

So a revocation is in force when the call returns success, and otherwise within 120 seconds at the
latest. The bound is `SecretProxy__MaxStateAgeSeconds` on the `fleet-secrets` service (30 to 86400).
Lowering it tightens that worst case and shortens how long the orchestrator may be down before
agents lose their bound hosts.

Deleting a binding is refused (409, with the agent names) while any agent references its key,
because the next provision of that agent would otherwise write the value itself into its container.
When that is what you want, the way back to plain delivery, say so:
`DELETE /api/secret-bindings/{key}?allowPlainDelivery=true`. The grants die at once, and each
listed agent receives the value itself on its next provision.

A new host on a binding reaches an agent's `/etc/hosts` only on its next provision. A removed host
stops being served at once.

## Security limits

- **Use, not disclosure, is what a grant allows.** An agent with a grant can send any request the
  bound host accepts with that credential. Bind narrow credentials to few hosts.
- **HTTPS on port 443, to exact host names.** No plain HTTP, SSH, database or message-broker
  protocols, no WebSocket or other protocol upgrade on a bound host, and no host without a dot.
- **Headers only.** A value is never written into a URL or a body, so it cannot be posted somewhere
  and read back. A placeholder in a body is relayed as the inert string it is.
- **A bound host that echoes the credential leaks it.** The forwarder withholds a response whose
  headers contain the value (502 `secret_reflected`); it does not inspect response bodies. Do not
  bind a host that reflects request headers in a body.
- **A placeholder is a capability inside `fleet-secrets-net`.** It works only from its agent's
  fixed address, so one copied into a chat or a memory cannot be replayed by another agent. A root
  process on that network that can spoof another container's address is not defended against.
- **The forwarder holds the bound values and the CA key.** Compromising it exposes both. It holds
  nothing else: not `.env`, not the database, not the Docker socket.
- **The CA is trusted only inside granted agent containers.** Whoever holds
  `secret-proxy/ca/ca.key` can impersonate any host to those agents. The directory is created
  owner-only; keep it that way and out of backups that others can read.
- **An agent with the Docker socket, or with `ORCHESTRATOR_AUTH_TOKEN` or
  `ORCHESTRATOR_CONFIG_TOKEN`, is outside the boundary.** It can read `.env` by other means.
- **Bindings are only as protected as the orchestrator's write API.** With an empty
  `ORCHESTRATOR_AUTH_TOKEN` that API is unauthenticated, for bindings as for every other write.
- **Not covered.** Provider OAuth credentials (Claude, Codex, Gemini); tokens the agent process
  itself uses (its Telegram bot, the journal, MinIO); SSH keys; anything not sent in an HTTP header.
- **Steering can be bypassed, harmlessly.** A request that does not reach the forwarder (a proxy,
  a hard-coded address, a tool with its own resolver) carries only the placeholder, which is worth
  nothing outside the secrets network. The request fails; nothing leaks.
- **Availability.** While `fleet-secrets` is down, granted agents cannot reach their bound hosts,
  and the same holds once the orchestrator has been down, or unable to write the state, for longer
  than the age bound (120 seconds by default). Every other destination is unaffected, because only
  bound hosts are steered.

## Faults at provision time

All are raised before Docker is called. None falls back to delivering the value. A reprovision
checks every one of them, and that the forwarder state can be written, before it stops the old
container, so a refusal leaves a working agent working. The one exception is
`secret_proxy_network_unavailable`, which only Docker can report: by then the old container is gone.

| Fault | Meaning |
|---|---|
| `secret_proxy_disabled` | The agent references a bound key and `FLEET_SECRETS_ENABLED` is not `true`. |
| `secret_proxy_setting_invalid`, `secret_proxy_subnet_invalid`, `secret_proxy_address_invalid` | A setting is wrong. Logged at startup too. |
| `secret_proxy_ca_missing` | `fleet-secrets` has not created its CA yet. |
| `secret_proxy_address_exhausted` | The subnet has no address for this agent id. |
| `secret_expose_conflict` | Two bound keys, or a bound and a plain key, use one variable name. |
| `secret_proxy_network_unavailable` | The container could not join `fleet-secrets-net` at its address. It is removed, not started. |

## Files and settings

| Path under the fleet base directory | Written by | Holds |
|---|---|---|
| `secret-proxy/state/state.json` (0600, directory 0700) | orchestrator | Bindings, current values, grant hashes |
| `secret-proxy/ca/ca.crt` | `fleet-secrets`, first start | The CA certificate mounted into granted agents |
| `secret-proxy/ca/ca.key` (0600) | `fleet-secrets`, first start | The CA key; never mounted anywhere else |

| `.env` key | Default | Used by |
|---|---|---|
| `FLEET_SECRETS_ENABLED` | `false` | Orchestrator `SecretProxy__Enabled`; selects the `secrets` compose profile |
| `FLEET_SECRETS_SUBNET` | `10.213.96.0/22` | `fleet-secrets-net`; orchestrator `SecretProxy__Subnet` |
| `FLEET_SECRETS_ADDRESS` | `10.213.96.2` | The forwarder's address; orchestrator `SecretProxy__Address` |

All three are read at startup. A change needs `./upgrade.sh`, and a changed subnet also needs every
granted agent reprovisioned.

Replacing the CA: stop `fleet-secrets`, remove both files in `secret-proxy/ca/`, start it, then
reprovision every agent that holds a placeholder. With only one of the two files present the
forwarder refuses to start rather than silently create a new CA.

The `fleet:secrets` image is built on the released .NET runtime, not the preview base the other
services use: on the preview base the forwarder failed to validate an upstream certificate in the
end-to-end harness. The cause was not established.

## Troubleshooting

- **An agent gets 403 with `X-Fleet-Secrets-Error`.** The code names the cause; see the table above.
  `secret_grant_unknown` after a reprovision usually means a tool cached the old placeholder.
- **TLS errors to a bound host from a tool.** The tool does not use the system trust store. The
  orchestrator sets `NODE_EXTRA_CA_CERTS` (Node), `REQUESTS_CA_BUNDLE` (Python `requests`, pip) and
  `SSL_CERT_FILE` (OpenSSL-style clients, rustls tools such as uv); a runtime with its own store,
  such as Java or Deno, needs its own setting pointed at
  `/usr/local/share/ca-certificates/fleet-secrets.crt`. Every request to a bound host goes through
  the forwarder, authenticated or not, so this matters most for a host many tools download from.
- **A bound host answers 401 and the forwarder log shows no request.** The tool did not go through
  the forwarder. Steering is an `/etc/hosts` entry, so a tool that sends its traffic through an HTTP
  proxy (`HTTPS_PROXY`) lets the proxy resolve the host and bypasses it; the real host then receives
  the placeholder, which it rejects. Add the bound hosts to `NO_PROXY` for that agent.
- **`docker logs fleet-secrets`** has one line per request: host, method class, agent, key, status
  and a fixed reason. It never contains a URL, a header value, a placeholder or a secret.
- **Every bound host refuses with `secret_state_unavailable`, or TLS to it fails.** The forwarder
  has no usable state. `docker exec fleet-secrets curl -s http://127.0.0.1:8080/health` names why:
  `state_missing` (never written, or withdrawn after a failed write), `state_stale` (not refreshed
  within the age bound: is the orchestrator running?), or `state_unreadable`. The orchestrator log
  has the matching `SecretProxy state …` line.
- **Health:** the same command reports `"state":"loaded"` when all is well.

## Tests

- `tests/Fleet.SecretProxy.Tests/`: injection, wrong destinations, revocation, leaks, the CA and
  GitHub App minting, against the real forwarder on loopback.
- `tests/Fleet.Orchestrator.Tests/Services/SecretPlaceholderProvisioningTests.cs` and
  `Endpoints/SecretBindingEndpointsTests.cs`: what a container is created with, what is written to
  disk, and that a save revokes at once.
- `bash tests/secret-proxy/run.sh`: the end-to-end harness. It runs the real `fleet-secrets` image, a
  fake HTTPS upstream and a client container using `curl`, `git`, `gh`, node and python. Set
  `AGENT_IMAGE=fleet:agent` to use the real agent image as the client.
