# No AI attribution on new commits and PRs

Agent-authored Git commit messages and pull-request descriptions default to carrying no
automatic AI attribution: no `Co-Authored-By` trailer naming an AI assistant or model, no
"generated with"/"created by" AI footer or badge, and no Claude session, conversation or
transcript link.

This is output hygiene, not concealment of provenance, and it is not a security boundary.

## Scope

**Covered** — new commit messages and new PR descriptions written by any provisioned agent.

**Explicitly preserved:**

- Normal Git author and committer identity (`GIT_USER_NAME` / `GIT_USER_EMAIL`, set per agent
  by the orchestrator). Nothing here changes or masks author metadata.
- Legitimate human co-authors, `Signed-off-by` trailers, and license notices.
- Links to real evidence: issues, CI runs, documentation.
- Existing history. Agents do not rewrite past commits, amend historical PRs, or change
  authorship acknowledgements in articles, papers or other documents.

**Not covered.** This is a default for ordinary agent output, not enforcement. There is no
post-generation scrubber and no Git hook, so a repository's own instructions or an explicit
human request for attribution still win, and an agent can still be asked for attribution on a
specific commit or PR.

## How it is delivered

Two independent mechanisms, both needed.

### 1. Shared prompt rule — all providers

`PromptBuilder.AppendGitAttributionPolicy` appends a `## Git and Pull Request Output` section
to the assembled system prompt in `src/Fleet.Agent/Services/PromptBuilder.cs`. It is appended
unconditionally on the single construction path all three providers read:

| Provider | Path |
|---|---|
| claude | `WriteSystemPromptFile()` → `--append-system-prompt-file` |
| codex | `WriteSystemPromptFile()` → read back → `thread/start.baseInstructions` |
| gemini | `BuildSystemPrompt()` → `GEMINI_SYSTEM_MD` file |

It lives in the runtime rather than in the bundled `roles/_base/system.md` on purpose. Role
files are DB-managed and generated per agent, so an installation whose role files were
generated before this change would never pick up a base-file edit. Because the rule is
appended after role, project and memory sections are loaded, **no seed change, instruction
assignment, project context, tool grant or per-repository file is required** — fresh and
existing installations both receive it from the updated agent runtime.

This rule is the only thing covering Claude session links on the codex and gemini paths.

### 2. Claude Code native settings — claude provider

`ContainerProvisioningService.GenerateSettingsJson` writes `.generated/settings.json`, which is
mounted read-only at `/root/.claude/settings.json`. It now emits:

```json
{
  "permissions": { "allow": ["..."] },
  "attribution": { "commit": "", "pr": "", "sessionUrl": false }
}
```

- `commit` / `pr` — an empty string hides that attribution, per the pinned CLI's settings
  schema ("Empty string hides attribution").
- `sessionUrl: false` — omits the `Claude-Session` trailer and the PR-body session link. This
  is a **separate control** from the attribution text, so empty `commit`/`pr` alone would not
  cover session links.
- `includeCoAuthoredBy` is marked deprecated by the same schema and is deliberately not used.

`permissions.allow` is untouched: the same tools, the same `memory_get` / `notify_cto`
auto-grants, and the same ordinal-ignore-case sort as before.

The pinned CLI is `@anthropic-ai/claude-code@2.1.259` (see `Dockerfile`). All four keys
(`commit`, `pr`, `sessionUrl`, `commitTrailers`) appear in that binary's embedded settings
schema. If the pin moves, re-check the schema before assuming these keys still apply.

## Rollout ordering

The two mechanisms deploy independently, and the order matters:

1. **Deploy the updated orchestrator first.** `settings.json` is written at provision time, so
   only an orchestrator carrying this change generates the `attribution` block.
2. **Then reprovision the affected agents.** A plain restart does not rewrite
   `.generated/settings.json`. Rebuilding the agent image is *not* evidence that an agent's
   generated settings changed.
3. **Deploy the updated agent runtime** for the shared prompt rule. It ships in the agent
   image and needs no reprovision to take effect on the next turn, but a reprovision is the
   normal way both changes land together.

Reprovisioning live agents is an operator action, separate from merging this change.

Verify on a provisioned agent:

```bash
# generated settings carry the attribution block
jq '.attribution' ./fleet/workspaces/<container>/.generated/settings.json

# the running agent's assembled prompt carries the shared rule
grep -c "## Git and Pull Request Output" <workdir>/system-prompt.md
```

## Tests

- `tests/Fleet.Agent.Tests/PromptBuilderGitAttributionTests.cs` — policy present with absent,
  ordinary and empty role files; present on both prompt entry points (covering all three
  provider delivery paths); unrelated role, memory and formatting sections intact.
- `tests/Fleet.Orchestrator.Tests/Services/ContainerProvisioningSettingsAttributionTests.cs` —
  generated settings deserialize with both attribution strings empty and `sessionUrl` false;
  permission contents, ordering and auto-grant behaviour unchanged.

Both suites call the real production helpers, so a regression in the generator or the prompt
builder fails them.
