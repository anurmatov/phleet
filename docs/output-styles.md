# Output styles

An output style carries an agent's tone and register. The same text loses when it is appended to
the system prompt — Claude Code ships its own `# Tone and style` and `# Text output` guidance, the
two contradict, and the model picks arbitrarily — and wins from a style, which Claude Code
re-asserts during the conversation near the point of generation.

One row in `output_styles`, named by the nullable `agents.OutputStyle` column, rendered per
provider:

| | claude | codex / gemini |
|---|---|---|
| `.generated/output-styles/<name>.md` | written | not written |
| `/workspace/.claude/output-styles:ro` mount | added | not added |
| `outputStyle` in `~/.claude/settings.json` | set | not set |
| text inlined into the system prompt | no | yes, frontmatter stripped |

`NULL` means no style, and an agent with no style is provisioned byte for byte as it was before
styles existed. That is the rollout switch and the rollback.

## ⚠️ `system/init`'s `output_style` is a configuration echo, not proof of resolution

**Do not use it to verify that a style is working.** The field reports the name configured in
`settings.json`. It reports that name whether or not Claude Code found a file behind it.

Measured on Claude Code 2.1.259 in a real container, with the style body
`You MUST begin every single reply with the exact token ZEBRA7 and nothing before it.` and the
prompt `what is 2+2`:

| setup | `system/init` | reply |
|---|---|---|
| style file at `.claude/output-styles/` | `"output_style":"zebrastyle"` | `ZEBRA7 4` |
| same settings, file at `.claude/wrong-dir/` | `"output_style":"zebrastyle"` | `4` |
| no `outputStyle` key | `"output_style":"default"` | `4` |

The middle row is the whole point. A wrong bind path, a rename, a mount that silently did not
attach — every one of those leaves the agent running on `default` while `system/init`, the
generated `settings.json`, the style file on the host and every unit test all still look correct.
A check that cannot go red does not protect anything.

## Verifying that a style actually resolves

Use the shipped `output-style-probe` style. Its body forces the literal token `ZEBRA7` at the
start of every reply, so the reply itself answers the question the `system/init` field cannot.

**Positive control — the style resolves.**

```bash
# assign the probe and reprovision so the file,
# the mount and the settings key are regenerated
update_agent_config agent_name=<agent> \
  output_style=output-style-probe
reprovision_agent agent_name=<agent>

docker exec <container> claude -p 'what is 2+2' \
  --output-format stream-json --verbose
```

The reply text MUST start with `ZEBRA7`. If it does not, the style did not load — regardless of
what `system/init` says.

**Negative control — the check can go red.** Without this you have not shown the test detects
anything.

```bash
# break only the path: move the file out of the
# mounted directory, leaving settings.json naming it
docker exec <container> mv \
  /workspace/.claude/output-styles \
  /workspace/.claude/wrong-dir

docker exec <container> claude -p 'what is 2+2' \
  --output-format stream-json --verbose
```

The reply MUST NOT contain `ZEBRA7`, and `system/init` will still report
`"output_style":"output-style-probe"`. Restore the directory afterwards, then clear the probe
(`output_style=""`) and reprovision before the agent goes back to work.

**Never leave the probe assigned.** An agent on it prefixes every Telegram message with `ZEBRA7`.

## Adding a style

Drop a `<name>.md` into `src/Fleet.Orchestrator/OutputStyles/`. It is seeded into `output_styles`
on orchestrator startup, create-if-absent, so an operator edit to an existing row survives a
restart. The file is a Claude Code output style: YAML frontmatter (`name`, `description`,
`keep-coding-instructions`) followed by the body.

Two rules the shipped `fleet-messaging` style follows and a new one should too:

- **`keep-coding-instructions: true`.** These agents run `gh`, `docker` and `dotnet`. Dropping the
  coding defaults to win a tone argument is a bad trade, and it does not remove the conflicting
  blocks anyway.
- **Do not grant or deny markup.** Structure belongs to the agent's formatting mode, which is a
  real per-agent setting; a style that banned headings would break a Rich-mode agent, and one that
  permitted them would break every other tier. Defer to the prompt's formatting guidance instead.

A name with no row makes provisioning refuse outright rather than emit an unresolvable reference,
so a typo fails at the edit or at the reprovision — not silently at runtime.
