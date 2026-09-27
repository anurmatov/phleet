# Codex Provider — Local Models

A `codex` agent can run its turns against a local OpenAI-compatible inference server
instead of a frontier model. This is a configuration path, not a second agent loop:
`CodexExecutor` still drives `codex app-server` and speaks the same JSON-RPC protocol.

**To set it up, use `local-models.md`.** In the dashboard you set **Runs on → Local
server**, the Server URL, the server software, the model tag and the context window. That page
also covers the API fields, validation, upgrading, rollback and troubleshooting. This page
explains how the Codex path works underneath.

## The prefix convention

An agent opts in through its **Model** value. Prefix the model id with the codex
built-in provider it should use:

```
ollama/gpt-oss:20b
lmstudio/qwen3-coder
```

At `thread/start` the executor splits that into two fields:

| field | value |
|---|---|
| `model` | `gpt-oss:20b` |
| `modelProvider` | `ollama` |

Only `ollama` and `lmstudio` are recognised, matched case-insensitively. They are
codex's own reserved built-in provider ids — codex rejects any other value, and it
also rejects attempts to redefine these two.

**Anything else passes through untouched.** A model with no prefix, or with a slash
that is not one of those two ids (`owl/t-lite`), is sent exactly as configured and
the `thread/start` payload carries no `modelProvider` key at all. An agent that has
not opted in behaves byte-for-byte as it did before.

The dashboard shows a prefixed model as two fields: **Server** (Ollama or LM Studio) and
**Model tag**. It stores them as `ollama/<tag>` or `lmstudio/<tag>`.

## Where the server URL comes from, and the fail-fast

codex resolves the provider's endpoint from `CODEX_OSS_BASE_URL` in the app-server's
environment. Fleet now sets it from the agent's config:

1. You enter the Server URL as an origin, for example `http://<server-address>:11434`.
2. Provisioning writes `Agent.CodexOssBaseUrl = <origin>/v1` into the generated
   `appsettings.json`.
3. `CodexExecutor` sets `CODEX_OSS_BASE_URL` on the codex app-server child from that value.
   It overrides any value the container inherited.

No Env Ref is needed. The value is read at process start, so a change takes effect on
reprovision. Agents that still use the Env Ref are covered under "Legacy Env Ref" below.

Provisioning catches a missing URL first. A prefixed model with no URL and no usable
`CODEX_OSS_BASE_URL` Env Ref fails provisioning with a named fault, and no container is
created (P1 in `local-models.md` §5).

The agent host checks again at startup. It reads `Agent.CodexOssBaseUrl` first, then the
environment. If `Agent.CodexOssBaseUrl` is present, it must be exactly the canonical origin plus
`/v1`, and the model must carry a prefix. Otherwise the host exits 1 with "CodexOssBaseUrl is not
<canonical origin>/v1; redeploy both images and reprovision" (S1).

If the model carries a prefix and neither source has a value, **the agent host
refuses to start**: `AgentHostRegistration.ValidateStartupConfiguration` runs before
`app.Run()` and logs the fault at `Critical`; `Program.cs` catches and calls
`Environment.Exit(1)`, so the process ends with exit code 1 and the container is
visibly down. That placement is the point — `/health` answers `ok` unconditionally
and `WarmupService` catches executor startup failures as a warning, so a check any
later would leave the container up, reported healthy, and unable to answer a single
turn.

⚠️ **The explicit exit is load-bearing — do not reduce this to a `throw`.** A throw
alone did not terminate reliably, and what it did instead **differed by platform**:

| platform | throw-only behaviour |
|---|---|
| arm64 (the real image) | never served `/health` — correct — but never exited either: no listeners in `/proc/net/tcp`, `dotnet` as PID 1 at ~101% CPU across three samples, `docker ps` reporting `Up`, which `restart: unless-stopped` never acts on |
| x86-64 | aborted with SIGABRT (exit 134), writing a ~154 MB core per attempt |

**Why they differed was never established.** The obvious explanation — a foreground
thread started by `builder.Build()` blocking teardown — was tested directly with a
minimal repro and falsified, so it is not recorded here and should not be inferred.
`Environment.Exit(1)` is used precisely because it sidesteps the question: it
terminates the process outright and does not depend on unhandled-exception
propagation at all.

With the exit in place and `restart: unless-stopped`, a misconfigured agent loops
visibly — measured over 60s: `status=restarting`, `ExitCode` 1, 10 restarts, **zero
core files**, writable layer 0 B, and `docker ps` showing `Restarting (1)` rather
than `Up`. That loop is the intended outcome: a container that cannot start should
be conspicuous. What made the throw-only loop harmful was the core dump each
iteration, not the looping.

The acceptance evidence for this gate is `docker inspect` — a non-zero `ExitCode`
and no core file — never a code read.

There is no fallback to codex's built-in default: that default is `localhost`, which
inside a container is the container itself. The executor keeps the same check as a
backstop for the paths that construct it without a host. On a sound configuration the
resolved provider, model and base URL are logged at `Information` on every process
start, with `source=agent config` or `source=legacy env` and `contextWindow=`.

## Legacy Env Ref

Before #382, the URL reached codex only through an Env Ref. Agents set up that way keep
working unchanged:

1. `CODEX_OSS_BASE_URL=http://<server-address>:11434/v1` in `./fleet/.env`.
2. The key attached to the agent as an **Env Ref**.
3. A reprovision.

Such an agent ignores its context window. Move it to the Server URL with the steps in
`local-models.md` §9, then remove the Env Ref. If an agent has both, the Server URL wins, and
provisioning logs "Env Ref superseded by the local server URL". Keep the Env Ref only for a
server behind a path prefix, which cannot be entered as an origin.

## Choosing a model

Prefer **`gpt-oss:20b`** — it is codex's own default for these providers, harmony
templated and tool-trained. Agent turns are mostly MCP tool calls, so structured
tool-use quality matters more than raw benchmark score.

Pin the context window on the inference host rather than leaving it to the default:

```
FROM gpt-oss:20b
PARAMETER num_ctx 65536
```

With no `num_ctx`, ollama's default is VRAM-tiered — it resolves to 262144 on a
large-memory host and steps down to 4096 if that will not fit. Either a multi-GB KV
cache grab or silent truncation, neither chosen deliberately.

Then set the agent's **Context window** to the same value, never higher. Fleet passes it to
codex as `thread/start` `config.model_context_window`, and codex compacts from it. Without it,
codex uses its own fallback window and does not compact before the server rejects the prompt.

For Qwen3.8, use the full tags `qwen3.8:27b` or `qwen3.8-flash-next:125b-a6b-q4_K_M`, and set
effort `none`. See `local-models.md` §8.

Measure the agent's assembled prompt before picking one: an agent whose prompt
exceeds the context window is out of reach regardless of tool-call quality.

## Operational cautions

- ⚠️ **Known gap until #383: a Codex local agent keeps the OpenAI credential.** It still
  holds the OpenAI credential (the `.codex-credentials.json` bind is mounted), applies Codex
  token broadcasts, and has its queue bound to the `fleet.relay` fanout. A Claude local agent
  does none of these. The local model has shell access in a container that holds a working
  OpenAI credential. If the model must not reach a cloud credential, use Claude local. See
  `local-models.md` §7.
- **Use Ollama 0.34 or later.** Codex sends MCP tools as namespaced tools. Older Ollama
  releases return the call without its namespace, and Codex rejects it as an unsupported
  call.
- **Use effort `none` for Qwen3.8.** At other settings a turn can end with reasoning only
  and no final message, so the agent sends nothing back. Fleet now sends `none` and
  `minimal` to codex as-is; before #382 it dropped them and sent no effort.
- **Do not point an agent at a model another workload has pinned with a long
  keep-alive.** OpenAI-compatible requests carry no `keep_alive` field, and every
  request resets the loaded model's expiry to the server default — one agent turn
  silently demotes another service's pin.
- **`app-server` runs no readiness check.** The auto-pull helper exists only on the
  exec/TUI `--oss` path, so there is no surprise multi-GB pull — but the model must
  already exist on the inference host.
- **Gate the first load on headroom.** Resident VRAM plus the new model's footprint
  must fit the reported total.
- **No wedge-mode guard, and the failure is agent-wide.** A `stream_idle_timeout_ms`
  override on `thread/start` would be the natural protection against a server that
  accepts a request and never answers, but it is not available on codex 0.153.4:
  `model_providers.ollama.*` is refused outright (`reserved built-in provider IDs …
  cannot be overridden`), and an unrecognised top-level config key is silently
  dropped.

  Nothing else bounds it. `CodexExecutor.ExecuteAsync` takes `_turnLock` for the
  whole turn, and a chat-driven turn carries no deadline — `TaskManager` builds its
  per-task `CancellationTokenSource` with no `CancelAfter`. So a wedged inference
  server does not merely stall one reply: the turn never completes, the lock is never
  released, and **every subsequent task queues behind it — the agent stops answering
  anything**. Only a delegated turn has a deadline of its own, from the caller's
  activity budget. Recovery is `/cancel` (or `/cancel_bg`), or restarting the
  container. Treat an agent on a local model that has gone silent as a wedged
  inference server until proven otherwise, and probe the server's `/api/embed` or
  `/v1` path directly rather than the agent.

## Rollback

Set **Runs on → Cloud**, pick a frontier model, and click **Save & Reprovision**. The Server
URL and the context window are cleared. With MCP:
`update_agent_config agent_name=<agent> local_base_url="" model=<cloud-model>`, then
`reprovision_agent <agent>`. Unload the local model with `keep_alive: 0`. Blast radius is
one agent — a mid-turn outage surfaces as a failed turn.

Rolling back the #382 release itself needs one step per Codex agent with a URL. See
`local-models.md` §11.
