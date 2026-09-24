# Codex hosted models: GLM on the Z.ai GLM Coding Plan

A codex agent can run GLM on your **Z.ai GLM Coding Plan subscription** by setting its `Model` to
`zai/glm-5.3`:

| Model string | Vendor route | Key env var |
|---|---|---|
| `zai/glm-5.3` | Z.ai Coding Plan, Responses endpoint `https://api.z.ai/api/v1` | `ZAI_CODING_PLAN_API_KEY` |

This follows Z.ai's own documented Codex setup: Codex speaks the OpenAI Responses protocol to Z.ai's
dedicated Responses endpoint, on the subscriber's plan, the same way Phleet already runs Claude and
Codex on their subscriptions. It is not prepaid API credit.

Codex stays the harness, so streaming, steering, cancel, the system prompt and MCP work the same way
they do for a frontier model. Hosted routing applies to the **codex provider only**. The `ollama/` and
`lmstudio/` prefixes are unchanged (see `codex-local-models.md`). Any other prefix, for example
`acme/x`, is passed to codex unchanged, exactly as before.

## 1. What it is, and what leaves the host

Everything the model sees goes to Z.ai: the system prompt, the conversation, tool results and MCP
output. That includes memory contents and anything an MCP server returns. Enable GLM per agent,
deliberately, and only for agents whose data you may share with Z.ai.

## 2. Plan terms

Z.ai's subscription terms (§4) decide who and what may use the quota. Read them before enabling GLM.

- **Supported tools only.** Codex is on Z.ai's list of supported Coding Plan tools. Z.ai does not
  document how it recognises one. Phleet's Codex identifies itself as `originator: phleet`, and
  Phleet does not disguise that.
- **Personal use only.** The quota is for the subscriber. **Never give a `zai/` model to an agent
  that answers other people**: household members, customers, shared group chats. Phleet cannot
  enforce this in code; it is the operator's rule.
- **One subscription per install.** Do not share the key between installs or people.
- **Risk notices.** Z.ai may restrict or suspend an account it flags; more than three violations can
  end in a ban. If the Plan Overview shows a notice, move GLM agents back to an unprefixed model
  (§8), then read and appeal the notice in the Z.ai console.

## 3. Setup

1. Subscribe to a GLM Coding Plan and create an API key for it.
2. Put the key in `.env` as `ZAI_CODING_PLAN_API_KEY=`. `setup.sh` offers it as an optional masked
   prompt when codex is selected.
3. Attach `ZAI_CODING_PLAN_API_KEY` to the agent as an **Env Ref** in the dashboard.
4. Set the agent's provider to `codex`, its model to `zai/glm-5.3`, and its effort to `low` or
   `high` (§5).
5. Reprovision the agent. A restart is not enough: the flags below are written at provision time.

## 4. How it works

### Routing

`HostedModelProviders` (`src/Fleet.Shared/`) is the only registry. The orchestrator writes two
fields into the agent's `appsettings.json`:

- `Agent.HostedProvider`: true for a codex agent whose model has the `zai/` prefix.
- `Agent.HostedProviderKeyEnv`: `ZAI_CODING_PLAN_API_KEY`, or null.

On `thread/start`, `CodexExecutor` sends the bare model `glm-5.3`, the provider id `phleet_zai`, and
`config` overrides that define that provider for this thread only, pointed at
`http://127.0.0.1:<port>/zai`. No `config.toml` is written, and codex gets no `env_key` and no
`experimental_bearer_token`. If the `thread/start` response does not echo `phleet_zai`, startup fails
with codex's message. There is no fallback.

### The forwarder

`HostedProviderAdapterHost` runs a **separate nested web app** bound to `127.0.0.1:0` only. It is
built with no configuration sources, so no `Kestrel:Endpoints` or `ASPNETCORE_URLS` setting can give
it a second listener. The main agent app has no forwarder route.

Its one handler, `HostedProviderForwarder`, is a transparent forwarder. It translates nothing. In
order, it:

1. Serves only `POST /zai/responses`; anything else is 404, including codex's `/models` refresh.
2. Requires the per-start token (below); a missing, repeated or wrong token is 401.
3. Requires a `Content-Length`; a chunked request is 411. A body over Kestrel's 30 MB limit is 413.
4. Streams the request body to `https://api.z.ai/api/v1/responses` as bytes with the same
   `Content-Length`. Every header passes with its value unchanged (`User-Agent` and `originator`
   included), except: `Authorization` is replaced with `Bearer <key>`, the token header is removed,
   `Host` is set by the handler, and `Expect` plus hop-by-hop headers (and any header named in
   `Connection`) are dropped.
5. Returns the upstream status and headers at once, then copies the body as it arrives, flushing
   after every read. Non-2xx answers pass through the same way, so a turn fails with Z.ai's own
   message.

The upstream URL is fixed and HTTPS, never read from config: a wrong Z.ai endpoint does not draw on
the subscription quota. When codex disconnects (cancel, interrupt), the upstream request is
cancelled with it.

### Key isolation

Codex runs a root shell for the model, so a key in any environment block is one `env` call away from
the transcript. The key therefore never reaches a child process:

1. `entrypoint.sh` reads the two orchestrator fields. It keeps no prefix list of its own. For a hosted
   agent it writes the named variable's value to `/run/phleet-hosted-key` (mode `0400`, `umask 077`).
2. It then **unconditionally, on every agent and provider**, unsets `ZAI_CODING_PLAN_API_KEY` before
   `exec dotnet`. PID 1 never has it. The name is reserved for hosted routing: a non-hosted agent that
   carries it loses it, and the entrypoint prints one line naming it. The list sits on the line under
   `# phleet:reserved-key-names`; a test keeps it equal to the registry.
3. At startup the agent reads the key file once, deletes it, and keeps the value in memory.
4. `CodexExecutor` also removes the name from codex's start environment, for every model.
5. A hosted agent holds no OpenAI credential. It gets no `.codex-credentials.json` bind,
   `entrypoint.sh` removes `/root/.codex/auth.json` from the persisted workspace volume, and codex
   token broadcasts are ignored.

### The per-start token

Every agent start generates a new 32-byte random token. Codex receives it through the `thread/start`
provider `http_headers` and sends it as `x-phleet-forwarder-token` on every request. It never goes
into an environment variable, a file, argv or a log line.

**What it does and does not do.** Only processes inside the agent container can reach the
forwarder, but that includes the model's shell and every MCP child. With the token, a plain request
from any of them gets 401 and never reaches Z.ai. The token is a **mitigation, not a guarantee**: a
root process in the container can still read it from Codex's memory or output and then spend quota.
The key itself cannot be read from the shell; a process with ptrace-level access to the agent could
still read the forwarder's memory. "Only Codex talks to Z.ai" is the intent, not an enforced
property.

## 5. Limits

- **Text only.** GLM on this route takes no image input; an image turn fails with Z.ai's message.
- **Effort: set `low` or `high`.** Only those two are forwarded; any other Phleet effort is omitted,
  never remapped, with one Warning. Z.ai documents `low`, `high` and `max` for Codex, and its
  handling of other values is undocumented and has changed: on 2026-09-24, before the plan was
  active, it rejected `medium` and `xhigh` (`invalid_request`: "please use low, high, or max"); after
  activation the same day it accepted them. When codex sends no effort, Z.ai answers normally, but it
  does not echo which effort it used, and its own Codex catalog defaults to `max`, the deepest,
  slowest and most quota-hungry setting. Codex has no `max`.
- **Context window.** Codex does not know the `glm-5.3` slug. It falls back to a 272K window and no
  default effort. If a thread outgrows the model's real window, Z.ai's error ends the turn. Restart
  the agent to start a fresh thread.
- **Z.ai's own MCP servers** (vision, web search, web reader) are not wired.
- **Concurrency** depends on the plan tier; excess requests get 429.

## 6. Troubleshooting

Healthy startup line:

```
Codex hosted provider zai via loopback adapter 127.0.0.1:<port>
```

One line per request:

```
HostedProviderAdapter provider=zai status=200 durationMs=… requestBytes=… responseBytes=…
```

Healthy means `status=200` lines during turns. Codex closes the stream as soon as it has read
`response.completed`, often before Z.ai's trailing `[DONE]`, so a `status=499` line with
`responseBytes` above zero in a turn that completed is normal and common (13 of 75 requests in the
#335 acceptance runs). A `status=499` on `/cancel` or on a turn that then fails is a real abort. Broken means other
non-200 lines, any `Critical` startup line, or a codex `unsupported call`. `status=401` lines while no codex turn is running mean something
other than codex tried to use the forwarder: check the agent's recent tool calls.

The checks run 404, then 401, then 411, so a tokenless chunked request logs `status=401`, not 411.

| Symptom | Cause | Fix |
|---|---|---|
| Container exits 1 before `dotnet` starts: `ERROR: ZAI_CODING_PLAN_API_KEY is unset, blank or '<secret>'` | Env Ref attached but no value in `.env`, or no Env Ref | Add the key to `.env`, attach the Env Ref, reprovision |
| `Critical` … `Hosted provider config parity failed` | Orchestrator and agent images disagree about the model | Deploy both images from the same commit, then reprovision |
| `Critical` … key file missing, empty or `<secret>` | The entrypoint handoff did not run, or the key is unusable | Check the entrypoint output and the Env Ref |
| `Critical` … loopback adapter failed to start | The nested app could not bind loopback | Check the container's network namespace; the host exits 1 |
| Startup failure `thread/start did not echo modelProvider` | Codex did not accept the per-thread provider definition | Check the codex CLI pin; do not work around it |
| Every request `status=401`, turn fails with `missing or invalid forwarder token` | Codex did not send the `http_headers` token | Check the codex CLI pin. Do not drop the token check or move it to an env var |
| Turn fails with `phleet adapter: upstream zai unreachable: <Type>` (502) | DNS, TLS, connect failure or the 10 s connect timeout | Check outbound network. Codex retries twice |
| Turn fails with `Quota exceeded. Check your plan and billing details.` | Z.ai answered `insufficient_quota`: "Insufficient balance or no resource package". The key has no active Coding Plan, the plan's 5-hour or weekly quota is used up, or Z.ai did not count the request as Coding Plan use | Check the Plan Overview in the Z.ai console: plan active, key from that account, quota left, no risk notice. There is no fallback to another provider |
| Turn fails after reconnect attempts with `please use low, high, or max` | The agent's effort reached Z.ai as something it rejects | Set the agent's effort to `low` or `high` and reprovision |
| Turn fails with `stream disconnected before completion: stream closed before response.completed`, after three `status=200` lines with a tiny `responseBytes` (about 60) | Bad or revoked key. Z.ai answers it with HTTP 200 and a JSON body `{"code":401,"msg":"token expired or incorrect"}`, not an SSE stream. The forwarder passes it through unchanged, and codex reports only the closed stream | Replace the key in `.env`, reprovision |
| Turn fails with 429 | Plan concurrency or rate limit | Codex retries twice; reduce parallel GLM agents |
| Codex logs `unsupported call` | Z.ai returned a tool call codex cannot route | Move the agent back to an unprefixed model and report it on issue #335 |
| Turn hangs, then fails after about 5 minutes | Upstream stream went silent | Codex's `stream_idle_timeout_ms` (300 s) fires, then the turn fails |

## 7. Ongoing checks

Z.ai's FAQ says a request it does not recognise as coming from a supported tool is billed to the cash
balance, silently. A one-time check is not enough. After the first 24 hours of real turns on a GLM
agent, and weekly after that, open the Plan Overview and confirm:

- the usage appears against the Coding Plan quota;
- the cash balance is unchanged;
- there is no risk notice.

## 8. Rollback

Set GLM agents back to an unprefixed model and reprovision. There is no schema or config migration.
The unconditional `unset` in `entrypoint.sh` applies to every agent whatever its model, so reverting
the change means reverting the PR.

## 9. Not supported

- **DeepSeek.** Its machine API draws on a topped-up balance, and no subscription-backed route for
  unattended use is documented.
- **OpenRouter**, or any other prepaid or usage-billed route.
- GLM through the claude executor, Z.ai's Anthropic or Chat Completions endpoints, and Z.ai's
  `max` effort.

An earlier revision of issue #335 probed these with `DEEPSEEK_API_KEY` and `OPENROUTER_API_KEY`.
Those names are no longer reserved, so `entrypoint.sh` no longer unsets them. **Detach any such Env
Refs from your agents and reprovision.**
