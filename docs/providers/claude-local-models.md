# Claude Provider — Local Models

A `claude` agent can run Claude Code against a local **Anthropic-compatible** inference
server (validated: Ollama serving a Qwen 27B model) instead of Anthropic. `ClaudeExecutor`
is reused unchanged: same persistent `claude -p` process, same stream-json protocol, same
flat `mcp__server__tool` names, streaming, mid-turn steering and cancellation. There is no
new executor, listener or host port, and no Claude credential anywhere in the container.

## 1. What it is, and when to use it

| | Claude local (this page) | Codex local (`codex-local-models.md`) |
|---|---|---|
| provider | `claude` | `codex` |
| server API | Anthropic Messages (`/v1/messages`) | OpenAI-compatible (`/v1`) |
| config | per-agent DB field `AnthropicBaseUrl` + bare `Model` tag | `ollama/…` model prefix + `CODEX_OSS_BASE_URL` env ref |
| harness | Claude Code: its tools, prompt, subagents, output styles | codex app-server |

Pick this path when the agent's instructions, tools and workflows are written for Claude
Code and you want to keep them while the model runs locally. Different agents may point at
different servers; changing one agent's endpoint needs no orchestrator restart.

**Local mode ⇔ provider `claude` and a non-empty `AnthropicBaseUrl`.** `null` or `""` is
off. A whitespace-only value is a fault, never off. `ClaudeLocalModel.IsEnabled`
(`src/Fleet.Shared/`) is the one predicate the orchestrator and agent both use.

## 2. Enabling one agent

```
update_agent_config agent_name=<agent> anthropic_base_url=http://<server-address>:11434 model=<tag> effort=""
reprovision_agent <agent>
```

Or the dashboard: agent config → **Anthropic-compatible base URL** (Claude only), a custom
**Model**, **Effort** = default, then *Save & reprovision*. A plain restart is not enough:
the value reaches the container through the generated `appsettings.json` and the binds.

What the write accepts (V1–V7, `ClaudeLocalModel.DescribeConfigFault`):

| rule | requirement |
|---|---|
| V1 | provider is `claude` — clear the field before changing provider |
| V2 | **origin only**: `http`/`https`, a host, no credentials, query or fragment, path empty or `/`, ≤ 500 chars, no surrounding whitespace. `…/v1` is rejected: Claude Code appends `/v1/messages` itself |
| V3 | host is not `localhost`, `127.0.0.0/8`, `::1`, `0.0.0.0`/`::` (or their mapped forms) — inside the container that is the container itself |
| V4 | model is 1–100 chars matching `^[A-Za-z0-9][A-Za-z0-9._:/-]*$` (`--model` is passed unquoted) |
| V5 | model is not `claude-*`, `opus`, `sonnet` or `haiku` (case-insensitive) |
| V6 | model does not start with `ollama/`, `lmstudio/` or a hosted prefix (`zai/`) — those select the codex path |
| V7 | effort is empty |

A fault is a tool error or HTTP 400 and nothing is saved. A valid value is stored in
canonical form (`scheme://host[:port]`, lowercased, default port and trailing `/` dropped).
The same rules run again at provision time (the agent stays down with the named fault) and
at agent startup (exit 1, see §6).

**Which address.** Use the inference server's LAN address or a host name the container can
resolve. `host.docker.internal` resolves on Docker Desktop, but **not** on a native Linux
Docker Engine host unless the container has an `extra_hosts: host-gateway` entry — and the
orchestrator adds none.

**Trust.** `http` is accepted to any host: the agent's whole conversation goes there in
cleartext. That is an operator decision, at the same trust level as the `image` override
that `update_agent_config` already exposes. Point it only at a server you control.

## 3. What changes in the container

**The claude child's environment** — the only place `ANTHROPIC_*` exists. It mirrors
`ollama launch claude` (`cmd/launch/claude.go`, commit `01c0fbfd`); only the token differs:

```
ANTHROPIC_BASE_URL=<AnthropicBaseUrl>
ANTHROPIC_API_KEY=
ANTHROPIC_AUTH_TOKEN=phleet-local-no-auth
CLAUDE_CODE_ATTRIBUTION_HEADER=0
CLAUDE_CODE_TOTAL_TOKENS_REMINDER=off
DISABLE_ERROR_REPORTING=1
DISABLE_FEEDBACK_COMMAND=1
CLAUDE_CODE_DISABLE_FEEDBACK_SURVEY=1
CLAUDE_CODE_AUTO_MODE_SERVER=0
ANTHROPIC_DEFAULT_OPUS_MODEL=<Model>
ANTHROPIC_DEFAULT_SONNET_MODEL=<Model>
ANTHROPIC_DEFAULT_HAIKU_MODEL=<Model>
CLAUDE_CODE_SUBAGENT_MODEL=<Model>
```

`CLAUDE_CODE_OAUTH_TOKEN` is removed. The placeholder token is a public constant, not a
secret; local servers ignore it. None of this is set in `.env`, the container env,
`settings.json` or the `Fleet.Agent` process, and it is never written to any file. The
argv is unchanged: `--model <Model>` already routes the main loop. Each process start logs
`Claude local model mode: base URL …, model …` at `Information`.

**Credential isolation — four paths closed:**

| path | local mode |
|---|---|
| orchestrator bind `./.claude-credentials.json → /root/.claude-host/.credentials.json` | not bound; a credential mount into `/root/.claude*` makes provisioning refuse |
| `entrypoint.sh` copies `.claude-host.json` / `.credentials.json` | skipped; `/root/.claude/.credentials.json` (persisted in the workspace volume) is removed, and the script exits 1 if it cannot be |
| claude `token-update` broadcast writes the credentials file and restarts claude | ignored before any write or restart (`Claude token update ignored — local model mode`) |
| agent queue bound to the `fleet.relay` fanout (token broadcasts only) | not bound; a binding from an earlier life is removed on a separate short-lived channel (a failure there is a warning — the handler guard still holds) |

At startup the agent also refuses to run if either credential file exists.

**Sessions.** Claude session ids live in agent memory only. Enabling or rolling back needs a
reprovision, which starts a fresh claude process, so a session built against Anthropic is
never resumed against the local server, or the reverse.

**Agents without the field** are unchanged: same argv, child env, container env and binds;
`settings.json`, `.mcp.json` and `roles/` byte-identical; the generated `appsettings.json`
gains one `"AnthropicBaseUrl": null` line.

## 4. Server setup

Create a **dedicated derived tag** for the agent with both `num_ctx` **and** `num_batch`
pinned. The Claude Code harness sends ~21K input tokens on the first turn; with `num_ctx`
alone Ollama still fails with *"input … too large … increase the physical batch size"*.

```
# Modelfile
FROM <base-model>
PARAMETER num_ctx <context>
PARAMETER num_batch <batch>
```

```
ollama create <tag> -f Modelfile
```

## 5. Never share a model instance another workload pins

Anthropic-compatible requests reset the instance's keep-alive and can reload it at a
different context length. Give the agent its own derived tag and never point it at an
instance a production workload depends on.

## 6. Failure behaviour

| dependency | failure | behaviour |
|---|---|---|
| inference server | refused / DNS failure / unknown model / non-Anthropic response | Claude Code retries, then emits an `is_error` result, delivered to the caller as an error. The container stays up; no restart loop |
| inference server | accepts but never answers (wedge) | each request is bounded by Claude Code's default API timeout, then retried; a chat turn has no deadline of its own, so the agent looks stalled. Recover with `/cancel` |
| `appsettings.json` | invalid or non-canonical local config, or a credential file present | startup gate logs `Critical` and exits 1 (`docker inspect` `ExitCode` 1) |
| orchestrator DB | write fails validation | tool error / HTTP 400, nothing saved |
| orchestrator DB | row edited directly into an invalid state | provisioning throws; the agent stays down with the named fault |
| filesystem | stale credentials file cannot be removed | `entrypoint.sh` prints `ERROR:` and exits 1 |
| RabbitMQ | unbind fails | warning; startup continues |
| warmup | 60 s warmup is shorter than a cold local prefill | existing warning; the first real turn cold-starts |

There is **no startup reachability probe** on purpose: an inference-host reboot must not
restart-loop every local agent.

## 7. Measured latency

Claude Code through Ollama's Anthropic endpoint, Qwen 27B, Apple Silicon, before
implementation:

| case | time |
|---|---|
| minimal prompt | 19 s |
| full harness, cold (~21K input tokens) | 194 s |
| identical turn, cached | 33 s |
| Bash round trip | 70 s |
| full Bash + MCP task | 109 s, correct ground truth |

The route is viable; cold latency and instruction compliance are what acceptance measures.

## 8. Verifying it

On the local agent, after reprovision:

```
# no Claude credential mount
docker inspect <container> --format '{{json .Mounts}}'
# nothing at the credentials path
docker exec <container> test ! -e /root/.claude/.credentials.json
# D2 values on claude, none on dotnet
docker exec <container> sh -c 'tr "\0" "\n" < /proc/$(pgrep -of "claude -p")/environ | grep -E "^(ANTHROPIC|CLAUDE_CODE)"'
docker exec <container> sh -c 'tr "\0" "\n" < /proc/1/environ | grep ^ANTHROPIC || echo none'
# no fleet.relay binding for this agent's queue
rabbitmqctl list_bindings source_name destination_name | grep fleet.relay
# placeholder never on disk
docker exec <container> grep -rlF phleet-local-no-auth /root/.claude /workspace || echo none
```

Never test the token-update guard by publishing to the `fleet.relay` fanout: it reaches
every agent and overwrites real credentials. Publish to `fleet.group` with the local
agent's routing key only.

## 9. Rollback

Per agent:

```
update_agent_config agent_name=<agent> anthropic_base_url="" model=<claude-model>
reprovision_agent <agent>
```

That restores the credentials bind and the `fleet.relay` binding. In code, revert the
change; the added nullable column is ignored by older code, so the down-migration is
optional.

**Version skew:** a new orchestrator with an old agent image runs a local agent with no
local routing and no credentials, so its turns fail. Deploy the agent image together with
the orchestrator.
