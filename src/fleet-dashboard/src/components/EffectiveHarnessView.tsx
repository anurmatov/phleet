import { useEffect, useState } from 'react'
import type { EffectiveHarness } from '../types'
import { claudeLines, codexLines, driftState, DRIFT_TEXT, shortSha, type EffectiveLine } from '../effectiveHarness'
import { apiFetch } from '../utils'
import FieldHint from './FieldHint'

function Lines({ title, lines }: { title: string; lines: EffectiveLine[] }) {
  if (lines.length === 0) return null
  return <>
    <div className="effective-harness-subtitle">{title}</div>
    <pre className="effective-harness-lines">{lines.map((l, i) =>
      <div key={i} className={l.override ? 'effective-harness-override' : undefined}>
        {l.text}{l.override && <span className="effective-harness-override-tag"> override</span>}
      </div>)}</pre>
  </>
}

/** Read-only view of GET /api/agents/{name}/effective-harness (#460 Part 3a). */
export function EffectiveHarnessBlock({ harness }: { harness: EffectiveHarness }) {
  const drift = driftState(harness)
  const codex = harness.codex ? codexLines(harness) : null
  const claude = harness.claude ? claudeLines(harness) : null
  return <div className="effective-harness">
    <div className="effective-harness-meta">
      <span>mode <strong>{harness.mode}</strong></span>
      <span>extra <strong>{harness.extra.state}</strong></span>
      {harness.codex?.model && <span>model <code>{harness.codex.model}</code></span>}
      <span title={harness.computedPlanSha256}>plan <code>{shortSha(harness.computedPlanSha256)}</code></span>
      <span title={harness.startedPlanSha256 ?? undefined}>running <code>{shortSha(harness.startedPlanSha256)}</code></span>
      <span className={`effective-harness-drift effective-harness-drift-${drift}`}>{DRIFT_TEXT[drift]}</span>
    </div>
    {harness.extra.state === 'ignored' && harness.extra.reason &&
      <div className="config-feedback config-feedback-error">Extra harness settings ignored: {harness.extra.reason}</div>}
    {(harness.extra.notes ?? []).map((n, i) => <FieldHint key={i}>{n}</FieldHint>)}
    {codex && <><Lines title="config.toml (root)" lines={codex.root} /><Lines title="thread config" lines={codex.thread} /></>}
    {claude && <><Lines title="arguments" lines={claude.args} /><Lines title="environment" lines={claude.env} /></>}
  </div>
}

/**
 * Fetches and shows the effective settings for the agent's SAVED config. `refreshKey` changes after a
 * save so the computed plan follows it; the running sha changes only after a reprovision.
 */
export default function EffectiveHarnessView({ agentName, refreshKey }: { agentName: string; refreshKey: number }) {
  const [harness, setHarness] = useState<EffectiveHarness | null>(null)
  const [error, setError] = useState<string | null>(null)
  useEffect(() => {
    let active = true
    apiFetch(`/api/agents/${encodeURIComponent(agentName)}/effective-harness`)
      .then(async r => {
        if (!r.ok) throw new Error(r.status === 404 ? 'not available for this agent' : `error ${r.status}`)
        const body = await r.json() as EffectiveHarness
        if (active) { setHarness(body); setError(null) }
      })
      .catch((e: Error) => { if (active) { setHarness(null); setError(e.message) } })
    return () => { active = false }
  }, [agentName, refreshKey])

  return <div className="config-field">
    <label className="config-label">Effective settings <span className="config-provider-badge">read-only</span></label>
    <FieldHint>What this agent starts with: the per-mode harness defaults with its extra harness settings applied, computed from the saved config. A change applies on reprovision. Keys marked <code>override</code> come from the extra settings.</FieldHint>
    {error && <div className="config-hint-text">Effective settings {error}.</div>}
    {!error && !harness && <div className="config-loading">Loading…</div>}
    {harness && <EffectiveHarnessBlock harness={harness} />}
  </div>
}
