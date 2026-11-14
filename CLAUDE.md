# Fleet — Autonomous AI Agent System

## Build and setup notes

The dashboard proxy resolves the orchestrator per request via Docker DNS (`resolver 127.0.0.11`) — do not reintroduce a literal `proxy_pass` host; the regression harness is `bash tests/dashboard-proxy/run.sh`.

`seed.example.json` ships with no agents — agents are provisioned via the dashboard (SetupBanner), not seed.json.

## Architecture

Each agent is a .NET 10 container that:
1. Loads config from the orchestrator DB (provisioned via `seed.json` on first run)
2. Receives tasks via Telegram DM or RabbitMQ
3. Runs an AI process per provider: claude CLI (`-p --append-system-prompt-file`), a persistent `codex app-server --listen stdio://` (JSON-RPC 2.0, driven directly by `CodexExecutor`), gemini CLI (`--output-format stream-json --yolo`)
4. Sends tasks via stdin, streams NDJSON/JSONL responses from stdout
5. Reports heartbeats to the orchestrator

The orchestrator manages agent lifecycle (create, provision, reprovision, stop) and exposes a REST + WebSocket API consumed by the dashboard.

Workflows are orchestrated via Temporal. The temporal bridge connects Temporal workers to agent containers via RabbitMQ.

### What services send each other: `Fleet.Contracts`

Everything one service puts on the broker or sends over HTTP for another is declared once, in
`src/Fleet.Contracts` (no package, no project reference): `RelayMessage` and `RelayMessageType`,
`AgentHeartbeat`, the allowlist and access-request payloads, `ConfigChangedEvent`,
`TokenUpdatePayload`, `EpicGrantDecisionRequest`, the gate names (`GateSignal`, `CeoGateSignals`),
the exchange, queue and routing-key names (`RelayBroker`, `OrchestratorBroker`,
`ConversationBroker`) and the journal token format (`JournalTokens`). MUST NOT declare one of these
again in a service. Each producer writes the `RelayMessage` fields it has always written
(`RelayShape`: the orchestrator and the token broadcast five, fleet-bridge seven, an agent nine, a
delegation all ten), and every consumer reads any of them.

`tests/fixtures/wire/` holds the exact bytes of each pair. The producer's test compares what its
production code writes with the file; the consumer's test, in another test project, reads the same
file through its own handler. Services are deployed one at a time and agents are reprovisioned
after them, so every pair runs with one old end for a while. MUST NOT rename a field, add a
required one, reorder them or change whether a null is written: a changed sample is a wire change.
`FLEET_WIRE_GOLDEN_RECORD=1` records a shape changed on purpose.

`Fleet.Shared` has no package and no project reference either (`SharedDependencyTests`): the
secret forwarder and the chat service reference it. The Telegram formatters are in
`Fleet.Shared.Telegram` and the RabbitMQ helper and `PeerConfigClient` in `Fleet.Shared.Messaging`;
both keep the `Fleet.Shared` namespace.

### Container start: `entrypoint.sh` and `prepare`

`entrypoint.sh` does only what must happen in the shell before the agent exists: the hosted-key
hand-off, CA trust, credential copy and removal, GitHub auth, the wait for the broker, `exec dotnet`.
It MUST NOT read `appsettings.json` or write a provider configuration file itself. Both are the agent
binary's one-shot mode, `dotnet Fleet.Agent.dll prepare` (`src/Fleet.Agent/Prepare/`), which binds the
same `AgentOptions` as the host: `prepare facts` prints the `NAME=value` lines the script acts on
(`EntrypointPrepare.FactNames`) and refuses (exit 1, the container does not start) an unreadable
configuration, an unknown provider or a hosted provider without a usable key name; `prepare config`
writes the Codex `~/.codex/config.toml` or the Gemini `~/.gemini/settings.json`, where a file it cannot
build is a WARN and the agent starts without those MCP tools. The script takes the reserved key names
out of its environment before the first call, so neither call can see a hosted key. Those two files
are byte-identical to what the script's former inline Node and Python wrote (`ScriptCompat`), and
`bash tests/entrypoint-prepare/run.sh` runs the original script (`entrypoint.legacy.sh`, kept there
only as the reference) beside the current one to prove it; change the output only on purpose, with
that harness updated.

## Provider Summary

| Provider | Auth | Process model | System prompt delivery | MCP transport |
|----------|------|--------------|----------------------|---------------|
| claude | OAuth via `~/.claude/.credentials.json` | Persistent process, session resumption | `--append-system-prompt-file` (temp file) | HTTP/SSE + stdio |
| codex | OAuth via `~/.codex/auth.json` | Persistent `codex app-server --listen stdio://`, JSON-RPC 2.0 over stdio; one ephemeral thread per process | `thread/start` `baseInstructions` (PromptBuilder file contents) | HTTP only, via `~/.codex/config.toml` generated at container start by `prepare config` (stdio skipped) |
| gemini | OAuth via `~/.gemini/oauth_creds.json` (writable bind mount) | Fresh `gemini` CLI per task | `GEMINI_SYSTEM_MD` env var → temp file | HTTP/SSE only (stdio skipped by `prepare config`) |

For gemini setup: run `gemini auth` once on the host, then `./setup.sh` (choose option 3 or 5).
No GEMINI_API_KEY needed — authentication is OAuth only.
See `docs/providers/gemini.md` for a full setup guide.

### Codex local models

A codex agent's `Model` may carry an `ollama/` or `lmstudio/` prefix (e.g. `ollama/gpt-oss:20b`).
`CodexExecutor` splits it and sends `modelProvider` alongside the bare model id in `thread/start`,
routing the thread to a local OpenAI-compatible inference server. Only those two ids are
recognised — codex reserves them, and any other prefix (`owl/t-lite`) passes through untouched, so
an unprefixed model produces exactly the payload it always did.

A prefixed model needs a server URL. The operator sets it like Claude local (**Runs on → Local
server**, the shared per-agent URL field; see "Claude local models"). When Codex local mode is on,
provisioning adds `Agent.CodexOssBaseUrl = <origin>/v1` and `Agent.ContextWindow` to the generated
appsettings by JSON-node edit, and every other agent's bytes stay unchanged. `CodexExecutor` sets
`CODEX_OSS_BASE_URL` on the app-server child's `ProcessStartInfo`, overriding any inherited value,
and merges `model_context_window` into the `thread/start` `config`. A legacy agent with no URL still
works through a `CODEX_OSS_BASE_URL` Env Ref (e.g. `http://<server-address>:11434/v1`); with neither,
provisioning fails with a named fault (P1). Until #383 lands, a Codex local agent still holds the
OpenAI credential, applies token broadcasts and is bound to `fleet.relay`, unlike a Claude local agent.

The agent-side gate reads `Agent.CodexOssBaseUrl` first, then the environment. A present config
value must be exactly the canonical origin plus `/v1` with a prefixed model (S1). With no URL from
either source, **the host refuses to start** —
`AgentHostRegistration.ValidateStartupConfiguration` throws before `app.Run()` and `Program.cs`
logs at `Critical` and calls `Environment.Exit(1)`, so the container exits non-zero rather than
coming up with `/health` answering ok. The explicit exit is not optional: **a throw alone did not
terminate reliably, and what it did instead varied by platform** — on the arm64 image the process
stayed resident at ~100% CPU with `docker ps` reporting `Up`, while on x86-64 it aborted with
SIGABRT (134) and wrote a core. The cause of the arm64 spin was never established, so do not
reason from a mechanism here; `Environment.Exit(1)` is used precisely because it does not depend
on unhandled-exception propagation at all. There is no fallback to codex's `localhost` default,
which inside a container is the container itself.

⚠️ A wedged local inference server takes the whole agent down, not one turn: `CodexExecutor` holds
`_turnLock` for the turn and a chat-driven turn has no deadline, so everything queues behind it
until the stall watchdog stops the silent turn (below).
See `docs/providers/local-models.md` (setup, validation, rollout, rollback, troubleshooting) and
`docs/providers/codex-local-models.md`.

### Codex hosted models

A codex agent's `Model` may carry a `zai/` prefix (`zai/glm-5.3`): GLM on the operator's **Z.ai GLM
Coding Plan subscription**, through Z.ai's documented Codex route (the Responses endpoint
`https://api.z.ai/api/v1`). `HostedModelProviders` (`src/Fleet.Shared/`) is the one registry the
agent and orchestrator both read. `CodexExecutor` defines a `phleet_zai` provider per thread through
`thread/start` `config` overrides and points it at `HostedProviderAdapterHost`, a nested
loopback-only web app (`127.0.0.1:0`) that exists only when `Agent.HostedProvider` is true. Its one
handler, `HostedProviderForwarder`, is a transparent forwarder: Codex's request bytes, framing and
headers go through unchanged (`User-Agent` and `originator` included), only `Authorization` is
replaced with the key, and the response streams back as bytes. Only `low` and `high` effort are
forwarded; any other value is omitted with one warning.

Key isolation is the load-bearing part. The orchestrator emits `Agent.HostedProvider` and
`Agent.HostedProviderKeyEnv`. `entrypoint.sh` **unconditionally unsets `ZAI_CODING_PLAN_API_KEY` on
every agent** before it starts anything (the value waits in a shell variable that is never exported),
and for a hosted agent then writes it to `/run/phleet-hosted-key` (0400), all before `exec dotnet`. The
agent reads the file once and deletes it, and `CodexExecutor` strips the name from codex's
environment. A hosted agent gets no `.codex-credentials.json` bind, has `auth.json` removed, and
ignores codex token broadcasts. At startup the agent checks the orchestrator's flags against its own
registry (parity) and exits 1 on a mismatch or an unusable key. The reserved list in `entrypoint.sh`
sits on the line under `# phleet:reserved-key-names`; `EntrypointReservedKeysTests` keeps it equal to
`HostedModelProviders.KeyEnvVars`.

The forwarder relays only requests carrying a per-start token (`x-phleet-forwarder-token`), which
Codex receives through `thread/start` `http_headers` and nothing else does. It turns away a plain
request from the model's shell; it is **not** protection against root in the container. Plan terms
make GLM agents subscriber-only: never give a `zai/` model to an agent that answers other people.

| Env var | Where | Purpose |
|---|---|---|
| `ZAI_CODING_PLAN_API_KEY` | `.env` + agent Env Ref | Z.ai GLM Coding Plan key for `zai/` models. Reserved name |

See `docs/providers/codex-hosted-models.md`.

### Claude local models

A claude agent may run Claude Code against a local Anthropic-compatible server (e.g. Ollama) by
setting the per-agent server URL (dashboard **Runs on → Local server**, `update_agent_config
local_base_url=…`, or `localBaseUrl` on `PUT /api/agents/{name}/config`; `anthropic_base_url` /
`anthropicBaseUrl` stay as pure aliases, resolved by rule A1). The orchestrator entity property is
`Agent.LocalBaseUrl`, mapped to the unchanged `AnthropicBaseUrl` column; `Fleet.Agent`'s
`AgentOptions.AnthropicBaseUrl` and the generated `Agent.AnthropicBaseUrl` key keep the old name,
because the agent reads that key (its options, and the `prepare` mode `entrypoint.sh` calls). **Local mode ⇔ provider `claude` and a non-empty URL**;
`ClaudeLocalModel.IsEnabled` in `Fleet.Shared` is the one predicate the orchestrator and agent both
use. `Model` is the server's bare tag, `Effort` is the
thinking level `off`/`low`/`medium`/`xhigh` (empty = model default, sent as `xhigh`), and the URL is
an origin only (`http://<server-address>:11434`, never `…/v1` — Claude Code appends `/v1/messages`
itself). `LocalModel` (L1/L2, shared with Codex local), `ClaudeLocalModel.DescribeConfigFault`
(V4–V7) and `DescribeLocalOnlyEffortFault` (V8: `off` is local-only) are enforced at write
time (tool error / HTTP 400, nothing saved; valid values stored canonical), at provision time
(`GenerateAppsettingsJson` throws) and at agent startup (`ValidateStartupConfiguration`, exit 1).

`ANTHROPIC_*` exists only on the claude child's `ProcessStartInfo` (`ClaudeExecutor.ApplyLocalModelEnvironment`,
mirroring `ollama launch claude`), never in `.env`, container env or `settings.json`. With the field
off, `psi.Environment` is never touched. A local agent gets no `.claude-credentials.json` bind,
`entrypoint.sh` removes `/root/.claude/.credentials.json` (exit 1 if it cannot), claude token
broadcasts are ignored, and its queue is not bound to `fleet.relay` (a stale binding is removed on a
separate channel). No startup reachability probe: an inference-host reboot must not restart-loop
agents. Enable and rollback both take effect on reprovision. The nullable per-agent `ContextWindow`
(#367, 4096–1048576, `0` clears) puts `CLAUDE_CODE_MAX_CONTEXT_TOKENS` in a local agent's container env
on reprovision so Claude CLI compacts before the server's limit; cloud agents never get it. See
`docs/providers/local-models.md` and `docs/providers/claude-local-models.md`.

### Harness defaults and `harnessExtra` (#460)

`HarnessDefaults.Build` (`src/Fleet.Shared/`) is the only code that computes per-mode Codex and Claude
Code settings (MUST NOT compute them anywhere else). The one per-agent override is `harnessExtra`
(REST `harnessExtra` on `GET`/`PUT /api/agents/{name}/config` and `POST /api/agents`; MCP
`harness_extra` on `update_agent_config` and `create_agent`; nullable `agents.harness_extra`, migration
`AddAgentHarnessExtra`): claude and codex only, an **allowlist** with typed ranges in
`HarnessExtra.cs`, checked on every write against the resulting provider and mode, `""` clears,
applies on reprovision. No `--tools` by default (every built-in kept); a `harnessExtra` `--tools` line is
the one explicit narrowing (kept set + AllowedTools built-ins + named). `GET
/api/agents/{name}/effective-harness` serves the computed plan (credentials masked) with
`computedPlanSha256`, `startedPlanSha256` (from registration/heartbeat `PlanSha256`) and
`pendingReprovision`. Provisioning writes `.generated/codex-root.toml` for codex agents before
container create and mounts it read-only at `/app/codex-root.toml` (`HarnessDefaults.CodexRootTomlContainerPath`).
The agent's `AgentHarness` builds the same plan once at startup (log line `harness: mode=… plan=… extra=…`);
`ClaudeExecutor` appends its args and child env, `CodexExecutor` merges its thread config without
overwriting existing keys, `prepare config` (called by `entrypoint.sh`) writes `codex-root.toml` before
the `[mcp_servers.*]` tables (a missing file WARNs and keeps the MCP tables), and heartbeats carry `PlanSha256`. ⚠️ The agent's
`AllowedTools` always starts with the six baked file tools — the config binder appends the generated
list to the `AgentOptions` default — so the orchestrator prepends them too
(`HarnessDefaults.AgentDefaultAllowedTools`) and the six never cause `--tools`; `--tools` silently ignores
unknown names, and `KeptTools` includes `TaskStop`/`SendMessage` because an explicit list drops them. An older image ignores the field
and the file (view: "not reported"). Cloud model picker: families — Claude `opus`/`sonnet`/`haiku`
passed through, Codex `sol`/`terra`/`luna` mapped in `HarnessDefaults.CodexModelAliases` (the pinned
Codex does not resolve them) — plus a **custom…** free-text entry (#465); dated ids keep working, never rewritten. The dashboard dialog is sectioned and shows the effective settings
and the `harnessExtra` textarea. See `docs/providers/harness-defaults.md`.

## Project contexts

Every assignment renders the project's canonical context (the version row `CurrentVersion` points
at, else the latest) to `projects/<name>/context.md`; provisioning deletes any leftover `full.md`.
Cards, `full`/`card` modes and routes were removed in #346. `ContextRemovalPreflight` runs in
`DbSeeder` before migration `RemoveProjectContextCards` and blocks, migrating nothing, when a project
with a card or card assignment lacks canonical content; `Program.cs` then exits 1 before
`app.Run()`. The Temporal delegate `repo` field stays for replay and the agent ignores it. Upgrade
runbook: `docs/upgrading/remove-project-context-cards.md`.

## Secret placeholders

Off by default (`FLEET_SECRETS_ENABLED=false` → orchestrator `SecretProxy__Enabled`; selects the
`secrets` compose profile). A `.env` key with a row in `secret_bindings` is a **bound secret** and is
never written into a container: `ContainerSpecBuilder.BuildEnv` skips it whatever else is
true, and `SecretGrantPlanner.Plan` (the only place that decides delivery) gives the agent a
placeholder (`SecretPlaceholders`, `fleet_placeholder_` + 43 base64url characters, minted on every
provision, only its SHA-256 stored in `agent_env_refs.PlaceholderSha256`; the prefix must never take
a vendor key's shape, which is what secret scanners match), `/etc/hosts` entries pointing each bound
host at `fleet-secrets`, the forwarder's CA at `/usr/local/share/ca-certificates/fleet-secrets.crt`
and a fixed address on the internal `fleet-secrets-net` (subnet base + 16 + agent id). An unbound key
is delivered as before, and an agent with no bound key gets byte-identical Docker input.

`Fleet.SecretProxy` (`fleet-secrets`) honours a placeholder only for a current grant, from that
agent's address, to one of the binding's hosts, in the binding's header. It never writes a value into
a URL or a body, never follows a redirect, and refuses before any upstream call. It reads one file,
`secret-proxy/state/state.json` (`SecretProxyState`, written atomically by `SecretProxyStateWriter`),
on every request and fails closed when it is missing or invalid; it has no database access and does
not read `.env`. Kind `github_app` keeps the App key there too: it mints installation tokens, and the
agent's `GH_TOKEN` is a placeholder (`entrypoint.sh` → `gh-auth.sh --placeholder`; the `gh` wrapper
appends `.owner` as the installation hint).

Kind `ssh_key` is never sent anywhere: the agent gets no variable, only its own read-only
directory `/run/fleet-secrets/ssh` (`secret-proxy/ssh/<agent>/` on the host, layout in
`SecretProxySsh`) with a socket (`SSH_AUTH_SOCK`) and the bound hosts' `known_hosts`, and it reaches
the SSH server directly, unsteered. `SshAgentGate` keeps one stock `ssh-agent` per granted agent with
the key loaded under destination constraints (`ssh-add -h user@host` against the binding's
`knownHosts`), so OpenSSH itself signs only for a bound host key and user. The gate re-reads the state
on every connection and relays only list, sign and session-bind; MUST NOT relay any other agent
request (add, remove, lock) or write a key to disk (it goes to `ssh-add` on stdin). The state member
`knownHosts` is omitted for every other kind, so a state without SSH keys is unchanged bytes and an
older forwarder still reads it. Needs a Linux Docker host (a socket shared by host path). An open SSH
session survives revocation; new logins do not.

MUST NOT: deliver a bound key any other way (every fault refuses the provision instead:
`secret_proxy_disabled`, `secret_proxy_ca_missing`, `secret_proxy_network_unavailable`, …; a
reprovision finds them in `DescribeSecretPlanFaultAsync`, before `DeprovisionAsync`, so a refusal
keeps the old container); add an
MCP tool that writes a binding (REST behind the operator token only; `manage_agent_env_refs` refuses
a bound key unless `AgentAttachable`); delete a binding while an agent references its key; log or
return a value or a placeholder. Revocation is the state file: `SecretStateSaveInterceptor` rewrites
it when a save touches a binding, an Env Ref or deletes an agent, and `SecretProxyStateService`
rewrites it every 10 s whether or not it changed (which keeps it fresh and applies a value rotated in
`.env`). MUST NOT report success for a change the file does not carry: a failed write withdraws
(deletes) the old state and throws `SecretStatePublicationException` after the commit (REST 503 via
`UseSecretStateFaults`, an explicit MCP answer, a failed deprovision), and the forwarder refuses a
state older than `SecretProxy:MaxStateAgeSeconds` (default 120), which is what ends a state the
orchestrator can neither replace nor remove. Do not skip an unchanged write: the timestamp is the lease. The `fleet:secrets`
image uses the released .NET base, not `10.0-preview`: on the preview base upstream certificate
validation failed in the harness, cause not established. Tests: `tests/Fleet.SecretProxy.Tests/`,
`SecretPlaceholderProvisioningTests`, `SecretBindingEndpointsTests`, and the Docker harness
`bash tests/secret-proxy/run.sh` (`AGENT_IMAGE=fleet:agent` for the real image). See
`docs/secret-placeholders.md` (limits, GitHub App, faults).

| Env var | Default | Purpose |
|---|---|---|
| `FLEET_SECRETS_ENABLED` | `false` | Turns placeholders on; a bound key refuses to provision while off. `fleet_secrets_enabled` (`scripts/lib/comms-profiles.sh`) and `SecretProxySettings` MUST read it identically (any case, quotes stripped, anything else refused) |
| `FLEET_SECRETS_SUBNET` | `10.213.96.0/22` | `fleet-secrets-net`; change only if it overlaps a host network |
| `FLEET_SECRETS_ADDRESS` | `10.213.96.2` | The forwarder's fixed address in that subnet |

The stock OpenShell integration (manual enrollment, managed sandbox runtime, saved sandbox policies,
identity signer, epoch broker lanes) was removed. `ManagedRuntimeRemovalPreflight` runs in `DbSeeder`
before migration `RemoveManagedStockRuntime` and blocks, migrating nothing, while any agent's selected
or applied runtime is not `docker` or its stored runtime record is not `none`/`revoked`; `Program.cs`
then exits 1. Upgrade runbook: `docs/upgrading/remove-managed-runtime.md`.

## Provider CLI Pins

The agent image pins every provider CLI explicitly in `Dockerfile`:

- `@anthropic-ai/claude-code@2.1.280` — the lowest version that accepts `claude-opus-5-5`. On `2.1.259` the same request returns `[claude-code:unrecognized_model]` and a 400 (*"does not support this model; version 2.1.280 or newer is required"*), which is a failed warmup, not a degraded turn. Re-verified on this pin for stream-json mid-turn user-message delivery without a `priority` field. The Docker build checks `claude --version` and fails if npm resolves a different version.
- `@openai/codex@0.164.0` — must stay in lockstep with `.github/workflows/ci.yml`, which installs the same version before regenerating and diffing `protocols/codex-app-server-v2/`.
- `@google/gemini-cli@0.40.1` — must stay in lockstep with `docs/providers/gemini.md`, which documents the host setup command and the verified headless flag set.

⚠️ **The `2.1.280` pin moves the `opus` alias, so some agents change model without any config edit.** `ClaudeExecutor.BuildArgs` passes `Agent.Model` to `--model` verbatim, so an agent configured with an alias rather than an explicit ID follows whatever the CLI resolves it to. Measured on the two pins with identical flags: `--model opus` resolved to `claude-opus-5` on `2.1.259` and resolves to `claude-opus-5-5` on `2.1.280` — a different context window and different pricing, taking effect on that agent's next reprovision. `opus` is the only alias that moves; `sonnet` (`claude-sonnet-5`) and `haiku` (`claude-haiku-4-5-20251001`) resolve identically on both pins. Whether alias agents should follow the CLI default or be pinned to explicit IDs is an owner decision; this pin does not settle it.

Read the resolved model from the stream-json `init` event's `model` field. For an alias it reports what the alias resolved to; for an explicit ID it echoes the ID back, so `init` alone does not tell you the model was accepted — a rejected explicit ID still inits and then fails at `result` with `is_error: true`.

When bumping a provider CLI, change every occurrence of that version in the same commit and rerun the provider-specific verification that depends on its wire protocol or flags. For Codex bumps, regenerate `protocols/codex-app-server-v2/` with the new pinned CLI before committing. For Claude bumps, also diff the resolved model for every alias in use — a bump can move an alias silently, and the version assertion in `Dockerfile` does not catch it.

## Telegram Reply Metadata

Task prompts carry `[telegram_message_id: M]` for the current message and
`[reply_to_message_id: R]` only for a local Telegram reply, including `/new`, voice,
media groups, queue parts and continuations; channel and current-sender envelopes stay unchanged.
Replied-to text, caption and sender are never copied into prompts, including legacy buffer entries
and reaction events; external replies and partial quotes produce no reply metadata.
`/tts` still reads the replied message for speech synthesis, not prompt assembly.

The orchestrator emits `Agent.ReplyLookup` only for bot agents, with precedence
`journal_capture_off`, `provider_headers_unsupported`, `tool_not_granted`, then `available`.
`PromptBuilder` renders the same pinned block on every provider; legacy bot agents without the
field report `tool_not_granted`, and headless agents receive no block.
Provider header flags remain proof-gated, and this field grants no tool or journal access.

The explicitly granted `mcp__fleet-journal-files__fetch_attachment` tool runs on
`127.0.0.1:8091` only for journal-enabled bot agents. Its headerless MCP server
uses `ModelContextProtocol.AspNetCore` `0.8.0-preview.1`, matching Comms. The
runtime uses its read-purpose token to fetch only observed files in the current
bound human conversation, verifies them into `{AttachmentDir}/journal/`, and
returns a private path. No byte-store credentials, URLs, automatic grants or
agent network changes. Files remain untrusted data; only an explicit requester
instruction permits sharing. See `docs/comms-journal.md` and
`docs/comms-deployment.md` for limits, errors and rollback.

## Telegram Image Handling

- **Image-only messages** (no caption): the Telegram transport (`TelegramMessageIntake`) passes the photo to `MessageRouter`, which substitutes `TelegramOptions.DefaultImagePrompt` (default: `"(image attached — please analyze)"`) as the task prompt so the executor always receives a non-empty string.
- **Media groups** (multiple photos sent together): Telegram delivers each photo as a separate `Message` update sharing the same `MediaGroupId`. `TelegramMessageIntake` buffers all photos for a group via `MediaGroupBuffer`: it debounces 1500 ms after the last photo, then flushes a single `IncomingMessage` carrying all images. A hard cap of `TelegramOptions.MaxGroupBufferMs` (default: 10 s) force-flushes the group if photos keep trickling in.
- **Size limits**: individual photos exceeding `MaxImageBytes` (default: 10 MB) are skipped with a Telegram reply warning. Groups exceeding `MaxImagesPerGroup` (default: 10) drop extra photos with a warning.
- **ClaudeExecutor**: forwards all images as separate content blocks in the multi-modal Claude CLI JSON payload.
- **CodexExecutor**: forwards images that have a persisted `FilePath` as `{type:"localImage",path}` entries in the app-server v2 `UserInput[]` of `turn/start`. Images without a `FilePath` (persistence disabled, size limit exceeded, or file swept before dispatch) are skipped with a per-batch warning. PDFs remain hint-only (`[document attachment: path]`).

## Telegram Media Attachments

Every Telegram message type backed by a downloadable Bot API file is persisted to
`TelegramOptions.AttachmentDir` so agents can reach it with `Read`/Bash: photo, document,
video, video note, audio, voice, animation, and static/animated/video stickers. Non-file
events — locations, contacts, polls, venue shares — are not attachments.

- **One pipeline**: `TelegramMediaMapper.TryMap` normalises every non-photo type to a
  `TelegramMediaFile` descriptor, which `DocumentDownloadHelper.DownloadMediaAsync` runs
  through the shared download, `MaxDocumentBytes` gate, safe-filename, and retention path.
  Photos keep `DownloadPhotoAsync` because they also feed provider vision blocks.
- **Forwarded == direct**: Telegram sends the same media payload plus `ForwardOrigin`
  metadata, and nothing in the mapper inspects forward state, so both produce the same file.
- **Extensions**: types with no filename (voice `.oga`, video note `.mp4`, sticker
  `.webp`/`.tgs`/`.webm`) rely on the per-kind `DefaultExtension`. `Animation` is matched
  before `Document` because the Bot API sets `document` alongside `animation`.
- **Hints**: `[image attachment: …]` only for `.jpg`/`.jpeg`/`.png` (what the provider paths
  consume); `[document attachment: …]` for `.pdf`; `[file attachment: …]` for everything else.
- **Voice and video notes** are both transcribed through the whisper service — they are the
  two spoken-bubble types, and the service decodes whatever container ffmpeg understands.
  The file is persisted first and transcription reuses those bytes, so each is downloaded
  once. A successful transcript replaces the text, is echoed back as `🎤 …`, and sets
  `MessageInputSource.VoiceTranscription` so the agent gets the `voice_transcription`
  marker. If transcription is disabled or fails, the message is still delivered with the
  persisted file and its `(voice message)` / `(video note)` placeholder — and is **not**
  marked. `Video` and `Audio` are deliberately not transcribed.
- **Seeing a video**: the agent image ships `ffmpeg` and `ffprobe`, so an agent can sample frames
  from a persisted video and read them as images:
  `ffmpeg -i in.mp4 -vf fps=1/2 -frames:v 6 /tmp/f_%02d.jpg`. Nothing extracts frames automatically.
- **Failures are non-fatal**: persistence disabled, oversize, or a download error drops the
  attachment only — the caption or placeholder still reaches the agent, and
  `HasMediaAttachment` stays set so group media is not lost behind the mention gate.

## Epic grants

Autonomous charters use the separate startup-only `FLEET_AUTONOMY_ENABLED` switch
(default false), mapped only to orchestrator `EpicGrants__AutonomyEnabled`.
Autonomy with grants disabled or a nonpositive maximum lifetime logs
`epic_autonomy_misconfigured` once at startup and is treated as off.

Opt-in (`FLEET_EPIC_GRANTS_ENABLED`, default false) run-scoped delegation of `design-approval`,
`merge-approval` and `doc-review` to the configured CTO agent (#436). A `wait_for_signal` with
`visitVar` publishes the `GateVisit` attribute; its `delegatedGuard` discards any payload carrying
`GrantId` whose `VisitId`/`ArtifactRef` miss the current visit and `vars.review_ref`. Consensus
`AttestedRef` (unanimous + `REVIEWED_REF:` lines, author and CTO excluded) is the only source of
`review_ref`/`ReviewRef`. The bridge forwards the CTO's `approved` + `GrantId` to the orchestrator
(`EpicGrantService`, D1–D11, unique visit key, one send, no retry); no MCP tool creates or revokes
grants. A payload without `GrantId`, a wait without `visitVar` and a run outside an active grant
behave exactly as before. See `docs/epic-grants.md` (threat model, scope, checks, deploy rule:
no bridge restart during consensus).

## Push notifications

Off by default (`FLEET_PUSH_ENABLED=false` → orchestrator `Push__Enabled`, plus
`FLEET_PUSH_APNS_KEY_BASE64`, `_KEY_ID`, `_TEAM_ID`, `_TOPIC`). `PushNotifierService` reads the
poller's `WorkflowStore` snapshot, `ListRecentFailuresAsync` and the `AgentRegistry` every 30 s and
sends an APNs alert (HTTP/2, ES256 provider token kept 50 minutes) to each row of `push_devices`
(migration `AddPushDevices`) for three events: a run enters a phase a registered signal names, a
run closes `Failed`/`TimedOut`, an agent seen alive is silent past the grace period.
`PushEventDetector` announces each once and nothing on its first pass after start-up. `PushDelivery`
sends a notification again, in memory, only to a device that did not accept it, while
`PushEventDetector.IsCurrent` and for at most `MaxAttempts` (10) sends. `OperatorStops`
(in memory) is set by `POST /api/agents/{name}/stop` and by `stop_agent`, so a requested stop is not "down". Every
notification carries the number of waiting gates as `aps.badge`, set at send time; a change nothing
else carries goes out as a `badge`-kind notification with no alert (`TakeBadgeChange`). Enabled
with a value missing or a key that cannot sign logs `push_misconfigured` once and is treated as
off. `PushReadiness` decides that once; the notifier and `/api/push` both read it, so `GET` then
says `enabled: false` and `PUT /api/push/devices/{deviceId}` answers `503 push_disabled`.

MUST NOT: put task text, comments, failure messages or output in a notification (`PushMessages`:
identifying fields only); log or return a device token or any part of the key (the `apns` HttpClient
has no factory loggers, which would log the URI and so the token); delete a device on
any failure other than Apple saying the token is gone (`ApnsOutcome.Unregistered`). See
`docs/push-notifications.md`.

## Sign-in and chat (#530)

The orchestrator owns sign-in (`src/Fleet.Orchestrator/Auth/`, `docs/sign-in.md`): one user `owner`;
devices are a browser (`fleet_session` cookie) or an app (15-minute access tokens minted from a device
secret it got with a one-time enrollment code); `ORCHESTRATOR_AUTH_TOKEN` is the service bearer and is
never shipped to a client. A request the dashboard's nginx marks with `X-Fleet-Edge` needs a
credential for reads too; an unmarked internal call keeps the old rule. A browser write needs
`X-Fleet-Request: 1`. Names shared with Comms live only in `Fleet.Shared.SignIn`.

Fleet.Comms keeps no credential store: every north request is checked with the orchestrator's
`POST /internal/auth/check` (a `cj1.auth-check.fleet-comms` token under `FLEET_COMMS_JOURNAL_KEY`,
required whenever conversations are on), cached at most 10 s and rechecked fresh on every stream ping.
Clients reach Comms through the dashboard's `/v1/`. Chat is on by default per agent
(`agents.chat_enabled`); `ChatAgents.IsEffective` decides whether provisioning writes the agent's
`Conversations` section and never refuses provisioning. A conversation is (channel, principal,
`agent_name`, external ref) and both clients use `externalRef: "main"`. Commands route by the
conversation's agent to `fleet.conversations.inbound.<Agent.Name>`; each agent's south token
`cj1.south.<Agent.Name>` acts only on its own conversations. The owner's chat gets primary-human
priority, cancel is executed, and an answer that commits with no stream attached becomes a push
(`POST /internal/push/chat-reply`, identifiers only).

MUST NOT: return, log or store in clear a device secret, access token, session token, enrollment code
or bootstrap token (hashes only); accept a principal, device or agent identity from a request field;
treat an auth lookup that could not complete as a pass; let a device token reach `/api/config/*` or
manage other devices; publish a command for an agent that is not chat-enabled, or let one agent's
south token act on another agent's conversation; put a credential in a URL.

## Workflow failure notifier

Off by default (`FLEET_WORKFLOW_FAILURE_NOTIFIER_ENABLED=false` → bridge
`WorkflowFailureNotifier__Enabled`; fresh setup writes it, upgrade adds it only when missing). When
on, the bridge polls each configured namespace for runs that closed `Failed` or `TimedOut`. Each
failure is a record in a durable state file (`WorkflowFailureNotifier__StateFile`, compose mounts
`${FLEET_BASE_DIR}/temporal-bridge`): pending, in flight, delivered or parked. Each attempt starts
the existing `NotifyCtoWorkflow` for `FLEET_CTO_AGENT` (or
`WorkflowFailureNotifier__CoordinatorAgent`) as `workflow-failure-<namespace>-<runId>-<attempt>`
with `ReportDeliveryFailure` on. That flag defaults off, so `notify_cto` is unchanged. A failure
counts as delivered only when that run completed; a failed hand-over or run is retried, up to
`MaxDeliveryAttempts`, then parked. `Completed`, `Canceled`, `Terminated` and continue-as-new never
notify. Notices carry identity, status and a fixed cause category, never failure text or error-type
strings. There is no recovery automation. Bridge `GET /workflow-failures` shows the records. See
`docs/workflow-failure-notifier.md`.

## Durable delegation

Off by default (`FLEET_DURABLE_DELEGATION_ENABLED=false` → bridge `DurableDelegation__Enabled`;
fresh setup writes it, upgrade adds it only when missing). Off, `DelegateToAgentActivity` waits for
the agent inside one activity execution and a bridge restart fails every delegation in flight. On,
the activity writes a record to `DurableDelegation__StateFile` (same mounted directory as the
failure notifier), publishes the directive and throws `CompleteAsyncException`;
`DurableDelegationService` then does from the record what the wait loop did (heartbeat every
`TickSeconds`, re-send every 5 minutes, budget → `TimeoutException`, incomplete-answer
continuations, cancel to the agent) and `TemporalRelayListener` completes the activity by its task
token when the reply arrives. Workflow commands are unchanged, so nothing is patched and recorded
histories replay. `DelegationRelay` is a deliberate second copy of the activity's publish, cancel
and timeout-report code so the switch-off path is untouched; `DelegationRelayParityTests` keeps the
two byte-identical. The pass (`TickAsync`) talks to Temporal only; every relay send (directive,
cancel, timeout notice) goes out on a separate lane that reads what a record owes and writes back
what was sent. At most 4 relay calls are unfinished at once (the first directive an activity
publishes itself is one of them, and is owed to the lane when there is no room) and one per record; a call past its
time is told to stop and still counts until it has ended; nothing is queued, so what is not sent
stays owed in the record. A retried step reuses its task id, so each record has an `ExecutionId`
(minted once, persisted): it is on every directive (optional `ExecutionId` on the relay message)
and every cancel (optional `?executionId=` on the orchestrator's and the agent's cancel routes;
`AgentTaskCancel` forwards it), and the agent's `TaskManager.CancelBridgeExecutionAsync` stops a
task only when the id it keeps with the task-id reservation is equal. That cancel and admission
are both decided under `_admissionGate`: a cancel that finds the task id reserved and nothing
queued or running marks the reservation, and the directive then stops at the gate instead of being
admitted (MUST NOT answer `not_found` while the reservation exists). A cancel without an id is
matched by task id as before, and the in-process path sends none. Answers are fenced the same way:
`RelayCompletionPublisher` reads the task's id while its completion is raised and writes it on the
answer, and `TryHandleReplyAsync` accepts an answer for a record that has an id only when the ids
are equal (another id, or none: acked, logged, nothing changed). ⚠️ So every agent image must be
upgraded BEFORE `FLEET_DURABLE_DELEGATION_ENABLED=true`: an older agent's answers name no execution
and its delegations end by timeout. `DurableDelegation__RequireAnswerExecutionId=false` (default
`true`) accepts id-less answers by task id and reopens the stale-answer hole for older agents. For an agent or orchestrator from
before the id the bridge still orders its sends: no directive while a cancel for the same task id
is owed, unfinished, or ended without a 2xx less than 30 s ago (`Fenced`, `PriorCancels`,
`PublishNotBefore`). The service is registered before
the peer-config bootstrap on purpose: heartbeats start with the host, relay sends wait for
`IDelegationRelay.Ready` (`FleetWorkflowConfig.PeerConfigLoaded` and a non-empty escalation target).

MUST NOT: await a relay send from a pass or a reply (ten 10 s cancels once put another activity's
heartbeat past its 90 s timeout); publish a directive ahead of a cancel for the same task id, or
give a place to a new call while the old one has not ended; send the `/cancel all` broadcast for a
record that has an execution id, mint a new id for a record that has one or was loaded without one,
or write the field when it is empty (the in-process path's bytes must not change); log or return an instruction, an answer or a task token (the state file is the only place
they are kept, mode 0600; `GET /pending-delegations` is identifiers, counters and times); raise
`MaximumAttempts` on a delegate (a retry re-publishes and the agent redoes the work); overwrite a
state file that cannot be read (it is moved aside). An outage longer than the activity's
`HeartbeatTimeout` (90 s consensus, 2 min UWE and `notify_cto`) still loses the delegation, and so
does the one restart that turns the switch on or off. See `docs/durable-delegation.md`.

## Configuration

Agent config is DB-driven (MySQL via EF Core). On first run, the orchestrator seeds from `seed.json`.

Key config files:
- `src/Fleet.Agent/appsettings.json` — fallback defaults baked into the agent image
- `src/Fleet.Orchestrator/appsettings.json` — orchestrator defaults
- `./fleet/.env` — secrets and environment-specific overrides (generated by setup.sh, never commit)
- `./fleet/seed.json` — initial agent definitions for DB bootstrap (never commit production configs)
- `./fleet/docker-compose.yml` — generated from `docker-compose.example.yml` with fleet-dir-relative build contexts

Size warnings (#346) are advisory and measured in UTF-8 bytes, never tokens: `PromptSizeWarnings__InstructionBytes` and
`PromptSizeWarnings__ProjectContextBytes` (orchestrator, default 10000 each) and `Embedding__InputGuidanceBytes` (fleet-memory,
compose var `FLEET_MEMORY_EMBEDDING_INPUT_GUIDANCE_BYTES`, default 0 = off). `0` disables a check; invalid values fall back to the
default with one Warning. They are parsed from the raw config string — never add them to an options class — and read once at
startup, so a change needs a recreate. Writes always succeed; REST adds `size`, MCP appends `Size:` lines. Suggested memory
values are in the README.

`Agent:TurnStallTimeoutMinutes` (agent, default 180, `0` = off, 1–10080; out of range exits 1 at startup) limits how long a
running turn may go without one executor event, on every provider. It is a limit on silence, never a deadline: `TurnRun`
restarts it on each event, and when it runs out cancels the executor read, so the provider process is stopped and the turn
fails with a fixed message. The orchestrator does not write it; until it does, set `Agent__TurnStallTimeoutMinutes` through an
Env Ref. MUST NOT lower the default without rechecking the silent cases listed in `TurnStallTimeout`.

The tracked repo root stays clean: only source, `.env.example`, `seed.example.json`, and `docker-compose.example.yml`. All runtime state lives under `./fleet/`.

Configuration priority (highest to lowest):
1. Environment variables (from `.env`)
2. Generated config files (orchestrator writes per-agent `appsettings.json` at provision time)
3. Baked defaults in the image

## Testing

`FLEET_PRIMARY_HUMAN_USER_ID` is an optional positive Telegram user id read from the deployment `.env` at provisioning; blank omits `Telegram.PrimaryHumanUserId`, and invalid values refuse before deprovisioning. See `docs/primary-human-priority.md`.

The journal attachment-send runtime and Comms live authorization/content routes
are implemented. Operator-owned exact-image and real-provider acceptance (AC13)
remains UNRUN; source merge does not prove deployment or authorize grants. The
cross-chat switch is a REST/dashboard-only, default-off agent DB field; no MCP
write or automatic grant is allowed. See `docs/comms-journal.md` and
`docs/comms-deployment.md` for the send trust boundary, dump-first rollout,
rotation key alignment and operator-owned exact-image acceptance prerequisites.

## Epic driver handoffs

The default-off orchestrator watcher reads `FLEET_EPIC_WATCHER_ENABLED=false` and
`FLEET_EPIC_WATCHER_INTERVAL_SECONDS=300` (60–3600) at startup. Fresh setup writes
defaults; upgrade adds only missing values. `GET /api/epic-drivers/{workflowId}/state`
is read-only and internal-only, with bounded Temporal reads, immutable outcome
parsing and a 30-second reply cache that retains errors. Dry-run grant decisions
fail closed at D6 (`visibility_unknown`) without GitHub or writes.
`temporal_start_workflow` accepts optional `id_reuse_policy=allow_duplicate` or
`reject_duplicate`; unknown values start nothing. Workflow config exposes
`OrchestratorUrl` from `TemporalBridge:OrchestratorUrl`. See `docs/epic-driver.md`:
seeds do not upgrade live definitions, wakes do not approve, and operator-owned
isolated-stack verification and rollout remain separate from source/CI evidence.

| watcher environment key | default | orchestrator option |
|---|---|---|
| `FLEET_EPIC_WATCHER_ENABLED` | `false` | `EpicDriverWatcher__Enabled` |
| `FLEET_EPIC_WATCHER_INTERVAL_SECONDS` | `300` | `EpicDriverWatcher__IntervalSeconds` |
