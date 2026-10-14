import type { EffectiveHarness } from './types'

// The read-only "Effective settings" block (#460 Part 3a): the plan the orchestrator computes for
// the agent's saved config, as the operator reads it. Kept free of JSX so `node --test` can run it.

/** One displayed line; `override` when the agent's harnessExtra replaced (or removed) that default. */
export interface EffectiveLine {
  text: string
  override: boolean
}

export type DriftState = 'pending' | 'running' | 'not-reported'

/** Whether the running agent started with the computed plan (#460 AC-X6). */
export function driftState(h: EffectiveHarness): DriftState {
  if (h.startedPlanSha256 === null) return 'not-reported'
  return h.pendingReprovision ? 'pending' : 'running'
}

export const DRIFT_TEXT: Record<DriftState, string> = {
  pending: 'pending reprovision',
  running: 'running this plan',
  'not-reported': 'not reported',
}

/** A sha shortened for display; the full value is in the title attribute. */
export function shortSha(sha: string | null): string {
  return sha === null ? '—' : sha.slice(0, 12)
}

function scalar(value: string | number | boolean): string {
  return typeof value === 'string' ? JSON.stringify(value) : String(value)
}

/** Codex `config.toml` root lines, then the thread config, each override marked. */
export function codexLines(h: EffectiveHarness): { root: EffectiveLine[]; thread: EffectiveLine[] } {
  const replaced = new Set(h.extra.replaced)
  let table = ''
  const root = (h.codex?.configToml ?? '').split('\n').filter(line => line !== '').map(line => {
    const header = /^\[(.+)\]$/.exec(line)
    if (header) { table = header[1]; return { text: line, override: false } }
    const key = line.slice(0, line.indexOf(' =')).trim()
    return { text: line, override: replaced.has(table ? `${table}.${key}` : key) }
  })
  const thread = Object.entries(h.codex?.threadConfig ?? {})
    .map(([key, value]) => ({ text: `${key} = ${scalar(value)}`, override: replaced.has(key) }))
  return { root, thread }
}

/** Claude arguments (a flag with its value on one line) and env, each override marked. */
export function claudeLines(h: EffectiveHarness): { args: EffectiveLine[]; env: EffectiveLine[] } {
  const replaced = new Set(h.extra.replaced)
  const argv = h.claude?.args ?? []
  const args: EffectiveLine[] = []
  for (let i = 0; i < argv.length; i++) {
    const flag = argv[i]
    const value = argv[i + 1] !== undefined && !argv[i + 1].startsWith('-') ? argv[++i] : null
    args.push({ text: value === null ? flag : `${flag} ${value}`, override: replaced.has(flag) })
  }
  // A default the extra turned off is gone from the arguments; show that it was removed.
  for (const name of replaced)
    if (name.startsWith('--') && !argv.includes(name)) args.push({ text: `!${name}`, override: true })
  const env = (h.claude?.env ?? []).map(e => ({ text: `${e.name}=${e.value}`, override: replaced.has(e.name) }))
  return { args, env }
}

/** The textarea placeholder for a provider's harnessExtra lines. */
export function harnessExtraPlaceholder(provider: string): string {
  return provider === 'codex'
    ? '# one key = value per line, e.g.\ntool_output_token_limit = 12000\nhistory.persistence = "save-all"'
    : '# one per line, e.g.\n--disallowedTools WebSearch\nMAX_MCP_OUTPUT_TOKENS=12000'
}
