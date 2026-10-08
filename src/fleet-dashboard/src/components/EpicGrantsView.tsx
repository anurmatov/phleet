import { useCallback, useEffect, useRef, useState } from 'react'
import { apiFetch } from '../utils'
import FieldHint from './FieldHint'
import type { EpicGrantDetail, EpicGrantView, ScopeValidationReport } from '../types'
import {
  applyRevoke, applyRevokeToDetail, asValidationReport, canRevoke, createBlockedReason,
  isDeliveryUnknown, isPublicTarget, parseScopeText, responseErrorMessage, revokeRequestBody,
  scopeRequestBody, shortHash, statusLabel,
} from '../epicGrants'

// Epic grants (#436). Paste a scope, validate it, create the grant, revoke it, and watch its
// decisions. The rules (when Create is enabled, "delivery unknown", revoke updates) live in
// epicGrants.ts and are tested there; this file only renders them.

const SCOPE_PLACEHOLDER = `{
  "driver": {"namespace": "fleet", "workflowId": "<driver-id>", "runId": "<driver-run-id>"},
  "targets": [{"repo": "<owner>/<repo>", "issues": [12, 13]}],
  "gates": ["design-approval", "merge-approval", "doc-review"],
  "workflows": [{"type": "UwePrImplementationWorkflow", "version": 21, "sha256": "<hex>"}],
  "expiresAt": "2026-11-01T00:00:00Z"
}`

const JSON_HEADERS = { 'Content-Type': 'application/json' }

async function readBody(res: Response): Promise<{ body: unknown; text: string }> {
  const text = await res.text()
  try {
    return { body: JSON.parse(text), text }
  } catch {
    return { body: null, text }
  }
}

function formatWhen(iso: string | null | undefined): string {
  if (!iso) return '—'
  const ms = Date.parse(iso)
  return Number.isNaN(ms) ? iso : new Date(ms).toLocaleString()
}

function isRunning(status: string): boolean {
  return status.toLowerCase().includes('running')
}

function ScopeReport({ report, stale }: { report: ScopeValidationReport; stale: boolean }) {
  return (
    <div className={`eg-report${stale ? ' eg-report-stale' : ''}`}>
      {stale && (
        <div className="wfd-validation-item warning">This report is for an earlier version of the text.</div>
      )}
      {report.errors.length > 0 ? (
        <div className="wfd-validation-bar">
          {report.errors.map((e, i) => <div key={i} className="wfd-validation-item blocking">{e}</div>)}
        </div>
      ) : report.valid ? (
        <div className="wfd-validation-ok">Scope is valid.</div>
      ) : (
        <div className="wfd-validation-item blocking">Scope is not valid.</div>
      )}

      <div className="eg-report-grid">
        <div className="eg-report-label">Scope SHA-256</div>
        <div className="eg-mono" title={report.scopeSha256 ?? ''}>{shortHash(report.scopeSha256) || '—'}</div>
        <div className="eg-report-label">Expires</div>
        <div title={report.expiresAt ?? ''}>{formatWhen(report.expiresAt)}</div>
        <div className="eg-report-label">Driver run</div>
        <div>
          {report.driver ? (
            <span className="eg-mono">
              {report.driver.namespace} / {report.driver.workflowId} / {report.driver.runId}{' '}
              <span className={isRunning(report.driver.status) ? 'eg-ok' : 'eg-bad'}>{report.driver.status || 'unknown'}</span>
            </span>
          ) : '—'}
        </div>
      </div>

      <div className="eg-subtitle">Pinned workflows</div>
      {report.workflows.length === 0 ? <div className="eg-muted">none</div> : (
        <table className="eg-table">
          <thead>
            <tr><th>Type</th><th>Version</th><th>SHA-256</th><th>Hash</th><th>Delegation</th><th>Gates</th></tr>
          </thead>
          <tbody>
            {report.workflows.map((w, i) => (
              <tr key={i} className={!w.hashMatches || !w.delegationCapable ? 'eg-row-bad' : undefined}>
                <td>{w.type}</td>
                <td>{w.version}</td>
                <td className="eg-mono" title={w.sha256}>{shortHash(w.sha256)}</td>
                <td className={w.hashMatches ? 'eg-ok' : 'eg-bad'}>{w.hashMatches ? 'match' : 'MISMATCH'}</td>
                <td className={w.delegationCapable ? 'eg-ok' : 'eg-bad'}>{w.delegationCapable ? 'capable' : 'NOT capable'}</td>
                <td>{w.gates.length ? w.gates.join(', ') : '—'}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}

      <div className="eg-subtitle">Targets</div>
      {report.targets.length === 0 ? <div className="eg-muted">none</div> : (
        <table className="eg-table">
          <thead>
            <tr><th>Repo</th><th>Issues</th><th>Visibility</th><th>allowPublic</th><th>Deny list</th></tr>
          </thead>
          <tbody>
            {report.targets.map((t, i) => (
              <tr key={i} className={t.denied ? 'eg-row-bad' : isPublicTarget(t) ? 'eg-row-public' : undefined}>
                <td className="eg-mono">{t.repo}</td>
                <td>{t.issues.map(n => `#${n}`).join(', ') || '—'}</td>
                <td>
                  {isPublicTarget(t)
                    ? <span className="eg-badge eg-badge-public">PUBLIC</span>
                    : t.visibility === 'unknown'
                      ? <span className="eg-badge eg-badge-bad">unknown</span>
                      : <span className="eg-muted">private</span>}
                </td>
                <td>{t.allowPublic ? 'yes' : 'no'}</td>
                <td>{t.denied ? <span className="eg-badge eg-badge-bad">DENIED</span> : <span className="eg-muted">no</span>}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </div>
  )
}

function GrantStatusBadge({ status }: { status: EpicGrantView['effectiveStatus'] }) {
  return <span className={`eg-badge eg-status-${status}`}>{status}</span>
}

export default function EpicGrantsView() {
  // ── Create form ──
  const [text, setText] = useState('')
  const [report, setReport] = useState<ScopeValidationReport | null>(null)
  const [validatedText, setValidatedText] = useState<string | null>(null)
  const [formError, setFormError] = useState<string | null>(null)
  const [validating, setValidating] = useState(false)
  const [createState, setCreateState] = useState<'idle' | 'pending' | 'success' | 'error'>('idle')
  const [createMsg, setCreateMsg] = useState('')

  // ── List and detail ──
  const [grants, setGrants] = useState<EpicGrantView[]>([])
  const [listLoading, setListLoading] = useState(true)
  const [listError, setListError] = useState<string | null>(null)
  const [selectedId, setSelectedId] = useState<string | null>(null)
  const [detail, setDetail] = useState<EpicGrantDetail | null>(null)
  const [detailLoading, setDetailLoading] = useState(false)
  const [detailError, setDetailError] = useState<string | null>(null)
  const latestDetailId = useRef<string | null>(null)

  // ── Revoke ──
  const [revokeFor, setRevokeFor] = useState<string | null>(null)
  const [revokeReason, setRevokeReason] = useState('')
  const [revokePending, setRevokePending] = useState(false)
  const [revokeMsg, setRevokeMsg] = useState<Record<string, string>>({})

  // "Delivery unknown" is age-based, so the table re-renders while a reserved row is on screen.
  const [now, setNow] = useState(() => Date.now())
  const hasReserved = detail?.decisions.some(d => d.status === 'reserved') ?? false
  useEffect(() => {
    if (!hasReserved) return
    const timer = setInterval(() => setNow(Date.now()), 5000)
    return () => clearInterval(timer)
  }, [hasReserved])

  const loadList = useCallback(async () => {
    setListLoading(true)
    try {
      const res = await apiFetch('/api/epic-grants')
      const { body, text: raw } = await readBody(res)
      if (!res.ok || !Array.isArray(body)) {
        setListError(responseErrorMessage(res.status, body, raw))
        setGrants([])
      } else {
        setListError(null)
        setGrants(body as EpicGrantView[])
      }
    } catch (e) {
      setListError(String(e))
      setGrants([])
    } finally {
      setListLoading(false)
    }
  }, [])

  useEffect(() => { loadList() }, [loadList])

  async function loadDetail(id: string) {
    latestDetailId.current = id
    setSelectedId(id)
    setDetailLoading(true)
    setDetailError(null)
    try {
      const res = await apiFetch(`/api/epic-grants/${encodeURIComponent(id)}`)
      const { body, text: raw } = await readBody(res)
      // A slower response for a row clicked earlier must not replace the row clicked last.
      if (latestDetailId.current !== id) return
      if (!res.ok || !body || typeof body !== 'object') {
        setDetail(null)
        setDetailError(responseErrorMessage(res.status, body, raw))
      } else {
        setDetail(body as EpicGrantDetail)
      }
    } catch (e) {
      if (latestDetailId.current !== id) return
      setDetail(null)
      setDetailError(String(e))
    } finally {
      if (latestDetailId.current === id) {
        setNow(Date.now())
        setDetailLoading(false)
      }
    }
  }

  async function handleValidate() {
    const textNow = text
    const parsed = parseScopeText(textNow)
    setCreateState('idle')
    if (!parsed.ok) {
      setReport(null)
      setValidatedText(null)
      setFormError(parsed.error)
      return
    }
    setFormError(null)
    setValidating(true)
    try {
      const res = await apiFetch('/api/epic-grants/validate', {
        method: 'POST', headers: JSON_HEADERS, body: scopeRequestBody(parsed.scope),
      })
      const { body, text: raw } = await readBody(res)
      const r = res.ok ? asValidationReport(body) : null
      if (r) {
        setReport(r)
        setValidatedText(textNow)
      } else {
        setReport(null)
        setValidatedText(null)
        setFormError(responseErrorMessage(res.status, body, raw))
      }
    } catch (e) {
      setReport(null)
      setValidatedText(null)
      setFormError(String(e))
    } finally {
      setValidating(false)
    }
  }

  async function handleCreate() {
    const textNow = text
    if (createBlockedReason(report, validatedText, textNow) !== null) return
    const parsed = parseScopeText(textNow)
    if (!parsed.ok) return
    setCreateState('pending')
    try {
      const res = await apiFetch('/api/epic-grants', {
        method: 'POST', headers: JSON_HEADERS, body: scopeRequestBody(parsed.scope),
      })
      const { body, text: raw } = await readBody(res)
      if (res.ok) {
        const created = body as EpicGrantDetail | null
        setCreateState('success')
        setCreateMsg(created?.id ? `Created grant ${created.id}` : 'Created')
        setText('')
        setReport(null)
        setValidatedText(null)
        setFormError(null)
        await loadList()
        if (created?.id) {
          latestDetailId.current = created.id
          setSelectedId(created.id)
          setDetailLoading(false)
          setDetail(created)
          setDetailError(null)
          setNow(Date.now())
        }
        return
      }
      const rejected = res.status === 400 ? asValidationReport(body) : null
      if (rejected) {
        // The orchestrator's report replaces ours; it is `valid: false`, so Create stays disabled.
        setReport(rejected)
        setValidatedText(textNow)
        setCreateMsg('Not created: the orchestrator rejected the scope. See the report.')
      } else {
        setCreateMsg(responseErrorMessage(res.status, body, raw))
      }
      setCreateState('error')
    } catch (e) {
      setCreateState('error')
      setCreateMsg(String(e))
    }
  }

  function openRevoke(id: string) {
    setRevokeFor(id)
    setRevokeReason('')
    setRevokeMsg(prev => ({ ...prev, [id]: '' }))
  }

  async function handleRevokeConfirm(id: string) {
    setRevokePending(true)
    try {
      const res = await apiFetch(`/api/epic-grants/${encodeURIComponent(id)}/revoke`, {
        method: 'POST', headers: JSON_HEADERS, body: JSON.stringify(revokeRequestBody(revokeReason)),
      })
      const { body, text: raw } = await readBody(res)
      if (res.ok && body && typeof body === 'object') {
        const updated = body as EpicGrantView
        setGrants(prev => applyRevoke(prev, updated))
        setDetail(prev => applyRevokeToDetail(prev, updated))
        setRevokeFor(null)
        setRevokeMsg(prev => ({ ...prev, [id]: '' }))
      } else {
        const msg = res.status === 409 ? 'Already revoked.'
          : res.status === 404 ? 'Unknown grant.'
          : responseErrorMessage(res.status, body, raw)
        setRevokeMsg(prev => ({ ...prev, [id]: msg }))
        if (res.status === 409 || res.status === 404) {
          setRevokeFor(null)
          loadList()
        }
      }
    } catch (e) {
      setRevokeMsg(prev => ({ ...prev, [id]: String(e) }))
    } finally {
      setRevokePending(false)
    }
  }

  const blockedReason = createBlockedReason(report, validatedText, text)
  const reportStale = report !== null && validatedText !== text

  return (
    <div className="view-page">
      <div className="view-page-header">
        <h1 className="view-page-title">
          Epic grants
          {grants.length > 0 && (
            <span className="section-count">
              {grants.filter(g => g.effectiveStatus === 'active').length} active · {grants.length} total
            </span>
          )}
        </h1>
        <button className="view-page-action" onClick={() => loadList()}>Refresh</button>
      </div>

      <div className="wfd-new-form">
        <div className="wfd-new-form-title">New grant</div>
        <div className="wfd-new-form-fields">
          <div className="wfd-form-row">
            <label className="config-label">Scope JSON <span className="wfd-required">*</span></label>
            <FieldHint>
              One driver run, exact target issues, pinned workflow definitions, gates and an expiry.
              The scope is immutable: to change it, revoke the grant and create a new one.
            </FieldHint>
            <textarea
              className="instr-editor"
              rows={14}
              value={text}
              placeholder={SCOPE_PLACEHOLDER}
              onChange={e => { setText(e.target.value); setCreateState('idle') }}
              spellCheck={false}
            />
            {formError && <div className="wfd-field-error">{formError}</div>}
          </div>
        </div>
        {report && <ScopeReport report={report} stale={reportStale} />}
        <div className="wfd-new-form-actions">
          <button className="wfd-cancel-btn" disabled={validating} onClick={handleValidate}>
            {validating ? 'Validating…' : 'Validate'}
          </button>
          <button
            className="config-save-btn"
            disabled={blockedReason !== null || createState === 'pending' || validating}
            title={blockedReason ?? undefined}
            onClick={handleCreate}
          >
            {createState === 'pending' ? '…' : 'Create'}
          </button>
          {blockedReason && text.trim() !== '' && createState !== 'error' && (
            <span className="eg-muted">{blockedReason}</span>
          )}
          {(createState === 'success' || createState === 'error') && (
            <span className={`config-feedback config-feedback-${createState}`}>{createMsg}</span>
          )}
        </div>
      </div>

      {listLoading && grants.length === 0 && <div className="view-empty">Loading…</div>}
      {listError && <div className="wfd-validation-item blocking">{listError}</div>}
      {!listLoading && !listError && grants.length === 0 && <div className="view-empty">No epic grants.</div>}

      <div className="instructions-list">
        {grants.map(g => {
          const selected = g.id === selectedId
          const confirming = revokeFor === g.id
          const msg = revokeMsg[g.id] ?? ''
          return (
            <div key={g.id} className={`instr-row${selected ? ' eg-selected' : ''}${g.effectiveStatus !== 'active' ? ' wfd-row-inactive' : ''}`}>
              <div className="instr-header" onClick={() => loadDetail(g.id)}>
                <span className="instr-name eg-mono">{g.id}</span>
                <GrantStatusBadge status={g.effectiveStatus} />
                <span className="instr-meta">
                  <span className="instr-total eg-mono" title={`${g.driverNamespace} / ${g.driverWorkflowId} / ${g.driverRunId}`}>
                    {g.driverNamespace} / {g.driverWorkflowId}
                  </span>
                  <span className="instr-total" title={g.expiresAt}>expires {formatWhen(g.expiresAt)}</span>
                  <span className="instr-total" title={g.createdAt}>created {formatWhen(g.createdAt)}</span>
                </span>
                <div className="wfd-row-actions" onClick={e => e.stopPropagation()}>
                  {canRevoke(g) && !confirming && (
                    <button className="wfd-toggle-btn" onClick={() => openRevoke(g.id)}>revoke</button>
                  )}
                  {msg && <span className="config-feedback config-feedback-error">{msg}</span>}
                </div>
              </div>
              {confirming && (
                <div className="eg-revoke-confirm" onClick={e => e.stopPropagation()}>
                  <span>Revoke this grant? Decisions not yet reserved are refused from then on.</span>
                  <input
                    className="config-input"
                    placeholder="Reason (optional)"
                    value={revokeReason}
                    onChange={e => setRevokeReason(e.target.value)}
                  />
                  <button
                    className="wfd-toggle-btn confirming"
                    disabled={revokePending}
                    onClick={() => handleRevokeConfirm(g.id)}
                  >
                    {revokePending ? '…' : 'confirm revoke'}
                  </button>
                  <button className="wfd-cancel-btn" disabled={revokePending} onClick={() => setRevokeFor(null)}>Cancel</button>
                </div>
              )}
              {selected && (
                <div className="eg-detail">
                  {detailLoading && <div className="eg-muted">Loading…</div>}
                  {detailError && <div className="wfd-validation-item blocking">{detailError}</div>}
                  {detail && detail.id === g.id && !detailLoading && (
                    <>
                      <div className="eg-report-grid">
                        <div className="eg-report-label">Scope SHA-256</div>
                        <div className="eg-mono" title={detail.scopeSha256}>{shortHash(detail.scopeSha256)}</div>
                        <div className="eg-report-label">Driver run</div>
                        <div className="eg-mono">{detail.driverNamespace} / {detail.driverWorkflowId} / {detail.driverRunId}</div>
                        <div className="eg-report-label">CTO agent</div>
                        <div>{detail.ctoAgent}</div>
                        {detail.revokedAt && (
                          <>
                            <div className="eg-report-label">Revoked</div>
                            <div>{formatWhen(detail.revokedAt)}{detail.revokeReason ? ` — ${detail.revokeReason}` : ''}</div>
                          </>
                        )}
                      </div>
                      <div className="eg-subtitle">
                        Decisions
                        <button className="wfd-toggle-btn eg-inline-btn" onClick={() => loadDetail(g.id)}>reload</button>
                      </div>
                      {detail.decisions.length === 0 ? <div className="eg-muted">No decisions.</div> : (
                        <table className="eg-table">
                          <thead>
                            <tr><th>Gate</th><th>Workflow</th><th>Run</th><th>Visit</th><th>Artifact</th><th>Status</th><th>Created</th></tr>
                          </thead>
                          <tbody>
                            {detail.decisions.map(d => {
                              const unknown = isDeliveryUnknown(d, now)
                              const bad = unknown || d.status === 'send_failed'
                              return (
                                <tr key={d.id} className={bad ? 'eg-row-bad' : undefined}>
                                  <td>{d.gate}</td>
                                  <td className="eg-mono">{d.workflowId}</td>
                                  <td className="eg-mono">{d.runId}</td>
                                  <td className="eg-mono">{d.visitId}</td>
                                  <td className="eg-mono" title={d.artifactRef}>{shortHash(d.artifactRef)}</td>
                                  <td className={bad ? 'eg-bad' : d.status === 'sent' ? 'eg-ok' : undefined}>{statusLabel(d, now)}</td>
                                  <td title={d.createdAt}>{formatWhen(d.createdAt)}</td>
                                </tr>
                              )
                            })}
                          </tbody>
                        </table>
                      )}
                    </>
                  )}
                </div>
              )}
            </div>
          )
        })}
      </div>
    </div>
  )
}
