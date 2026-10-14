# Pi Provider — Local Models

Provider `pi` runs an agent's turns through [Pi](https://github.com/earendil-works/pi) 1.0.4
(`@earendil-works/pi-coding-agent`, tag `v1.0.4` = `7c10bd4`) against an inference server you
run. It is a fourth harness next to `claude`, `codex` and `gemini`, and it is **always local**:
there is no cloud mode, no API key and no login.

`PiExecutor` keeps one long-lived `pi --mode rpc` process per agent. Pi speaks the **Anthropic
Messages API** at `<origin>/v1/messages`, the same server contract as Claude local mode. A
Fleet-owned extension, baked into the agent image at `/opt/fleet-pi-extension`, does two jobs:
it bridges Fleet's MCP servers as Pi tools under their exact Fleet names, and it replaces Pi's
compaction summary call with one built to reuse the server's live prefix cache (§5).

The shared local-model flow (Runs on, Server URL, rollout across harnesses) is in
`local-models.md`. This page covers what is specific to Pi.

## Status: what has and has not been measured

Read this before you rely on any number on this page.

- **The reopen gate passed once.** On 2026-10-13 one synthetic run on one server passed G1–G4:
  Pi 1.0.4 with only the compaction extension, mlx-serve 26.10.2, a Qwen MLX model at 4.7 bits
  per weight, a server context of 262144 with its SSD prefix cache on, a 42-tool synthetic
  conversation with a base prompt of 22072 tokens, and the §5 instruction below.
  - G1, summary-call cache ratio: 0.9990 and 0.9995.
  - G2, compaction time: 19.1 s and 24.9 s.
  - G3, first delta after compaction, host-measured: 62.0 s and 37.1 s against a 65 s budget.
    The first run had a **3 s margin**. G3 counts as passed only under the P2 latency rule (§5.6).
  - G4: 3 of 3 anchors recalled exactly after two compactions.
- **That result applies only to that exact setup.** It is not a reliability claim for the
  server, the build or the model. Any other server must pass G1–G4 on its own measurements.
- **Not measured:**
  - Fleet integration: the A1–A33 acceptance criteria (MCP, permissions, credentials, lifecycle,
    media, steering, install, upgrade, rollback);
  - the production-path canary (B1–B13) on the target server;
  - the L2c and L3 latency budgets;
  - the cold prefill rate `r` of the tested build. It is unknown;
  - attachments (#455): no image or PDF has gone end to end through Fleet, Pi and a real server.
    The attachment baseline (B) and canary (V1–V9) have not run, so no local model is known to
    read images (§8a).

Until the canary runs, nothing on this page is evidence that Pi reuses the cache, compacts in
time or recalls context inside Fleet. `docs/spikes/real-adapter-capability-matrix.md` (`## pi`)
holds only canary measurements, and every cell there is still pending.

## 1. When to use Pi

| | Pi | Claude local | Codex local |
|---|---|---|---|
| provider | `pi` | `claude` | `codex` |
| harness | Pi 1.0.4 RPC + the Fleet extension | Claude Code | codex app-server |
| server API | Anthropic Messages (`/v1/messages`) | Anthropic Messages | OpenAI-compatible (`/v1`) |
| cloud mode | none: always local | yes | yes |
| stored model | bare tag | bare tag | `ollama/<tag>` or `lmstudio/<tag>` |
| compaction | Pi's threshold, with Fleet's prefix-reusing summary (§5) | Claude Code's | codex's, best-effort |
| provider credential in the container | none | none | still present until #383 |
| `fleet.relay` binding | none | none | bound |
| images | sent only with **Image input** on (§8a); otherwise dropped with a note | sent | sent |
| PDFs | text extracted locally on every agent; scanned pages as images with Image input (§8b) | native document block | path only |

Use Pi when a local model needs Fleet's MCP tools and a compaction path designed around the
server's prefix cache, in a container that holds no provider credential.

Do not use Pi for hosted models, servers that need an API key, or a server that does not speak
Anthropic Messages. Those are out of scope for this version. Image input is opt-in per agent and
only for a model whose vision has passed the V canary (§8a).

## 2. Server requirements

- **Anthropic Messages** at `<origin>/v1/messages` with no authentication. Fleet sends the public
  placeholder key `phleet-local-no-auth`.
- **Its context size (`--ctx`) equals the agent's Context window**, and is at least 65536. Pi
  compacts at `ContextWindow − 16384`; a window larger than the server's means overflow 400s.
  Why 65536: a 16384 reserve, an 8192 tail, a 1280-token summary and a base prompt of about 22k
  add up to about 48k; below 65536 the agent would compact on almost every turn.
- **It serves the bare tag** you store, for example `qwen3.8-flash-next`.
- **Prefix reuse.** The fast path (§5) only pays off when the server reuses KV for a request whose
  tokens extend the live sequence. Whether yours does is a measurement, not an assumption: run
  the reopen gate (G1–G4) against it first.
- **Thinking.** Fleet sends thinking on (`medium`) or off; a server that reads it as on/off is
  enough.
- **Context overflow.** Pi recognises an overflow by error text (for example
  `Prompt has N tokens, but the configured context size is M tokens`), by input + cacheRead above
  the window, or by a length stop with no output.

## 3. Setup

### Dashboard

```
Provider    [ Pi (local model)        v ]
Runs on     (• Local server )            Pi runs only on a local server
Server URL  [ http://inference-host:8000 ]
Model tag   [ qwen3.8-flash-next ]
Context window (tokens)
            [ 131072 ]                   at least 65536; your server's --ctx
Effort      [ Default (on) v ]           Default (on) · Off · On
[ ] Image input                          off by default; §8a
```

Screenshots (rendered from the dashboard components at the PR head, placeholder values):
[Pi config](images/pi/config-pi.jpg) · [Codex local before](images/pi/switch-codex-before.jpg) and
[after the switch to Pi](images/pi/switch-codex-to-pi-after.jpg) · [New Agent, Create blocked](images/pi/create-pi-blocked.jpg).

A new agent: in **New Agent**, pick **Provider → Pi (local model)** and fill **Server URL**,
**Context window** and **Model tag**. Create stays disabled, with the PI1 or PI3 text, until all
three are valid, so the agent is created complete and can provision at once.

An existing agent:

1. Open the agent's config and pick **Provider → Pi (local model)**. **Runs on** is locked to
   Local server.
2. Enter the **Server URL** as an origin only. Fleet adds `/v1/messages`.
3. Enter the bare **Model tag**, with no `ollama/` or `lmstudio/` prefix.
4. Enter the **Context window**: exactly the server's `--ctx`. Save is blocked below 65536.
5. Pick **Effort** (§7) and click **Save & Reprovision**. A restart is not enough.
6. Leave **Image input** off unless the model's vision has passed V1 (§8a). It is shown only for
   Pi, stored for every provider, sent only while the provider is Pi, and applies at the next
   reprovision. "Copy from existing agent" never copies it.

### MCP

```
create_agent agent_name=<agent> role=<role> provider=pi model=qwen3.8-flash-next local_base_url=http://inference-host:8000 context_window=131072
```

or, for an existing agent:

```
update_agent_config agent_name=<agent> provider=pi local_base_url=http://inference-host:8000 model=qwen3.8-flash-next context_window=131072 effort=off
reprovision_agent <agent>
get_agent_config <agent>
```

`get_agent_config` then prints `Provider: pi` and
`Local model: on — server http://inference-host:8000 (Pi)`.

Image input (§8a), only after V1 passes for this model: `update_agent_config agent_name=<agent>
pi_image_input=true`, then `reprovision_agent <agent>`. `get_agent_config` prints
`- Pi image input: True`; on another provider it adds `(ignored: pi only)`. A value that is not a
boolean is a tool error and nothing is saved.

### REST

```
POST /api/agents
{"name":"<agent>","role":"<role>","provider":"pi","model":"qwen3.8-flash-next","localBaseUrl":"http://inference-host:8000","contextWindow":131072}

PUT /api/agents/<agent>/config
{"provider":"pi","model":"qwen3.8-flash-next","localBaseUrl":"http://inference-host:8000","contextWindow":131072,"effort":"off"}

POST /api/agents/<agent>/reprovision
```

`piImageInput` (boolean, omit to keep) works on both calls and is in the config response. A value
of the wrong JSON type, on any field, is an HTTP 400 and nothing is saved.

### Validation

The rules run on write, create included (HTTP 400 or a tool error; nothing is saved), at provisioning (a named
fault before any container is created or removed) and at agent startup (exit 1).

| id | when | message |
|---|---|---|
| PI1 | provider `pi`, no URL | "Pi runs only on a local server; set the local server URL." |
| L2 | the URL is not an http(s) origin, or points at localhost | the shared L2 text (`local-models.md` §5) |
| PI2 | the tag is empty, fails the tag charset, or starts with `ollama/`, `lmstudio/` or a hosted prefix | "Pi takes the bare model tag the server serves (no ollama/ or lmstudio/ prefix)." |
| PI3 | context window unset or below 65536 | "Pi needs the server's context size, at least 65536; set the context window to the server's --ctx." |
| PI4 | effort is not empty, `off` or `on` | "Pi effort is off or on." |
| PI5 | provisioning only: a credential mount under `/root/.claude`, `/root/.codex`, `/root/.gemini` or `/root/.pi` | "Pi agents must hold no provider credential, but a credential mount targets {MountPath}. Remove it, then reprovision." |
| L1 | a URL on a provider other than claude, codex or pi | "Local model runs on claude, codex or pi; clear the local server URL first." |
| PV1 | any provider other than empty, `claude`, `codex`, `gemini` or `pi` | "Unknown provider '{p}'; use claude, codex, gemini or pi." |

## 4. What Fleet generates, and why

### Orchestrator, at provisioning

- `Agent.PiBaseUrl` (the canonical origin) and `Agent.ContextWindow` in the generated
  `appsettings.json`, added by JSON-node edit. Every other agent's generated files are unchanged.
- `Agent.PiImageInput: true` only when Image input is on (#455); off, the key is absent and the
  bytes are what they were before. On any other provider the flag is kept but ignored, with one
  log line `PiImageInput ignored for '<agent>': pi only`.
- `Agent.AllowedTools` gets the usual auto-grants: `mcp__fleet-memory__memory_get`, and
  `mcp__fleet-temporal__notify_cto` except on the configured CTO agent.
- No credential mount and no `CLAUDE_CODE_MAX_CONTEXT_TOKENS`.
- `.mcp.json` is unchanged; MCP header tokens are supported for pi.
- Log line: `Pi agent '<agent>': server <origin>, model <tag>, context <W>`.

### Agent, before every Pi start

Fleet rewrites three files in the container-local `PI_CODING_AGENT_DIR=/root/.pi/agent`, so an
edit made from the model's shell cannot survive a restart.

`models.json` declares one provider, `fleet-local`, with `api: anthropic-messages`, the
placeholder key, `supportsStrictTools: false`, `supportsEagerToolInputStreaming: false`, and one
model with your tag, `reasoning: true`, `input: ["text"]` (`["text","image"]` with Image input),
`contextWindow: W`,
`maxTokens: 16384` and zero cost. The generator refuses a key that starts with `!` or `$` or
contains `sk-ant-oat`: Pi would run the first as a command, read the second from the
environment, and send the third down an OAuth path.

`settings.json`:

```json
{"compaction":{"enabled":true,"reserveTokens":16384,"keepRecentTokens":8192},
 "retry":{"enabled":true,"maxRetries":3},
 "steeringMode":"all","cacheWarming":"off",
 "enableInstallTelemetry":false,"enableAnalytics":false}
```

`cacheWarming` is off because a warming replay is an extra full request to your server.

`fleet-pi.json` configures the extension: `mcpConfigPath` `/workspace/.mcp.json`,
`allowedTools` (the agent's grants), `toolResultMaxBytes` 49152, `toolCallTimeoutSeconds` 300 and
`summaryMaxTokens` 1280.

### Process arguments

```
pi --mode rpc --offline --no-approve
   --provider fleet-local --model <tag>
   --thinking <off|medium>
   --system-prompt <PromptBuilder file>
   -nc -ns -np -ne --no-mcp
   -e /opt/fleet-pi-extension
   --tools read,bash,edit,write,<each granted mcp__ name>
   [--session <absolute file path>]
```

| flag | why |
|---|---|
| `--mode rpc` | JSONL commands on stdin, events on stdout |
| `--offline` | no model-catalog refresh or package install at startup, and no rg/fd download |
| `--no-approve` | Pi ignores project-local files for the run |
| `--provider fleet-local --model <tag>` | the only model Fleet declares |
| `--thinking` | from Effort (§7) |
| `--system-prompt` | Fleet's prompt, the same builder every harness uses |
| `-nc` | no `AGENTS.md`/`CLAUDE.md` discovery: a repository cannot inject a prompt |
| `-ns`, `-np` | no skills, no prompt templates |
| `-ne` | no discovered **or built-in** extensions (mcp, codemode, tool_search); an explicit `-e` still loads |
| `--no-mcp` | Pi's own MCP client stays off: it renames tools (`fleet-memory` → `fleet_memory`), cuts long names, enables codemode and reads an `mcp.json` the shell can rewrite |
| `-e /opt/fleet-pi-extension` | the Fleet extension (§5, §6) |
| `--tools …` | every granted MCP name, exactly; without an `mcp__` entry Pi never declares extension MCP tools to the model |
| `--session <path>` | resume after a crash or kill; a full path, so Pi never prompts |

Fleet sends no `set_*` RPC command: those write `settings.json`.

### Child environment

The container environment **minus** the credential denylist (61 exact names and the prefixes
`ANTHROPIC_`, `CLAUDE_CODE_`, `OPENAI_`, `CODEX_`, `GEMINI_`, `AWS_`, `GOOGLE_`; the list is
`PiLocalModel.CredentialEnvNames`/`CredentialEnvPrefixes`), **plus** `PI_OFFLINE=1`,
`PI_TELEMETRY=0` and `PI_CODING_AGENT_DIR=/root/.pi/agent`. `GH_*`, `GITHUB_*` and git settings
are kept: the `bash` tool needs them for repository work, and they grant no model access.

### Container start (`entrypoint.sh`)

For a pi agent the entrypoint, before `exec dotnet`:

1. unsets the reserved hosted-key names, as for every agent;
2. unsets the same denylist from PID 1 and prints one `NOTE: unsetting <NAME> for a pi agent`
   line on stderr for each variable that was set (names only, never values);
3. deletes `/root/.claude/.credentials.json`, `/root/.codex/auth.json`,
   `/root/.gemini/oauth_creds.json`, `/root/.pi/agent/auth.json` and
   `/root/.pi/agent/mcp-auth.json`, then `/root/.pi/agent/sessions`. If any of those files is
   still there (a bind mount cannot be deleted), it exits 1 with "pi agent must hold no provider
   credential";
4. waits for RabbitMQ and the orchestrator as usual. It generates no MCP config.

### Image

The agent image installs Pi with `scripts/install-pi.sh` into `/opt/pi` (`/usr/local/bin/pi`),
not with `npm install -g`: Pi declares its own `@earendil-works/*` packages as `^1.0.4`, and a
plain global install resolved them to 1.2.0 on 2026-10-13. npm `overrides` hold all of them at
the pin. `scripts/check-pi-pin.sh` then fails the build unless `pi --version` is the pin, node is
at least 22.19, `pi --help` lists `--mode`, `--offline`, `--no-approve`, `--no-mcp` and
`--no-prompt-templates`, every installed `@earendil-works/*` package is exactly the pin, and the
extension's `@modelcontextprotocol/sdk` is exactly 1.30.1.

The image also installs `poppler-utils` (#455, §8b) on the same `--no-install-recommends` apt line
as `ffmpeg`, **with no version pin**: it parses untrusted PDFs, so every image rebuild picks up the
distro's security fixes. `scripts/check-attachment-tools.sh` fails the build unless `pdfinfo`,
`pdftotext`, `pdftoppm`, `prlimit` and `ffmpeg` all run, and prints the poppler version (24.02.0
on the current base). For a published poppler CVE that affects the installed version, rebuild and
deploy the agent image; the §8b process limits bound the impact meanwhile. Other providers'
executors never call it.

## 5. Compaction

### 5.1 When Pi compacts

| constant | value | meaning |
|---|---|---|
| reserve R | 16384 | Pi compacts when the context exceeds `W − 16384` (114688 at W = 131072) |
| raw tail T | 8192 | `keepRecentTokens`: a target, not a cap. Pi cuts at the next valid point after it and never at a tool result |
| tail budget | 10240 | the latency budgets apply only to kept tails up to this size; a larger tail logs a Warning |
| summary cap S | 1280 tokens, thinking off | about 43 s of decode at 30 tokens/s |
| per-result MCP cap | 48 KiB | one tool result stays below R |

Pi's default summary is a separate request with its own system prompt and serialized history,
so on a server that reuses only exact prefixes it prefills the whole history cold. That was
measured on 2026-09-28 on one server and is why Fleet replaces it.

### 5.2 The fast path

The extension records a copy of every main request just before Pi sends it, and changes nothing.
Summary calls never overwrite that copy. On `session_before_compact` (reasons `threshold` and
`manual`):

1. **Eligibility.** The fast path runs only if the reason is not `overflow`, a main request was
   captured, Pi reports a numeric context size (nothing is estimated), `tokens + 600 + 1280 ≤
   W − 1024`, and Pi's first kept entry resolves to an entry in the session. Otherwise Pi's
   default summary runs, with a Warning (§5.4).
2. **Request.** The conversation exactly as Pi would send it next, plus **one** user message
   holding the instruction below. `system` and `tools` are copied byte for byte from the captured
   main request, thinking is disabled, `max_tokens` is 1280, and the call times out after 90 s.
   If the request does not extend the captured one, a Warning names the first differing message;
   the request is still sent.
3. **Split turns.** One summary covers the whole conversation, including a turn that was cut.
4. **Instruction.** This exact text, byte for byte:

```text
Do not call tools. Summarize the conversation so far under exactly these headings: Goal / Constraints / Progress / Key decisions / Next steps / Critical context. Copy verbatim every value the user explicitly asked to keep and every identifier, path, number and command still in use. Carry forward the essential facts of earlier summaries. Omit repetitive or historical detail that is not needed to continue; do not enumerate repeated or completed items one by one, state them once in aggregate. Plain text, at most 300 words.
```

   It is the 525-byte ASCII string with SHA-256
   `b755fed8758856edf45a96afd398a23a4599fe690cd8aecb5a88ea736fc9e4be`, with no trailing newline;
   the line break around the fence is not part of it. The gate passed with this text only.
   Changing, paraphrasing or lengthening it reopens the gate. The earlier text ("at most about
   1000 words") hit the 1280-token cap in a later run.
5. **Accept** only a normalized `stop` with no tool call and non-empty text. A summary that
   stopped at the cap (`length`) is never used, even with text: it would silently drop its last
   sections. The compaction is logged with `path=prefix`.

### 5.3 Overflow

Pi recognises the server's context-overflow 400, compacts with its **default** summary and
retries once. A second failure ends the turn with "context overflowed and automatic recovery
failed — send /reset". Fleet never resets a session on its own.

### 5.4 Fallback reasons

Every fallback runs Pi's default summary and logs
`Warning fast-path fallback reason=<reason>`:

| reason | cause |
|---|---|
| `overflow` | the compaction was triggered by a context overflow |
| `no_capture` | no main request captured yet, for example right after a `--session` restart |
| `usage_unavailable` | Pi reported no numeric context size |
| `size` | `tokens + 600 + 1280 > W − 1024`: the summary request would not fit |
| `kept_unresolved` | Pi's first kept entry did not resolve to a session entry; the tail is never guessed |
| `toolUse` | the model answered with a tool call |
| `length` | the summary reached the 1280-token cap |
| `error` | the summary request failed |
| `aborted` | the summary request was aborted |
| `empty` | the summary had no text |
| `timeout` | no answer within 90 s |

Any other stop reason is logged verbatim.

### 5.5 Latency budgets

TTFT is host-measured: Pi reports none, so `PiExecutor` times each model call from `turn_start`
to the first `message_update` delta on stdout. Exceeding any budget fails a run, even with a
correct answer.

| id | from → to | budget |
|---|---|---|
| L0 | warm turn: call sent → first delta | ≤ 15 s |
| L1 | `compaction_start` → `compaction_end` | ≤ 60 s |
| L2 | first call after compaction → first delta | ≤ 65 s |
| L2c | first answer after compaction completes (effort off, one line) | ≤ 75 s |
| L3 | message sent within 2 s of the crossing answer → answer delivered | ≤ 120 s |

### 5.6 Cold-only prediction versus measured latency (P2)

The formula `r × 65 s ≥ B + S + tail + message` predicts L2 by assuming the first request after
compaction prefills everything cold, where B is the base prompt (system prompt and tool schemas)
and r the server's cold prefill rate. In the 2026-10-13 run the server reused the stable base
prefix on that request (21504 of 22072 tokens cached), so the formula can predict a miss that a
measurement does not show. One run does not prove the formula wrong.

Under P2:

- the prediction is computed from B, r, the tail and the message and reported with them. It is
  never a pass or a fail;
- when it predicts a miss, the run continues, and the **measured** L2, L2c and L3 decide against
  the unchanged budgets;
- each result records the first post-compaction call's `prompt` and `cached`;
- r counts only from a cold request (server `cached` = 0) with a host-measured first delta. A
  total duration does not give r. The r of the tested build is unknown.

Without P2 the older rule stands: a predicted miss stops the run and G3 stays unmet. In both
cases a base prompt over 24000 tokens stops the run, to report B, r and the tail.

## 6. Tools, MCP and grants

- **Built-in tools:** `read`, `bash`, `edit`, `write`, with Pi's truncation limits. `grep` and
  `find` stay off.
- **MCP:** at start, the extension reads `/workspace/.mcp.json` and connects to every `http`
  server in parallel (15 s per server, 20 s in total), sending its `headers` if any. The tool list
  is then fixed for the life of the process. A server that is down at start keeps its tools absent
  until the next restart; registering them later would change the prompt head.
- **Grants:** a tool is registered under its exact name `mcp__<server>__<tool>`, unchanged, only
  if that name is in the agent's `AllowedTools`, the same source as every provider. A name over
  64 characters is skipped with a Warning, never shortened. A non-`http` server is skipped with a
  Warning.
- **Calls:** a 300 s timeout, cancelled by Pi's abort; an error, timeout or abort returns exactly
  one error result. Text over 48 KiB is cut and ends with
  `[fleet-mcp: output truncated from N to 49152 bytes]`.
- **Logging:** tool names and byte sizes only.

## 7. Effort

| stored effort | Pi argument |
|---|---|
| `""` (Default) | `--thinking medium` |
| `on` | `--thinking medium` |
| `off` | `--thinking off` |

Anything else is refused (PI4). The compaction summary always runs with thinking off.

## 8. Sessions, `/reset` and `/stop`

- One Pi process per agent, started on the first turn. Its session is a JSONL file under
  `/root/.pi/agent/sessions`, in the container only: never mounted or uploaded. It holds
  conversation text.
- A crash or a kill: the next turn restarts Pi with `--session <same file>` and prefills once,
  cold.
- `/reset` and `/stop` kill the process and **delete** the session file; the next turn starts a
  new session. A new container starts with no session: the entrypoint removes the directory.
- A message that arrives while a turn runs is steered into it after the current tool batch.
- A prompt Pi does not acknowledge within 10 s (with no compaction open) is never run later: the
  process is killed and the turn answers "The local model did not accept the message within
  10 s; it was not processed. Send it again."
- No Pi event for 900 s during a run aborts it with "local model stopped responding".
- Attachments: §8a (images), §8b (PDFs), §8c (every other type). A frame with images can be
  about 8.5 MB; once a stdin write starts it is finished whole within 30 s, or the process is
  killed and the next turn resumes the session ("Pi exited unexpectedly…"). Cancelling a turn
  never cuts a frame.
- The session file holds image bytes and inline PDF text verbatim until `/reset`, `/stop` or a
  new container; the 48 h attachment sweep does not reach it. Each time it grows past 256 MiB the
  agent logs one Warning; Fleet never edits or resets it.

### 8a. Images

**Image input** (`PiImageInput`, default off) declares the model's vision in `models.json` and
makes Fleet send image bytes on every path: the prompt, a steer, and a queued batch. Fleet never
detects vision from server metadata or a model name, because a text-only model behind a
permissive server ignores images or fails every later turn. **Turn it on only after V1 passes**:
one PNG, one JPEG and one WebP sent by prompt, each with a different code, all three read back.

Off, nothing is processed and the turn gets exactly the old note, "[N image attachment(s) omitted:
this local model is text-only]".

On, Fleet enforces every bound itself, before writing to Pi. At Pi 1.0.4 only an idle prompt
normalizes images; steer and streaming prompts pass them raw, and `inputLimits` is never read.
Each user image, in order:

| step | check | note reason |
|---|---|---|
| 1 | magic bytes are JPEG, PNG, GIF or WebP (the transport's label is ignored) | `unsupported format` |
| 2 | the header gives width and height | `unreadable` |
| 3 | each side ≤ 12 000 px and ≤ 50 MP | `too large to process` |
| 4 | fewer than 2 images already inline, checked before any downscale | `not inline: over the 2-image limit — open the path with read` (with no saved file: `over the 2-image limit`) |
| 5 | ≤ 2000 px and ≤ 3 MiB: sent byte for byte | — |
| 6 | otherwise downscaled with `ffmpeg` (§8b limits, single-threaded), never upscaled: PNG for a PNG or GIF (first frame), JPEG otherwise, JPEG with alpha flattened on white when the PNG is still over 3 MiB | `processing failed`, `processing timed out`, `over 3 MiB` |

- A downscaled image adds "[N image(s) resized to fit 2000 px]". An ordinary 2560 px Telegram
  photo comes out at 2000×1500. ffmpeg 6.1 applies EXIF orientation, so a phone photo stored
  rotated arrives upright (measured on the orientation-6 fixture).
- An image holds its slot from step 4 even if its downscale then fails, so ffmpeg runs at most
  twice per message.
- Images over the limit keep their `[image attachment: path]` hint; with Image input on, Pi's
  `read` returns them as images. That is the known difference from Claude, which takes a
  10-photo album inline; raising the cap waits for V4's token measurement.
- After the prompt is acknowledged Fleet counts the images in Pi's echo of the message and logs
  `pi images: Pi dropped D of S image(s) on the prompt path` when Pi dropped one (its image
  library failed to load).
- A server that rejects image blocks (HTTP 4xx) fails the turn, and the images stay in the
  session, so later turns fail too until `/reset`.

### 8b. PDFs

Every Pi agent extracts PDF text locally, whatever Image input says: Claude reads PDFs natively,
so tool-only access would not be parity. For each PDF (`application/pdf` or a `.pdf` name), at
most 3 per message:

1. `pdfinfo` reads the page count and the encryption line.
2. `pdftotext -enc UTF-8 -layout -f 1 -l 100` extracts pages 1 to min(100, P). Up to 4 MiB is
   kept; more is cut and marked `truncated at 4 MiB` (not a failure).
3. The full text goes atomically to `<file>.txt` next to the attachment (the 48 h sweep removes
   it), and the message gets
   `[PDF text: <name>, pages 1–p of P; full text: <file>.txt]`, the text, `[end of PDF text]`.
   All PDFs of one message share 24 576 inline characters; the rest ends with
   `…rest in <file>.txt`, which `read` can page through.
4. A PDF with no text layer (under 32 non-whitespace characters), or one that does not allow
   copying text, is rendered with `pdftoppm` as JPEG pages (1600 px, then 1200 and 800 for a page
   over 3 MiB) into the image slots left, **only with Image input on**. Off, or with no slot left,
   the note says so. There is no OCR.

Named failures: `file missing`, `not a valid PDF`, `damaged`, `password-protected`,
`text copying restricted by the PDF`, `extraction timed out`, `extractor unavailable`. No
password is ever supplied and no permission is bypassed: a copy-restricted PDF is only ever
viewed as page images.

**Copy restriction comes from `pdfinfo`.** poppler 24.02's `pdftotext` ignores the restriction
and exits 0 with the text, so Fleet reads `copy:no` from `pdfinfo`'s `Encrypted:` line and never
runs `pdftotext` on such a file. Exit 3 from a build that enforces it is honoured too.

**Limits for every local tool** (`pdfinfo`, `pdftotext`, `pdftoppm`, `ffmpeg`): an argument list
with no shell; `prlimit --as=1073741824 --cpu=60 --nofile=256`; a 30 s wall timeout per call, cut
to what is left of **one 60 s deadline per message**; an output cap; the credential-stripped
environment plus `MALLOC_ARENA_MAX=2`; a private temporary directory, removed afterwards. A
timeout, the cap or a cancellation kills the process tree. Work still pending at the deadline gets
`processing timed out` or `extraction timed out`, and the turn goes on with the rest.

The steer path processes attachments before writing, then checks again that the same run is
still live and unanswered. If it is not, nothing is written and the message is queued as a
prompt. A cache of the last 32 PDFs (text excerpts and outcomes only, never images, keyed by
path, size and mtime) means that prompt does not extract again.

### 8c. Attachment parity

The baseline every Pi cell is held to is the better of Claude and Codex (#455 §2):

| input | Claude | Codex | Pi |
|---|---|---|---|
| photo | native image | native image | native with Image input (§8a); otherwise a note |
| PDF | native document block | path hint | extracted text (§8b); scanned pages as images with Image input |
| image sent as a file | path + file tool | path + file tool | `read` returns an image with Image input |
| txt, code, md, CSV, JSON | path + file and shell tools | same | path + `read`/`bash` (A9) |
| docx, xlsx, pptx, odt, ods, odp | path + shell (`python3`) | same | same (A9) |
| video, animation | path + `ffmpeg` | same | same; frames read as images with Image input (A11) |
| audio file | path; no speech recognition | same | same |
| voice and video notes | transcript | transcript | transcript |
| legacy doc/xls/ppt, `.tgs`, unknown binary | path; no extractor | same | same |

What has run: the source and CI rows (A1–A18). What has not: the separately approved baseline
(B) with throwaway Claude, Codex and Pi agents, and the V canary. Until B runs, Claude's and
Codex's handling of files reached by path is unverified.

## 9. Migration from Codex local and Claude local

| from → to | Model | Effort | URL and context |
|---|---|---|---|
| Codex local → Pi | `ollama/<tag>` or `lmstudio/<tag>` → `<tag>` | `none`/`minimal` → `off`; anything else → `""` | kept |
| Claude local → Pi | unchanged | `off` → `off`; anything else → `""` | kept |
| Pi → Claude local | unchanged | `off` → `off`; `on`/`""` → `""` | kept |
| Pi → Codex local | `<tag>` → `ollama/<tag>` | `off` → `none`; `on`/`""` → `""` | kept |

- The dashboard normalizes these values. REST and MCP never rewrite `Model` or `Effort`: send the
  matching values in the same call. Into pi, a mismatch fails with PI2 or PI4. Out of pi, a
  stored `on` (any provider) or `off` (codex, gemini, or claude cloud) is refused, as is an
  `on` on a create; send the mapped effort, or `""`, with the switch.
- A legacy Codex agent that reaches its server through the `CODEX_OSS_BASE_URL` Env Ref needs
  its Server URL entered first (`local-models.md` §9).
- The server must serve Anthropic Messages. The Codex thread is dropped. Reprovision.

## 10. Rollout

Roll out only after the reopen gate has passed on the server the first Pi agents will use.

0. Before deploying, check every agent's provider on the **current** release: the `provider` field
   of `GET /api/agents/<agent>/config` (what the dashboard's config modal loads), or the
   `Provider` column of the `agents` table (`SELECT Name, Provider FROM agents;`). Each must be
   in PV1's set (empty, `claude`, `codex`, `gemini`, `pi`); fix any other row first, because the
   new release refuses to provision it. `get_agent_config` prints a `Provider:` line only from
   this release on.
1. Deploy the orchestrator, dashboard and agent image of the same release together. There is no
   migration and no `.env` change; `upgrade.sh` picks up the new image with no new prompt.
2. Check that every non-pi agent's generated `appsettings.json` is unchanged and that every agent
   warms up.
3. Confirm the server's `--ctx` equals each future Pi agent's Context window.
4. Move local agents to Pi **one at a time** (§9). Reprovision each, then send one ordinary turn,
   one tool call and one MCP call.

## 11. Rollback

**One agent:** switch it back per §9 and reprovision. Its Pi session goes with the container.

**The release:** first move **every** pi agent off `pi`, then revert the pin.

> **Precondition for any pin revert:** `get_agent_config` must show no agent with
> `Provider: pi`. An older orchestrator mounts Claude credentials for an unknown provider, and an
> older agent image falls back to the Claude executor. A `pi` row left behind would get Claude
> credentials in a container whose model drives a shell.

#386 has no schema change. #455 adds the `PiImageInput` column; an older orchestrator ignores it
and new rows default to false, so no down-migration is needed, and an older agent image ignores
the generated `PiImageInput` key and runs text-only. To undo only images on one agent, turn
Image input off and reprovision: the new container starts with no session.

## 12. Troubleshooting by log line

Log lines carry counts and names only, never prompt text or credentials.

| where | line | level | meaning |
|---|---|---|---|
| agent, start | `pi start: version … model … server … context … reserve 16384 tail 8192 summaryMax 1280 thinking … session new\|resumed images on\|off poppler <version>\|unavailable` | Information | Pi started and passed its `get_state` check |
| agent, per message with images or PDFs | `pi attachments: images sent S resized R omitted O (reason:k,…); pdf extracted E truncated T failed F (reason:k,…) chars C ms M` | Information | §8a, §8b; counts and reason names only. Healthy: `pdf extracted 1 failed 0` |
| agent | `pi images: Pi dropped D of S image(s) on the prompt path` | Warning | Pi could not load its image library; the model got Pi's hint instead |
| agent | `pi pdf: extractor unavailable` | Error, once per process | poppler or prlimit is missing from the image; rebuild it |
| agent | `pi pdf: the full-text file could not be written; …` | Warning | the inline text was still sent |
| agent | `pi: a stdin frame was not written within 30 s; killing the process` | Error | Pi stopped reading its stdin; the next turn resumes the session |
| agent | `pi session file is N MiB; /reset starts a new one` | Warning | the session holds large images or PDF text; send `/reset` when convenient |
| orchestrator | `PiImageInput ignored for '<agent>': pi only` | Information | the flag is stored on a non-pi agent and has no effect |
| agent, per call | `pi call: prompt=… cached=… output=… ttft_ms=… total_ms=… stop=…` | Information | ordinary turns should keep `cached/prompt` high |
| agent, compaction | `pi compaction <reason> path=prefix\|default tokensBefore=… tail=… splitTurn=… summary=… cached=… ms=…` | Information | `path=prefix` is the fast path |
| agent | `post-compaction TTFT over 65 s` | **Warning** | **the first sign of a latency regression.** The tested margin was 3 s (62.0 s against 65 s), so a small slowdown of the server, a longer prompt or a bigger tail crosses it. Check the call's `prompt` and `cached`, the tail and the server load |
| agent | `fast-path fallback reason=…` | Warning | §5.4 |
| agent | `summary request does not extend the last request (first difference at message N)` | Warning | the summary will likely prefill cold; report it |
| agent | `tail over 10240` | Warning | a large last message; the latency budgets do not apply to this compaction |
| agent | `compaction over 60 s`, `ack paused over 60 s` | Warning | the server is slow; compare with L1 |
| agent | `auto_retry` | Warning | Pi retried a failed request |
| agent | MCP server down, MCP name over 64 chars | Warning | §6 |
| agent | `compaction failed`, `overflow recovery failed`, `ack timeout (process killed)` | Error | §5.3, §8 |
| agent | `Pi failed to start: <reason or field>` | Error | `get_state` did not answer in 30 s or reported a different model, provider, thinking level, steering mode or auto-compaction; after 3 attempts. Check the image and the generated files |
| entrypoint | `NOTE: unsetting <NAME> for a pi agent` | stderr | a credential variable reached the container; remove its Env Ref |
| entrypoint | exit 1, "pi agent must hold no provider credential" | stderr | a credential file is bind-mounted into the container; remove the mount (PI5 should have refused it) |
| entrypoint or startup gate | exit 1, "Unknown provider '…'" | stderr / Critical | PV1; fix the provider |
| orchestrator | `Pi agent '<agent>': server …, model …, context …` | Information | provisioning picked up the pi settings |
| turn | "local model server unreachable (<origin>)" | turn error | the server is down or the URL is wrong; Fleet does not probe it |
| turn | "context overflowed and automatic recovery failed — send /reset" | turn error | the context is above the server's; check `--ctx` against the Context window |
| turn | error results on every turn after one with an image | turn error | the server rejects image blocks and the image is in the session: send `/reset`, then turn Image input off (§8a) |
| turn | `extraction timed out` on ordinary PDFs, or `extractor unavailable` | note | the 60 s deadline is too short for the file, or poppler is missing |

**Healthy:** `get_agent_config` prints the Local model line, ordinary turns keep a high
`cached/prompt`, and compactions log `path=prefix`. There is no metrics endpoint.

## 13. Security

- **Pi is not a sandbox.** It has no approval step, no sandbox and no path limit. The model's
  `bash` tool runs as root, can read every file in the container and can reach any network the
  container can.
- **The control is absence.** No provider credential exists anywhere that shell can read: PI5
  refuses credential mounts, the entrypoint deletes leftover credential files and refuses to
  start beside a mounted one, PID 1 drops the credential denylist, and the Pi child drops it
  again. Pi's hosted and login auth (`auth.json`, `/login`, environment API keys, `sk-ant-oat`
  tokens) is never used.
- **Pi's own empty `auth.json`.** Pi 1.0.4 writes `{}` to `/root/.pi/agent/auth.json` on every
  start and has no flag to skip it. Fleet deletes `auth.json` and `mcp-auth.json` before every
  start, and refuses a started Pi ("Pi failed to start: auth.json") whose `auth.json` holds
  anything but `{}`, or that has an `mcp-auth.json`. That one empty file is the only
  credential-named file a pi agent's shell can find. This is a declared amendment of A32 in
  PR #452, pending human design approval.
- **What stays readable:** MCP header tokens in `/workspace/.mcp.json`, as for every provider
  (they are MCP grants, not model credentials), and `GH_*`/`GITHUB_*` for repository work.
- **Network:** `--offline`, `PI_OFFLINE=1` and `--no-mcp`. Pi is meant to connect only to your
  server and the MCP endpoints; the CI check for that (A23) has not run yet.
- **Cleartext:** `http` is accepted to any host, and the whole conversation goes there. Point an
  agent only at a server you control.
- **`fleet.relay`:** a pi agent unbinds from it; it carries only OAuth token broadcasts.
- **PV1:** an unknown provider string is refused everywhere (write, provisioning, entrypoint,
  agent startup). It used to fall through to Claude credentials.
- **Attachments (#455).** PDFs and images are untrusted input to C parsers (poppler, ffmpeg).
  Fleet runs them with no shell, under `prlimit`, timeouts, an output cap and the
  credential-stripped environment (§8b). That bounds cost, not privilege: they run as the agent's
  user, like the model's own shell. Inline PDF text is a prompt-injection surface equal to
  Claude's document block. Logs never carry image bytes, base64, extracted text, file names,
  paths or hashes; the session file and the `<file>.txt` sidecar do hold content (§8).

## 14. Checklist for a Pi bump

1. **Re-read the new tag's source** for every fact this page relies on: the compaction trigger
   and the hooks the extension uses, RPC framing and events, the flags in §4, the credential
   environment names Pi and its provider SDKs read, the built-in MCP client, and network use in
   RPC mode.
2. **Replace the vendored sources** `tests/Fleet.Agent.Tests/Fixtures/pi-source/args.ts` and
   `env-api-keys.ts` and their hashes in `SOURCE.txt`. `PiCredentialEnvListTests` fails until
   every name in them is covered; add new names to `PiLocalModel.CredentialEnvNames` and to the
   line under `# phleet:pi-credential-env-names` in `entrypoint.sh` in the same commit.
3. **Bump every pin together:** `ARG PI_CODING_AGENT_VERSION` in `Dockerfile`,
   `PiLocalModel.PinnedVersion` (CI fails when they differ), the Provider CLI Pins row in
   `CLAUDE.md`, and this page.
4. **Dependency guard:** if `scripts/check-pi-pin.sh` names an `@earendil-works/*` package that
   is not pinned, add it to `PI_SIBLINGS` in `scripts/install-pi.sh`. Never relax the guard.
   Report the image-size change.
5. **Extension:** rerun its tests against the new Pi. The instruction hash in §5.2 must not
   change.
6. **Real Pi:** the CI `pi` job (`Category=RealPi`, extension tests in strict mode) must pass,
   and the recorded fixtures must be re-recorded.
7. **Gate and canary:** if compaction, the summary path or the RPC protocol changed upstream,
   rerun the reopen gate (G1–G4) and the production-path canary before rollout.
8. **Attachments (#455):** recheck P1–P6 in the new source — the `images` field on `prompt`,
   `steer` and `follow_up`, which paths normalize images, Pi's resize bounds, and whether
   `inputLimits` is enforced — and the placeholder texts A11 asserts. The RealPi attachment rows
   (A9–A12) must pass.
