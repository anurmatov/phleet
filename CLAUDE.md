# Fleet — Autonomous AI Agent System

## Repository Structure

```
src/
├── Fleet.Agent/        — core agent process (Telegram bot + multi-provider executor)
├── Fleet.Orchestrator/ — agent registry, Temporal workflow polling, MCP + REST + WebSocket
├── Fleet.Temporal/     — workflow orchestration bridge (MCP server)
├── Fleet.Bridge/       — RabbitMQ relay for inter-agent messaging
├── Fleet.Memory/       — semantic memory MCP server (Qdrant + embeddings)
├── Fleet.Shared/       — shared utilities
└── fleet-dashboard/    — React SPA for agent monitoring and lifecycle management
Dockerfile              — agent image (multi-stage)
Dockerfile.temporal     — temporal bridge image
entrypoint.sh           — container init script
gh-auth.sh              — GitHub App JWT generation utility
seed.example.json       — example agent bootstrap config (setup.sh copies → ./fleet/seed.json)
docker-compose.example.yml — full stack example (setup.sh copies → ./fleet/docker-compose.yml)
.env.example            — required environment variables (setup.sh copies → ./fleet/.env)
setup.sh                — guided installer
upgrade.sh              — rebuild images + restart (no prompts)
./fleet/                — gitignored runtime data dir (created by setup.sh)
```

## Build Commands

```bash
# Build the entire solution
dotnet build

# Build a specific project
dotnet build src/Fleet.Agent/Fleet.Agent.csproj

# Run tests
dotnet test

# Build Docker images (from repo root)
docker build -t fleet:agent .
docker build -t fleet:memory -f src/Fleet.Memory/Dockerfile .
docker build -t fleet:orchestrator -f src/Fleet.Orchestrator/Dockerfile .
docker build -t fleet:temporal-bridge -f Dockerfile.temporal .
docker build -t fleet:bridge -f src/Fleet.Bridge/Dockerfile .
docker build -t fleet:dashboard \
  --build-arg VITE_AUTH_TOKEN=your-token \
  -f src/fleet-dashboard/Dockerfile .

# Dashboard (React)
cd src/fleet-dashboard
npm install
npm run dev    # local dev server
npm run build  # production build
```

## Quick Start

1. Run the setup wizard from the repo root:
   ```bash
   ./setup.sh
   ```

   This creates a `./fleet/` subdirectory next to the repo holding all
   runtime state: `.env`, `seed.json`, generated `docker-compose.yml`,
   `workspaces/`, `memories/`, credentials, mysql backups. The entire
   dir is gitignored — `rm -rf fleet/` to reset.

2. Fill in the prompted values (Telegram bot tokens, GitHub App credentials, etc.) — setup.sh writes them to `./fleet/.env`.

3. Open the dashboard and use the SetupBanner to provision your first agent (co-cto). `seed.example.json` ships with no agents — agents are provisioned via the dashboard, not seed.json.

4. setup.sh starts the stack automatically. To start/stop later:
   ```bash
   cd fleet && docker compose up -d
   cd fleet && docker compose down
   ```

5. The orchestrator reads `seed.json` on first start and bootstraps your agents into the database.

## Architecture

Each agent is a .NET 10 container that:
1. Loads config from the orchestrator DB (provisioned via `seed.json` on first run)
2. Receives tasks via Telegram DM or RabbitMQ
3. Runs an AI process per provider: claude CLI (`-p --append-system-prompt-file`), Codex SDK bridge (`codex-bridge.mjs`), or gemini CLI (`--output-format stream-json --yolo`)
4. Sends tasks via stdin, streams NDJSON responses from stdout
5. Reports heartbeats to the orchestrator

The orchestrator manages agent lifecycle (create, provision, reprovision, stop) and exposes a REST + WebSocket API consumed by the dashboard.

Workflows are orchestrated via Temporal. The temporal bridge connects Temporal workers to agent containers via RabbitMQ.

## Provider Summary

| Provider | Auth | Process model | System prompt delivery | MCP transport |
|----------|------|--------------|----------------------|---------------|
| claude | OAuth via `~/.claude/.credentials.json` | Persistent process, session resumption | `--append-system-prompt-file` (temp file) | HTTP/SSE + stdio |
| codex | OAuth via `~/.codex/auth.json` | Per-turn via `codex-bridge.mjs` | stdin JSON field | HTTP/SSE only |
| gemini | OAuth via `~/.gemini/oauth_creds.json` (writable bind mount) | Fresh `gemini` CLI per task | `GEMINI_SYSTEM_MD` env var → temp file | HTTP/SSE only (stdio skipped in `entrypoint.sh`) |

For gemini setup: run `gemini auth` once on the host, then `./setup.sh` (choose option 3 or 5).
No GEMINI_API_KEY needed — authentication is OAuth only.
See `docs/providers/gemini.md` for a full setup guide.

### Codex local models

A codex agent's `Model` may carry an `ollama/` or `lmstudio/` prefix (e.g. `ollama/gpt-oss:20b`).
`CodexExecutor` splits it and sends `modelProvider` alongside the bare model id in `thread/start`,
routing the thread to a local OpenAI-compatible inference server. Only those two ids are
recognised — codex reserves them, and any other prefix (`owl/t-lite`) passes through untouched, so
an unprefixed model produces exactly the payload it always did.

A prefixed model requires `CODEX_OSS_BASE_URL` (e.g. `http://host.docker.internal:11434/v1`) as a
provision-time env var on the agent. If it is unset or blank, **the host refuses to start** —
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
`_turnLock` for the turn and a chat-driven turn has no deadline, so everything queues behind it.
See `docs/providers/codex-local-models.md`.

### Codex hosted models

A codex agent's `Model` may carry a `deepseek/` or `openrouter/` prefix (e.g.
`deepseek/deepseek-v4-pro`, `openrouter/z-ai/glm-5.3` for GLM). `HostedModelProviders`
(`src/Fleet.Shared/`) is the one registry the agent and orchestrator both read. `CodexExecutor`
defines a `phleet_deepseek` / `phleet_openrouter` provider per thread through `thread/start`
`config` overrides and points it at `HostedProviderAdapterHost`, a nested loopback-only web app
(`127.0.0.1:0`) that exists only when `Agent.HostedProvider` is true. The adapter
(`ResponsesNamespaceAdapter`) flattens Codex's MCP `namespace` tools into `function` tools, applies
a per-vendor field allowlist, and restores `namespace` + `name` on returned calls by exact match only.

Key isolation is the load-bearing part. The orchestrator emits `Agent.HostedProvider` and
`Agent.HostedProviderKeyEnv`. `entrypoint.sh` moves that key into `/run/phleet-hosted-key` (0400),
then **unconditionally unsets `DEEPSEEK_API_KEY` and `OPENROUTER_API_KEY` on every agent** before
`exec dotnet`. The agent reads the file once and deletes it, and `CodexExecutor` strips both names
from codex's environment. A hosted agent gets no `.codex-credentials.json` bind, has `auth.json`
removed, and ignores codex token broadcasts. At startup the agent checks the orchestrator's flags
against its own registry (parity) and exits 1 on a mismatch or an unusable key.

| Env var | Where | Purpose |
|---|---|---|
| `DEEPSEEK_API_KEY` | `.env` + agent Env Ref | Key for `deepseek/` models. Reserved name |
| `OPENROUTER_API_KEY` | `.env` + agent Env Ref | Key for `openrouter/` models. Reserved name |

See `docs/providers/codex-hosted-models.md`.

## Provider CLI Pins

The agent image pins every provider CLI explicitly in `Dockerfile`:

- `@anthropic-ai/claude-code@2.1.280` — the lowest version that accepts `claude-opus-5-5`. On `2.1.259` the same request returns `[claude-code:unrecognized_model]` and a 400 (*"does not support this model; version 2.1.280 or newer is required"*), which is a failed warmup, not a degraded turn. Re-verified on this pin for stream-json mid-turn user-message delivery without a `priority` field. The Docker build checks `claude --version` and fails if npm resolves a different version.
- `@openai/codex@0.153.4` — must stay in lockstep with `.github/workflows/ci.yml`, which installs the same version before regenerating and diffing `protocols/codex-app-server-v2/`.
- `@google/gemini-cli@0.40.1` — must stay in lockstep with `docs/providers/gemini.md`, which documents the host setup command and the verified headless flag set.

⚠️ **The `2.1.280` pin moves the `opus` alias, so some agents change model without any config edit.** `ClaudeExecutor.BuildArgs` passes `Agent.Model` to `--model` verbatim, so an agent configured with an alias rather than an explicit ID follows whatever the CLI resolves it to. Measured on the two pins with identical flags: `--model opus` resolved to `claude-opus-5` on `2.1.259` and resolves to `claude-opus-5-5` on `2.1.280` — a different context window and different pricing, taking effect on that agent's next reprovision. `opus` is the only alias that moves; `sonnet` (`claude-sonnet-5`) and `haiku` (`claude-haiku-4-5-20251001`) resolve identically on both pins. Whether alias agents should follow the CLI default or be pinned to explicit IDs is an owner decision; this pin does not settle it.

Read the resolved model from the stream-json `init` event's `model` field. For an alias it reports what the alias resolved to; for an explicit ID it echoes the ID back, so `init` alone does not tell you the model was accepted — a rejected explicit ID still inits and then fails at `result` with `is_error: true`.

When bumping a provider CLI, change every occurrence of that version in the same commit and rerun the provider-specific verification that depends on its wire protocol or flags. For Codex bumps, regenerate `protocols/codex-app-server-v2/` with the new pinned CLI before committing. For Claude bumps, also diff the resolved model for every alias in use — a bump can move an alias silently, and the version assertion in `Dockerfile` does not catch it.

## Telegram Image Handling

- **Image-only messages** (no caption): `AgentTransport` passes the photo to `MessageRouter`, which substitutes `TelegramOptions.DefaultImagePrompt` (default: `"(image attached — please analyze)"`) as the task prompt so the executor always receives a non-empty string.
- **Media groups** (multiple photos sent together): Telegram delivers each photo as a separate `Message` update sharing the same `MediaGroupId`. `AgentTransport` buffers all photos for a group via `MediaGroupBuffer`: it debounces 1500 ms after the last photo, then flushes a single `IncomingMessage` carrying all images. A hard cap of `TelegramOptions.MaxGroupBufferMs` (default: 10 s) force-flushes the group if photos keep trickling in.
- **Size limits**: individual photos exceeding `MaxImageBytes` (default: 10 MB) are skipped with a Telegram reply warning. Groups exceeding `MaxImagesPerGroup` (default: 10) drop extra photos with a warning.
- **ClaudeExecutor**: forwards all images as separate content blocks in the multi-modal Claude CLI JSON payload.
- **CodexExecutor**: forwards images that have a persisted `FilePath` as `{type:"local_image",path}` blocks via `@openai/codex-sdk@0.118.0`'s `UserInput[]` form. Images without a `FilePath` (persistence disabled, size limit exceeded, or file swept before dispatch) are skipped with a per-batch warning. PDFs remain hint-only (`[document attachment: path]`).

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
- **Failures are non-fatal**: persistence disabled, oversize, or a download error drops the
  attachment only — the caption or placeholder still reaches the agent, and
  `HasMediaAttachment` stays set so group media is not lost behind the mention gate.

## Code Conventions

- .NET 10, C# latest features
- File-scoped namespaces
- `required` keyword for mandatory config properties
- `IAsyncEnumerable` for streaming
- Microsoft.Extensions.Hosting for service lifecycle
- Options pattern for configuration
- React + TypeScript + Vite + Tailwind for the dashboard

## Configuration

Agent config is DB-driven (MySQL via EF Core). On first run, the orchestrator seeds from `seed.json`.

Key config files:
- `src/Fleet.Agent/appsettings.json` — fallback defaults baked into the agent image
- `src/Fleet.Orchestrator/appsettings.json` — orchestrator defaults
- `./fleet/.env` — secrets and environment-specific overrides (generated by setup.sh, never commit)
- `./fleet/seed.json` — initial agent definitions for DB bootstrap (never commit production configs)
- `./fleet/docker-compose.yml` — generated from `docker-compose.example.yml` with fleet-dir-relative build contexts

The tracked repo root stays clean: only source, `.env.example`, `seed.example.json`, and `docker-compose.example.yml`. All runtime state lives under `./fleet/`.

Configuration priority (highest to lowest):
1. Environment variables (from `.env`)
2. Generated config files (orchestrator writes per-agent `appsettings.json` at provision time)
3. Baked defaults in the image

## Testing

```bash
# Run all tests
dotnet test

# Run with output
dotnet test --logger "console;verbosity=normal"

# Run specific test project
dotnet test tests/Fleet.Agent.Tests/
```
