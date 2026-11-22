# Gemini provider

A `gemini` agent runs Google's **Antigravity CLI** (`agy`) with a **paid Gemini API key**. It used to
run the Gemini CLI with a personal Google sign-in; Google ended that for personal accounts on
2026-06-18, and the Gemini CLI is no longer installed in the agent image.

Read this first:

- **Nothing here was run against a real Gemini model.** Everything below was measured on the real
  pinned binary against a scripted stand-in for the Gemini API
  ([evidence](../evidence/antigravity-cli-1.5.14.md), `bash tests/antigravity-cli/run.sh`). What a
  real model does with the CLI's tools, prompts and limits is not known from this repository.
- **The binary is closed and pinned.** `agy` is a Go binary from Google's GitHub releases
  (`google-antigravity/antigravity-cli`), version `1.5.14`, checked against a sha256 in the
  `Dockerfile`. Its behaviour is known only from running it, so the harness is what says whether a
  new release still fits.
- **The agent's instructions are added to Google's prompt; they do not replace it.** The CLI sends
  its own system instruction (about 15 KB) and puts the fleet's instructions inside it as user
  rules. There is no way to send only ours.
- **No Google sign-in.** Signing in to a Google account from an agent is not supported here.

## The key

The CLI reads the key from `GEMINI_API_KEY`. Put it in the deployment's `.env` and give the agent
an Env Ref named `GEMINI_API_KEY`, like any other value.

- Provisioning refuses a gemini agent without it (`gemini_api_key_missing`), on provision,
  reprovision and preview, before the old container or any file is touched: no Env Ref of that
  name, an Env Ref with no non-blank value in `.env`, or a bound secret that is not exposed under
  that name.
- `entrypoint.sh` exits 1 when the variable is unset or blank, and so does the agent's startup
  check. Neither prints the value.
- The CLI sends the key to `https://generativelanguage.googleapis.com` in the header
  `X-Goog-Api-Key`, and nowhere else: not in a URL, a body or another header, not to an MCP server,
  and not into a file under `~/.gemini` (measured with the stand-in).
- A token broadcast for `gemini` is ignored. `.gemini-credentials.json` is no longer mounted.

### With secret placeholders

To keep the key out of the container, bind it ([secret placeholders](../secret-placeholders.md)):

```sh
curl -X PUT http://localhost:3600/api/secret-bindings/GEMINI_API_KEY \
  -H "Authorization: Bearer $ORCHESTRATOR_AUTH_TOKEN" -H "Content-Type: application/json" \
  -d '{"hosts": ["generativelanguage.googleapis.com"], "headerName": "X-Goog-Api-Key"}'
```

The agent then holds a placeholder as `GEMINI_API_KEY`, and the forwarder puts the real key into
`X-Goog-Api-Key` on requests to that host. Outside `Authorization` the forwarder replaces the
placeholder where it stands, so the key goes out bare, as the API expects.

**Read from the code, not run:** that a `static` binding accepts this host and header, that the
value is written without a scheme, and that the agent image adds the forwarder's CA to the system
bundle (`update-ca-certificates`, `SSL_CERT_FILE`), which a Go program normally trusts. **Not
verified:** `agy` reaching the Gemini API through the forwarder. The CLI's request is a streaming
`POST …:streamGenerateContent?alt=sse`; if it ever sent the key in the URL the forwarder would
refuse it. Try it with one agent before relying on it.

## Models

`Agent.Model` is passed to `agy --model` as it is. With no model the flag is left out and the CLI
uses its own default (the stand-in saw `gemini-3.1-pro-preview`). A slug carries the reasoning
effort, so there is **no effort setting** for gemini: `--effort` is never passed (the CLI refuses it
beside a slug that names one), a config update that sets `Effort` on a gemini agent is refused, and
a stored one is ignored.

The dashboard offers what `agy models` lists for an API key; a custom id can still be typed:

| Family | Slugs |
|---|---|
| Gemini 4 Neon | `gemini-4-neon-low`, `-medium`, `-high`, `-xhigh` |
| Gemini 4 Carbon | `gemini-4-carbon-low`, `-medium`, `-high` |
| Gemini 4 Hydrogen | `gemini-4-hydrogen-low`, `-medium`, `-high`, `-xhigh` |
| Gemini 4 Argon | `gemini-4-argon-low`, `-medium`, `-high` |
| Gemini 3.x Flash | `gemini-3.8-flash-…`, `gemini-3.7-flash-…`, `gemini-3.6-flash-…`, each `-low`, `-medium`, `-high` |
| Gemini 3.1 Pro | `gemini-3.1-pro-low`, `gemini-3.1-pro-high` |

A model the CLI does not know ends the turn with the CLI's error ("invalid model selection") before
any conversation is created.

## How a turn runs

One `agy` process per turn, in the agent's work directory:

```
agy --output-format stream-json --input-format stream-json --disable-slash-commands \
    --dangerously-skip-permissions --settings '{"modelProvider":"gemini","enableTerminalSandbox":false}' \
    [--model <Agent.Model>] [--conversation <id>]
```

- The CLI prints `init` (with the conversation id) before it reads its input. The agent then writes
  one line, `{"event":"user","message":{"content":"…"}}`, and closes the input; the CLI runs the
  turn, prints `step_update` events and one `result`, and exits.
- `--settings` are for that process only and nothing is saved, so there is no settings file.
  `modelProvider` selects the API key. The CLI's terminal sandbox is off because the container is
  the sandbox (with `--sandbox` every shell command fails there).
- `--dangerously-skip-permissions`: without it print mode denies every shell command and every
  write inside the workspace. With it the model's `run_command`, `write_file` and the other tools
  run without asking, as the other providers' tools do in an agent container.
- `--disable-slash-commands`: a message that starts with `/` is a message for the model, not a
  command of the CLI.
- Cancelling a turn kills the process (print mode has no cancel message). The conversation stays
  usable; the killed turn's message stays in it without an answer.

## Sessions

The rule is the one in `CLAUDE.md` "Provider sessions". The conversation id from `init` is kept in
`<WorkDir>/.fleet/session.json` with the hash of the exact instructions, and a start resumes it only
when provider and instructions hash match. `/reset` and `/stop` remove the file. The CLI keeps the
conversations under `/root/.gemini/antigravity-cli/`, which is a host directory
(`workspaces/<container>/gemini`) and so survives a reprovision.

- The CLI **does not fail** on a conversation it cannot find or read: it warns on stderr, starts
  another one and names it in `init`. Because `init` comes before the input, the agent sees the
  other id before it has written anything: that start is a failed resume, the session file is
  replaced, and the message is sent as the first of a new session (with the new-session line).
  A resume whose process ends before `init` is also a failed resume, and the turn starts once more
  as a new session.
- A resumed conversation gets the instructions as they are at that start. The fleet does not rely
  on that: changed instructions start a new conversation, as for the other providers.
- Old conversations are never deleted from `/root/.gemini`. They grow with the agent's history.
- The usage row of a turn carries the token counts of the CLI's `result`. The CLI counts the whole
  conversation, so the row holds this turn's difference and the totals (`cum_*`); thinking tokens
  are inside `output_tokens`; no cost is reported.

## Instructions

The CLI has no flag for a system prompt. It adds every always-on rule file to its system
instruction, so the agent writes its instructions as rule files in the CLI's global rules
directory before each process starts:

```
/root/.gemini/config/rules/fleet-instructions-001.md, -002.md, …
```

Each begins with `---`, `trigger: always_on`, `---` (without it the CLI ignores a file there). The
text is split on line boundaries into files of at most 20,000 bytes; files of ours the current text
no longer needs are removed first. The session hash is of the whole text.

The CLI cuts what does not fit **without failing**, so the agent refuses instead:

| Measured on 1.5.14 | What the agent does |
|---|---|
| one rule file is cut at 24,000 bytes | a file holds at most 20,000; a single line longer than that refuses the turn |
| all always-on rule files share 60,000 bytes; over that the CLI leaves the largest files out and only names their paths | instructions over **56,000 bytes** refuse the turn |
| other rule files count against the same 60,000 | the bytes of other rule files the CLI would load are added before comparing with 56,000 |

"Other rule files" are `GEMINI.md` and `AGENTS.md` in `/root/.gemini`, in `/root/.gemini/config`,
in the work directory and in every directory above it, other `*.md` files in the rules directory
and in plugins' `rules/`, and `*.md` under `.agents/rules` (also `.agent`, `_agents`, `_agent`) in
the work directory and above. **The CLI adds those to the prompt too**: a `GEMINI.md` or `AGENTS.md`
at the top of the work directory becomes part of the agent's instructions, and a large one can
refuse every turn. The error names the files.

A refused turn ends with an error that states the limit and the sizes; no process is started and
nothing is sent. The limit is well below what the other providers take, so a long role prompt plus
project contexts may not fit: shorten them, or use another provider for that agent.

## Messages

A message is sent whole or not at all. The CLI cuts one user message at 192,000 bytes including
about 400 bytes it adds itself (191,500 arrived whole, 191,600 did not), again without failing. A
message over **190,000 bytes** of UTF-8 ends the turn with an error that states the limit and the
size, before a process is started.

## MCP

`prepare config` (called by `entrypoint.sh`) writes `/root/.gemini/config/mcp_config.json` from
`/workspace/.mcp.json` on every start, mode 0600:

- a server with a `url` becomes `{"url": …, "headers": {…}}` (Streamable HTTP; the CLI does not
  speak the older HTTP+SSE transport);
- a server with a `command` becomes `{"command": …, "args": […], "env": {…}}` (stdio);
- nothing else is copied, and a server with headers is left out when the orchestrator did not set
  `Agent.McpHeaderSupport`.

The pinned CLI sends the configured headers on every MCP request: `server/discover`, `initialize`,
the `GET` event stream, notifications, `tools/list` and `tools/call`. It also asks
`/.well-known/oauth-protected-resource` without a header; a `401` there (what the fleet's servers
answer) changes nothing. That transcript is why `McpHeaderSupport` is true for gemini.

MCP tools are not offered to the model one by one. The model gets one tool, `call_mcp_tool`
(`ServerName`, `ToolName`, `Arguments`), and reads each tool's schema from a file the CLI writes
under `/root/.gemini/antigravity-cli/mcp/<server>/`. The agent's progress messages show such a
call as `mcp__<server>__<tool>`. **`Agent.AllowedTools` does not narrow anything for gemini:** every
tool of every configured server can be called, and so can every built-in tool of the CLI.

## Attachments

Print mode takes text only. An image, PDF or other file reaches the model as a path hint in the
message (`[image attachment: /path]`, `[document attachment: /path]`, `[file attachment: /path]`),
which the model can open with the CLI's `read_file` tool (it reads images, PDFs, audio and video).
Whether a real model looks at an image this way was not tested.

## What the CLI does on its own

- **Self-update.** Started from a directory its user can write, the CLI starts a background updater
  that fetches a newer release from `antigravity-cli-auto-updater-…run.app` and replaces the binary.
  Root can always write `/usr/local/bin`, so the image sets the CLI's own switch,
  `AGY_CLI_DISABLE_AUTO_UPDATE=true` (only the value `true` works), and the agent sets it on every
  process. With it the updater host is not contacted and the binary's sha256 does not change (the
  harness checks both, and that without it the host is contacted). `agy update`, typed by hand or
  run by the model in a shell, ignores the switch.
- **Calls to `play.googleapis.com`.** The CLI makes them on every turn. No switch for them was found
  in print mode, they are not blocked, and what they carry was not examined.
- It writes its own state under `/root/.gemini/antigravity-cli/` (conversations, logs, an
  installation id).

## Limits

- No session narration, no mid-turn injection, no background task tracking: a message that arrives
  during a turn is queued for the next one.
- No effort setting, no `harnessExtra`, no local model mode.
- Instructions at most 56,000 bytes, a message at most 190,000 bytes (above).
- `AllowedTools` is not enforced by the CLI (above).
- Not run against a real model, and not on x86-64 by hand: the measurements were made on arm64; CI
  runs the harness on x86-64.

## Upgrading the pin

Change `AGY_CLI_VERSION` and both sha256 values in the `Dockerfile`, then run
`bash tests/antigravity-cli/run.sh` (it reads the pin from the `Dockerfile`) and, with a built image,
`AGENT_IMAGE=<image> bash tests/antigravity-cli/run.sh`. A failing check names the assumption that
no longer holds: the flags, `init` before input, the header on MCP requests, the two size limits, the
updater switch, the model list. Update `docs/evidence/` with the new transcript.

## Troubleshooting

**The container exits at start with `GEMINI_API_KEY is not set`.** The agent has no usable key:
attach the Env Ref (and put the key in `.env`), then reprovision.

**Provisioning answers `gemini_api_key_missing`.** The same, found earlier; the message says which
of the three cases it is.

**A turn ends with "The turn was not started: the agent's instructions are … bytes".** The
instructions, together with other rule files the CLI loads, are over the limit. Shorten the role
instructions or project contexts, or remove the `GEMINI.md` / `AGENTS.md` the message names.

**A turn ends with "The message is … bytes".** The message is over 190,000 bytes. Send less, or put
the material in a file and name the path.

**No MCP tools.** `docker exec <container> cat /root/.gemini/config/mcp_config.json` should list the
servers of `/workspace/.mcp.json`. A `WARN:` line from `prepare config` in `docker logs` says why a
server was left out. A server that answers 401 to the agent's own header is dropped by the CLI
without an error; the model then has no `call_mcp_tool`.

**Every turn fails after a model change.** `docker logs` shows the CLI's "invalid model selection"
with the list it accepts.

**The agent seems to have forgotten the conversation.** The log says which it was: `Gemini session
resumed: …`, `Gemini session new: instructions changed`, or `Gemini session new: resume failed: …`.

## Journal replies and priority

Telegram replies carry only message ids, never quoted text. Conversation-bound reply lookup
requires an explicitly granted journal tool and a read-token header; gemini agents can have both
now that the CLI's headers are proved.

Primary-human priority changes only the next queued turn, not the running turn. Same-chat
injection or Inbox fallback runs before global queued work; workflow and cross-chat turns stay
isolated. See [primary-human priority](../primary-human-priority.md) for configuration and limits.
