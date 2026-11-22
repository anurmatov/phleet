# Troubleshooting

## Agents start returning "unauthorized" from Claude / Codex

OAuth tokens in `./fleet/.claude-credentials.json` and `./fleet/.codex-credentials.json` expire. When they do, every agent backed by that provider starts failing mid-task with an auth error. There is no in-container refresh path — you refresh on the host, then push the new file in.

1. Re-authenticate on your host with the vanilla CLI (`claude` or `codex`). This is the same CLI login flow you used during initial setup.
2. Copy the refreshed credentials into the fleet dir, overwriting the old file:
   ```bash
   # Claude — file location varies by platform:
   # Linux: ~/.claude/.credentials.json
   # macOS: stored in the login keychain as "Claude Code-credentials"
   #   (setup.sh handles the keychain extraction; for a manual refresh,
   #    the easiest path is to re-run ./setup.sh)
   cp ~/.claude/.credentials.json ./fleet/.claude-credentials.json
   chmod 600 ./fleet/.claude-credentials.json

   # Codex
   cp ~/.codex/auth.json ./fleet/.codex-credentials.json
   chmod 600 ./fleet/.codex-credentials.json
   ```
3. Reprovision every affected agent so each container picks up the new file. From the dashboard: click **Reprovision** on each agent. From the CLI:
   ```bash
   TOKEN=$(grep '^ORCHESTRATOR_AUTH_TOKEN=' ./fleet/.env | cut -d= -f2)
   for name in $(curl -s -H "Authorization: Bearer $TOKEN" http://localhost:3600/api/agents | jq -r '.[].name'); do
     curl -s -X POST -H "Authorization: Bearer $TOKEN" "http://localhost:3600/api/agents/$name/reprovision"
   done
   ```

Re-running `./setup.sh` also works — it re-copies the credentials and leaves the stack running.

## An agent should forget its conversation

A Claude or Codex agent keeps its provider session across a reprovision when its provider and instructions are unchanged. To make it start a new session, reprovision it with a new session: in the dashboard tick **New session** in the Reprovision question (also offered by **Reprovision all**), or from the CLI:

```bash
curl -s -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  -d '{"session":"fresh"}' "http://localhost:3600/api/agents/$name/reprovision"
```

The MCP tool takes the same word: `reprovision_agent agent_name=<agent> session=fresh`. The orchestrator removes `workspaces/<container>/.fleet/session.json` after the old container is gone and before the new one starts. If it answers `session_file_not_removed`, the old container was removed and no new one was started: remove that file under the fleet base directory and reprovision again. Changed instructions start a new session without any of this. Creating an agent removes that file as well, so a new agent never resumes the conversation of a deleted agent that had the same name; `provision_agent` after `deprovision_agent` keeps it.

## A gemini agent does not start, or refuses its turns

A gemini agent runs Google's Antigravity CLI with a Gemini API key; there is no Google sign-in and no credential file to refresh.

- **`gemini_api_key_missing` at provision, or `GEMINI_API_KEY is not set` in the container log:** put the key in `./fleet/.env` as `GEMINI_API_KEY`, give the agent an Env Ref of that name, reprovision.
- **A turn ends with "The turn was not started: the agent's instructions are … bytes":** the CLI would cut instructions that long, so the agent refuses. The limit is 56,000 bytes including any `GEMINI.md` / `AGENTS.md` in the work directory.
- **A turn ends with "The message is … bytes":** one message may be at most 190,000 bytes.

See `docs/providers/gemini.md` for full details.

## Telegram returns 409 Conflict at startup

Telegram allows only one long-poller per bot token. If two or more agents share a bot token and more than one tries to poll, Telegram rejects them all with 409. Fix: set `telegramSendOnly: true` on every non-CTO agent that shares a token — they'll still send messages through the bot but won't poll for incoming updates. Only the CTO agent (or whichever single agent owns DMs for that token) should poll. After editing `seed.json` or the DB, reprovision the affected agents.

## Temporal workflow types list looks empty right after a restart

`temporal_list_workflow_types` populates lazily on first call after `fleet-temporal-bridge` starts. Immediately after a restart it may return only the hardcoded built-ins and none of the seeded UWE workflow definitions. Wait a few seconds and call it again, or start any workflow once to warm the cache.
