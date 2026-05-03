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
3. Runs a persistent AI process (`claude -p` or Codex SDK bridge)
4. Sends tasks via stdin NDJSON, streams responses from stdout
5. Reports heartbeats to the orchestrator

The orchestrator manages agent lifecycle (create, provision, reprovision, stop) and exposes a REST + WebSocket API consumed by the dashboard.

Workflows are orchestrated via Temporal. The temporal bridge connects Temporal workers to agent containers via RabbitMQ.

## Telegram Image Handling

- **Image-only messages** (no caption): `AgentTransport` passes the photo to `MessageRouter`, which substitutes `TelegramOptions.DefaultImagePrompt` (default: `"(image attached — please analyze)"`) as the task prompt so the executor always receives a non-empty string.
- **Media groups** (multiple photos sent together): Telegram delivers each photo as a separate `Message` update sharing the same `MediaGroupId`. `AgentTransport` buffers all photos for a group via `MediaGroupBuffer`: it debounces 1500 ms after the last photo, then flushes a single `IncomingMessage` carrying all images. A hard cap of `TelegramOptions.MaxGroupBufferMs` (default: 10 s) force-flushes the group if photos keep trickling in.
- **Size limits**: individual photos exceeding `MaxImageBytes` (default: 10 MB) are skipped with a Telegram reply warning. Groups exceeding `MaxImagesPerGroup` (default: 10) drop extra photos with a warning.
- **ClaudeExecutor**: forwards all images as separate content blocks in the multi-modal Claude CLI JSON payload.
- **CodexExecutor**: emits a user-facing warning and drops images (Codex SDK does not support image input via `runStreamed`).

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

## Supported Providers

### `claude` — Anthropic

- **Auth:** OAuth credentials file at `~/.claude/.credentials.json`. Managed by `AuthTokenRefreshWorkflow` (rotates every 30 min).
- **System prompt:** delivered via `--append-system-prompt-file` flag pointing to `{WorkDir}/system-prompt.md` (written by `PromptBuilder.WriteSystemPromptFile()`).
- **MCP:** all transport types from `.mcp.json`.
- **Images:** inline multimodal passthrough. **PDFs:** native `type: "document"` content blocks.

### `codex` — OpenAI

- **Auth:** credentials file at `~/.codex/auth.json`. Managed by `AuthTokenRefreshWorkflow`.
- **System prompt:** delivered as `systemPrompt` field over stdin JSON to `codex-bridge.mjs`, written to `/workspace/AGENTS.md`.
- **MCP:** HTTP/SSE transport servers from `~/.codex/config.toml` (generated by `entrypoint.sh`).
- **Images:** not supported — Codex SDK has no image input API. **PDFs:** hint-only.

### `gemini` — Google AI Studio

- **Auth:** `GEMINI_API_KEY` environment variable. Get a free key at
  https://aistudio.google.com/apikey (no credit card required).
  No OAuth setup, no token refresh workflow — API keys do not expire.
- **System prompt:** delivered via `GEMINI_SYSTEM_MD` file
  (written by `PromptBuilder.WriteSystemPromptFile()`).
- **MCP:** HTTP/SSE transport servers from `.mcp.json`.
  stdio-transport servers are logged as warnings and skipped.
- **Images:** inline multimodal passthrough.
  PDFs: hint-only (`[document attachment: <path>]`).
- **Model default:** `gemini-2.5-flash`.
  Free tier: 60 req/min, 1,000 req/day.
