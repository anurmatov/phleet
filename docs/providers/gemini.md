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

## Known limitations vs other providers

- **No session persistence in headless mode** ([gemini-cli #13924](https://github.com/google-gemini/gemini-cli/issues/13924), [PR #23414](https://github.com/google-gemini/gemini-cli/pull/23414)). The system prompt is re-sent via `GEMINI_SYSTEM_MD` on every task. There is no `--resume` shortcut available in v0.40.1; once upstream lands persistent session support in headless mode, phleet can revisit this.
- **No native PDF content blocks.** PDFs are passed as `@<path>` filesystem references (hint: `[document attachment: /path]` injected into task text); the agent reads from disk via Read/Bash tools. Compare to claude's `type: "document"` content blocks, which embed the full PDF in the model's context window.
- **HTTP/SSE MCP transport only.** The gemini CLI does not support stdio-transport MCP servers. `entrypoint.sh` filters them out automatically when generating `~/.gemini/settings.json` from `.generated/.mcp.json`.
- **OAuth-only auth.** `GEMINI_API_KEY` and `GOOGLE_API_KEY` are explicitly fail-fast'd in `entrypoint.sh`. The agent must mount `~/.gemini/oauth_creds.json` read-write (google-auth-library refreshes tokens in-place). A personal Google account is required.

## Setup with fleet

Run `./setup.sh` and choose option **3** (gemini) or **5** (claude + gemini).
setup.sh copies `~/.gemini/oauth_creds.json` to `./fleet/.gemini-credentials.json`.

## How it works

Each task spawns a fresh `gemini` CLI process:

```
gemini --output-format stream-json -m <model> --yolo
```

- **System prompt**: delivered via `GEMINI_SYSTEM_MD` env var pointing to a temp file.
  The flag `--system-prompt-file` does NOT exist in v0.40.1.
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
