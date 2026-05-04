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
| `gemini-2.0-flash` | Fast |

## Token refresh

The Gemini CLI's `google-auth-library` refreshes OAuth tokens in-place at
`~/.gemini/oauth_creds.json`. The writable bind mount ensures refreshed tokens
propagate back to `./fleet/.gemini-credentials.json` on the host, preventing
stale-token failures across container restarts.

No separate `AuthTokenRefreshWorkflow` schedule is needed for gemini agents.

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
