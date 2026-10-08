import type {
  EpicGrantDecisionView, EpicGrantView, ScopeValidationReport, ScopeValidationTarget,
} from './types'

// Epic grants view logic (#436). Kept free of `import.meta.env` and JSX so `node --test` can load
// it directly (see epicGrants.test.ts); EpicGrantsView.tsx only renders what these return.

export type ScopeParse =
  | { ok: true; scope: Record<string, unknown> }
  | { ok: false; error: string }

/**
 * Parses the pasted scope locally, before anything is sent. The orchestrator validates the
 * content; this only catches what it could never accept: empty text, broken JSON, or JSON that is
 * not an object.
 */
export function parseScopeText(text: string): ScopeParse {
  if (text.trim() === '') return { ok: false, error: 'Paste a scope JSON object.' }
  let parsed: unknown
  try {
    parsed = JSON.parse(text)
  } catch (e) {
    return { ok: false, error: `Invalid JSON: ${e instanceof Error ? e.message : String(e)}` }
  }
  if (parsed === null || typeof parsed !== 'object' || Array.isArray(parsed))
    return { ok: false, error: 'Scope must be a JSON object.' }
  return { ok: true, scope: parsed as Record<string, unknown> }
}

/**
 * The request body for both validate and create. One serialization of the parsed object, so the
 * bytes the orchestrator validates are the bytes it stores and hashes on create.
 */
export function scopeRequestBody(scope: Record<string, unknown>): string {
  return JSON.stringify(scope)
}

/**
 * Why Create is disabled, or null when it is enabled. Create needs a report for exactly the
 * current text, and that report must be `valid: true`. Any edit after Validate disables it again.
 */
export function createBlockedReason(
  report: ScopeValidationReport | null,
  textAtValidation: string | null,
  currentText: string,
): string | null {
  const parsed = parseScopeText(currentText)
  if (!parsed.ok) return parsed.error
  if (report === null || textAtValidation === null) return 'Validate the scope first.'
  if (textAtValidation !== currentText) return 'The scope changed since it was validated. Validate again.'
  if (report.valid !== true) return 'The scope is not valid. Fix the errors in the report.'
  return null
}

export function canCreate(
  report: ScopeValidationReport | null,
  textAtValidation: string | null,
  currentText: string,
): boolean {
  return createBlockedReason(report, textAtValidation, currentText) === null
}

/**
 * Accepts a response body as a validation report only if it has the report's shape. Anything else
 * (an `{"error": ...}` body, HTML from a proxy) is shown as an error instead.
 */
export function asValidationReport(body: unknown): ScopeValidationReport | null {
  if (body === null || typeof body !== 'object' || Array.isArray(body)) return null
  const b = body as Record<string, unknown>
  if (typeof b.valid !== 'boolean' || !Array.isArray(b.errors)) return null
  return {
    valid: b.valid,
    errors: b.errors.map(String),
    scopeSha256: typeof b.scopeSha256 === 'string' ? b.scopeSha256 : null,
    driver: b.driver && typeof b.driver === 'object' ? b.driver as ScopeValidationReport['driver'] : null,
    workflows: Array.isArray(b.workflows) ? b.workflows as ScopeValidationReport['workflows'] : [],
    targets: Array.isArray(b.targets) ? b.targets as ScopeValidationReport['targets'] : [],
    expiresAt: typeof b.expiresAt === 'string' ? b.expiresAt : null,
  }
}

/** A readable message for a failed request: the body's `error` string, else the status. */
export function responseErrorMessage(status: number, body: unknown, fallbackText = ''): string {
  if (body && typeof body === 'object' && !Array.isArray(body)) {
    const error = (body as Record<string, unknown>).error
    if (typeof error === 'string' && error.trim() !== '') return error
  }
  const text = fallbackText.trim()
  return text ? `HTTP ${status}: ${text.slice(0, 300)}` : `HTTP ${status}`
}

export function isPublicTarget(target: ScopeValidationTarget): boolean {
  return target.visibility === 'public'
}

/** First `length` characters of a hash or ref, with an ellipsis when it was cut. */
export function shortHash(value: string | null | undefined, length = 12): string {
  if (!value) return ''
  return value.length <= length ? value : `${value.slice(0, length)}…`
}

/** A `reserved` row older than this has no known delivery outcome (crash between D11 and send). */
export const DELIVERY_UNKNOWN_AFTER_MS = 60_000

/**
 * True for a `reserved` decision created more than 60 s before `nowMs`. A reserved row whose
 * timestamp cannot be read is also flagged: its freshness cannot be shown, so it is surfaced.
 */
export function isDeliveryUnknown(
  decision: Pick<EpicGrantDecisionView, 'status' | 'createdAt'>,
  nowMs: number,
): boolean {
  if (decision.status !== 'reserved') return false
  const createdMs = Date.parse(decision.createdAt)
  if (Number.isNaN(createdMs)) return true
  return nowMs - createdMs > DELIVERY_UNKNOWN_AFTER_MS
}

/** The label shown in the decisions table's status column. */
export function statusLabel(
  decision: Pick<EpicGrantDecisionView, 'status' | 'createdAt'>,
  nowMs: number,
): string {
  if (isDeliveryUnknown(decision, nowMs)) return 'delivery unknown'
  if (decision.status === 'send_failed') return 'send failed'
  return decision.status
}

/** Revoke is offered only on grants that are still in force. */
export function canRevoke(grant: Pick<EpicGrantView, 'effectiveStatus'>): boolean {
  return grant.effectiveStatus === 'active'
}

/** `POST /api/epic-grants/{id}/revoke` body; a blank reason is sent as null. */
export function revokeRequestBody(reason: string): { reason: string | null } {
  const trimmed = reason.trim()
  return { reason: trimmed === '' ? null : trimmed }
}

/** Replaces the revoked grant's row with the server's view of it. Other rows are untouched. */
export function applyRevoke(list: EpicGrantView[], updated: EpicGrantView): EpicGrantView[] {
  return list.map(g => g.id === updated.id ? { ...g, ...updated } : g)
}

/** The same update for the selected grant's detail, keeping its scope and decisions. */
export function applyRevokeToDetail<T extends EpicGrantView>(detail: T | null, updated: EpicGrantView): T | null {
  if (detail === null || detail.id !== updated.id) return detail
  return { ...detail, ...updated }
}
