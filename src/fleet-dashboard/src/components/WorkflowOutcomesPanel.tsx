import { useCallback, useEffect, useRef, useState } from 'react'
import { apiFetch, AUTH_TOKEN, TEMPORAL_UI } from '../utils'
import type {
  WorkflowOutcomeBatchMembership, WorkflowOutcomeCounts, WorkflowOutcomeCoverage, WorkflowOutcomeDecisionView,
  WorkflowOutcomeDetail, WorkflowOutcomeHealth, WorkflowOutcomeListPage, WorkflowOutcomeReceipt, WorkflowOutcomeView,
  WorkflowRecoveryPlanResult,
} from '../types'
import {
  ackableMembers, batchReviewBlocked, buildAckRequest, buildDecisionRequest, causeLabel, classificationLabel,
  coverageGapRows, currentStageLabel, DEFAULT_PAGE_LIMIT, deliveryVisual, dueLabel, EMPTY_DECISION_DRAFT, EMPTY_PAGING,
  fetchBatchReceipt, fetchCoverage, fetchHealth, fetchOutcomeDetail, fetchOutcomeList, MAX_OWNER_CHARS, MAX_REASON_CHARS,
  navSummaryFrom, OPERATOR_DECISIONS, outcomeBanner, pagingFailed, pagingSummary, postAck, postDecision, postRecoveryPlan,
  recoveryCandidates, recoveryPlanSummary, shortId, stageRows, applyOutcomePage,
} from '../workflowOutcomes'
import type {
  ApiResult, DecisionDraft, OutcomeBanner, OutcomeHttp, OutcomeListKind, OutcomeNavSummary, OutcomePaging, StageRow,
} from '../workflowOutcomes'

// Workflow Failures panel (#476): confirmed failures and a separate unresolved-cause review, with
// all six handoff stages, coverage gaps and record-only operator actions. Every rule lives in
// workflowOutcomes.ts; this file renders it. No success inbox, no success receipt, no raw evidence
// payload, and no button here executes, approves or reserves recovery.

/** Every request carries the dashboard bearer; without one nothing is requested. */
const HTTP: OutcomeHttp | null = AUTH_TOKEN ? apiFetch : null

// Scoped styles (index.css is shared); `wo-` prefix only.
const WO_CSS = `
.wo-page { max-width: 1100px; }
.wo-header-right { display: flex; flex-wrap: wrap; align-items: center; gap: 8px; }
.wo-counts { display: flex; flex-wrap: wrap; gap: 6px; margin-bottom: 12px; }
.wo-chip { font-size: 11px; padding: 1px 8px; border-radius: 10px; border: 1px solid var(--border); color: var(--muted); white-space: nowrap; }
.wo-chip-attn { color: #ef4444; border-color: rgba(239,68,68,0.35); background: rgba(239,68,68,0.12); font-weight: 600; }
.wo-banner { border: 1px solid; border-radius: 8px; padding: 10px 14px; margin-bottom: 12px; font-size: 13px; }
.wo-banner-error { border-color: rgba(248,113,113,0.5); background: rgba(248,113,113,0.08); color: var(--red); }
.wo-banner-clear { border-color: rgba(74,222,128,0.35); background: rgba(74,222,128,0.06); color: var(--accent); }
.wo-banner-title { font-weight: 600; }
.wo-banner ul { margin: 6px 0 0 18px; color: var(--text); font-size: 12px; }
.wo-section { border: 1px solid var(--border); border-radius: 8px; margin-bottom: 16px; overflow: hidden; }
.wo-section-header { display: flex; flex-wrap: wrap; align-items: center; gap: 8px; padding: 10px 14px; background: var(--surface); border-bottom: 1px solid var(--border); font-size: 13px; font-weight: 500; }
.wo-section-note { font-size: 11px; color: var(--muted); font-weight: 400; }
.wo-empty { padding: 12px 14px; font-size: 12px; color: var(--muted); }
.wo-row { display: grid; grid-template-columns: minmax(0,2fr) minmax(0,2fr) minmax(0,2fr) auto; gap: 6px 12px; padding: 10px 14px; border-bottom: 1px solid var(--border); align-items: start; font-size: 12px; }
.wo-row:last-child { border-bottom: none; }
.wo-cell { min-width: 0; display: flex; flex-direction: column; gap: 3px; }
.wo-title { font-weight: 600; font-size: 13px; overflow-wrap: anywhere; }
.wo-mono { font-family: monospace; font-size: 11px; overflow-wrap: anywhere; color: var(--muted); }
.wo-badges { display: flex; flex-wrap: wrap; gap: 4px; align-items: center; }
.wo-badge { font-size: 10px; padding: 1px 6px; border-radius: 8px; font-weight: 600; max-width: 100%; overflow-wrap: anywhere; border: 1px solid transparent; }
.wo-cause-transport { background: rgba(248,113,113,0.12); color: var(--red); }
.wo-cause-business { background: rgba(245,158,11,0.15); color: var(--amber); }
.wo-cause-cause_review, .wo-cause-coverage, .wo-cause-other { background: rgba(136,136,136,0.15); color: var(--text); }
.wo-access { background: rgba(136,136,136,0.15); color: var(--muted); }
.wo-delivery-not_enqueued { background: rgba(136,136,136,0.15); color: var(--muted); }
.wo-delivery-pending { background: rgba(251,191,36,0.12); color: var(--yellow); border-color: rgba(251,191,36,0.4); }
.wo-delivery-failed { background: rgba(248,113,113,0.18); color: var(--red); border-color: var(--red); }
.wo-delivery-unknown { color: #c084fc; border: 1px dashed #c084fc; }
.wo-delivery-delivered { background: rgba(96,165,250,0.12); color: #60a5fa; }
.wo-delivery-acknowledged { background: rgba(74,222,128,0.12); color: var(--accent); }
.wo-turn-ambiguous { color: #f472b6; border: 1px dashed #f472b6; }
.wo-overdue { color: var(--red); font-weight: 600; }
.wo-pager { display: flex; flex-wrap: wrap; align-items: center; gap: 8px; padding: 8px 14px; font-size: 12px; color: var(--muted); border-top: 1px solid var(--border); }
.wo-pager-error { color: var(--red); }
.wo-stages { display: grid; grid-template-columns: repeat(6, minmax(0,1fr)); gap: 6px; margin: 8px 0 12px; }
.wo-stage { border: 1px solid var(--border); border-radius: 6px; padding: 6px 8px; font-size: 11px; min-width: 0; }
.wo-stage-label { font-weight: 600; }
.wo-stage-state { font-size: 10px; text-transform: uppercase; letter-spacing: 0.04em; color: var(--muted); }
.wo-stage-at { font-family: monospace; font-size: 10px; color: var(--muted); overflow-wrap: anywhere; }
.wo-stage-done { border-color: rgba(74,222,128,0.45); }
.wo-stage-pending { border-color: rgba(251,191,36,0.55); }
.wo-stage-failed { border-color: var(--red); background: rgba(248,113,113,0.08); }
.wo-stage-unknown { border: 1px dashed #c084fc; }
.wo-stage-attention { border-color: var(--amber); background: rgba(245,158,11,0.08); }
.wo-stage-not_reached { opacity: 0.55; }
.wo-grid { display: grid; grid-template-columns: 150px minmax(0,1fr); gap: 4px 12px; font-size: 12px; }
.wo-label { color: var(--muted); }
.wo-subtitle { margin: 16px 0 6px; font-size: 12px; font-weight: 600; display: flex; flex-wrap: wrap; align-items: center; gap: 8px; }
.wo-table-wrap { overflow-x: auto; }
.wo-form { display: grid; grid-template-columns: repeat(2, minmax(0,1fr)); gap: 8px 12px; }
.wo-form label { display: flex; flex-direction: column; gap: 3px; min-width: 0; }
.wo-form .config-input, .wo-form .config-textarea { width: 100%; }
.wo-wide { grid-column: 1 / -1; }
.wo-actions { display: flex; flex-wrap: wrap; align-items: center; gap: 8px; margin-top: 8px; }
.wo-note { font-size: 11px; color: var(--muted); }
.wo-result-ok { color: var(--accent); font-size: 12px; }
.wo-result-bad { color: var(--red); font-size: 12px; }
.wo-detail-modal { width: min(94vw, 1100px); height: min(90vh, 960px); }
.wo-members { display: flex; flex-direction: column; gap: 4px; margin-top: 6px; font-size: 12px; }
.wo-members label { display: flex; align-items: center; gap: 6px; }
@media (max-width: 768px) {
  .wo-row { grid-template-columns: minmax(0,1fr); }
  .wo-stages { grid-template-columns: repeat(2, minmax(0,1fr)); }
  .wo-grid { grid-template-columns: minmax(0,1fr); gap: 0 12px; }
  .wo-grid .wo-label { margin-top: 6px; }
  .wo-form { grid-template-columns: minmax(0,1fr); }
  .wo-detail-modal { width: 100%; height: 100%; }
}
`

function formatWhen(iso: string | null | undefined): string {
  if (!iso) return '—'
  const ms = Date.parse(iso)
  return Number.isNaN(ms) ? iso : new Date(ms).toLocaleString()
}

function temporalRunUrl(namespace: string, workflowId: string, runId: string | null): string | null {
  if (!TEMPORAL_UI || !runId) return null
  return `${TEMPORAL_UI}/namespaces/${encodeURIComponent(namespace)}/workflows/${encodeURIComponent(workflowId)}/${encodeURIComponent(runId)}/history`
}

function RunRef({ namespace, workflowId, runId }: { namespace: string; workflowId: string | null; runId: string | null }) {
  if (!workflowId && !runId) return <span className="eg-muted">—</span>
  const url = workflowId ? temporalRunUrl(namespace, workflowId, runId) : null
  const text = `${workflowId ?? '(same workflow)'} / ${runId ?? '—'}`
  return url
    ? <a className="wo-mono wf-link" href={url} target="_blank" rel="noreferrer">{text}</a>
    : <span className="wo-mono">{text}</span>
}

function EvidenceRef({ value }: { value: string }) {
  return value.startsWith('https://')
    ? <a className="wo-mono wf-link" href={value} target="_blank" rel="noreferrer">{value}</a>
    : <span className="wo-mono">{value}</span>
}

// ── Pieces exported for the render test ─────────────────────────────────────────

export function OutcomeBannerView({ banner }: { banner: OutcomeBanner }) {
  if (banner.level === 'none') return null
  return (
    <div className={`wo-banner wo-banner-${banner.level}`} role={banner.level === 'error' ? 'alert' : 'status'}>
      <div className="wo-banner-title">{banner.title}</div>
      {banner.details.length > 0 && <ul>{banner.details.map((d, i) => <li key={i}>{d}</li>)}</ul>}
    </div>
  )
}

export function CountChips({ counts }: { counts: WorkflowOutcomeCounts | null }) {
  if (!counts) return <div className="wo-counts"><span className="wo-chip wo-chip-attn">counts not loaded</span></div>
  return (
    <div className="wo-counts">
      <span className={`wo-chip${counts.failures > 0 ? ' wo-chip-attn' : ''}`}>{counts.failures} failure item(s)</span>
      <span className={`wo-chip${counts.unresolvedCauses > 0 ? ' wo-chip-attn' : ''}`}>{counts.unresolvedCauses} cause review(s)</span>
      <span className={`wo-chip${counts.coverageGaps > 0 ? ' wo-chip-attn' : ''}`}>{counts.coverageGaps} coverage gap(s)</span>
    </div>
  )
}

export function OutcomeRow({ item, nowMs, onOpen }: { item: WorkflowOutcomeView; nowMs: number; onOpen: (id: string) => void }) {
  const cause = causeLabel(item.classification, item.reasonCode)
  const delivery = deliveryVisual(item)
  const due = dueLabel(item.dueAt, item.attentionOverdue, nowMs)
  return (
    <div className="wo-row" data-outcome-id={item.id}>
      <div className="wo-cell">
        <span className="wo-title">{item.workflowType || '(unknown type)'}</span>
        <span className="wo-mono" title={item.workflowId}>{item.workflowId}</span>
        <span className="wo-mono" title={item.runId}>run {shortId(item.runId)} · v{item.version}</span>
        <span className="wo-badges">
          <span className="wf-ns-badge">{item.namespace}</span>
          {item.accessNeeded && <span className="wo-badge wo-access">access needed</span>}
        </span>
      </div>
      <div className="wo-cell">
        <span className="wo-badges">
          <span className={`wo-badge wo-cause-${cause.kind}`}>{cause.kindLabel}</span>
          <span>{classificationLabel(item.classification)}</span>
        </span>
        <span>{cause.label}{cause.code && <> <span className="wo-mono">({cause.code})</span></>}</span>
        {item.terminalEventType && (
          <span className="wo-mono">{item.terminalEventType}{item.terminalEventId !== null ? ` #${item.terminalEventId}` : ''}</span>
        )}
      </div>
      <div className="wo-cell">
        <span className="wo-badges">
          <span className={`wo-badge wo-delivery-${delivery.state}`}>{delivery.label}</span>
          {item.turnAmbiguous && <span className="wo-badge wo-turn-ambiguous">Coordinator turn ambiguous</span>}
        </span>
        <span>Stage: {currentStageLabel(item.stage)}</span>
        <span>Owner {item.coordinator ?? 'unassigned'} · task owner {item.taskOwner ?? 'unknown'}</span>
        <span className={due.overdue ? 'wo-overdue' : 'wo-note'}>{due.text}</span>
      </div>
      <div className="wo-cell">
        <button className="wfd-toggle-btn" onClick={() => onOpen(item.id)}>Details</button>
      </div>
    </div>
  )
}

export function OutcomeList({
  title, note, paging, serverCount, loading, nowMs, onOpen, onMore, onReload,
}: {
  title: string
  note: string
  paging: OutcomePaging
  serverCount: number | null
  loading: boolean
  nowMs: number
  onOpen: (id: string) => void
  onMore: () => void
  onReload: () => void
}) {
  return (
    <div className="wo-section">
      <div className="wo-section-header">
        <span>{title}</span>
        {serverCount !== null && <span className="section-count">{serverCount}</span>}
        <span className="wo-section-note">{note}</span>
      </div>
      {paging.items.length === 0
        ? <div className="wo-empty">{paging.loadedPages === 0 ? (loading ? 'Loading…' : 'Not loaded.') : 'No items returned.'}</div>
        : paging.items.map(item => <OutcomeRow key={item.id} item={item} nowMs={nowMs} onOpen={onOpen} />)}
      <div className="wo-pager">
        <span className={paging.error ? 'wo-pager-error' : undefined}>{pagingSummary(paging, serverCount)}</span>
        {paging.hidden > 0 && <span className="wo-pager-error">{paging.hidden} row(s) that do not belong in this list were hidden.</span>}
        {paging.nextCursor && (
          <button className="wfd-toggle-btn" onClick={onMore} disabled={loading}>{loading ? 'Loading…' : `Load next ${DEFAULT_PAGE_LIMIT}`}</button>
        )}
        {paging.error && <button className="wfd-toggle-btn" onClick={onReload} disabled={loading}>Reload</button>}
      </div>
    </div>
  )
}

export function CoverageSection({ coverage }: { coverage: WorkflowOutcomeCoverage | null }) {
  if (!coverage) return null
  const gaps = coverageGapRows(coverage)
  return (
    <div className="wo-section">
      <div className="wo-section-header">
        <span>Coverage</span>
        <span className={`wo-badge ${coverage.enabled && !coverage.degraded ? 'wo-delivery-acknowledged' : 'wo-delivery-failed'}`}>
          {!coverage.enabled ? 'disabled' : coverage.degraded ? 'degraded' : 'healthy'}
        </span>
        {gaps.length > 0 && <span className="wo-chip wo-chip-attn">{gaps.length} gap(s)</span>}
      </div>
      <div className="wo-table-wrap" style={{ padding: '8px 14px' }}>
        {coverage.namespaces.length === 0 ? <div className="eg-muted">No namespace enrolled.</div> : (
          <table className="eg-table">
            <thead><tr><th>Namespace</th><th>State</th><th>Known pending</th><th>Recent pass</th><th>Full pass</th><th>Retention</th></tr></thead>
            <tbody>
              {coverage.namespaces.map(ns => (
                <tr key={ns.namespace} className={ns.state !== 'healthy' ? 'eg-row-bad' : undefined}>
                  <td className="eg-mono">{ns.namespace}</td>
                  <td>{ns.state}</td>
                  <td>{ns.knownPending}</td>
                  <td title={ns.lastRecentPassAt ?? ''}>{formatWhen(ns.lastRecentPassAt)}</td>
                  <td title={ns.lastFullPassAt ?? ''}>{formatWhen(ns.lastFullPassAt)}</td>
                  <td>{ns.retentionSeconds !== null ? `${Math.round(ns.retentionSeconds / 86400)}d` : 'unknown'}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
        {gaps.length > 0 && <>
          <div className="wo-subtitle">Coverage gaps (runs in these windows are unknown, not clear)</div>
          <table className="eg-table">
            <thead><tr><th>Namespace</th><th>From</th><th>To</th><th>Reason</th><th>Unknown runs</th></tr></thead>
            <tbody>
              {gaps.map((g, i) => (
                <tr key={i} className="eg-row-bad">
                  <td className="eg-mono">{g.namespace}</td>
                  <td title={g.start ?? ''}>{formatWhen(g.start)}</td>
                  <td title={g.end ?? ''}>{formatWhen(g.end)}</td>
                  <td>{g.reason}</td>
                  <td>{g.unknownRunCount ?? 'unknown'}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </>}
      </div>
    </div>
  )
}

export function StageTimeline({ rows }: { rows: StageRow[] }) {
  return (
    <div className="wo-stages">
      {rows.map(r => (
        <div key={r.key} className={`wo-stage wo-stage-${r.state}`} data-stage={r.key}>
          <div className="wo-stage-label">{r.label}</div>
          <div className="wo-stage-state">{r.state.replace('_', ' ')}</div>
          <div className="wo-stage-at" title={r.at ?? ''}>{r.at ? formatWhen(r.at) : '—'}</div>
          {r.note && <div className="wo-note">{r.note}</div>}
        </div>
      ))}
    </div>
  )
}

// ── Exact-version acknowledgement of one batch ─────────────────────────────────

export function BatchMembers({ batch, outcomeId, http, onAcked, initialReceipt = null }: {
  batch: WorkflowOutcomeBatchMembership
  outcomeId: string
  http: OutcomeHttp | null
  onAcked: () => void
  /** Seeds the member list (render test); normally loaded by "Review members". */
  initialReceipt?: ApiResult<WorkflowOutcomeReceipt> | null
}) {
  const [receipt, setReceipt] = useState<ApiResult<WorkflowOutcomeReceipt> | null>(initialReceipt)
  const [selected, setSelected] = useState<Set<string>>(new Set())
  const [confirming, setConfirming] = useState(false)
  const [pending, setPending] = useState(false)
  const [message, setMessage] = useState<{ ok: boolean; text: string } | null>(null)

  const blocked = batchReviewBlocked(batch)
  const loaded = receipt && receipt.ok ? receipt.data : null
  const ackable = loaded ? ackableMembers(loaded) : []
  const ackedKeys = new Set(loaded ? loaded.ackedVersions.map(r => `${r.id}:${r.version}`) : [])
  const selection = ackable.filter(r => selected.has(`${r.id}:${r.version}`))
  const build = loaded ? buildAckRequest(loaded, selection) : null

  async function review() {
    if (!batch.nonce) return
    setPending(true)
    setMessage(null)
    const r = await fetchBatchReceipt(http, batch.batchId, batch.nonce)
    setReceipt(r)
    setSelected(new Set())
    setConfirming(false)
    setPending(false)
  }

  async function acknowledge() {
    if (!loaded || !build || !build.ok) return
    setPending(true)
    const r = await postAck(http, batch.batchId, build.body)
    setPending(false)
    setConfirming(false)
    if (r.ok) {
      setReceipt(r)
      setSelected(new Set())
      setMessage({ ok: true, text: `Acknowledged ${build.body.items.length} exact member version(s). This records receipt only; it approves nothing.` })
      onAcked()
    } else {
      setMessage({ ok: false, text: r.error })
    }
  }

  function toggle(key: string, on: boolean) {
    setSelected(prev => {
      const next = new Set(prev)
      if (on) next.add(key)
      else next.delete(key)
      return next
    })
    setConfirming(false)
  }

  return (
    <div>
      <div className="wo-actions" style={{ marginTop: 0 }}>
        <button className="wfd-toggle-btn" onClick={review} disabled={blocked !== null || pending}
          title={blocked ?? 'Read the exact delivered and acknowledged member versions'}>
          {loaded ? 'Reload members' : 'Review members'}
        </button>
        {blocked && <span className="wo-note">{blocked}</span>}
      </div>
      {receipt && !receipt.ok && <div className="wo-result-bad">{receipt.error}</div>}
      {loaded && (
        <div className="wo-members">
          {loaded.items.map(m => {
            const key = `${m.id}:${m.version}`
            const deliveredVersion = loaded.deliveredVersions.find(d => d.id === m.id)?.version
            const canAck = ackable.some(a => a.id === m.id && a.version === m.version)
            return (
              <label key={key}>
                <input type="checkbox" checked={selected.has(key)} disabled={!canAck || pending}
                  onChange={e => toggle(key, e.target.checked)} />
                <span className="wo-mono">{shortId(m.id, 13)} v{m.version}</span>
                {m.id === outcomeId && <span className="wo-badge wo-access">this run</span>}
                <span className="wo-note">
                  {ackedKeys.has(key) ? 'acknowledged' : deliveredVersion === m.version ? 'delivered' : 'not delivered'}
                </span>
              </label>
            )
          })}
          <div className="wo-actions">
            {!confirming ? (
              <button className="wfd-toggle-btn" disabled={selection.length === 0 || pending} onClick={() => setConfirming(true)}>
                Acknowledge selected ({selection.length})
              </button>
            ) : (
              <>
                <span className="wo-note">Acknowledge exactly {selection.length} member version(s)? Receipt only — not an approval or a retry.</span>
                <button className="wfd-toggle-btn confirming" disabled={pending || !build?.ok} onClick={acknowledge}>Confirm</button>
                <button className="wfd-cancel-btn" onClick={() => setConfirming(false)}>Cancel</button>
              </>
            )}
            {build && !build.ok && selection.length > 0 && <span className="wo-result-bad">{build.error}</span>}
          </div>
        </div>
      )}
      {message && <div className={message.ok ? 'wo-result-ok' : 'wo-result-bad'}>{message.text}</div>}
    </div>
  )
}

// ── Record-only decision form ──────────────────────────────────────────────────

export function DecisionForm({ item, http, onRecorded }: {
  item: WorkflowOutcomeView
  http: OutcomeHttp | null
  onRecorded: () => void
}) {
  const [draft, setDraft] = useState<DecisionDraft>(EMPTY_DECISION_DRAFT)
  const [showErrors, setShowErrors] = useState(false)
  const [confirming, setConfirming] = useState(false)
  const [pending, setPending] = useState(false)
  const [message, setMessage] = useState<{ ok: boolean; text: string } | null>(null)

  const build = buildDecisionRequest(draft, item, Date.now())
  const offered = OPERATOR_DECISIONS.find(d => d.operation === draft.operation)
  const needsDue = draft.operation === 'wait' || draft.operation === 'hold'

  function set<K extends keyof DecisionDraft>(key: K, value: DecisionDraft[K]) {
    setDraft(d => ({ ...d, [key]: value }))
    setConfirming(false)
    setMessage(null)
  }

  async function record() {
    // Re-validated against the clock at the moment of sending (the future due-time rule).
    const fresh = buildDecisionRequest(draft, item, Date.now())
    if (!fresh.ok) { setShowErrors(true); setConfirming(false); return }
    setPending(true)
    const r = await postDecision(http, item.id, fresh.body)
    setPending(false)
    setConfirming(false)
    if (r.ok) {
      setMessage({ ok: true, text: `Recorded ${fresh.body.operation} (decision ${r.data.decisionId}). Record only: nothing was executed.` })
      setDraft(EMPTY_DECISION_DRAFT)
      setShowErrors(false)
      onRecorded()
    } else {
      setMessage({ ok: false, text: r.error })
    }
  }

  return (
    <div>
      <div className="wo-form">
        <label>
          <span className="config-label">Decision</span>
          <select className="config-input" value={draft.operation} onChange={e => set('operation', e.target.value)}>
            {OPERATOR_DECISIONS.map(d => <option key={d.operation} value={d.operation}>{d.label}</option>)}
          </select>
        </label>
        <label>
          <span className="config-label">Owner (next owner, ≤{MAX_OWNER_CHARS})</span>
          <input className="config-input" value={draft.owner} onChange={e => set('owner', e.target.value)} />
        </label>
        <label className="wo-wide">
          <span className="config-label">Reason ({draft.reason.trim().length}/{MAX_REASON_CHARS})</span>
          <textarea className="config-textarea" rows={3} value={draft.reason} onChange={e => set('reason', e.target.value)} />
        </label>
        <label className="wo-wide">
          <span className="config-label">Evidence refs (one per line: outcome:, run:, event:, task:, artifact:, late-result:, decision:, operation:, https://)</span>
          <textarea className="config-textarea wo-mono" rows={3} value={draft.evidenceRefsText} onChange={e => set('evidenceRefsText', e.target.value)} />
        </label>
        <label>
          <span className="config-label">Due / next review{needsDue ? ' (required, future)' : ' (optional)'}</span>
          <input className="config-input" type="datetime-local" value={draft.dueAt} onChange={e => set('dueAt', e.target.value)} />
        </label>
        <label>
          <span className="config-label">Task ref (optional)</span>
          <input className="config-input wo-mono" value={draft.taskRef} onChange={e => set('taskRef', e.target.value)} />
        </label>
        <label>
          <span className="config-label">Artifact ref (optional)</span>
          <input className="config-input wo-mono" value={draft.artifactRef} onChange={e => set('artifactRef', e.target.value)} />
        </label>
      </div>
      {offered && <div className="wo-note" style={{ marginTop: 6 }}>{offered.hint}</div>}
      {showErrors && !build.ok && (
        <div className="wfd-validation-bar">{build.errors.map((e, i) => <div key={i} className="wfd-validation-item blocking">{e}</div>)}</div>
      )}
      <div className="wo-actions">
        {!confirming ? (
          <button className="wfd-toggle-btn" disabled={pending}
            onClick={() => { if (build.ok) setConfirming(true); else setShowErrors(true) }}>
            Record {offered?.label ?? 'decision'}
          </button>
        ) : (
          <>
            <span className="wo-note">Record “{offered?.label}” for v{item.version}? Record only — nothing is executed, approved or retried.</span>
            <button className="wfd-toggle-btn confirming" disabled={pending} onClick={record}>Confirm</button>
            <button className="wfd-cancel-btn" onClick={() => setConfirming(false)}>Cancel</button>
          </>
        )}
      </div>
      {message && <div className={message.ok ? 'wo-result-ok' : 'wo-result-bad'}>{message.text}</div>}
    </div>
  )
}

// ── Plan recovery: read-only check, no execution control ───────────────────────

export function RecoveryPlan({ outcomeId, decision, http, initialResult = null }: {
  outcomeId: string
  decision: WorkflowOutcomeDecisionView
  http: OutcomeHttp | null
  /** Seeds the result (render test); normally set by "Plan recovery". */
  initialResult?: ApiResult<WorkflowRecoveryPlanResult> | null
}) {
  const [result, setResult] = useState<ApiResult<WorkflowRecoveryPlanResult> | null>(initialResult)
  const [pending, setPending] = useState(false)

  async function plan() {
    if (!decision.decisionId) return
    setPending(true)
    setResult(await postRecoveryPlan(http, outcomeId, decision.decisionId))
    setPending(false)
  }

  const summary = result && result.ok ? recoveryPlanSummary(result.data) : null
  return (
    <div style={{ marginTop: 6 }}>
      <div className="wo-actions" style={{ marginTop: 0 }}>
        <span className="wo-mono">{decision.operation} · {shortId(decision.decisionId, 13)} · {decision.owner}</span>
        <button className="wfd-toggle-btn" onClick={plan} disabled={pending || !decision.decisionId}
          title="Read-only recheck of proofs. Reserves, approves and starts nothing.">
          {pending ? 'Checking…' : 'Plan recovery'}
        </button>
      </div>
      {result && !result.ok && <div className="wo-result-bad">{result.error}</div>}
      {summary && (
        <div className={summary.allowed ? 'wo-result-ok' : 'wo-result-bad'}>
          <div>{summary.title}</div>
          {summary.lines.map((l, i) => <div key={i} className="wo-note">{l}</div>)}
        </div>
      )}
    </div>
  )
}

// ── Detail ─────────────────────────────────────────────────────────────────────

export function OutcomeDetailView({ detail, nowMs, http, onChanged }: {
  detail: WorkflowOutcomeDetail
  nowMs: number
  http: OutcomeHttp | null
  onChanged: () => void
}) {
  const cause = causeLabel(detail.classification, detail.reasonCode)
  const delivery = deliveryVisual(detail)
  const due = dueLabel(detail.dueAt, detail.attentionOverdue, nowMs)
  const candidates = recoveryCandidates(detail.decisions)
  return (
    <div>
      <div className="wo-badges" style={{ margin: '6px 0' }}>
        <span className={`wo-badge wo-cause-${cause.kind}`}>{cause.kindLabel}</span>
        <span className={`wo-badge wo-delivery-${delivery.state}`}>{delivery.label}</span>
        {detail.turnAmbiguous && <span className="wo-badge wo-turn-ambiguous">Coordinator turn ambiguous</span>}
        {detail.accessNeeded && <span className="wo-badge wo-access">access needed</span>}
        <span className={due.overdue ? 'wo-overdue' : 'wo-note'}>{due.text}</span>
      </div>

      <div className="wo-subtitle">Handoff stages</div>
      <StageTimeline rows={stageRows(detail)} />

      <div className="wo-grid">
        <div className="wo-label">Namespace</div><div className="eg-mono">{detail.namespace}</div>
        <div className="wo-label">Run</div><div><RunRef namespace={detail.namespace} workflowId={detail.workflowId} runId={detail.runId} /></div>
        <div className="wo-label">Version</div><div>v{detail.version}</div>
        <div className="wo-label">Classification</div>
        <div>{classificationLabel(detail.classification)} · {cause.label} {cause.code && <span className="wo-mono">({cause.code})</span>}</div>
        <div className="wo-label">Terminal event</div>
        <div className="wo-mono">{detail.terminalEventType ?? '—'}{detail.terminalEventId !== null ? ` #${detail.terminalEventId}` : ''} · {detail.status || '—'}</div>
        <div className="wo-label">Evidence state</div><div>{detail.evidenceState ?? 'unknown'}</div>
        <div className="wo-label">Owner (coordinator)</div><div>{detail.coordinator ?? 'unassigned'}</div>
        <div className="wo-label">Task owner</div><div>{detail.taskOwner ?? 'unknown'}</div>
        <div className="wo-label">Project</div><div>{detail.project ?? (detail.accessNeeded ? 'access needed' : 'unknown')}</div>
        <div className="wo-label">Due</div><div title={detail.dueAt ?? ''}>{formatWhen(detail.dueAt)}</div>
        <div className="wo-label">Started / closed</div><div>{formatWhen(detail.startTime)} → {formatWhen(detail.closeTime)}</div>
        <div className="wo-label">Failure incident</div><div className="wo-mono">{detail.failureIncidentId ?? '—'}</div>
        <div className="wo-label">Parent</div><div><RunRef namespace={detail.namespace} workflowId={detail.parentWorkflowId} runId={detail.parentRunId} /></div>
        <div className="wo-label">Previous run</div><div><RunRef namespace={detail.namespace} workflowId={detail.previousRunId ? detail.workflowId : null} runId={detail.previousRunId} /></div>
        <div className="wo-label">Next run</div><div><RunRef namespace={detail.namespace} workflowId={detail.nextRunId ? detail.workflowId : null} runId={detail.nextRunId} /></div>
        <div className="wo-label">Recovery operation</div>
        <div className="wo-mono">{detail.recovery ? `${detail.recovery.operationId} · ${detail.recovery.state}` : '—'}</div>
      </div>

      <div className="wo-subtitle">Batch membership <span className="wo-note">acknowledge exact delivered versions only</span></div>
      {detail.batches.length === 0 ? <div className="eg-muted">Not a member of any batch yet.</div> : (
        <div className="wo-table-wrap">
          <table className="eg-table">
            <thead><tr><th>Batch</th><th>Generation</th><th>Member version</th><th>State</th><th>Delivered</th><th>Acknowledged</th><th>Members</th></tr></thead>
            <tbody>
              {detail.batches.map(b => (
                <tr key={b.batchId} data-batch-id={b.batchId}>
                  <td className="eg-mono" title={b.batchId}>{shortId(b.batchId, 13)}</td>
                  <td>{b.generation}</td>
                  <td>{b.version !== null ? `v${b.version}` : 'unknown'}</td>
                  <td>{b.state}</td>
                  <td title={b.deliveredAt ?? ''}>{formatWhen(b.deliveredAt)}</td>
                  <td title={b.ackedAt ?? ''}>{formatWhen(b.ackedAt)}</td>
                  <td><BatchMembers batch={b} outcomeId={detail.id} http={http} onAcked={onChanged} /></td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      <div className="wo-subtitle">Evidence and artifact refs <span className="wo-note">references only; payloads are not shown</span></div>
      {detail.evidenceRefs.length === 0 ? <div className="eg-muted">none</div> : (
        <div className="wo-members">{detail.evidenceRefs.map(r => <EvidenceRef key={r} value={r} />)}</div>
      )}

      <div className="wo-subtitle">Decision audit</div>
      {detail.decisions.length === 0 ? <div className="eg-muted">No decision recorded.</div> : (
        <div className="wo-table-wrap">
          <table className="eg-table">
            <thead><tr><th>When</th><th>Decision</th><th>Owner</th><th>Status</th><th>Via</th><th>Due</th><th>Reason</th></tr></thead>
            <tbody>
              {detail.decisions.map((d, i) => (
                <tr key={d.decisionId ?? i} className={d.status === 'rejected' ? 'eg-row-bad' : undefined}>
                  <td title={d.createdAt}>{formatWhen(d.createdAt)}</td>
                  <td>{d.operation}{d.version !== null ? ` (v${d.version})` : ''}</td>
                  <td>{d.owner}</td>
                  <td>{d.status}</td>
                  <td>{d.decidedVia}</td>
                  <td title={d.dueAt ?? ''}>{formatWhen(d.dueAt)}</td>
                  <td>{d.reason}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      <div className="wo-subtitle">Plan recovery <span className="wo-note">read-only check; execution needs the separate existing authorized path</span></div>
      {candidates.length === 0
        ? <div className="eg-muted">Needs an accepted resume, retry, reassign, accept_late or cancel decision.</div>
        : candidates.map(d => <RecoveryPlan key={d.decisionId} outcomeId={detail.id} decision={d} http={http} />)}

      <div className="wo-subtitle">Record a decision <span className="wo-note">record only</span></div>
      <DecisionForm item={detail} http={http} onRecorded={onChanged} />

      <div className="wo-subtitle">Observations</div>
      {detail.observations.length === 0 ? <div className="eg-muted">none</div> : (
        <div className="wo-table-wrap">
          <table className="eg-table">
            <thead><tr><th>Observed</th><th>Event</th><th>Type</th><th>Classification</th><th>Reason</th></tr></thead>
            <tbody>
              {detail.observations.map((o, i) => (
                <tr key={i}>
                  <td title={o.observedAt ?? ''}>{formatWhen(o.observedAt)}</td>
                  <td>{o.eventId ?? '—'}</td>
                  <td>{o.eventType}</td>
                  <td>{o.classification ?? '—'}</td>
                  <td className="eg-mono">{o.reasonCode ?? '—'}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  )
}

// ── Panel ──────────────────────────────────────────────────────────────────────

type ListMeta = { counts: WorkflowOutcomeCounts; namespaceErrors: { namespace: string; error: string }[] } | null

export default function WorkflowOutcomesPanel({ onSummary, http = HTTP }: {
  onSummary?: (summary: OutcomeNavSummary) => void
  http?: OutcomeHttp | null
}) {
  const [namespace, setNamespace] = useState('')
  const [failures, setFailures] = useState<OutcomePaging>(EMPTY_PAGING)
  const [causes, setCauses] = useState<OutcomePaging>(EMPTY_PAGING)
  const [loading, setLoading] = useState<Record<OutcomeListKind, boolean>>({ failures: false, unresolved_cause: false })
  const [meta, setMeta] = useState<Record<OutcomeListKind, ListMeta>>({ failures: null, unresolved_cause: null })
  const [firstFailures, setFirstFailures] = useState<ApiResult<WorkflowOutcomeListPage> | null>(null)
  const [coverage, setCoverage] = useState<ApiResult<WorkflowOutcomeCoverage> | null>(null)
  const [health, setHealth] = useState<ApiResult<WorkflowOutcomeHealth> | null>(null)
  const [selectedId, setSelectedId] = useState<string | null>(null)
  const [detail, setDetail] = useState<ApiResult<WorkflowOutcomeDetail> | null>(null)
  const [detailLoading, setDetailLoading] = useState(false)
  const [nowMs, setNowMs] = useState(() => Date.now())
  // Responses from an older namespace/refresh are dropped, never merged into the current list.
  const generation = useRef(0)
  const selectedRef = useRef<string | null>(null)

  const loadPage = useCallback(async (kind: OutcomeListKind, cursor: string | null) => {
    const gen = generation.current
    const setPaging = kind === 'failures' ? setFailures : setCauses
    setLoading(l => ({ ...l, [kind]: true }))
    const res = await fetchOutcomeList(http, { state: kind, namespace: namespace || null, cursor, limit: DEFAULT_PAGE_LIMIT })
    if (gen !== generation.current) return
    setLoading(l => ({ ...l, [kind]: false }))
    if (res.ok) {
      setPaging(prev => applyOutcomePage(prev, res.data, cursor, kind))
      setMeta(m => ({ ...m, [kind]: { counts: res.data.counts, namespaceErrors: res.data.namespaceErrors } }))
    } else {
      setPaging(prev => pagingFailed(prev, res.error))
      if (cursor === null) setMeta(m => ({ ...m, [kind]: null }))
    }
    if (kind === 'failures' && cursor === null) setFirstFailures(res)
  }, [http, namespace])

  const refresh = useCallback(() => {
    generation.current += 1
    const gen = generation.current
    void loadPage('failures', null)
    void loadPage('unresolved_cause', null)
    void fetchCoverage(http).then(r => { if (gen === generation.current) setCoverage(r) })
    void fetchHealth(http).then(r => { if (gen === generation.current) setHealth(r) })
  }, [http, loadPage])

  const openDetail = useCallback(async (id: string) => {
    selectedRef.current = id
    setSelectedId(id)
    // Keep the shown detail only while refreshing the same item.
    setDetail(prev => prev && prev.ok && prev.data.id === id ? prev : null)
    setDetailLoading(true)
    const r = await fetchOutcomeDetail(http, id)
    if (selectedRef.current !== id) return
    setDetail(r)
    setDetailLoading(false)
  }, [http])

  const closeDetail = useCallback(() => {
    selectedRef.current = null
    setSelectedId(null)
    setDetail(null)
  }, [])

  const onChanged = useCallback(() => {
    if (selectedRef.current) void openDetail(selectedRef.current)
    refresh()
  }, [openDetail, refresh])

  useEffect(() => {
    setFailures(EMPTY_PAGING)
    setCauses(EMPTY_PAGING)
    refresh()
  }, [refresh])

  useEffect(() => {
    const t = setInterval(() => setNowMs(Date.now()), 30_000)
    return () => clearInterval(t)
  }, [])

  // Side-nav counts are fleet-wide, so only an unfiltered load reports them.
  useEffect(() => {
    if (!onSummary || !firstFailures || !coverage || namespace !== '') return
    onSummary(navSummaryFrom(firstFailures, coverage))
  }, [onSummary, firstFailures, coverage, namespace])

  const coverageData = coverage && coverage.ok ? coverage.data : null
  const healthData = health && health.ok ? health.data : null
  const counts = meta.failures?.counts ?? meta.unresolved_cause?.counts ?? null
  const namespaceErrors = [...(meta.failures?.namespaceErrors ?? []), ...(meta.unresolved_cause?.namespaceErrors ?? [])]
    .filter((e, i, all) => all.findIndex(x => x.namespace === e.namespace && x.error === e.error) === i)
  const banner = outcomeBanner({
    listErrors: [failures.error, causes.error].filter((e): e is string => e !== null),
    namespaceErrors,
    coverage: coverageData,
    coverageError: coverage && !coverage.ok ? coverage.error : null,
    health: healthData,
    healthError: health && !health.ok ? health.error : null,
    itemCount: failures.items.length + causes.items.length,
    counts,
    hasMore: failures.nextCursor !== null || causes.nextCursor !== null || failures.loadedPages === 0 || causes.loadedPages === 0,
  })
  const anyLoading = loading.failures || loading.unresolved_cause
  const selectedDetail = detail && detail.ok ? detail.data : null

  return (
    <div className="view-page wo-page">
      <style>{WO_CSS}</style>
      <div className="view-page-header" style={{ flexWrap: 'wrap', gap: 8 }}>
        <h1 className="view-page-title">Workflow Failures</h1>
        <div className="wo-header-right">
          <select className="config-input" value={namespace} onChange={e => setNamespace(e.target.value)} aria-label="Namespace">
            <option value="">all namespaces</option>
            {(coverageData?.namespaces ?? []).map(ns => <option key={ns.namespace} value={ns.namespace}>{ns.namespace}</option>)}
          </select>
          <button className="completed-refresh-btn" onClick={refresh} disabled={anyLoading} title="Refresh">↻</button>
        </div>
      </div>

      <CountChips counts={counts} />
      <OutcomeBannerView banner={banner} />

      <OutcomeList
        title="Confirmed failures"
        note="failure-caused exits needing delivery, acknowledgement and an owned decision"
        paging={failures}
        serverCount={meta.failures?.counts.failures ?? null}
        loading={loading.failures}
        nowMs={nowMs}
        onOpen={openDetail}
        onMore={() => { void loadPage('failures', failures.nextCursor) }}
        onReload={refresh}
      />

      <OutcomeList
        title="Unresolved-cause review"
        note="cause not proven: never counted as a failure or as success until reviewed"
        paging={causes}
        serverCount={meta.unresolved_cause?.counts.unresolvedCauses ?? null}
        loading={loading.unresolved_cause}
        nowMs={nowMs}
        onOpen={openDetail}
        onMore={() => { void loadPage('unresolved_cause', causes.nextCursor) }}
        onReload={refresh}
      />

      <CoverageSection coverage={coverageData} />

      {selectedId && (
        <div className="wf-detail-overlay" onClick={closeDetail}>
          <div className="wf-detail-modal wo-detail-modal" onClick={e => e.stopPropagation()} role="dialog" aria-label="Workflow failure detail">
            <div className="wf-detail-header">
              <span className="wf-detail-title">
                {selectedDetail?.workflowType ?? 'Workflow failure'} / <span className="wf-detail-id">{selectedDetail?.workflowId ?? selectedId}</span>
              </span>
              <div className="wf-detail-header-actions">
                <button className="wf-detail-refresh" onClick={() => { void openDetail(selectedId) }} disabled={detailLoading} title="Refresh">↻</button>
                <button className="wf-detail-close" onClick={closeDetail}>✕</button>
              </div>
            </div>
            <div className="wf-detail-body">
              {detailLoading && <div className="wf-detail-loading">Loading…</div>}
              {detail && !detail.ok && <div className="wf-detail-error">Error: {detail.error}</div>}
              {selectedDetail && <OutcomeDetailView detail={selectedDetail} nowMs={nowMs} http={http} onChanged={onChanged} />}
            </div>
          </div>
        </div>
      )}
    </div>
  )
}
