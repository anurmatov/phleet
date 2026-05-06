# Gemini Provider Setup

Fleet supports Google Gemini as an AI provider via the `gemini` CLI in headless mode.

## Prerequisites

1. Install the Gemini CLI globally:
   ```bash
   npm install -g @google/gemini-cli@0.40.1
   ```

2. Authenticate (OAuth flow — no API key needed):
   ```bash
   gemini auth
   ```
   This opens a browser window for Google OAuth consent. After completing it,
   credentials are saved to `~/.gemini/oauth_creds.json`.

## Setup with fleet

Run `./setup.sh` and choose option **3** (gemini) or **5** (claude + gemini).
setup.sh copies `~/.gemini/oauth_creds.json` to `./fleet/.gemini-credentials.json`.

## How it works

Each task spawns a fresh `gemini` CLI process. After the first task, subsequent tasks
resume the same CLI session via `--resume`:

```
# First task — starts a new session
gemini --output-format stream-json -m <model> --yolo

# Subsequent tasks — resume the established session
gemini --resume <session-uuid> --output-format stream-json -m <model> --yolo
```

The CLI emits an `init` event containing `session_id` at the start of each run.
`GeminiExecutor` captures this ID and passes `--resume <uuid>` on the next task.
Sessions auto-save to `~/.gemini/tmp/<project_hash>/chats/<session-uuid>.json`; the
system prompt baked into the session is not re-transmitted on resumed calls.

Session state is held in-memory per container process. A container restart begins a
fresh session on the first task, then resumes from that new session for the lifetime
of the process.

- **System prompt**: delivered via `GEMINI_SYSTEM_MD` env var pointing to a temp file
  on the first task. On resumed sessions the system prompt is already in the saved
  history and is not re-sent. `--system-prompt-file` does NOT exist in v0.40.1.
- **Task input**: written to process stdin; stdin is closed to signal EOF.
- **Output**: parsed as NDJSON stream-json events from stdout.
- **Auth tokens**: refreshed in-place by the CLI's google-auth-library. The credential
  file is mounted **read-write** into the container so refreshes persist to the host.

## MCP tools

The gemini CLI only supports HTTP/SSE MCP transport. stdio-transport servers are
automatically skipped when `entrypoint.sh` generates `~/.gemini/settings.json`
from `.generated/.mcp.json`.

## Attachments

- **Images**: hint-only (`[image attachment: /path]` injected into task text).
  Gemini CLI v0.40.1 headless file attachment is unconfirmed.
- **PDFs**: hint-only (`[document attachment: /path]`). The CLI has no document
  content-block API equivalent to Claude's `type: "document"`.

## Available models

| Model | Notes |
|-------|-------|
| `gemini-2.5-pro` | Most capable |
| `gemini-2.5-flash` | Balanced (default) |
| `gemini-2.5-flash-lite` | Lightweight / low-cost |
| `gemini-2.0-flash` | Fast |

## Token refresh

Token refresh is handled on two levels:

1. **In-process refresh (CLI-driven):** The Gemini CLI's `google-auth-library`
   refreshes OAuth tokens in-place at `~/.gemini/oauth_creds.json` during normal
   operation. The writable bind mount ensures refreshed tokens propagate back to
   `./fleet/.gemini-credentials.json` on the host.

2. **Scheduled refresh (AuthTokenRefreshWorkflow):** The same Temporal workflow
   that refreshes Claude and Codex tokens also handles gemini. Add `"gemini"` to
   the `Providers` list when starting the schedule. The workflow calls
   `RefreshAuthTokenActivity` (which POSTs to `https://oauth2.googleapis.com/token`
   using the `client_id` and `client_secret` stored in the credentials file itself)
   and broadcasts the new `access_token` to all agents via `fleet.relay`.
   Google does not rotate the refresh token, so only `access_token` and
   `expiry_date` are updated in the file.

## Known limitations vs other providers

Phleet is multi-vendor by design — pick the provider that matches your constraints.
Here is what to expect from the gemini provider today:

**Per-task spawn with session resume.**
Each task spawns a new `gemini` process and resumes the prior session via `--resume`.
Conversation history and tool context are preserved across tasks, but each call still
incurs ~1–3 s CLI spin-up overhead and re-establishes MCP connections from scratch.

**Latency vs claude / codex.**
The Claude provider (`claude -p`) keeps a single persistent process alive across
turns with persistent MCP connections — no per-task spin-up. The Codex provider uses
a Node.js bridge with a similar persistent-process model. The gemini provider cannot
do this today because the gemini CLI exits when stdin is not a TTY (no streaming-mode
equivalent for long-running headless sessions). The per-task spawn + `--resume`
approach is the closest native alternative until upstream support lands.

**Streaming mode roadmap.**
True persistent-process streaming mode (equivalent to `claude -p`) is being tracked
upstream in [google-gemini/gemini-cli#13924](https://github.com/google-gemini/gemini-cli/issues/13924),
currently being implemented in [PR #23414](https://github.com/google-gemini/gemini-cli/pull/23414).
When that lands upstream, phleet will upgrade to streaming mode in a future release.

**When to pick gemini.**
Choose the gemini provider if you specifically want gemini's free-tier quota or
Cloud Code Assist entitlements, and are comfortable with the per-task spin-up
overhead. For the lowest-latency experience today, claude is the better choice.

## Troubleshooting

**Agent starts but cannot call MCP tools**
- Check `~/.gemini/settings.json` inside the container (run `docker exec -it <container> cat /root/.gemini/settings.json`).
  It should contain an `mcpServers` block generated from `.generated/.mcp.json`.
  If missing, the entrypoint.sh gemini block may have failed — check `docker logs <container>`.

**`gemini credentials not found` error on container start**
- Run `gemini auth` on the host, then re-run `./setup.sh`.

**Tool calls hang without response**
- `--yolo` flag suppresses interactive approval prompts. Verify it is present in
  the process arguments: `docker exec -it <container> ps aux | grep gemini`.
