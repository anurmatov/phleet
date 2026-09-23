# Codex hosted models: DeepSeek and GLM

A codex agent can run a hosted DeepSeek or GLM model by prefixing its `Model`:

| Model string | Vendor | Key env var |
|---|---|---|
| `deepseek/deepseek-v4-pro` | DeepSeek (`https://api.deepseek.com`) | `DEEPSEEK_API_KEY` |
| `openrouter/z-ai/glm-5.3` | OpenRouter (`https://openrouter.ai/api/v1`) | `OPENROUTER_API_KEY` |

Codex stays the harness, so streaming, steering, cancel, the system prompt and MCP work the same
way they do for a frontier model. Hosted routing applies to the **codex provider only**. The
`ollama/` and `lmstudio/` prefixes are unchanged (see `codex-local-models.md`).

## 1. What leaves the host

Everything the model sees goes to the vendor: the system prompt, the conversation, tool results and
MCP output. That includes memory contents and anything an MCP server returns. Enable a hosted model
per agent, deliberately, and only for agents whose data you may share with that vendor.

## 2. Setup

1. Put the key in `.env` (`DEEPSEEK_API_KEY=` or `OPENROUTER_API_KEY=`). `setup.sh` offers both as
   optional masked prompts when codex is selected.
2. Attach the same name to the agent as an **Env Ref** in the dashboard.
3. Set the agent's provider to `codex` and its model to the prefixed string.
4. Reprovision the agent. A restart is not enough: the flags below are written at provision time.

## 3. How it works

### Routing

`HostedModelProviders` (`src/Fleet.Shared/`) is the only registry. The orchestrator writes two
fields into the agent's `appsettings.json`:

- `Agent.HostedProvider`: true for a codex agent whose model has a hosted prefix.
- `Agent.HostedProviderKeyEnv`: the key's env var name, or null.

On `thread/start`, `CodexExecutor` sends the bare model (`deepseek-v4-pro`, `z-ai/glm-5.3`), the
provider id `phleet_deepseek` or `phleet_openrouter`, and `config` overrides that define that provider
for this thread only, pointed at `http://127.0.0.1:<port>/<prefix>`. No `config.toml` is written,
and codex gets no `env_key`. If the `thread/start` response does not echo the provider id, startup
fails with codex's message. There is no fallback.

### The adapter

Codex declares every MCP tool as a Responses `namespace` tool. Neither vendor accepts those, so a
direct connection would give the model zero MCP tools. `HostedProviderAdapterHost` runs a **separate
nested web app** bound to `127.0.0.1:0` only. It is built with no configuration sources, so no
`Kestrel:Endpoints` or `ASPNETCORE_URLS` setting can give it a second listener. The main agent app
has no adapter route. The adapter serves only `POST /<prefix>/responses` and returns 404 for anything
else, including codex's `/models` refresh.

For each request it:

1. Expands every `namespace` entry into `function` tools, using codex's own join rule
   (`mcp__memory` + `memory_get` becomes `mcp__memory__memory_get`). Other tool types
   (`web_search`, `custom`, …) are removed and logged.
2. Returns 400 before any upstream call if two names collide after flattening, or a name fails
   `^[A-Za-z0-9_-]+$` or exceeds the vendor limit (DeepSeek 128, OpenRouter 64).
3. Applies the top-level field allowlist:

   | Field | DeepSeek | OpenRouter |
   |---|---|---|
   | `model`, `instructions`, `input`, `tools`, `tool_choice`, `stream`, `max_output_tokens` | pass | pass |
   | `parallel_tool_calls`, `include`, `prompt_cache_key`, `text.verbosity` | strip | pass |
   | `store` | strip | pass if `false`; 400 if `true` |
   | `reasoning.effort`, `reasoning.summary`, `text.format` | pass | pass |
   | `reasoning.context`, `service_tier`, `client_metadata`, `stream_options`, `access_programs`, `previous_response_id`, anything unknown | strip | strip |

4. Applies the `input[]` item policy: `message` passes. `function_call` and `function_call_output`
   have `namespace` folded into `name`. `reasoning` passes, and DeepSeek also loses
   `encrypted_content`. Any other item type is stripped and counted.
5. Sends the request to the fixed HTTPS upstream with `Authorization: Bearer <key>`. No inbound header
   is forwarded, and upstream URLs are not configurable.
6. Forwards the SSE stream one event at a time. On `response.output_item.added`,
   `response.output_item.done` and `response.completed` / `.incomplete` / `.failed` (and in a JSON
   `output[]`), a `function_call` whose name **exactly** matches a flattened name gets its
   `namespace` and `name` restored. Any other name is forwarded unchanged, and codex reports it as
   an unsupported call. There is no prefix or similarity matching: a guessed name could dispatch a
   different, privileged MCP tool.

When codex disconnects (cancel, interrupt), the upstream request is cancelled with it.

### Key isolation

Codex runs a root shell for the model, so a key in any environment block is one `env` call away from
the transcript. The key therefore never reaches a child process:

1. `entrypoint.sh` reads the two orchestrator fields. It keeps no prefix list of its own. For a hosted
   agent it writes the named variable's value to `/run/phleet-hosted-key` (mode `0400`, `umask 077`).
2. It then **unconditionally, on every agent and provider**, unsets `DEEPSEEK_API_KEY` and
   `OPENROUTER_API_KEY` before `exec dotnet`. PID 1 never has either. Both names are reserved for
   hosted routing: a non-hosted agent that carries one loses it, and the entrypoint prints one line
   naming it.
3. At startup the agent reads the key file once, deletes it, and keeps the value in memory.
4. `CodexExecutor` also removes both names from codex's start environment, for every model.
5. A hosted agent holds no OpenAI credential. It gets no `.codex-credentials.json` bind,
   `entrypoint.sh` removes `/root/.codex/auth.json` from the persisted workspace volume, and codex
   token broadcasts are ignored.

Residual risk: a same-container process with ptrace-level access could still read the adapter's
memory. After startup the key is in no environment block, no file and no child process.

## 4. Limits

- **Context window.** Codex does not know these model slugs. It falls back to a 272K window and no
  default effort. If a thread outgrows the model's real window, the vendor's error ends the turn.
  Restart the agent to start a fresh thread.
- **Effort.** DeepSeek receives every codex value (it maps `minimal`, `medium` and `xhigh` itself).
  OpenRouter receives `minimal`, `low`, `medium` and `high` only. Any other value is omitted (never
  remapped) and one Warning names it. Fleet `max` becomes codex `xhigh`, so it is omitted for
  OpenRouter.
- **No hosted web search or image tools.** Those tool types are removed.
- **GLM only through OpenRouter.** Z.ai's first-party API has no Responses endpoint.
- **Vendor behaviour is doc-derived until the Phase 0 probe runs.** The field policy follows each
  vendor's published documentation. The adapter test fixtures are marked `synthetic` until scrubbed
  captures from a raw probe with real keys replace them (issue #335, Phase 0).

## 5. Troubleshooting

Healthy startup line:

```
Codex hosted provider deepseek via loopback adapter 127.0.0.1:<port>
```

One line per request:

```
HostedProviderAdapter provider=deepseek status=200 durationMs=… toolsFlattened=… fieldsStripped=… itemsStripped=… droppedToolTypes=… unmatchedCalls=0
```

Healthy means `status=200` lines during turns. Broken means non-200 lines, any `Critical` startup
line, or `unmatchedCalls>0`, which means the model called a tool name that was not offered.

| Symptom | Cause | Fix |
|---|---|---|
| Container exits 1 before `dotnet` starts: `ERROR: DEEPSEEK_API_KEY is unset, blank or '<secret>'` | Env Ref attached but no value in `.env`, or no Env Ref | Add the key to `.env`, attach the Env Ref, reprovision |
| `Critical` … `Hosted provider config parity failed` | Orchestrator and agent images disagree about the model | Deploy both images from the same commit, then reprovision |
| `Critical` … key file missing, empty or `<secret>` | The entrypoint handoff did not run, or the key is unusable | Check the entrypoint output and the Env Ref |
| `Critical` … loopback adapter failed to start | The nested app could not bind loopback | Check the container's network namespace; the host exits 1 |
| Startup failure `thread/start did not echo modelProvider` | Codex did not accept the per-thread provider definition | Check the codex CLI pin; do not work around it |
| Turn fails with `phleet adapter: upstream deepseek unreachable: <Type>` (502) | DNS, TLS or connect failure | Check outbound network. Codex retries twice |
| Turn fails with the vendor's 401 / 429 / 400 message | Bad key, rate limit, or a request the vendor rejects | Fix the key, or read the vendor message |
| Turn fails with `phleet adapter: rejected tools …` (400) | Tool name collision or an over-long name | Rename the tool at its MCP server |
| Turn hangs, then fails after about 5 minutes | Upstream stream went silent | Codex's `stream_idle_timeout_ms` (300 s) fires, then the turn fails |

## 6. Rollback

Set affected agents back to an unprefixed model and reprovision. There is no schema or config
migration. The unconditional `unset` in `entrypoint.sh` applies to every agent whatever its model,
so reverting the change means reverting the PR.
