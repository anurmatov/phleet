# Harness defaults per mode, the effective-settings view and `harnessExtra` (#460)

Phleet passes opinionated settings to the Codex and Claude Code CLIs based only on the mode an agent
already runs in, and lets an operator adjust them through one per-agent field. `HarnessDefaults.Build`
(`src/Fleet.Shared/HarnessDefaults.cs`) is the only code that computes them; the orchestrator's view,
the generated `codex-root.toml` and (from the agent-image PR) the executors all call it.

> **Rollout status.** The orchestrator part ships first: it stores and validates `harnessExtra`,
> serves `GET /api/agents/{name}/effective-harness`, writes `.generated/codex-root.toml` and mounts it.
> An agent image without the executor and `entrypoint.sh` changes ignores the file and the field, and
> reports no plan sha, so the view shows "not reported" until agents are recreated on the new image.

**Mode** is derived, never configured: **local** when the agent has a local server URL (a Codex local
provider, or `ClaudeLocalModel.IsEnabled`), **cloud** otherwise. Hosted Codex providers (`zai/`) are cloud.

## Codex

| setting | where | cloud | local |
|---|---|---|---|
| `web_search` | thread `config` | unchanged (`cached`) | `"disabled"` |
| `tools.view_image` | thread `config` | unchanged | `false` |
| `model_reasoning_summary` | thread `config` | unchanged | `"none"` |
| `tool_output_token_limit` | thread `config` | unchanged | `clamp(ContextWindow / 16, 4000, 25000)`, omitted when unset |
| `features.apps`, `features.remote_plugin`, `features.skill_mcp_dependency_install` | thread `config` | `false` | `false` |
| `check_for_update_on_startup` | `config.toml` root | `false` | `false` |
| `analytics.enabled` | `[analytics]` | `false` | `false` |
| `history.persistence` | `[history]` | `"none"` | `"none"` |

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
  `TodoWrite`; in cloud mode also `WebFetch` and `WebSearch`. One fixed constant, `HarnessDefaults.KeptTools`.
- **Name rule:** an `AllowedTools` entry adds the text before its first `(` (`Bash(git *)` → `Bash`).
  `mcp__*` entries are never passed. Duplicates are removed and the names sorted, so input order never
  changes the plan.
- An `AllowedTools` with no built-in name gets **no** `--tools` flag: the agent keeps every built-in.

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
| codex | `tools.view_image` | `true`/`false`; local: `false` only |
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
no-op. Narrowing an agent's built-ins is done through its `AllowedTools`, never through this field.

**At start.** The agent re-runs the same validator. A stored value that no longer passes (for example a
newer image with a narrower allowlist) is skipped as a whole with one WARN, the agent starts on the mode
defaults, and the view shows `ignored` with the reason. Provisioning logs the same WARN once.

## `GET /api/agents/{name}/effective-harness`

Read-only, unauthenticated like `GET /api/agents/{name}/config`.

| field | meaning |
|---|---|
| `provider`, `mode` | `claude`/`codex`, `cloud`/`local` |
| `codex.configToml` | the exact bytes of the generated `codex-root.toml` |
| `codex.threadConfig` | the thread `config` entries, typed |
| `claude.args`, `claude.env` | the appended arguments and the child env; `ANTHROPIC_API_KEY` and `ANTHROPIC_AUTH_TOKEN` shown as `***` |
| `extra.state`, `extra.reason`, `extra.replaced` | `none`/`applied`/`ignored`, why ignored, defaults the extra replaced or removed |
| `planSha256` = `computedPlanSha256` | SHA-256 over the canonical JSON of the effective settings |
| `startedPlanSha256` | the sha the running agent reported on registration and every heartbeat; `null` = not reported |
| `pendingReprovision` | `true` when a started sha exists and differs from the computed one |

Unknown agent → 404; gemini → 404 `{"reason":"unsupported_provider"}`. The agent prints
`harness: mode=<mode> plan=<sha256> extra=<state>` once at startup (`HarnessPlan.StartupLine`).

## Rollout and rollback

Order: (1) orchestrator — migration `AddAgentHarnessExtra`, API and validation, the endpoint,
`codex-root.toml`; (2) dashboard — sectioned dialog, effective-settings view and textarea; (3) agent
image — executors, `entrypoint.sh`, the started-plan report and the startup line. The agent-image
defaults apply only when containers are **recreated on the new image** (canary first, then the shared
tag); a restart on the old image changes nothing. Rollback is reverse order: recreate agents on the
previous image, revert the dashboard, revert the orchestrator; the nullable column stays unused.
