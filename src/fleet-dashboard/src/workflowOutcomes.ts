import type {
  WorkflowOutcomeBatchMembership, WorkflowOutcomeCounts, WorkflowOutcomeCoverage, WorkflowOutcomeCoverageGap,
  WorkflowOutcomeDecisionRequest, WorkflowOutcomeDecisionView, WorkflowOutcomeDetail, WorkflowOutcomeHealth,
  WorkflowOutcomeItemRef, WorkflowOutcomeListPage, WorkflowOutcomeListState, WorkflowOutcomeNamespaceCoverage,
  WorkflowOutcomeObservationView, WorkflowOutcomeOperation, WorkflowOutcomeReceipt, WorkflowOutcomeAckRequest,
  WorkflowOutcomeView, WorkflowRecoveryPlanResult, WorkflowOutcomeHealthIncident,
} from './types'

// Workflow failure accountability view logic (#476). Free of `import.meta.env` and JSX so
// `node --test` loads it directly (see workflowOutcomes.test.ts); WorkflowOutcomesPanel.tsx only
// renders what these return. The orchestrator is the final validator: the checks here mirror its
// limits so an operator sees the refusal before sending, never instead of the server's.

// ── Limits mirrored from Fleet.Shared.WorkflowOutcomeLimits ──────────────────────

export const MAX_BATCH_ITEMS = 20
export const MAX_OWNER_CHARS = 200
export const MAX_REASON_CHARS = 2000
export const MAX_EVIDENCE_REFS = 10
export const MAX_EVIDENCE_REF_CHARS = 500
export const MAX_PAGE_LIMIT = 100
/** Always sent explicitly so the server never applies an unseen default page size. */
export const DEFAULT_PAGE_LIMIT = 50
export const EVIDENCE_REF_PREFIXES = ['outcome:', 'run:', 'event:', 'task:', 'artifact:', 'late-result:', 'decision:', 'operation:', 'https://']

// C# char.IsControl: U+0000–U+001F and U+007F–U+009F.
// eslint-disable-next-line no-control-regex
const CONTROL_CHARS = /[\u0000-\u001f\u007f-\u009f]/

/** Same rule as `WorkflowOutcomeLimits.IsEvidenceRef`. */
export function isEvidenceRef(value: string | null | undefined): boolean {
  if (typeof value !== 'string' || value.trim() === '' || value.length > MAX_EVIDENCE_REF_CHARS) return false
  if (CONTROL_CHARS.test(value)) return false
  if (value !== value.trim()) return false
  return EVIDENCE_REF_PREFIXES.some(p => value.startsWith(p) && value.length > p.length)
}

// ── Labels ───────────────────────────────────────────────────────────────────────

export type CauseKind = 'transport' | 'business' | 'cause_review' | 'coverage' | 'other'

const REASON_LABELS: Record<string, { kind: CauseKind; label: string }> = {
  workflow_failed: { kind: 'transport', label: 'Workflow failed' },
  workflow_timed_out: { kind: 'transport', label: 'Workflow timed out' },
  failure_caused_cancel: { kind: 'transport', label: 'Cancel caused by a failure' },
  failure_caused_terminate: { kind: 'transport', label: 'Terminate caused by a failure' },
  business_task_failed: { kind: 'business', label: 'Task failed' },
  business_deadline: { kind: 'business', label: 'Deadline missed' },
  business_review_cap: { kind: 'business', label: 'Review cap reached' },
  business_artifact_missing: { kind: 'business', label: 'Artifact missing' },
  abort_cause_unknown: { kind: 'cause_review', label: 'Abort cause unknown' },
  abort_cause_conflicting: { kind: 'cause_review', label: 'Abort cause conflicting' },
  null_result_only: { kind: 'cause_review', label: 'Null result only' },
  correlation_unverified: { kind: 'cause_review', label: 'Correlation unverified' },
  classifier_missing: { kind: 'coverage', label: 'Classifier missing' },
  history_incomplete: { kind: 'coverage', label: 'History incomplete' },
}

const CAUSE_KIND_LABELS: Record<CauseKind, string> = {
  transport: 'Transport failure',
  business: 'Business failure',
  cause_review: 'Cause review',
  coverage: 'Coverage gap',
  other: 'Failure',
}

const CLASSIFICATION_LABELS: Record<string, string> = {
  confirmed_failure: 'Confirmed failure',
  cause_unresolved: 'Cause unresolved',
  insufficient_evidence: 'Insufficient evidence',
  classification_pending: 'Classification pending',
  intentional_nonfailure: 'Not a failure (intentional)',
  excluded_nonfailure: 'Not a failure (excluded)',
}

export function classificationLabel(classification: string): string {
  return CLASSIFICATION_LABELS[classification] ?? classification
}

/** Transport vs business cause, from the classification and reason code; the raw code is kept. */
export function causeLabel(classification: string, reasonCode: string | null): { kind: CauseKind; kindLabel: string; label: string; code: string } {
  const known = reasonCode ? REASON_LABELS[reasonCode] : undefined
  let kind: CauseKind = known?.kind ?? 'other'
  if (!known) {
    if (classification === 'cause_unresolved' || classification === 'insufficient_evidence') kind = 'cause_review'
    else if (classification === 'classification_pending') kind = 'coverage'
  }
  return {
    kind,
    kindLabel: CAUSE_KIND_LABELS[kind],
    label: known?.label ?? (reasonCode || 'no reason code'),
    code: reasonCode ?? '',
  }
}

/** First `length` characters of an id, with an ellipsis when it was cut. */
export function shortId(value: string | null | undefined, length = 12): string {
  if (!value) return ''
  return value.length <= length ? value : `${value.slice(0, length)}…`
}

// ── Stages ───────────────────────────────────────────────────────────────────────

export type StageKey = 'stored' | 'enqueued' | 'delivered' | 'acknowledged' | 'decision_made' | 'unresolved'
export type StageState = 'done' | 'pending' | 'failed' | 'unknown' | 'attention' | 'not_reached'

export interface StageRow {
  key: StageKey
  label: string
  at: string | null
  state: StageState
  note: string
}

export const STAGE_LABELS: Record<StageKey, string> = {
  stored: 'Stored',
  enqueued: 'Enqueued',
  delivered: 'Delivered',
  acknowledged: 'Acknowledged',
  decision_made: 'Decision made',
  unresolved: 'Unresolved / escalated',
}

const STAGE_ORDER: StageKey[] = ['stored', 'enqueued', 'delivered', 'acknowledged', 'decision_made', 'unresolved']

const CURRENT_STAGE_LABELS: Record<string, string> = {
  detected: 'Detected',
  stored: 'Stored',
  enqueued: 'Enqueued',
  delivered: 'Delivered',
  acknowledged: 'Acknowledged',
  decision_made: 'Decision made',
  unresolved: 'Unresolved',
  escalated: 'Escalated',
}

export function currentStageLabel(stage: string): string {
  return CURRENT_STAGE_LABELS[stage] ?? `Unknown stage "${stage}"`
}

/** True when delivery has not happened and the item has gone overdue or was escalated. */
function deliveryFailed(view: WorkflowOutcomeView): boolean {
  if (view.stages.deliveredAt) return false
  if (view.deliveryState !== 'none' && view.deliveryState !== 'pending') return false
  return view.stage === 'escalated' || view.attentionOverdue
}

export type DeliveryVisual = 'not_enqueued' | 'pending' | 'failed' | 'unknown' | 'delivered' | 'acknowledged'

/**
 * Delivery state as shown: pending, failed and unknown are distinct, and an unrecognised server
 * value is shown as unknown — never as delivered.
 */
export function deliveryVisual(view: WorkflowOutcomeView): { state: DeliveryVisual; label: string } {
  if (view.deliveryState === 'unknown') return { state: 'unknown', label: 'Delivery unknown (receipt ambiguous)' }
  if (view.deliveryState === 'acknowledged') return { state: 'acknowledged', label: 'Acknowledged (not an approval)' }
  if (view.deliveryState === 'delivered') return { state: 'delivered', label: 'Delivered, awaiting acknowledgement' }
  if (view.deliveryState === 'none' || view.deliveryState === 'pending') {
    if (deliveryFailed(view)) return { state: 'failed', label: 'Delivery failed / overdue' }
    return view.deliveryState === 'pending'
      ? { state: 'pending', label: 'Delivery pending' }
      : { state: 'not_enqueued', label: 'Not enqueued yet' }
  }
  return { state: 'unknown', label: `Delivery state "${view.deliveryState}" not recognised` }
}

/** All six handoff stages in order, each with its own timestamp and visual state. */
export function stageRows(view: WorkflowOutcomeView): StageRow[] {
  const s = view.stages
  const rows: Record<StageKey, Omit<StageRow, 'key' | 'label'>> = {
    stored: s.storedAt
      ? { at: s.storedAt, state: 'done', note: '' }
      : { at: null, state: 'pending', note: 'not durably stored yet' },
    enqueued: s.enqueuedAt
      ? { at: s.enqueuedAt, state: 'done', note: '' }
      : { at: null, state: s.storedAt ? 'pending' : 'not_reached', note: s.storedAt ? 'waiting for a batch' : '' },
    delivered: (() => {
      if (s.deliveredAt) return { at: s.deliveredAt, state: 'done' as StageState, note: 'coordinator fsynced receipt' }
      const d = deliveryVisual(view)
      if (d.state === 'unknown') return { at: null, state: 'unknown' as StageState, note: d.label }
      if (d.state === 'failed') return { at: null, state: 'failed' as StageState, note: d.label }
      return { at: null, state: (s.enqueuedAt ? 'pending' : 'not_reached') as StageState, note: s.enqueuedAt ? 'delivery pending' : '' }
    })(),
    acknowledged: s.acknowledgedAt
      ? { at: s.acknowledgedAt, state: 'done', note: 'not an approval' }
      : view.turnAmbiguous
        ? { at: null, state: 'unknown', note: 'coordinator turn ambiguous — no acknowledgement inferred' }
        : { at: null, state: s.deliveredAt ? 'pending' : 'not_reached', note: s.deliveredAt ? 'awaiting explicit acknowledgement' : '' },
    decision_made: s.decisionMadeAt
      ? { at: s.decisionMadeAt, state: 'done', note: view.lastDecision ? `${view.lastDecision.operation} by ${view.lastDecision.owner}` : '' }
      : { at: null, state: s.acknowledgedAt ? 'pending' : 'not_reached', note: s.acknowledgedAt ? 'awaiting a per-run decision' : '' },
    unresolved: (() => {
      if (view.stage === 'escalated') return { at: null, state: 'failed' as StageState, note: 'escalated' }
      if (view.attentionOverdue) return { at: null, state: 'failed' as StageState, note: 'attention overdue' }
      if (view.stage === 'unresolved') return { at: null, state: 'attention' as StageState, note: 'unresolved' }
      const op = view.lastDecision?.operation
      if (op === 'wait' || op === 'hold')
        return { at: null, state: 'attention' as StageState, note: `${op} until ${view.lastDecision?.dueAt ?? 'unknown'}` }
      return { at: null, state: 'not_reached' as StageState, note: '' }
    })(),
  }
  return STAGE_ORDER.map(key => ({ key, label: STAGE_LABELS[key], ...rows[key] }))
}

// ── Due time ─────────────────────────────────────────────────────────────────────

function formatSpan(ms: number): string {
  const mins = Math.round(ms / 60_000)
  if (mins < 60) return `${mins}m`
  const hours = Math.floor(mins / 60)
  if (hours < 48) return `${hours}h ${mins % 60}m`
  return `${Math.floor(hours / 24)}d`
}

export function dueLabel(dueAt: string | null, attentionOverdue: boolean, nowMs: number): { text: string; overdue: boolean } {
  if (!dueAt) return attentionOverdue ? { text: 'attention overdue', overdue: true } : { text: 'no due time', overdue: false }
  const dueMs = Date.parse(dueAt)
  if (Number.isNaN(dueMs)) return { text: `due ${dueAt} (unreadable)`, overdue: true }
  if (dueMs <= nowMs) return { text: `overdue by ${formatSpan(nowMs - dueMs)}`, overdue: true }
  return { text: `due in ${formatSpan(dueMs - nowMs)}`, overdue: attentionOverdue }
}

// ── Response parsing (a malformed body is an error, never an empty list) ─────────

type Obj = Record<string, unknown>

function isObj(v: unknown): v is Obj {
  return v !== null && typeof v === 'object' && !Array.isArray(v)
}
function str(v: unknown): string | null {
  return typeof v === 'string' ? v : null
}
function num(v: unknown): number | null {
  return typeof v === 'number' && Number.isFinite(v) ? v : null
}
function strList(v: unknown): string[] {
  return Array.isArray(v) ? v.filter((x): x is string => typeof x === 'string') : []
}
function refList(v: unknown): WorkflowOutcomeItemRef[] | null {
  if (!Array.isArray(v)) return null
  const out: WorkflowOutcomeItemRef[] = []
  for (const r of v) {
    if (!isObj(r) || typeof r.id !== 'string' || num(r.version) === null) return null
    out.push({ id: r.id, version: r.version as number })
  }
  return out
}

export function asOutcomeView(v: unknown): WorkflowOutcomeView | null {
  if (!isObj(v)) return null
  const id = str(v.id), version = num(v.version)
  if (id === null || version === null) return null
  for (const k of ['namespace', 'workflowId', 'runId', 'classification'] as const) if (typeof v[k] !== 'string') return null
  const stages = isObj(v.stages) ? v.stages : {}
  const lastDecision = isObj(v.lastDecision) ? {
    operation: str(v.lastDecision.operation) ?? '',
    owner: str(v.lastDecision.owner) ?? '',
    reason: str(v.lastDecision.reason) ?? '',
    dueAt: str(v.lastDecision.dueAt),
    status: str(v.lastDecision.status) ?? '',
    decidedVia: str(v.lastDecision.decidedVia) ?? '',
    createdAt: str(v.lastDecision.createdAt) ?? '',
  } : null
  const recovery = isObj(v.recovery) && typeof v.recovery.operationId === 'string'
    ? { operationId: v.recovery.operationId, state: str(v.recovery.state) ?? 'unknown' }
    : null
  return {
    id, version,
    namespace: v.namespace as string,
    workflowId: v.workflowId as string,
    runId: v.runId as string,
    workflowType: str(v.workflowType) ?? '',
    status: str(v.status) ?? '',
    classification: v.classification as string,
    reasonCode: str(v.reasonCode),
    terminalEventType: str(v.terminalEventType),
    terminalEventId: num(v.terminalEventId),
    startTime: str(v.startTime),
    closeTime: str(v.closeTime),
    project: str(v.project),
    accessNeeded: v.accessNeeded === true,
    coordinator: str(v.coordinator),
    taskOwner: str(v.taskOwner),
    evidenceState: str(v.evidenceState),
    failureIncidentId: str(v.failureIncidentId),
    parentWorkflowId: str(v.parentWorkflowId),
    parentRunId: str(v.parentRunId),
    previousRunId: str(v.previousRunId),
    nextRunId: str(v.nextRunId),
    stage: str(v.stage) ?? 'unknown',
    stages: {
      storedAt: str(stages.storedAt),
      enqueuedAt: str(stages.enqueuedAt),
      deliveredAt: str(stages.deliveredAt),
      acknowledgedAt: str(stages.acknowledgedAt),
      decisionMadeAt: str(stages.decisionMadeAt),
    },
    // A missing value is unknown, not "none": nothing is inferred about delivery.
    deliveryState: str(v.deliveryState) ?? 'unknown',
    turnAmbiguous: v.turnAmbiguous === true,
    lastDecision,
    recovery,
    dueAt: str(v.dueAt),
    attentionOverdue: v.attentionOverdue === true,
  }
}

function asCounts(v: unknown): WorkflowOutcomeCounts | null {
  if (!isObj(v)) return null
  const failures = num(v.failures), unresolvedCauses = num(v.unresolvedCauses), coverageGaps = num(v.coverageGaps)
  if (failures === null || unresolvedCauses === null || coverageGaps === null) return null
  return { failures, unresolvedCauses, coverageGaps }
}

export function asListPage(v: unknown): WorkflowOutcomeListPage | null {
  if (!isObj(v) || !Array.isArray(v.items) || !Array.isArray(v.namespaceErrors)) return null
  if (v.nextCursor !== null && v.nextCursor !== undefined && typeof v.nextCursor !== 'string') return null
  const counts = asCounts(v.counts)
  if (!counts) return null
  const items: WorkflowOutcomeView[] = []
  for (const raw of v.items) {
    const item = asOutcomeView(raw)
    if (!item) return null
    items.push(item)
  }
  const namespaceErrors: { namespace: string; error: string }[] = []
  for (const e of v.namespaceErrors) {
    if (!isObj(e)) return null
    namespaceErrors.push({ namespace: str(e.namespace) ?? '(unknown namespace)', error: str(e.error) ?? 'error' })
  }
  return { items, nextCursor: typeof v.nextCursor === 'string' && v.nextCursor !== '' ? v.nextCursor : null, namespaceErrors, counts }
}

function asDecisionView(v: unknown): WorkflowOutcomeDecisionView | null {
  if (!isObj(v) || typeof v.operation !== 'string') return null
  return {
    decisionId: str(v.decisionId) ?? str(v.id),
    version: num(v.version),
    operation: v.operation,
    owner: str(v.owner) ?? '',
    reason: str(v.reason) ?? '',
    dueAt: str(v.dueAt),
    status: str(v.status) ?? '',
    decidedVia: str(v.decidedVia) ?? '',
    createdAt: str(v.createdAt) ?? '',
    evidenceRefs: strList(v.evidenceRefs),
  }
}

function asBatchMembership(v: unknown): WorkflowOutcomeBatchMembership | null {
  if (!isObj(v) || typeof v.batchId !== 'string' || num(v.generation) === null) return null
  return {
    batchId: v.batchId,
    generation: v.generation as number,
    state: str(v.state) ?? 'unknown',
    deliveredAt: str(v.deliveredAt),
    ackedAt: str(v.ackedAt),
    nonce: str(v.nonce),
    version: num(v.version),
  }
}

function asObservation(v: unknown): WorkflowOutcomeObservationView | null {
  if (!isObj(v)) return null
  return {
    eventId: num(v.eventId),
    eventType: str(v.eventType) ?? '',
    classification: str(v.classification),
    reasonCode: str(v.reasonCode),
    observedAt: str(v.observedAt),
  }
}

function listOf<T>(v: unknown, parse: (x: unknown) => T | null): T[] | null {
  if (!Array.isArray(v)) return null
  const out: T[] = []
  for (const x of v) {
    const p = parse(x)
    if (p === null) return null
    out.push(p)
  }
  return out
}

export function asOutcomeDetail(v: unknown): WorkflowOutcomeDetail | null {
  const view = asOutcomeView(v)
  if (!view || !isObj(v)) return null
  const decisions = listOf(v.decisions ?? [], asDecisionView)
  const batches = listOf(v.batches ?? [], asBatchMembership)
  const observations = listOf(v.observations ?? [], asObservation)
  if (!decisions || !batches || !observations) return null
  return { ...view, decisions, batches, observations, evidenceRefs: strList(v.evidenceRefs) }
}

function asGap(v: unknown): WorkflowOutcomeCoverageGap | null {
  if (!isObj(v)) return null
  return { start: str(v.start), end: str(v.end), reason: str(v.reason) ?? 'unknown', unknownRunCount: num(v.unknownRunCount) }
}

function asNamespaceCoverage(v: unknown): WorkflowOutcomeNamespaceCoverage | null {
  if (!isObj(v) || typeof v.namespace !== 'string') return null
  const gaps = listOf(v.gaps ?? [], asGap)
  if (!gaps) return null
  return {
    namespace: v.namespace,
    state: str(v.state) ?? 'unknown',
    retentionSeconds: num(v.retentionSeconds),
    enrolledAt: str(v.enrolledAt),
    lastRecentPassAt: str(v.lastRecentPassAt),
    lastFullPassAt: str(v.lastFullPassAt),
    knownPending: num(v.knownPending) ?? 0,
    gaps,
  }
}

export function asCoverage(v: unknown): WorkflowOutcomeCoverage | null {
  if (!isObj(v) || typeof v.enabled !== 'boolean' || typeof v.degraded !== 'boolean') return null
  const namespaces = listOf(v.namespaces, asNamespaceCoverage)
  if (!namespaces) return null
  return { enabled: v.enabled, degraded: v.degraded, namespaces }
}

function asIncident(v: unknown): WorkflowOutcomeHealthIncident | null {
  if (!isObj(v)) return null
  return {
    component: str(v.component) ?? '',
    outageKey: str(v.outageKey) ?? '',
    state: str(v.state) ?? '',
    reason: str(v.reason) ?? '',
    namespace: str(v.namespace),
    firstSeenAt: str(v.firstSeenAt),
    lastSeenAt: str(v.lastSeenAt),
  }
}

export function asHealth(v: unknown): WorkflowOutcomeHealth | null {
  if (!isObj(v) || typeof v.state !== 'string') return null
  const incidents = listOf(v.incidents ?? [], asIncident)
  if (!incidents) return null
  return { state: v.state, incidents }
}

export function asReceipt(v: unknown): WorkflowOutcomeReceipt | null {
  if (!isObj(v) || typeof v.batchId !== 'string' || num(v.generation) === null || typeof v.nonce !== 'string') return null
  const items = refList(v.items), deliveredVersions = refList(v.deliveredVersions), ackedVersions = refList(v.ackedVersions)
  if (!items || !deliveredVersions || !ackedVersions) return null
  return {
    batchId: v.batchId,
    generation: v.generation as number,
    nonce: v.nonce,
    itemVersionsHash: str(v.itemVersionsHash) ?? '',
    items, deliveredVersions, ackedVersions,
    receiptHash: str(v.receiptHash),
    deliveredAt: str(v.deliveredAt),
    acknowledgedAt: str(v.acknowledgedAt),
  }
}

export function asRecoveryPlan(v: unknown): WorkflowRecoveryPlanResult | null {
  if (!isObj(v) || typeof v.allowed !== 'boolean') return null
  return {
    allowed: v.allowed,
    operation: str(v.operation),
    blockedReason: str(v.blockedReason),
    missingProofs: strList(v.missingProofs),
    checkedAt: str(v.checkedAt),
  }
}

export function asDecisionAccepted(v: unknown): { decisionId: string; status: string } | null {
  if (!isObj(v) || typeof v.decisionId !== 'string') return null
  return { decisionId: v.decisionId, status: str(v.status) ?? '' }
}

// ── API client (the caller passes the bearer-carrying fetch; null means no bearer) ─

/** `apiFetch` from utils.tsx, or null when the dashboard has no bearer configured. */
export type OutcomeHttp = (url: string, init?: RequestInit) => Promise<Response>

export type ApiResult<T> = { ok: true; status: number; data: T } | { ok: false; status: number; error: string }

export const OUTCOMES_API = '/api/workflow-outcomes'
export const NO_BEARER_ERROR = 'The dashboard has no bearer token (VITE_AUTH_TOKEN); workflow failure routes require it. Nothing was requested.'
export const MALFORMED_ERROR = 'The orchestrator returned a response this dashboard cannot read. It is not treated as an empty list.'

/** A readable message for a failed request: the body's `error` string, else a status meaning. */
export function outcomeErrorMessage(status: number, body: unknown, text = ''): string {
  const serverError = isObj(body) && typeof body.error === 'string' && body.error.trim() !== '' ? body.error : null
  const meaning: Record<number, string> = {
    401: 'The dashboard bearer was refused (401).',
    403: 'This bearer may not read or change that item (403).',
    404: 'Not found (404). The orchestrator may not have workflow failure routes.',
    409: 'Stale (409): the item or batch changed. Reload and review the current version.',
    422: 'Refused as invalid or unsafe (422).',
    503: 'Workflow failure monitoring is disabled or its auth configuration is missing (503).',
  }
  const base = meaning[status] ?? `HTTP ${status}`
  if (serverError) return `${base} ${serverError}`
  const t = text.trim()
  return t && !meaning[status] ? `${base}: ${t.slice(0, 300)}` : base
}

async function requestJson<T>(
  http: OutcomeHttp | null, url: string, init: RequestInit | undefined, parse: (b: unknown) => T | null,
): Promise<ApiResult<T>> {
  if (!http) return { ok: false, status: 0, error: NO_BEARER_ERROR }
  let res: Response
  try {
    res = await http(url, init)
  } catch (e) {
    return { ok: false, status: 0, error: `Request failed: ${e instanceof Error ? e.message : String(e)}` }
  }
  let text = ''
  try { text = await res.text() } catch { /* body unreadable */ }
  let body: unknown = null
  try { body = text ? JSON.parse(text) : null } catch { body = null }
  if (!res.ok) return { ok: false, status: res.status, error: outcomeErrorMessage(res.status, body, text) }
  const data = parse(body)
  if (data === null) return { ok: false, status: res.status, error: MALFORMED_ERROR }
  return { ok: true, status: res.status, data }
}

function postInit(body: unknown): RequestInit {
  return { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) }
}

export interface ListQuery {
  state: WorkflowOutcomeListState
  namespace?: string | null
  cursor?: string | null
  limit?: number
}

export function listUrl(q: ListQuery): string {
  const p = new URLSearchParams()
  p.set('state', q.state)
  const limit = Math.min(MAX_PAGE_LIMIT, Math.max(1, Math.trunc(q.limit ?? DEFAULT_PAGE_LIMIT)))
  p.set('limit', String(limit))
  if (q.namespace) p.set('namespace', q.namespace)
  if (q.cursor) p.set('cursor', q.cursor)
  return `${OUTCOMES_API}?${p.toString()}`
}

/** Never `?detail=payload`: the dashboard does not request decrypted evidence. */
export function detailUrl(id: string): string {
  return `${OUTCOMES_API}/${encodeURIComponent(id)}`
}

export function fetchOutcomeList(http: OutcomeHttp | null, q: ListQuery) {
  return requestJson(http, listUrl(q), undefined, asListPage)
}
export function fetchOutcomeDetail(http: OutcomeHttp | null, id: string) {
  return requestJson(http, detailUrl(id), undefined, asOutcomeDetail)
}
export function fetchCoverage(http: OutcomeHttp | null) {
  return requestJson(http, `${OUTCOMES_API}/coverage`, undefined, asCoverage)
}
export function fetchHealth(http: OutcomeHttp | null) {
  return requestJson(http, `${OUTCOMES_API}/health`, undefined, asHealth)
}
export function fetchBatchReceipt(http: OutcomeHttp | null, batchId: string, nonce: string) {
  return requestJson(http, `${OUTCOMES_API}/batches/${encodeURIComponent(batchId)}/receipt?nonce=${encodeURIComponent(nonce)}`, undefined, asReceipt)
}
export function postAck(http: OutcomeHttp | null, batchId: string, body: WorkflowOutcomeAckRequest) {
  return requestJson(http, `${OUTCOMES_API}/batches/${encodeURIComponent(batchId)}/ack`, postInit(body), asReceipt)
}
export function postDecision(http: OutcomeHttp | null, id: string, body: WorkflowOutcomeDecisionRequest) {
  return requestJson(http, `${OUTCOMES_API}/${encodeURIComponent(id)}/decisions`, postInit(body), asDecisionAccepted)
}
export function postRecoveryPlan(http: OutcomeHttp | null, id: string, decisionId: string) {
  return requestJson(http, `${OUTCOMES_API}/${encodeURIComponent(id)}/recovery-plan`, postInit({ decisionId }), asRecoveryPlan)
}

// ── Keyset paging ────────────────────────────────────────────────────────────────

export type OutcomeListKind = 'failures' | 'unresolved_cause'

const LIST_CLASSIFICATIONS: Record<OutcomeListKind, string[]> = {
  failures: ['confirmed_failure'],
  unresolved_cause: ['cause_unresolved', 'insufficient_evidence', 'classification_pending'],
}

export interface OutcomePaging {
  items: WorkflowOutcomeView[]
  nextCursor: string | null
  loadedPages: number
  error: string | null
  /** Rows the server returned that do not belong in this list (e.g. a non-failure); never shown. */
  hidden: number
}

export const EMPTY_PAGING: OutcomePaging = { items: [], nextCursor: null, loadedPages: 0, error: null, hidden: 0 }

/**
 * Applies one page. A null `requestedCursor` starts over; otherwise items are appended, a repeated
 * id replaced by its newer row. A server that hands back the cursor it was given stops paging with
 * a visible error instead of looping or claiming the list is complete.
 */
export function applyOutcomePage(prev: OutcomePaging, page: WorkflowOutcomeListPage, requestedCursor: string | null, kind: OutcomeListKind): OutcomePaging {
  const base = requestedCursor === null ? EMPTY_PAGING : prev
  const allowed = LIST_CLASSIFICATIONS[kind]
  const keep = page.items.filter(i => allowed.includes(i.classification))
  const items = [...base.items]
  for (const item of keep) {
    const at = items.findIndex(x => x.id === item.id)
    if (at === -1) items.push(item)
    else items[at] = item
  }
  const repeated = page.nextCursor !== null && page.nextCursor === requestedCursor
  return {
    items,
    nextCursor: repeated ? null : page.nextCursor,
    loadedPages: base.loadedPages + 1,
    error: repeated ? 'The server returned the same cursor again; paging stopped and the list is incomplete.' : null,
    hidden: base.hidden + (page.items.length - keep.length),
  }
}

/** A failed page keeps what was loaded and its cursor, so the operator can retry that page. */
export function pagingFailed(prev: OutcomePaging, error: string): OutcomePaging {
  return { ...prev, error }
}

export function pagingComplete(p: OutcomePaging): boolean {
  return p.loadedPages > 0 && p.nextCursor === null && p.error === null
}

/** Never says "all" while a page is unread or failed. */
export function pagingSummary(p: OutcomePaging, serverCount: number | null): string {
  if (p.loadedPages === 0) return p.error ? `Not loaded: ${p.error}` : 'Loading…'
  const n = p.items.length
  if (p.error) return `Showing ${n} — incomplete: ${p.error}`
  if (p.nextCursor) return `Showing ${n}${serverCount !== null ? ` of ${serverCount}` : ''} — more pages available`
  if (serverCount !== null && serverCount !== n) return `Showing ${n} (server count ${serverCount})`
  return `All ${n} shown`
}

// ── Banner: degraded or incomplete coverage is never "all clear" ─────────────────

export interface BannerInput {
  listErrors: string[]
  namespaceErrors: { namespace: string; error: string }[]
  coverage: WorkflowOutcomeCoverage | null
  coverageError: string | null
  health: WorkflowOutcomeHealth | null
  healthError: string | null
  /** Rows shown across both lists. */
  itemCount: number
  counts: WorkflowOutcomeCounts | null
  /** Any list still has an unread page. */
  hasMore: boolean
}

export type OutcomeBanner =
  | { level: 'error'; title: string; details: string[] }
  | { level: 'clear'; title: string; details: string[] }
  | { level: 'none'; title: string; details: string[] }

export function coverageGapRows(coverage: WorkflowOutcomeCoverage | null): (WorkflowOutcomeCoverageGap & { namespace: string })[] {
  if (!coverage) return []
  return coverage.namespaces.flatMap(ns => ns.gaps.map(g => ({ namespace: ns.namespace, ...g })))
}

export function outcomeBanner(input: BannerInput): OutcomeBanner {
  const problems: string[] = []
  for (const e of input.listErrors) problems.push(`Failure list unavailable: ${e}`)
  for (const e of input.namespaceErrors) problems.push(`Namespace ${e.namespace} could not be read: ${e.error}`)
  if (input.coverageError) problems.push(`Coverage unavailable: ${input.coverageError}`)
  else if (!input.coverage) problems.push('Coverage has not been loaded.')
  else {
    const c = input.coverage
    if (!c.enabled) problems.push('Workflow failure monitoring is disabled: failures are not being observed.')
    if (c.degraded) problems.push('The observer reports degraded coverage.')
    if (c.enabled && c.namespaces.length === 0) problems.push('No namespace is enrolled.')
    for (const ns of c.namespaces) {
      if (ns.state !== 'healthy')
        problems.push(`Namespace ${ns.namespace} is ${ns.state}${ns.knownPending > 0 ? ` (${ns.knownPending} known pending)` : ''}.`)
      if (ns.gaps.length > 0) {
        const unknown = ns.gaps.reduce((sum, g) => sum + (g.unknownRunCount ?? 0), 0)
        problems.push(`Namespace ${ns.namespace}: ${ns.gaps.length} coverage gap(s), ${unknown} unknown run(s).`)
      }
    }
  }
  if (input.counts && input.counts.coverageGaps > 0 && coverageGapRows(input.coverage).length === 0)
    problems.push(`${input.counts.coverageGaps} coverage gap(s) reported.`)
  if (input.healthError) problems.push(`Health unavailable: ${input.healthError}`)
  // Unread health is unknown health: never part of an "all clear".
  else if (!input.health) problems.push('Health has not been loaded.')
  else if (input.health.state !== 'healthy') {
    problems.push(`Health is ${input.health.state}.`)
    for (const i of input.health.incidents)
      problems.push(`${i.component}${i.namespace ? ` (${i.namespace})` : ''}: ${i.state} — ${i.reason}`)
  }

  if (problems.length > 0) {
    return {
      level: 'error',
      title: input.itemCount === 0
        ? 'No failures listed, but coverage is incomplete — this is NOT all clear.'
        : 'Coverage is incomplete — the failure list may be missing items.',
      details: problems,
    }
  }
  const counts = input.counts
  if (input.itemCount === 0 && !input.hasMore && counts && counts.failures === 0 && counts.unresolvedCauses === 0 && counts.coverageGaps === 0)
    return { level: 'clear', title: 'No open failure items or cause reviews; every enrolled namespace is healthy with no gaps.', details: [] }
  return { level: 'none', title: '', details: [] }
}

// ── Side-nav counts: failure items, cause reviews, coverage gaps. No success count ─

export type OutcomeNavSummary =
  | { status: 'ok'; failures: number; causes: number; gaps: number; degraded: boolean }
  | { status: 'off'; reason: string }
  | { status: 'error'; reason: string }

export function navSummaryFrom(list: ApiResult<WorkflowOutcomeListPage>, coverage: ApiResult<WorkflowOutcomeCoverage> | null): OutcomeNavSummary {
  if (!list.ok) {
    if (list.status === 503 || list.status === 404) return { status: 'off', reason: list.error }
    return { status: 'error', reason: list.error }
  }
  const c = coverage && coverage.ok ? coverage.data : null
  const degraded = list.data.namespaceErrors.length > 0 || !c || !c.enabled || c.degraded
    || c.namespaces.some(ns => ns.state !== 'healthy' || ns.gaps.length > 0)
  return {
    status: 'ok',
    failures: list.data.counts.failures,
    causes: list.data.counts.unresolvedCauses,
    gaps: list.data.counts.coverageGaps,
    degraded,
  }
}

/** One fleet-wide poll for the side-nav: the list's counts (one row requested) plus coverage. */
export async function loadNavSummary(http: OutcomeHttp | null): Promise<OutcomeNavSummary> {
  const [list, coverage] = await Promise.all([
    fetchOutcomeList(http, { state: 'failures', limit: 1 }),
    fetchCoverage(http),
  ])
  return navSummaryFrom(list, coverage)
}

/** A line shown beside the counts when they cannot be read as complete, or null. */
export function navSummaryNote(summary: OutcomeNavSummary | null): string | null {
  if (!summary) return 'Loading workflow failure counts…'
  if (summary.status !== 'ok') return summary.reason
  return summary.degraded ? 'Coverage is degraded or unknown: these counts may be incomplete and zero is not all clear.' : null
}

export interface NavBadge {
  key: 'failures' | 'causes' | 'gaps' | 'state'
  /** Short word shown before the value; '' for the failure count itself. */
  label: string
  value: string
  attention: boolean
  title: string
}

/** Separate badges for failure items, cause reviews and coverage gaps (never a success count). */
export function navBadges(summary: OutcomeNavSummary | null): NavBadge[] {
  if (!summary) return []
  if (summary.status === 'off') return [{ key: 'state', label: '', value: 'off', attention: false, title: summary.reason }]
  if (summary.status === 'error') return [{ key: 'state', label: '', value: '!', attention: true, title: summary.reason }]
  return [
    { key: 'failures', label: '', value: String(summary.failures), attention: summary.failures > 0, title: `${summary.failures} open failure item(s)` },
    { key: 'causes', label: 'cause', value: String(summary.causes), attention: summary.causes > 0, title: `${summary.causes} unresolved cause review(s)` },
    {
      key: 'gaps', label: 'gap', value: `${summary.gaps}${summary.degraded ? '!' : ''}`, attention: summary.gaps > 0 || summary.degraded,
      title: `${summary.gaps} coverage gap(s)${summary.degraded ? '; coverage degraded or unknown' : ''}`,
    },
  ]
}

// ── Exact-version acknowledgement ────────────────────────────────────────────────

const refKey = (r: WorkflowOutcomeItemRef) => `${r.id}:${r.version}`

/** Delivered members whose exact version is not acknowledged yet. */
export function ackableMembers(receipt: WorkflowOutcomeReceipt): WorkflowOutcomeItemRef[] {
  const acked = new Set(receipt.ackedVersions.map(refKey))
  return receipt.deliveredVersions.filter(r => !acked.has(refKey(r)))
}

export type AckBuild = { ok: true; body: WorkflowOutcomeAckRequest } | { ok: false; error: string }

/**
 * Body for `POST /batches/{id}/ack` with exactly the selected members. Every selected id+version
 * must be a delivered, not yet acknowledged member of this receipt; there is no "all" shortcut.
 */
export function buildAckRequest(receipt: WorkflowOutcomeReceipt, selected: WorkflowOutcomeItemRef[]): AckBuild {
  if (!receipt.deliveredAt || receipt.deliveredVersions.length === 0)
    return { ok: false, error: 'This batch has no delivered receipt; nothing can be acknowledged.' }
  if (selected.length === 0) return { ok: false, error: 'Select at least one delivered member to acknowledge.' }
  if (selected.length > MAX_BATCH_ITEMS) return { ok: false, error: `At most ${MAX_BATCH_ITEMS} members per acknowledgement.` }
  const ids = new Set<string>()
  const ackable = new Set(ackableMembers(receipt).map(refKey))
  const delivered = new Map(receipt.deliveredVersions.map(r => [r.id, r.version]))
  for (const s of selected) {
    if (ids.has(s.id)) return { ok: false, error: `Member ${s.id} is selected twice.` }
    ids.add(s.id)
    const v = delivered.get(s.id)
    if (v === undefined) return { ok: false, error: `Member ${s.id} was not delivered in this batch.` }
    if (v !== s.version) return { ok: false, error: `Member ${s.id} was delivered at version ${v}, not ${s.version}.` }
    if (!ackable.has(refKey(s))) return { ok: false, error: `Member ${s.id} version ${s.version} is already acknowledged.` }
  }
  // Delivered order, so the same selection always produces the same body.
  const items = receipt.deliveredVersions.filter(r => ids.has(r.id)).map(r => ({ id: r.id, version: r.version }))
  return { ok: true, body: { generation: receipt.generation, nonce: receipt.nonce, items, taskId: null } }
}

/** Why the batch members cannot be reviewed from this row, or null. */
export function batchReviewBlocked(batch: WorkflowOutcomeBatchMembership): string | null {
  if (!batch.nonce) return 'The server did not return this batch nonce, so its receipt cannot be read.'
  if (!batch.deliveredAt) return 'Not delivered yet: only delivered members can be acknowledged.'
  return null
}

// ── Record-only decisions offered to the operator ───────────────────────────────

export const OPERATOR_DECISIONS: { operation: WorkflowOutcomeOperation; label: string; hint: string }[] = [
  { operation: 'hold', label: 'Hold', hint: 'Owned hold with a reason and a future review time. Nothing is cancelled or retried.' },
  { operation: 'wait', label: 'Wait', hint: 'Owned wait with a reason and a future review time. Nothing times out automatically.' },
  { operation: 'accept_late', label: 'Accept late', hint: 'Records review of an exact late result (late-result: ref). Not an approval and no signal is sent.' },
  { operation: 'finish', label: 'Finish', hint: 'Closes this failure item with an existing recovered artifact as evidence.' },
  { operation: 'close', label: 'Close', hint: 'Closes this failure item as an accepted failure or deliberate closure, with evidence.' },
]

const RECOVERY_OPERATIONS = ['resume', 'retry', 'reassign', 'accept_late', 'cancel']

export interface DecisionDraft {
  operation: string
  owner: string
  reason: string
  /** One evidence ref per line. */
  evidenceRefsText: string
  /** `datetime-local` value (local time) or an ISO timestamp with an explicit offset. */
  dueAt: string
  taskRef: string
  artifactRef: string
}

export const EMPTY_DECISION_DRAFT: DecisionDraft = { operation: 'hold', owner: '', reason: '', evidenceRefsText: '', dueAt: '', taskRef: '', artifactRef: '' }

/** Parses a due time to a UTC ISO string; a zone-less ISO value is read as local time. */
export function parseDueAt(text: string): string | null {
  const t = text.trim()
  if (t === '') return null
  const local = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2}(\.\d{1,3})?)?$/.test(t)
  const zoned = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2}(\.\d+)?)?(Z|[+-]\d{2}:\d{2})$/.test(t)
  if (!local && !zoned) return null
  const ms = Date.parse(t)
  return Number.isNaN(ms) ? null : new Date(ms).toISOString()
}

export type DecisionBuild = { ok: true; body: WorkflowOutcomeDecisionRequest } | { ok: false; errors: string[] }

/** Validates an operator decision and builds its record-only request body. */
export function buildDecisionRequest(draft: DecisionDraft, item: Pick<WorkflowOutcomeView, 'version'>, nowMs: number): DecisionBuild {
  const errors: string[] = []
  const offered = OPERATOR_DECISIONS.find(d => d.operation === draft.operation)
  if (!offered) errors.push(`"${draft.operation}" is not a decision this dashboard records.`)
  const operation = draft.operation as WorkflowOutcomeOperation

  const owner = draft.owner.trim()
  if (owner === '') errors.push('Owner is required.')
  else if (owner.length > MAX_OWNER_CHARS) errors.push(`Owner is ${owner.length} characters; the limit is ${MAX_OWNER_CHARS}.`)

  const reason = draft.reason.trim()
  if (reason === '') errors.push('Reason is required.')
  else if (reason.length > MAX_REASON_CHARS) errors.push(`Reason is ${reason.length} characters; the limit is ${MAX_REASON_CHARS}.`)

  const refs = draft.evidenceRefsText.split('\n').map(l => l.trim()).filter(l => l !== '')
  if (refs.length > MAX_EVIDENCE_REFS) errors.push(`${refs.length} evidence refs; the limit is ${MAX_EVIDENCE_REFS}.`)
  for (const r of refs) if (!isEvidenceRef(r)) errors.push(`Not an accepted evidence ref: ${r.slice(0, 80)}`)
  if (new Set(refs).size !== refs.length) errors.push('An evidence ref is listed twice.')
  if ((operation === 'finish' || operation === 'close') && refs.length === 0)
    errors.push(`${offered?.label ?? operation} needs at least one evidence ref.`)
  if (operation === 'accept_late' && !refs.some(r => r.startsWith('late-result:')))
    errors.push('Accept late needs the exact late result as a late-result: evidence ref.')

  let dueAt: string | null = null
  if (draft.dueAt.trim() !== '') {
    dueAt = parseDueAt(draft.dueAt)
    if (dueAt === null) errors.push('Due time is not a valid date and time.')
  }
  if (operation === 'wait' || operation === 'hold') {
    if (draft.dueAt.trim() === '') errors.push(`${offered?.label ?? operation} needs a future due time.`)
    else if (dueAt !== null && Date.parse(dueAt) <= nowMs) errors.push('Due time must be in the future.')
  }

  const optionalRef = (label: string, value: string): string | null => {
    const v = value.trim()
    if (v === '') return null
    if (!isEvidenceRef(v)) errors.push(`${label} is not an accepted ref: ${v.slice(0, 80)}`)
    return v
  }
  const taskRef = optionalRef('Task ref', draft.taskRef)
  const artifactRef = optionalRef('Artifact ref', draft.artifactRef)

  if (!Number.isInteger(item.version) || item.version <= 0) errors.push('The item has no valid version to decide on.')
  if (errors.length > 0) return { ok: false, errors }
  return {
    ok: true,
    body: { expectedVersion: item.version, operation, owner, reason, evidenceRefs: refs, dueAt, taskRef, artifactRef, batchId: null, generation: null },
  }
}

// ── Plan recovery: read-only, never an approval or an execution ──────────────────

/** Accepted decisions whose operation can lead to a recovery reservation. */
export function recoveryCandidates(decisions: WorkflowOutcomeDecisionView[]): WorkflowOutcomeDecisionView[] {
  return decisions.filter(d => d.decisionId && d.status === 'accepted' && RECOVERY_OPERATIONS.includes(d.operation))
}

export function recoveryPlanSummary(result: WorkflowRecoveryPlanResult): { allowed: boolean; title: string; lines: string[] } {
  const lines: string[] = []
  if (result.checkedAt) lines.push(`Checked at ${result.checkedAt}.`)
  if (result.missingProofs.length > 0) lines.push(`Missing proofs: ${result.missingProofs.join(', ')}.`)
  lines.push('This check reserved, approved and started nothing. Execution needs the separate existing authorized path.')
  return result.allowed
    ? { allowed: true, title: `Read-only check passed for ${result.operation ?? 'the decision'}.`, lines }
    : { allowed: false, title: `Blocked: ${result.blockedReason || 'no reason given'}.`, lines }
}
