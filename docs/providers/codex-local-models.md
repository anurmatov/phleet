# Codex Provider — Local Models

A `codex` agent can run its turns against a local OpenAI-compatible inference server
instead of a frontier model. This is a configuration path, not a second agent loop:
`CodexExecutor` still drives `codex app-server` and speaks the same JSON-RPC protocol.

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

## `CODEX_OSS_BASE_URL` and the fail-fast

codex resolves the provider's endpoint from `CODEX_OSS_BASE_URL` in the app-server's
environment. The agent container inherits it, so it is a **provision-time env var**:

1. Add the key to `./fleet/.env`.
2. Attach it to the agent as an **Env Ref** (dashboard → agent config → Env Refs).
3. Reprovision the agent. It is read once at process start and is not live-mutable.

From inside a container the host's inference port is reachable on
`host.docker.internal`:

```
CODEX_OSS_BASE_URL=http://host.docker.internal:11434/v1
```

If the model carries a prefix and this value is missing or blank, **the agent host
refuses to start**: `AgentHostRegistration.ValidateStartupConfiguration` runs before
`app.Run()` and logs the fault at `Critical`; `Program.cs` catches and calls
`Environment.Exit(1)`, so the process ends with exit code 1 and the container is
visibly down. That placement is the point — `/health` answers `ok` unconditionally
and `WarmupService` catches executor startup failures as a warning, so a check any
later would leave the container up, reported healthy, and unable to answer a single
turn.

⚠️ **The explicit exit is load-bearing — do not reduce this to a `throw`.** By the
time the gate runs, `builder.Build()` holds a foreground thread, and .NET tears a
process down on an unhandled main-thread exception only once none remain. Booted on
the real image, the throw-only version never served `/health` (correct) but never
exited either: no listeners in `/proc/net/tcp`, `dotnet` as PID 1 at ~101% CPU across
three samples, and `docker ps` reporting `Up`. `restart: unless-stopped` never acts
on that, so it is the wedge signature that took ~15 agents offline on 2026-08-13. The
acceptance evidence for this gate is `docker inspect` showing `status=exited` with a
non-zero `ExitCode` — never a code read.

There is no fallback to codex's built-in default: that default is `localhost`, which
inside a container is the container itself. The executor keeps the same check as a
backstop for the paths that construct it without a host. On a sound configuration the
resolved provider, model and base URL are logged at `Information` on every process
start.

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

Measure the agent's assembled prompt before picking one: an agent whose prompt
exceeds the context window is out of reach regardless of tool-call quality.

## Operational cautions

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

Set the agent's Model back to the frontier value and reprovision. Unload the local
model with `keep_alive: 0`. Blast radius is one agent — a mid-turn outage surfaces as
a failed turn.
