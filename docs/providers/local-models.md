# Local models for Claude and Codex agents

A `claude` or `codex` agent can run its turns on an inference server you run, instead of the
cloud. You turn this on per agent with one setting: **Runs on → Local server**. You enter the
server URL, the model tag and the context window. Fleet derives each harness's runtime settings
from those values.

Local is a *where it runs* choice inside the harness you picked. There is no "Local" provider.
The harness still decides the tools, the prompt and the protocol. Gemini has no local mode.

Harness detail lives on two more pages:

- `claude-local-models.md`: the Claude Code child environment, thinking control, credential
  isolation, server setup and measured latency.
- `codex-local-models.md`: the `ollama/` and `lmstudio/` model prefix, the startup gate and
  operational cautions.

## 1. When to use it

Use it when an agent's turns should run on a model you host, for example Qwen3.8 on Ollama.

| | Claude local | Codex local |
|---|---|---|
| provider | `claude` | `codex` |
| harness | Claude Code | codex app-server |
| server API | Anthropic Messages (`/v1/messages`) | OpenAI-compatible (`/v1`) |
| server software | Anthropic-compatible (validated: Ollama) | Ollama or LM Studio |
| stored model | bare tag: `qwen3.8:27b` | prefixed tag: `ollama/qwen3.8:27b` or `lmstudio/qwen3.8:27b` |
| cloud credential in the container | none | **still present until #383** (§7) |

Pick the harness first. If the agent's instructions and workflows are written for Claude Code,
use Claude local. If you need a container with no cloud credential in it, use Claude local.

Everything the agent sees goes to that server: the system prompt, the conversation, tool
results and MCP output. `http` is accepted to any host, so this traffic can travel in
cleartext. Point an agent only at a server you control.

Costs to expect:

- Cold turns are slow. The harness prompt alone is tens of thousands of tokens.
- The server's context size limits what the agent can do. See §6.
- Fleet does not probe the server. A server that is down, a wrong URL or a missing tag shows
  up as a failed turn, not as a failed save or a failed start.

## 2. Set it up in the dashboard

```
Provider    [ Codex (OpenAI)          v ]
Runs on     ( Cloud ) (• Local server )
Server URL  [ http://<server-address>:11434 ]
            Origin only; Fleet adds the API path.
Server      [ Ollama v ]            ← Codex only
Model tag   [ qwen3.8:27b ]
            [Qwen3.8 27B] [Qwen3.8 Flash Next] [gpt-oss 20B]
Context window (tokens)
            [ 131072 ]
            Your server's context size (e.g. num_ctx / --ctx).
            Takes effect on reprovision.
```

1. Open the agent's config.
2. Pick the **Provider**: Claude or Codex.
3. Set **Runs on** to **Local server**.
4. Enter the **Server URL** as an origin only: `http://<server-address>:11434`. Do not add
   `/v1`. Fleet adds the path each harness needs. If the URL ends in `/v1`, the dashboard shows
   "Remove /v1 — Fleet adds the right path for each harness". It does not rewrite the value, and
   Save fails until you remove it.
5. Codex only: pick the **Server**, Ollama (the default) or LM Studio.
6. Enter the **Model tag** your server serves, or click a suggestion chip (§2.1).
7. Enter the **Context window**: the context size your server is configured with, for example
   Ollama's `num_ctx` or a server's `--ctx`. An amber warning shows while it is empty.
8. Check **Effort** (§6).
9. Click **Save & Reprovision**. A plain restart is not enough: the values reach the container
   at provision time.

**Which address.** Use the server's LAN address or a host name the container can resolve.
`localhost` and other loopback addresses are rejected: inside the container they point at the
container itself. `host.docker.internal` resolves on Docker Desktop. It does not resolve on a
native Linux Docker Engine host, because the orchestrator adds no `extra_hosts` entry.

### 2.1 Suggestion chips

A chip fills the model tag. It never fills the URL or the context window.

| chip | tag | harness | also sets |
|---|---|---|---|
| Qwen3.8 27B | `qwen3.8:27b` | Claude, Codex | Codex: effort `none`, with the note "Effort set to none — recommended for Qwen3.8 on Codex" |
| Qwen3.8 Flash Next | `qwen3.8-flash-next:125b-a6b-q4_K_M` | Claude, Codex | same as above |
| gpt-oss 20B | `gpt-oss:20b` | Codex | nothing |

Chips hold public registry tags only. If you serve a derived tag (for example one with
`num_ctx` and `num_batch` pinned), type that tag instead.

### 2.2 Switching

The dashboard applies these rules when you change a setting. The server never rewrites
`Model`. It validates the result and rejects a mismatch.

- **Cloud → Local:** the model tag is empty and you must choose one. The URL is empty.
- **Local → Cloud:** the URL and the context window are cleared (the context is sent as `0`).
  The model resets to the provider's default. Effort is kept; a value that is not valid in the
  new mode shows "(not valid in this mode)".
- **Claude ↔ Codex while Local:** the URL and the context window are kept. The model is
  normalized before Save:

  | switch | model before | model after |
  |---|---|---|
  | Codex → Claude | `ollama/qwen3.8:27b` or `lmstudio/qwen3.8:27b` | `qwen3.8:27b` (the server choice is dropped) |
  | Claude → Codex | `qwen3.8:27b` | `ollama/qwen3.8:27b` (pick LM Studio afterwards if needed) |

  An effort value that is not valid for the new harness shows "(not valid in this mode)".
- **Any provider → Gemini:** the URL and the context window are cleared.

API callers get no normalization. Send the matching model in the same call as the provider
change. V6 and C1 (§5) reject a mismatch, and nothing is saved.

## 3. What Fleet generates

Local mode is on when the provider is `claude` or `codex` and the stored URL is not empty.
Fleet stores the URL in canonical form: `scheme://host[:port]`, lowercased, default port and
trailing `/` dropped. The DB column is still `AnthropicBaseUrl`. There is no migration.

| stored value | Claude local | Codex local |
|---|---|---|
| Server URL | `Agent.AnthropicBaseUrl` in the generated `appsettings.json` → `ANTHROPIC_BASE_URL` on the claude child | `Agent.CodexOssBaseUrl = <origin>/v1` in the generated `appsettings.json` → `CODEX_OSS_BASE_URL` on the codex app-server child, overriding any inherited value |
| Model | bare tag → `ANTHROPIC_DEFAULT_*_MODEL` | `modelProvider` + bare tag at `thread/start` |
| Context window | `CLAUDE_CODE_MAX_CONTEXT_TOKENS` in the container env | `Agent.ContextWindow` in the generated `appsettings.json` → `thread/start` `config.model_context_window` |
| Effort | `--effort`, or the disabled-thinking body for `off` | turn `effort`; `none` and `minimal` are sent as-is |

- Fleet adds the two Codex keys only when Codex local mode is on. Every other agent's generated
  files are unchanged.
- The container env of a Codex local agent has neither `CODEX_OSS_BASE_URL` nor
  `CLAUDE_CODE_MAX_CONTEXT_TOKENS`. It needs no Env Ref.
- Codex derives its own compaction threshold from `model_context_window`. Fleet sets no second
  threshold.
- Every change takes effect on reprovision, not on save.

## 4. API

### REST

`GET` and `PUT /api/agents/{name}/config` carry the URL as `localBaseUrl`.

```
PUT /api/agents/<agent>/config
{"provider":"codex","model":"ollama/qwen3.8:27b","localBaseUrl":"http://<server-address>:11434","contextWindow":131072,"effort":"none"}

POST /api/agents/<agent>/reprovision
```

`anthropicBaseUrl` is kept as a pure alias. `GET` returns both keys with the same value, for
every provider. `PUT` accepts either key. The dashboard sends only `localBaseUrl`.

### MCP

`update_agent_config` takes `local_base_url`. `anthropic_base_url` is a deprecated alias.

```
update_agent_config agent_name=<agent> provider=codex model=ollama/qwen3.8:27b local_base_url=http://<server-address>:11434 context_window=131072 effort=none
reprovision_agent <agent>
```

`get_agent_config` shows one line for the mode:

| line | meaning |
|---|---|
| `Local model: on — server <origin>` | the stored URL is set |
| `Local model: on — server from CODEX_OSS_BASE_URL Env Ref (legacy)` | a legacy Codex agent (§9) |
| `Local model: off` | a cloud agent |

When local mode is off, a stored context window is shown with "(ignored: local model off)".

### Sending both field names (rule A1)

The same rule applies to `localBaseUrl`/`anthropicBaseUrl` and to
`local_base_url`/`anthropic_base_url`.

- Null or absent means "not sent". `""` means "clear". A non-empty string means "set".
- If you send one field, it wins.
- If you send both:
  - both `""`: the URL is cleared;
  - one `""` and one non-empty: conflict;
  - both non-empty: each is checked with L2 first (a fault is a 400 that names that field).
    Then their canonical forms are compared. Equal values are accepted and stored canonical.
    Different values are a conflict.
- A conflict is a 400 or a tool error that names both fields. Nothing is saved.

## 5. Validation

The same rules run in three places:

- **on write:** HTTP 400 or a tool error, and nothing is saved;
- **on provision:** provisioning fails with the named fault, and the agent stays down;
- **on agent startup:** the agent logs `Critical` and exits 1.

| id | when | message or result |
|---|---|---|
| L1 | provider `gemini` with a URL | "Local model runs on claude or codex; clear the local server URL first." |
| L2 | URL is malformed or has a path, credentials, query or fragment | for example "Local server URL has a path. It must be an http(s) origin such as http://<server-address>:11434 — no path, credentials, query or fragment. Fleet adds /v1/messages for Claude and /v1 for Codex." |
| L2 | URL points at `localhost`, `127.0.0.0/8`, `::1` or `0.0.0.0` | "Local server URL points at localhost, which inside an agent container is the container itself; use the inference server's LAN address or host name." |
| V4 / C2 | the tag is not 1–100 characters matching `^[A-Za-z0-9][A-Za-z0-9._:/-]*$` | "The local model tag must be 1–100 characters matching …" |
| V5 | Claude local, model is a Claude id (`claude-*`, `opus`, `sonnet`, `haiku`) | "Model '…' is a Claude model id; set the local server's model tag." |
| V6 | Claude local, model starts with `ollama/`, `lmstudio/` or a hosted prefix (`zai/`) | "The 'ollama/' prefix selects the codex path; use provider codex or the bare tag." |
| V7 | Claude local, effort outside empty/`off`/`low`/`medium`/`xhigh` | "Effort on a local Claude model must be empty (model default, sent as xhigh), off, low, medium or xhigh." |
| V8 | Claude cloud, effort `off` | "Effort 'off' applies only to local Claude models; …" |
| C1 | Codex local, model is not `ollama/<tag>` or `lmstudio/<tag>` (includes `zai/…`, bare tags and cloud ids) | "Codex local model must be ollama/<tag> or lmstudio/<tag>." |
| — | context window outside 4096–1048576 | "Context window … is outside the valid range 4096..1048576 tokens (0 clears it)." `0` clears it. |
| P1 | provision: Codex `ollama/`/`lmstudio/` model, no URL, and no usable `CODEX_OSS_BASE_URL` Env Ref | provisioning fails before the container is created, with "Codex local model '<model>' has no server URL: …" (§12). A reprovision fails before the old container is removed. |
| S1 | startup: `Agent.CodexOssBaseUrl` is present but wrong | exit 1 with "CodexOssBaseUrl is not <canonical origin>/v1; redeploy both images and reprovision" (§12) |

"Usable" in P1 means the agent has the Env Ref and `.env` holds a non-blank value for it. A
missing key, a blank value, a missing `.env` and an unreadable `.env` all fail P1.

In the dashboard, Save is blocked with "Enter the local server URL" when the URL is empty. For
Codex there is one exception: the agent's Env Refs contain exactly `CODEX_OSS_BASE_URL`
(case-sensitive). That is the legacy path (§9).

## 6. Per-harness notes

### Claude

- **Effort.** The vocabulary is empty (the default, sent as `xhigh`), `off`, `low`, `medium`
  and `xhigh`. Any other value is rejected (V7). `off` is rejected on a cloud agent (V8), so
  send `effort=""` when you switch such an agent back to the cloud. Details:
  `claude-local-models.md` §2.1.
- **Context window.** Claude Code keeps a fixed compaction buffer of about 33k tokens,
  whatever the window. A Fleet Claude agent's base prompt is about 30k tokens. At 65536, only
  about 2k tokens are free before compaction starts. Use at least 131072 for real work, on the
  server and on the agent.
- **Credentials.** The container holds no Claude credential. Token broadcasts are ignored, and
  the agent's queue is not bound to `fleet.relay`.
- **Server.** Create a derived tag with both `num_ctx` and `num_batch` pinned
  (`claude-local-models.md` §4).

### Codex

- **Ollama version.** Use Ollama 0.34 or later. Codex sends MCP tools as namespaced tools.
  Older Ollama releases return the call without its namespace, and Codex rejects it as an
  unsupported call.
- **Effort.** Use `none` for Qwen3.8. At other settings a turn can end with reasoning only and
  no final message, so the agent sends nothing back.
- **`none` and `minimal` are now sent.** Before this change, Fleet dropped them and sent no
  effort at all. This applies to cloud Codex agents stored with those values too.
- **Context window.** The value reaches codex as `model_context_window`. Codex compacts from
  it. Without it, codex uses its own fallback window and does not compact before the server
  rejects the prompt.
- **No wedge guard.** A server that accepts a request and never answers blocks the whole agent,
  not one turn. Recover with `/cancel`. Details: `codex-local-models.md`, "Operational cautions".
- **LM Studio.** Pick **Server → LM Studio** and enter its origin, for example
  `http://<server-address>:1234`.

## 7. ⚠️ Known security gap: Codex local keeps the OpenAI credential (#383)

**Until #383 lands, a Codex local agent is not credential-isolated.** It is treated like a
cloud Codex agent:

- it still holds the OpenAI credential (the `.codex-credentials.json` bind is mounted);
- it applies Codex token broadcasts;
- its queue is bound to the `fleet.relay` fanout.

A Claude local agent does none of these.

What this means for you:

- The local model has shell access in a container that holds a working OpenAI credential.
- Codex local mode does not keep a cloud credential away from the model. If you need that, use
  Claude local.
- Give Codex local mode only to agents whose inputs you trust as much as a cloud Codex agent's.

## 8. Qwen3.8 tags

Use these public registry tags:

- `qwen3.8:27b`
- `qwen3.8-flash-next:125b-a6b-q4_K_M`

The bare `qwen3.8-flash-next` does not pull. The public Ollama registry has no `latest` tag for
it: the `latest` manifest returns 404, while `125b-a6b-q4_K_M` returns 200 (checked
2026-09-27). Always type the full tag.

Qwen3.8 supports a context of up to 262144 tokens. Enter the context your server is actually
configured with, not the model's maximum.

On Codex, set effort `none` (§6). The chips do this for you.

## 9. Upgrading a legacy Codex agent from the Env Ref

A legacy Codex agent has an `ollama/` or `lmstudio/` model, no Server URL, and a
`CODEX_OSS_BASE_URL` Env Ref. It keeps working after the upgrade, unchanged. The dashboard shows
it as Local with an empty URL and this notice: "Reaching the server through the
CODEX_OSS_BASE_URL Env Ref (legacy). Enter the server URL here to manage it in Fleet; the Env Ref
is then ignored for this agent and can be removed." Its context window is ignored, and
provisioning logs that.

Before you start, the new orchestrator **and** the new agent image must both be running. Do not
set a URL on any Codex agent before that. An old agent image ignores the URL and exits 1 at
startup.

Move one agent at a time:

1. Open the agent's config. Enter the **Server URL**: the `CODEX_OSS_BASE_URL` value without
   `/v1`. For example, `http://<server-address>:11434/v1` becomes
   `http://<server-address>:11434`.
2. Enter the **Context window**: the context your server is configured with. Never set it
   higher than the server.
3. Click **Save & Reprovision**.
4. Check the orchestrator log. It shows the provision line for the agent and the `Warning`
   "Env Ref superseded by the local server URL". The warning is expected while the Env Ref is
   still attached.
5. Check the agent log. The `CodexExecutor: local inference` line must show
   `source=agent config` and your `contextWindow=`.
6. Remove the `CODEX_OSS_BASE_URL` Env Ref, in the dashboard or with
   `manage_agent_env_refs agent_name=<agent> action=remove env_key_name=CODEX_OSS_BASE_URL`.
   Reprovision. The warning is gone.
7. Send the agent one test turn.

Keep `CODEX_OSS_BASE_URL` in `.env` until no agent refers to it.

A server behind a path prefix (a URL like `http://<server-address>/ollama/v1`) cannot be entered
as an origin. Keep the legacy Env Ref for it.

## 10. Rollout

Use this order when you upgrade a Fleet install to this release.

1. Deploy the orchestrator and the agent image in the same release.
2. Existing Claude local agents keep working. There is no DB migration and no config change.
3. Legacy Codex agents keep working through their Env Ref.
4. Move legacy Codex agents one at a time (§9).
5. Keep each agent's context window in lockstep with its server. Raise the server first, then
   the agent. Lower the agent first, then the server. The agent's value must never be higher
   than the server's.

## 11. Rollback

### One agent

Back to the cloud, in the dashboard: set **Runs on → Cloud**, pick a model, and click
**Save & Reprovision**. The URL and the context window are cleared.

With MCP:

```
update_agent_config agent_name=<agent> local_base_url="" model=<cloud-model>
reprovision_agent <agent>
```

A Claude agent with effort `off` also needs `effort=""` in the same call (V8).

Back to the legacy Env Ref, for a Codex agent: clear the URL, add the `CODEX_OSS_BASE_URL` Env
Ref, make sure `.env` has a value for it, then reprovision. The `ollama/` model stays.

Unload the local model on the server with `keep_alive: 0` if nothing else uses it.

### The release

1. Revert the change and redeploy both images. There is no DB step: the column is unchanged.
2. Before you reprovision any agent under the old images, list the Codex agents with a URL:

   ```sql
   SELECT Name FROM agents WHERE Provider='codex' AND AnthropicBaseUrl IS NOT NULL;
   ```

3. For each one, clear the field and restore its Env Ref. The old orchestrator knows only the
   old field name:

   ```
   update_agent_config agent_name=<agent> anthropic_base_url=""
   manage_agent_env_refs agent_name=<agent> action=add env_key_name=CODEX_OSS_BASE_URL
   ```

   Make sure `.env` has a value for `CODEX_OSS_BASE_URL`, then reprovision.
4. If you skip step 3, those agents fail provisioning with the old fault "AnthropicBaseUrl
   applies only to provider claude; clear it before changing provider." The failure is loud,
   not silent. The old orchestrator removes the running container before it hits that fault,
   so a reprovision leaves the agent down until you do step 3 and reprovision again.

Claude local agents are unaffected. After the revert, Codex agents stored with effort `none` or
`minimal` again send no effort.

## 12. Troubleshooting by log line

Log lines carry counts and settings only. They never include prompt text or any Env Ref value
other than the URL.

### Orchestrator, on provision

| log line | meaning | what to do |
|---|---|---|
| `Information` "Local model for '<agent>': harness codex, server http://<server-address>:11434, context window 131072" | provisioning picked up local mode | nothing. If it says `context window unset`, set the context window and reprovision |
| `Warning` "Env Ref superseded by the local server URL" | the Codex agent has both a URL and the `CODEX_OSS_BASE_URL` Env Ref; the URL wins | remove the Env Ref (§9 step 6) |
| P1 provisioning fault, no container created | a Codex `ollama/`/`lmstudio/` model with no URL and no usable Env Ref value | enter the Server URL (preferred), or fix the Env Ref and the `.env` value |
| the old fault "AnthropicBaseUrl applies only to provider claude …" | an old orchestrator is running and a Codex agent has a URL | follow §11 "The release" |

### Agent, on codex start

The `CodexExecutor: local inference — provider …, model …` line now ends with `source=` and
`contextWindow=`.

| what you see | meaning | what to do |
|---|---|---|
| `source=agent config` | the URL comes from the agent's config | nothing |
| `source=legacy env` | the URL comes from the `CODEX_OSS_BASE_URL` Env Ref | expected for a legacy agent. If you entered a URL, you have not reprovisioned yet |
| no `source=` field | the agent runs an old image | redeploy the agent image and reprovision |
| `contextWindow=unset` | codex uses its own fallback window | set the context window and reprovision. A legacy agent ignores it: move it first (§9) |
| `Warning` "the inherited CODEX_OSS_BASE_URL is superseded by Agent:CodexOssBaseUrl …" | the container still inherits the Env Ref value; the config URL wins | remove the Env Ref (§9 step 6) |

### Agent, during a thread

| log line | meaning | what to do |
|---|---|---|
| `Information` "codex reports modelContextWindow N (configured W)" | once per thread. N is the window codex works with. It is at or a little below W | if it says `configured unset`, set the context window |
| `Warning` "codex did not apply model_context_window" | N is higher than W. Codex ignored the setting and will not compact in time | check that the agent image is current and report it. Expect `context_length_exceeded` on long threads until it is fixed |
| `Information` "codex compacted the thread context" | codex shrank the thread before the limit | nothing. This is the intended behaviour |
| turn error `context_length_exceeded`, or HTTP 400 from the server | the prompt is larger than the server's context | make the agent's context window equal to the server's, never higher. Reprovision |

### Agent startup exits

| what you see | meaning | what to do |
|---|---|---|
| exit 1 with "CodexOssBaseUrl is not <canonical origin>/v1; redeploy both images and reprovision" (S1) | the generated config and the agent image disagree, or the file was edited by hand | redeploy both images, then reprovision |
| exit 1 because a prefixed model has neither a config URL nor `CODEX_OSS_BASE_URL` | an old agent image with a URL set, or a legacy agent that lost its Env Ref | deploy the new agent image, or restore the Env Ref. See `codex-local-models.md` for the gate |

### Claude local

| log line | meaning |
|---|---|
| `Claude local model mode: base URL …, model …` | the claude child started in local mode |
| `Claude local thinking: effort=… wire=…` | the thinking level sent to the server |
| `[claude-code:unrecognized_model]` at `Warning` | expected for a local tag; turns still succeed |

For any harness: a turn that fails with the server's own error usually means the server is
down, the URL is wrong, or the tag is not pulled. Check the server directly. Fleet does not probe
it.
