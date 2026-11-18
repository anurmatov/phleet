# CLI upgrade #625 — 2026-11-18

Source-only qualification remains incomplete until the native checks below and exact-head CI pass.
No deployment, stored-model rewrite, credential change or reprovision was performed.

## Pins and upstream review

| surface | before | candidate |
|---|---|---|
| Claude Code image/native fixture | 2.1.280 | 2.1.327 |
| Codex image/CI/native fixture | 0.164.0 | 0.170.0 |
| Gemini | 0.40.1 | unchanged |
| `sol` | `gpt-6.1-sol` | `gpt-6.2-sol` |
| `terra`, `luna`, explicit IDs | existing values | unchanged |

| upstream source | relevant change |
|---|---|
| [Claude changelog](https://github.com/anthropics/claude-code/blob/main/CHANGELOG.md), 2.1.281–2.1.327 | Sonnet 5.5 becomes default at 2.1.284, Haiku 5.5 at 2.1.293; aliases follow CLI defaults, explicit IDs remain untouched |
| same changelog | 2.1.283 accepts text and file system-prompt forms together; 2.1.299 fixes queued content-block joining; 2.1.311 escapes control characters without changing parsed values; 2.1.327 fixes headless resume persistence |
| [Codex 0.165.0](https://github.com/openai/codex/releases/tag/rust-v0.165.0) | catalog additions, reasoning-effort overrides and MCP cancellation changes |
| [Codex 0.166.0](https://github.com/openai/codex/releases/tag/rust-v0.166.0) | startup/shutdown race fixes and preserved resume permissions/token accounting |
| [Codex 0.167.0](https://github.com/openai/codex/releases/tag/rust-v0.167.0) | provider-specific saved sessions and failed-response compaction accounting |
| [Codex 0.168.0](https://github.com/openai/codex/releases/tag/rust-v0.168.0) | steering delivery fixes, saved provider retention and deprecated `features.multi_agent` (not emitted here) |
| [Codex 0.169.0](https://github.com/openai/codex/releases/tag/rust-v0.169.0) | untrusted-project MCP restrictions for exec/review, MCP startup events and delayed-abort handling |
| [Codex 0.170.0](https://github.com/openai/codex/releases/tag/rust-v0.170.0) | MCP OAuth races, optional environment-scoped MCP support and oversized WebSocket HTTP fallback |

## Independent schema comparison

Both npm versions generated their own `app-server generate-json-schema` output, and the old output matched the committed baseline before replacement.

| consumed contract | 0.164.0 → 0.170.0 |
|---|---|
| initialize | new optional `requiredHumanApproval` capability; existing initialization unchanged |
| thread/start | params unchanged |
| thread/resume | added audio file-ID alternative in supplied history; existing text/history forms unchanged |
| turn/start, turn/steer | text form and required fields unchanged; optional context name/audio alternative added |
| item/completed | optional `contentTokenCount`; flat thread items add optional timestamps/moderation/image generation ID |
| turn/completed, errors | required completion fields unchanged; additional error variants remain strings handled by existing fallback |
| generated baseline | all 329 JSON files match independent 0.170.0 output; full drift gate retained, including new unconsumed types |

## Local checks and remaining acceptance

| check | result |
|---|---|
| alias regression | new `sol` expectation fails before mapping change; explicit old/new IDs, other aliases and local mode covered |
| pin regression | two tests fail with old Docker pins; Python script suite 28/28 passes |
| Codex real 0.170.0 harness | 10 config-key discriminators, negative control, tool-removal request capture and catalog/alias checks pass |
| Claude real 2.1.327 local wire | all seven rows pass, including pre-fix negative control and inherited-effort override |
| Claude alias init over synthetic endpoint | old/new `opus`: `claude-opus-5-5`; `sonnet`: `claude-sonnet-5` → `claude-sonnet-5-5`; `haiku`: `claude-haiku-4-5-20251001` → `claude-haiku-5-5`; initialization is not API model-acceptance proof |
| Claude real stream-json | two synthetic turns through the existing local wire stub return parsed final `result=ok`; file prompt and executor input/output/model/turn-limit flags accepted |
| GPT-6.2 actual inference | both `gpt-6.2-sol` and `gpt-6.2-astra` complete fresh and process-restarted resumed app-server turns using an isolated temporary home; `turn/steer` accepts the matching active turn ID on both |
| agent focused tests | 742 pass, including the two new explicit-ID regression rows |
| effective-harness endpoint | 18 pass |
| agent build | zero errors |
| full agent suite | 2,778 pass, eight listener tests fail because their fixed loopback listener cannot bind; no tests disabled |
| production-rendered signed native MCP fixture | local prerequisite exit 2: Docker daemon unavailable; existing image CI invokes this fixture with the new pins |
| remaining | signed native MCP success/negative controls, GPT-6.2 native read-only MCP calls, Claude live mid-turn/resume acceptance and green exact-head CI |

Historical fixtures and dated provider receipts retain their original versions rather than being relabelled.
