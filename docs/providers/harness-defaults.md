# Harness defaults per mode, the effective-settings view and `harnessExtra` (#460)

Phleet passes opinionated settings to the Codex and Claude Code CLIs based only on the mode an agent
already runs in, and lets an operator adjust them through one per-agent field. `HarnessDefaults.Build`
(`src/Fleet.Shared/HarnessDefaults.cs`) is the only code that computes them; the orchestrator's view,
the generated `codex-root.toml`, both executors and the agent's started-plan report all call it.

> **Rollout status.** The orchestrator part (#462) stores and validates `harnessExtra`, serves
> `GET /api/agents/{name}/effective-harness`, writes `.generated/codex-root.toml` and mounts it. The
> agent image applies the plan (executors, `entrypoint.sh`) and reports its sha; the dashboard shows it.
> An agent still on an older image ignores the file and the field and reports no sha, so the view
> shows "not reported" until the agent is **recreated on the new image** (a restart is not enough).

**Mode** is derived, never configured: **local** when the agent has a local server URL (a Codex local
provider, or `ClaudeLocalModel.IsEnabled`), **cloud** otherwise. Hosted Codex providers (`zai/`) are cloud.

## Codex

| setting | where | cloud | local |
|---|---|---|---|
| `web_search` | thread `config` | unchanged (`cached`) | `"disabled"` |
| `features.view_image` | thread `config` | unchanged | `false` |
| `model_reasoning_summary` | thread `config` | unchanged | `"none"` |
| `tool_output_token_limit` | thread `config` | unchanged | `clamp(ContextWindow / 16, 4000, 25000)`, omitted when unset |
| `features.apps`, `features.remote_plugin`, `features.skill_mcp_dependency_install` | thread `config` | `false` | `false` |
| `check_for_update_on_startup` | `config.toml` root | `false` | `false` |
| `analytics.enabled` | `[analytics]` | `false` | `false` |
| `history.persistence` | `[history]` | `"none"` | `"none"` |

**Checked on the pinned codex 0.159.2 (AC-C1, `tests/codex-harness-keys/run.py` in CI).** Codex's
config parser ignores unknown keys, so each key is sent with a wrong-typed value: every key above makes
`thread/start` fail naming it, so the CLI reads it. The design's `tools.view_image` is **not** read by
this pin (any value is accepted and the tool stays); `features.view_image` is, and is what Fleet sets.
On a captured local request the defaults remove exactly the `web_search` and `view_image` tools; the
multi-agent tool stays.

Unchanged: `features.multi_agent` (the Codex runtime instruction relies on its tools),
`model_auto_compact_token_limit`, `project_doc_max_bytes` and AGENTS.md handling. Thread entries never
overwrite keys already in `thread/start` `config` (hosted-provider keys, `model_context_window`).

`codex-root.toml` holds the root key first, then the `[analytics]` and `[history]` tables: a key
written after a table header would belong to that table. Provisioning writes it to
`.generated/codex-root.toml` before the container is created and mounts it read-only at
**`/app/codex-root.toml`**, for Codex agents only. `entrypoint.sh` reads that path and writes it first
into `~/.codex/config.toml`, then the `[mcp_servers.*]` tables.

## Claude Code

| setting | cloud | local |
|---|---|---|
| `--tools <kept set ∪ built-ins in AllowedTools>` | applied | applied |
| `--disallowedTools WebSearch` | unchanged | applied |
| `--exclude-dynamic-system-prompt-sections` | applied | applied |
| `--strict-mcp-config` | applied | applied |
| env `CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC=1` | applied | applied |
| env `MAX_MCP_OUTPUT_TOKENS` | unchanged | `clamp(ContextWindow / 16, 4000, 25000)`, omitted when unset |

In local mode the env list starts with today's local-model entries (`ClaudeLocalModel.BuildEnvironment`),
all kept.

`--tools` rules:

- **Always kept:** `Agent`, `Task` (the pinned CLI reports `Agent` as `Task`), `ToolSearch`, `Skill`,
  `TodoWrite`, `TaskStop`, `SendMessage`; in cloud mode also `WebFetch` and `WebSearch`. One fixed
  constant, `HarnessDefaults.KeptTools`.
- **Name rule:** an `AllowedTools` entry adds the text before its first `(` (`Bash(git *)` → `Bash`).
  `mcp__*` entries are never passed. Duplicates are removed and the names sorted, so input order never
  changes the plan.
- **The agent's own six.** `AgentOptions.AllowedTools` defaults to `Read, Write, Edit, Bash, Glob, Grep`,
  and the .NET configuration binder **appends** the generated list to that default rather than
  replacing it. Every running agent's `AllowedTools` — and the `--allowedTools` it has always passed —
  therefore starts with those six, so they are in `--tools` too (`HarnessDefaults.AgentDefaultAllowedTools`;
  the orchestrator prepends them in `HarnessInputFor`, so its computed plan is the one the agent runs).
- An `AllowedTools` with no built-in name gets **no** `--tools` flag: the agent keeps every built-in.
  Because of the six above, no provisioned agent reaches this case today.

**Checked on the pinned CLI 2.1.280 (AC-C2).** With no `--tools`, `-p` mode offers `Bash`, `Cron*`,
`DesignSync`, `Edit`, `EnterWorktree`, `ExitWorktree`, `ListAgents`, `Monitor`, `NotebookEdit`,
`PushNotification`, `Read`, `ReportFindings`, `ScheduleWakeup`, `SendMessage`, `Skill`, `Task`,
`TaskStop`, `WebFetch`, `WebSearch`, `Workflow` and `Write`. An explicit list keeps only the names it
gives and **silently ignores unknown ones**, so a misspelled kept name fails without an error. That is
why `SendMessage` (continue a sub-agent) and `TaskStop` (stop a background task) are kept; the
`TaskCreate`/`TaskList` todo tools are not in the default `-p` set and are not added. `TodoWrite` is not
offered on this pin and `ToolSearch` only against the first-party API: with a local base URL Claude Code
turns tool search off and loads MCP tools up front. A real cloud session with the full cloud argument
list loaded a deferred MCP tool through `ToolSearch`, called it, and ran a background `Agent` task to
`task_notification completed`. Every agent's offered built-in set shrinks to the kept set plus its
`AllowedTools` built-ins: the dropped names are `Cron*`, `DesignSync`, `EnterWorktree`, `ExitWorktree`,
`ListAgents`, `Monitor`, `NotebookEdit`, `PushNotification`, `ReportFindings`, `ScheduleWakeup` and
`Workflow`, unless `AllowedTools` names them. `harnessExtra` `--tools` can add any of them back.

## `harnessExtra`

One nullable `text` column (`agents.harness_extra`), at most 4,096 characters and 50 lines. Claude and
codex only; gemini refuses a non-empty value with `unsupported_provider`.

| surface | name |
|---|---|
| REST | `harnessExtra` on `GET`/`PUT /api/agents/{name}/config` and `POST /api/agents` |
| MCP | `harness_extra` on `update_agent_config` and `create_agent`; shown by `get_agent_config` |
| agent | `Agent.HarnessExtra` in the generated `appsettings.json`, written only when set |

`""` clears it (stored `null`); any other text is stored verbatim and `GET` returns it byte for byte. A
change applies on **reprovision**. Blank lines and lines starting with `#` are ignored.

**Codex lines** are `key = value` (strings quoted, as in TOML). **Claude lines** are `--flag [value]`,
`!--flag` (turn a removable default off) or `NAME=value` (env).

**Allowlist** — anything else is refused with HTTP 400 (`{error, code, field, line}`) or an MCP tool
error naming the line, the reason and the allowed names; nothing is written:

| provider | setting | value |
|---|---|---|
| codex | `web_search` | `"disabled"`, `"cached"` or `"live"`; local: `"disabled"` only |
| codex | `features.view_image` | `true`/`false`; local: `false` only |
| codex | `model_reasoning_summary` | `"none"`, `"auto"`, `"concise"` or `"detailed"` |
| codex | `tool_output_token_limit` | integer 1000–100000 |
| codex | `features.apps`, `features.remote_plugin`, `features.skill_mcp_dependency_install` | `true`/`false` |
| codex | `check_for_update_on_startup`, `analytics.enabled` | `true`/`false` |
| codex | `history.persistence` | `"none"` or `"save-all"` |
| claude | `--tools <names>` | comma-separated names from `HarnessExtra.KnownBuiltInTools`; local: not `WebSearch` |
| claude | `--disallowedTools WebSearch` | that value only, any mode |
| claude | `!--exclude-dynamic-system-prompt-sections` | the only removable default |
| claude | `MAX_MCP_OUTPUT_TOKENS=<n>` | integer 1000–100000 |
| claude | `CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC=<n>` | `0` or `1` |

Not settable: `--allowedTools`, `--strict-mcp-config`, `features.multi_agent`,
`model_auto_compact_token_limit`, `project_doc_*`, and every row marked unchanged. A duplicate line is
refused. Validation runs against the agent's **resulting** provider and mode on every write, so a
switch that would leave a stored value outside the allowlist (to gemini, or to local with
`web_search = "live"`) is refused until the value is changed in the same request.

**Precedence.** An extra replaces the mode default for the same Codex key, Claude flag or env name;
otherwise it is added. `--tools` only **adds** names to the computed list and never removes one; when
the defaults compute no `--tools` flag (the agent keeps all built-ins), an extra `--tools` line is a
no-op, and the plan carries a note saying so (applying it would narrow that agent). Narrowing an agent's
built-ins is done through its `AllowedTools`, never through this field.

**At start.** The agent re-runs the same validator. A stored value that no longer passes (for example a
newer image with a narrower allowlist) is skipped as a whole with one WARN, the agent starts on the mode
defaults, and the view shows `ignored` with the reason. Provisioning logs the same WARN once. If the
Claude CLI exits before its `system/init` while an extra is applied, the executor starts it once more on
the mode defaults alone and logs one ERROR naming `harnessExtra`; the reported sha changes with it, so
the view shows the agent as pending reprovision.

## `GET /api/agents/{name}/effective-harness`

Read-only, unauthenticated like `GET /api/agents/{name}/config`.

| field | meaning |
|---|---|
| `provider`, `mode` | `claude`/`codex`, `cloud`/`local` |
| `codex.configToml` | the exact bytes of the generated `codex-root.toml` |
| `codex.threadConfig` | the thread `config` entries, typed |
| `claude.args`, `claude.env` | the appended arguments and the child env; `ANTHROPIC_API_KEY` and `ANTHROPIC_AUTH_TOKEN` shown as `***` |
| `extra.state`, `extra.reason`, `extra.replaced` | `none`/`applied`/`ignored`, why ignored, defaults the extra replaced or removed |
| `extra.notes` | accepted lines that change nothing (an extra `--tools` on an all-tools agent) |
| `planSha256` = `computedPlanSha256` | SHA-256 over the canonical JSON of the effective settings |
| `startedPlanSha256` | the sha the running agent reported on registration and every heartbeat; `null` = not reported |
| `pendingReprovision` | `true` when a started sha exists and differs from the computed one |

Unknown agent → 404; gemini → 404 `{"reason":"unsupported_provider"}`. The agent prints
`harness: mode=<mode> plan=<sha256> extra=<state>` once at startup (`HarnessPlan.StartupLine`).

## Agent side

`AgentHarness` (`src/Fleet.Agent/Services/`) builds the plan once at startup from the agent's own
generated config with the orchestrator's rules: mode from `Agent.AnthropicBaseUrl` (Claude) or
`Agent.CodexOssBaseUrl` (Codex), the context window from `CLAUDE_CODE_MAX_CONTEXT_TOKENS` in the
container env (Claude local) or `Agent.ContextWindow` (Codex local), and the bound `AllowedTools`.

| consumer | what it does with the plan |
|---|---|
| `ClaudeExecutor` | appends `ClaudeArgs` last in `BuildArgs`; sets `ClaudeEnv` on the claude child only (local mode first removes the OAuth token and thinking overrides, then today's local entries, then the defaults) |
| `CodexExecutor` | merges `CodexThreadConfig` into `thread/start` `config` after the hosted-provider keys and `model_context_window`, never overwriting a key already there |
| `entrypoint.sh` | writes `/app/codex-root.toml` first into `~/.codex/config.toml`, then today's `[mcp_servers.*]` tables byte for byte; written even without `.mcp.json` |
| heartbeat and registration | `PlanSha256` on every message; absent for gemini |

`entrypoint.sh` failure paths: a missing, unreadable or directory `/app/codex-root.toml` prints
`WARN: codex-root.toml unavailable: <reason>` and the MCP tables are still written (the CLI uses its
built-in root defaults); a failure to build or write the file prints
`WARN: codex config.toml not written: <reason>`. `tests/entrypoint-codex/run.sh` covers each case and
parses every result with Python's `tomllib`.

## Dashboard

The agent config dialog is sectioned: General, Model & runtime, Instructions & projects, Tools & MCP,
Telegram and Advanced, each with today's "N customized" count; below 720 px the list becomes a tab row.
Every field keeps its component, hint and validation, and the save request is the one the unsectioned
dialog sent (`agentConfigSave.ts`, checked against a recorded fixture). Model & runtime shows the
read-only **Effective settings** from this endpoint (Codex root TOML and thread config, Claude arguments
and env with credentials masked, overrides marked, both shas and the pending-reprovision badge). Advanced
has the **Extra harness settings** textarea (Claude and Codex only); a refused save shows the line and
the reason. `harnessExtra` is sent only when the text was edited.

## Rollout and rollback

Order: (1) orchestrator — migration `AddAgentHarnessExtra`, API and validation, the endpoint,
`codex-root.toml`; (2) dashboard — sectioned dialog, effective-settings view and textarea; (3) agent
image — executors, `entrypoint.sh`, the started-plan report and the startup line. The agent-image
defaults apply only when containers are **recreated on the new image** (canary first, then the shared
tag); a restart on the old image changes nothing. Rollback is reverse order: recreate agents on the
previous image, revert the dashboard, revert the orchestrator; the nullable column stays unused.
