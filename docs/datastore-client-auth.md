# Credentials for Qdrant and Temporal

The Qdrant and the Temporal the example deployment starts take no credential. What keeps an agent
away from them is the network: neither is on the network agents join
([docs/agent-identity.md](agent-identity.md#networks)), so an agent reads memory through
fleet-memory and runs workflows through the Temporal bridge, which know who it is.

A deployment that puts a credential in front of either server can give Fleet's own services that
credential to present:

- fleet-memory can send Qdrant an **API key**, over TLS only;
- the orchestrator and the Temporal bridge can send Temporal a **bearer token**.

Both are off by default. With nothing set, every connection is made exactly as before these
settings existed.

**These settings are the client's half.** Nothing here makes the example compose file's `qdrant` or
`temporal-server` require a credential, and nothing here was tested against a Temporal that checks
tokens. What the servers need is listed under each section as the operator's part.

## Qdrant: an API key

| `.env` | fleet-memory setting | Default | Meaning |
|---|---|---|---|
| `FLEET_QDRANT_URL` | `Qdrant__Url` | `http://qdrant:6334` | Qdrant's gRPC address as fleet-memory reaches it. |
| `FLEET_QDRANT_API_KEY` | `Qdrant__ApiKey` | empty | The API key. Empty: none is sent. |

- **A key needs `https`.** With `FLEET_QDRANT_API_KEY` set and a `FLEET_QDRANT_URL` that is not an
  `https` URL, fleet-memory prints
  `Fleet.Memory startup aborted, exiting 1: Qdrant:ApiKey is set and Qdrant:Url is not an https URL. …`
  and exits with code 1 before it connects to anything. The key is never sent in the clear and is
  never dropped to connect without it.
- **The server's certificate is validated the normal way**, by the platform, against the roots the
  fleet-memory container trusts. There is no setting that relaxes this and no fingerprint pinning.
  A server whose certificate is not trusted is not sent the key: the connection fails first.
- **Without a key nothing changes.** The URL is used as it always was: `https` means TLS, anything
  else a plaintext connection.
- The key is read when fleet-memory starts. Restart it after replacing the key.
- When a key is in use, fleet-memory logs one line saying so
  (`Qdrant connection: https://…, with an API key`). The key is in no log line and no error.

A deployment with a secured Qdrant, in `.env`:

```bash
FLEET_QDRANT_URL=https://qdrant.example.internal:6334
FLEET_QDRANT_API_KEY=<the key>
```

### The operator's part

- **Qdrant itself.** It requires a key and serves TLS when it is started with
  `QDRANT__SERVICE__API_KEY`, `QDRANT__SERVICE__ENABLE_TLS=true`, `QDRANT__TLS__CERT` and
  `QDRANT__TLS__KEY` (this is how `tests/memory-qdrant/secured.sh` starts the one it tests
  against; Qdrant's own documentation is the reference). The example compose file's `qdrant`
  service sets none of these and mounts no certificate.
- **Trust.** The certificate must be valid for the host name in `FLEET_QDRANT_URL` and issued by an
  authority the fleet-memory container trusts. For a private authority that means adding it to the
  container's trusted roots. .NET on Linux also reads the `SSL_CERT_FILE` and `SSL_CERT_DIR`
  variables; that is how the test script gives its test process the authority it made. Giving the
  fleet-memory container a private authority has not been tried here.
- **Other readers of Qdrant.** Nothing else in Fleet talks to Qdrant. The compose health check of
  the `qdrant` service only opens the port, and `setup.sh` and `upgrade.sh` read the release from
  the container's labels, so neither needs the key. Anything of your own that calls Qdrant's REST
  API does.

## Temporal: a bearer token

| `.env` | Orchestrator setting | Temporal bridge setting | Default |
|---|---|---|---|
| `FLEET_TEMPORAL_BEARER_TOKEN` | `Temporal__BearerToken` | `TemporalBridge__BearerToken` | empty |

With the token set, **every** Temporal client the two services construct sends
`Authorization: Bearer <token>` on every call: the orchestrator's per-namespace clients, and in the
bridge the clients its tools use, the two start-up initializers (namespaces and search attributes)
and the workers. With it empty, none of them sends a credential.

- **Give the token alone.** A value that starts with `Bearer ` is refused, with a message that says
  to give the token alone. So is a value of blanks only, a value with a blank or a line break
  anywhere in it (also at its end), and a value with a character an HTTP header cannot carry. Each
  service checks this once, before it connects to anything, prints
  `Fleet.Orchestrator startup aborted, exiting 1: Temporal:BearerToken …` (the bridge:
  `Fleet.Temporal startup aborted, exiting 1: TemporalBridge:BearerToken …`) and exits with code 1.
  The message names the setting and never the value. A token that cannot be sent is never dropped
  to connect without it.
- **The token is opaque to Fleet.** It need not be a JWT, and nothing about it is verified here.
- **Read once, at startup.** A replaced token takes a restart of both services. There is no live
  rotation.
- **The connection stays plaintext.** There is no TLS setting for Temporal. The token changes who
  the server can tell is calling, not who can read the connection: on a network where the
  connection can be read, the token can be read with it. The example deployment's Temporal is on a
  network that only the orchestrator, the bridge and the Temporal UI join. A deployment that needs
  the token protected in transit needs TLS between these services and Temporal, which Fleet does
  not provide.

  The header is sent as connection metadata rather than through the Temporal SDK's `ApiKey`
  option, because in the pinned SDK (Temporalio 1.11.1) an API key without TLS options switches
  the connection to TLS, which a plaintext server does not answer.

Both services read the same variable in the example compose file. To give each its own token (the
orchestrator needs less than the bridge, see below), point one service's line at another variable
and give it a name of its own in `.env`.

In `.env`:

```bash
FLEET_TEMPORAL_BEARER_TOKEN=<the token, without "Bearer ">
```

### When the token expires

A token that expires takes every Temporal call down when it does, so each service says what it can
about that once, at startup. When the token reads as a JWT with a numeric `exp` claim:

| `exp` is | Log line | Level |
|---|---|---|
| 30 days away or more | `The Temporal bearer token (Temporal:BearerToken) expires at 2027-01-15T08:00:00Z.` | information |
| less than 30 days away | `… expires at …, less than 30 days from now. Replace it and restart this service before then: after that every Temporal call is refused.` | warning |
| past | `… expired at … by its own exp claim. Temporal is expected to refuse every call; replace the token and restart this service. Starting anyway: the server decides, and clocks differ.` | error |

The instant is in UTC. Nothing else from the token is read or logged: no signature is checked and
no other claim is looked at. An expired token does not stop the service. A token that does not read
as a JWT with a numeric `exp` (an opaque token, say) is sent as it is and nothing is logged about
it.

### The operator's part

None of this is configured or tested by Fleet. The first five points are from Temporal's own
sources; treat Temporal's documentation and the release you run as the reference.

- **The server checks tokens only when its authorizer and claim mapper are enabled.** The
  `temporalio/auto-setup` image's configuration template exposes `TEMPORAL_AUTH_AUTHORIZER`,
  `TEMPORAL_AUTH_CLAIM_MAPPER`, `TEMPORAL_JWT_KEY_SOURCE1` and `TEMPORAL_JWT_KEY_SOURCE2`,
  `TEMPORAL_JWT_KEY_REFRESH` and `TEMPORAL_JWT_PERMISSIONS_CLAIM`.
- **Keys.** The default key provider reads each key source as a JWKS document, by HTTP GET, or from
  a local file when the URI is `file://…` (`common/authorization/default_token_key_provider.go`).
  Only RS256 and ES256 keys are accepted; there is no shared-secret (HMAC) option.
- **Permissions** are claims of the form `<namespace>:<role>`, with the roles `read`, `write`,
  `worker` and `admin`, and `temporal-system:<role>` for cluster-level calls.
- **The server's own system worker.** Since server 1.20 it needs the `internal-frontend` service
  (`USE_INTERNAL_FRONTEND` in the template) or a custom claim mapper.
- **The Temporal Web UI** documents client certificates and sign-in providers for reaching the
  server, and no setting for a static token. A server that checks tokens needs a separate answer
  for the UI.
- **The `temporal` CLI.** The example compose file's health check of `temporal-server` runs
  `temporal workflow list --namespace default` inside the container, and the orchestrator, the
  bridge and the UI wait for that check to pass. Against a server that checks tokens the CLI needs
  a token too, or the check needs replacing. The same goes for any script of your own that calls
  the CLI.

#### What each service's token must allow

Derived from reading, not tested: Fleet's calls from this repository's code, and what each call
requires from Temporal server 1.29.1 (the release the example compose file pins), in
`common/api/metadata.go`, `common/authorization/default_authorizer.go`,
`common/authorization/roles.go` and `common/authorization/default_jwt_claim_mapper.go`. The default
authorizer there allows a call when the caller's role in the call's namespace (or its
`temporal-system` role) is at least the call's: `read` for a read-only call, `write` for one that
changes something, `admin` for the rest. `write` covers `read`; `admin` covers both. `worker` on
its own covers neither. The default claim mapper refuses a token whose `sub` claim is not a string.
`GetSystemInfo`, the call every client makes when it connects, is allowed without a role.

| Service | Namespaces | Calls | Role |
|---|---|---|---|
| Orchestrator | each of `Temporal__Namespaces__*` (the example: `fleet`) | list, describe and read the history of workflows; list and describe schedules | `read` |
| | | start, signal, update, cancel and terminate workflows; create, update, pause, unpause, trigger and delete schedules | `write` |
| Temporal bridge | each of `TemporalBridge__Namespaces__*` (the example: `fleet`), and any other namespace a tool is asked to act in | the workers' polling and task completion; start, signal, cancel and terminate workflows; complete, fail and heartbeat activities; schedules | `write` |
| | each of `TemporalBridge__Namespaces__*` | at every start: register the namespace (`RegisterNamespace`), list its search attributes, add the missing ones (`AddSearchAttributes`) | `admin` for the two that change something |

So the orchestrator needs `<namespace>:write` for each of its namespaces, and the bridge needs
`<namespace>:admin` for each of its own if it is to create them and register their search
attributes itself. With the namespaces created and the search attributes registered beforehand,
`<namespace>:write` is what the bridge's other calls require. The namespace initializer then logs
a warning at each start, because the registration it tries is refused, and the search-attribute
initializer lists the attributes, finds none missing and adds nothing; the bridge carries on, as it
does today when Temporal cannot be reached at start.

## Keep both credentials away from agents

The orchestrator reads the deployment's `.env` to provision agents, and both credentials are in
it. Each is one service's own: an agent with the Qdrant key could read and write every memory's
vectors around fleet-memory, and an agent with the Temporal token could run workflows around the
bridge.

- Nothing gives either to an agent by itself. In the example compose file the Qdrant key is handed
  to fleet-memory alone and the Temporal token to the orchestrator and the bridge alone, and no
  path is mounted into an agent for either: both arrive as environment variables of their services.
- `setup.sh` and `upgrade.sh` drop a value of any of them exported in the shell, so `.env` decides.
- Nothing refuses an operator who attaches the `.env` name to an agent as an Env Ref, or maps it to
  an MCP header: do not do that. An agent that needs memory or workflows already has the tools.

## What is tested, and what is not

- **Qdrant, against a real server.** `bash tests/memory-qdrant/secured.sh` starts a Qdrant that
  requires TLS and a key, with a certificate from an authority made for the run, and runs
  fleet-memory's real store against it: the configured key is accepted, a missing or wrong key is
  refused by the server, and plaintext gets no answer. The test process is given the authority as
  a trusted root; nothing relaxes validation. CI runs it for the pinned release.
- **Qdrant, without a server.** A server whose certificate is not trusted receives no request and
  no key; a key with an `http` URL constructs no client; fleet-memory started that way exits 1.
- **Temporal, against a local endpoint that records requests.** For each of the five places Fleet
  connects to Temporal, including a worker's own client: with a token every request carries the
  header, over plaintext; without one no request carries a credential; with a token that cannot be
  sent, nothing is sent at all. Both services started with such a token exit 1.
- **Not tested: a Temporal that checks tokens.** No test here runs a Temporal server with its
  authorizer enabled, so nothing shows a token being accepted or refused by Temporal, or that the
  roles above are the ones needed.
