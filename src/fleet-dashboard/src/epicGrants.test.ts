// Run with `npm test` (node --test, Node ≥ 22.18 strips the types). Type-checked by `tsc -b`.
// AC-U1 (#436): the view logic lives in epicGrants.ts so it runs here; EpicGrantsView.tsx only renders it.
import { test } from 'node:test'
import assert from 'node:assert/strict'
import type { EpicGrantDecisionView, EpicGrantDetail, EpicGrantView, ScopeValidationReport } from './types'
import {
  DELIVERY_UNKNOWN_AFTER_MS, applyRevoke, applyRevokeToDetail, asValidationReport, canCreate,
  canRevoke, createBlockedReason, isDeliveryUnknown, isPublicTarget, parseScopeText,
  responseErrorMessage, revokeRequestBody, scopeRequestBody, shortHash, statusLabel,
} from './epicGrants.ts'

const SCOPE_TEXT = JSON.stringify({
  driver: { namespace: 'fleet', workflowId: 'driver-1', runId: '00000000-0000-0000-0000-000000000001' },
  targets: [{ repo: 'example-org/example-repo', issues: [12, 13] }],
  gates: ['merge-approval'],
  workflows: [{ type: 'UwePrImplementationWorkflow', version: 21, sha256: 'a'.repeat(64) }],
  expiresAt: '2026-11-01T00:00:00Z',
}, null, 2)

function report(overrides: Partial<ScopeValidationReport> = {}): ScopeValidationReport {
  return {
    valid: true, errors: [], scopeSha256: 'b'.repeat(64),
    driver: { namespace: 'fleet', workflowId: 'driver-1', runId: '00000000-0000-0000-0000-000000000001', status: 'Running' },
    workflows: [{ type: 'UwePrImplementationWorkflow', version: 21, sha256: 'a'.repeat(64), hashMatches: true, delegationCapable: true, gates: ['merge-approval'] }],
    targets: [{ repo: 'example-org/example-repo', issues: [12, 13], allowPublic: false, visibility: 'private', denied: false }],
    expiresAt: '2026-11-01T00:00:00Z',
    ...overrides,
  }
}

function grant(overrides: Partial<EpicGrantView> = {}): EpicGrantView {
  return {
    id: 'grant-1', status: 'active', effectiveStatus: 'active', scopeSha256: 'b'.repeat(64),
    driverNamespace: 'fleet', driverWorkflowId: 'driver-1', driverRunId: '00000000-0000-0000-0000-000000000001',
    ctoAgent: 'agent1', createdAt: '2026-10-01T00:00:00Z', expiresAt: '2026-11-01T00:00:00Z',
    revokedAt: null, revokeReason: null, ...overrides,
  }
}

function decision(status: EpicGrantDecisionView['status'], createdAt: string): Pick<EpicGrantDecisionView, 'status' | 'createdAt'> {
  return { status, createdAt }
}

// ── Parse errors surface before anything is sent ─────────────────────────────────

test('parse: a scope object is accepted as the parsed object', () => {
  const parsed = parseScopeText(SCOPE_TEXT)
  assert.equal(parsed.ok, true)
  if (parsed.ok) assert.deepEqual(parsed.scope, JSON.parse(SCOPE_TEXT))
})

test('parse: empty text, broken JSON and non-objects are refused locally with a message', () => {
  for (const raw of ['', '   \n']) {
    const parsed = parseScopeText(raw)
    assert.equal(parsed.ok, false, JSON.stringify(raw))
    if (!parsed.ok) assert.match(parsed.error, /Paste a scope/)
  }
  const broken = parseScopeText('{"driver": {')
  assert.equal(broken.ok, false)
  if (!broken.ok) assert.match(broken.error, /^Invalid JSON: /)
  for (const raw of ['[]', '[{"driver":{}}]', 'null', '42', '"scope"', 'true']) {
    const parsed = parseScopeText(raw)
    assert.equal(parsed.ok, false, raw)
    if (!parsed.ok) assert.equal(parsed.error, 'Scope must be a JSON object.', raw)
  }
})

test('request body: validate and create send one serialization of the parsed object', () => {
  const parsed = parseScopeText(SCOPE_TEXT)
  assert.equal(parsed.ok, true)
  if (parsed.ok) {
    const body = scopeRequestBody(parsed.scope)
    assert.deepEqual(JSON.parse(body), JSON.parse(SCOPE_TEXT))
    assert.equal(scopeRequestBody(parsed.scope), body)
  }
})

// ── Create is enabled only by a valid report for exactly the current text ──────────

test('create: enabled only when the report for the current text is valid', () => {
  assert.equal(canCreate(report(), SCOPE_TEXT, SCOPE_TEXT), true)
  assert.equal(createBlockedReason(report(), SCOPE_TEXT, SCOPE_TEXT), null)
})

test('create: disabled before any validation', () => {
  assert.equal(canCreate(null, null, SCOPE_TEXT), false)
  assert.match(createBlockedReason(null, null, SCOPE_TEXT)!, /Validate the scope first/)
})

test('create: disabled when the report is invalid, with the report errors to show', () => {
  const invalid = report({
    valid: false,
    errors: ['targets[0]: example-org/example-repo is public; allowPublic is required',
      'workflows[0]: sha256 does not match UwePrImplementationWorkflow v21'],
    targets: [{ repo: 'example-org/example-repo', issues: [12], allowPublic: false, visibility: 'public', denied: false }],
    workflows: [{ type: 'UwePrImplementationWorkflow', version: 21, sha256: 'c'.repeat(64), hashMatches: false, delegationCapable: true, gates: [] }],
  })
  assert.equal(canCreate(invalid, SCOPE_TEXT, SCOPE_TEXT), false)
  assert.match(createBlockedReason(invalid, SCOPE_TEXT, SCOPE_TEXT)!, /not valid/)
  assert.equal(isPublicTarget(invalid.targets[0]), true)
  assert.equal(invalid.workflows[0].hashMatches, false)
})

test('create: disabled when the text changed after a valid report, even by whitespace', () => {
  for (const edited of [SCOPE_TEXT + ' ', SCOPE_TEXT.replace('"issues": [\n', '"issues": [\n        14,\n')]) {
    assert.equal(canCreate(report(), SCOPE_TEXT, edited), false)
    assert.match(createBlockedReason(report(), SCOPE_TEXT, edited)!, /changed since it was validated/)
  }
})

test('create: disabled when the current text does not parse, whatever the old report says', () => {
  assert.equal(canCreate(report(), SCOPE_TEXT, '{'), false)
  assert.match(createBlockedReason(report(), SCOPE_TEXT, '{')!, /^Invalid JSON: /)
})

test('report: a 400/200 body with the report shape is read as a report; other bodies are not', () => {
  const r = asValidationReport(report({ valid: false, errors: ['expiresAt is in the past'] }))
  assert.ok(r)
  assert.equal(r.valid, false)
  assert.deepEqual(r.errors, ['expiresAt is in the past'])
  const sparse = asValidationReport({ valid: false, errors: ['driver is required'] })
  assert.deepEqual(sparse, { valid: false, errors: ['driver is required'], scopeSha256: null, driver: null, workflows: [], targets: [], expiresAt: null })
  for (const body of [null, [], 'text', { error: 'epic grants are disabled' }, { valid: 'yes', errors: [] }])
    assert.equal(asValidationReport(body), null, JSON.stringify(body))
})

test('errors: the body error string is shown, else the status and text', () => {
  assert.equal(responseErrorMessage(503, { error: 'epic grants are disabled' }), 'epic grants are disabled')
  assert.equal(responseErrorMessage(502, null, 'Bad Gateway'), 'HTTP 502: Bad Gateway')
  assert.equal(responseErrorMessage(500, null), 'HTTP 500')
})

// ── Revoke updates the status ───────────────────────────────────────────────────

test('revoke: offered only on grants in force', () => {
  assert.equal(canRevoke(grant()), true)
  assert.equal(canRevoke(grant({ effectiveStatus: 'expired' })), false)
  assert.equal(canRevoke(grant({ status: 'revoked', effectiveStatus: 'revoked' })), false)
})

test('revoke: the response replaces only that row, so its status shows revoked', () => {
  const list = [grant({ id: 'grant-2' }), grant(), grant({ id: 'grant-0', effectiveStatus: 'expired' })]
  const revoked = grant({ status: 'revoked', effectiveStatus: 'revoked', revokedAt: '2026-10-08T10:00:00Z', revokeReason: 'scope too wide' })
  const next = applyRevoke(list, revoked)
  assert.deepEqual(next.map(g => [g.id, g.effectiveStatus]), [['grant-2', 'active'], ['grant-1', 'revoked'], ['grant-0', 'expired']])
  assert.equal(next[1].revokeReason, 'scope too wide')
  assert.equal(next[0], list[0])
  assert.equal(list[1].effectiveStatus, 'active', 'the input list is not mutated')
  assert.equal(canRevoke(next[1]), false)
})

test('revoke: the selected detail takes the new status and keeps its decisions', () => {
  const detail: EpicGrantDetail = { ...grant(), scope: {}, decisions: [] }
  const revoked = grant({ status: 'revoked', effectiveStatus: 'revoked', revokedAt: '2026-10-08T10:00:00Z' })
  const next = applyRevokeToDetail(detail, revoked)
  assert.equal(next?.effectiveStatus, 'revoked')
  assert.equal(next?.decisions, detail.decisions)
  assert.equal(applyRevokeToDetail(detail, grant({ id: 'other', effectiveStatus: 'revoked' })), detail)
  assert.equal(applyRevokeToDetail(null, revoked), null)
})

test('revoke: the reason is trimmed, blank is null', () => {
  assert.deepEqual(revokeRequestBody('  scope too wide '), { reason: 'scope too wide' })
  assert.deepEqual(revokeRequestBody('   '), { reason: null })
})

// ── A reserved row older than 60 s is "delivery unknown" ──────────────────────────

const CREATED = '2026-10-08T10:00:00Z'
const createdMs = Date.parse(CREATED)

test('delivery unknown: reserved for 61 s', () => {
  assert.equal(DELIVERY_UNKNOWN_AFTER_MS, 60_000)
  assert.equal(isDeliveryUnknown(decision('reserved', CREATED), createdMs + 61_000), true)
  assert.equal(statusLabel(decision('reserved', CREATED), createdMs + 61_000), 'delivery unknown')
  assert.equal(statusLabel(decision('reserved', CREATED), createdMs + 3_600_000), 'delivery unknown')
})

test('delivery unknown: not for reserved at 59 s or exactly 60 s', () => {
  for (const age of [0, 59_000, 60_000]) {
    assert.equal(isDeliveryUnknown(decision('reserved', CREATED), createdMs + age), false, String(age))
    assert.equal(statusLabel(decision('reserved', CREATED), createdMs + age), 'reserved', String(age))
  }
})

test('delivery unknown: never for sent or send_failed, however old', () => {
  const later = createdMs + 86_400_000
  assert.equal(isDeliveryUnknown(decision('sent', CREATED), later), false)
  assert.equal(statusLabel(decision('sent', CREATED), later), 'sent')
  assert.equal(isDeliveryUnknown(decision('send_failed', CREATED), later), false)
  assert.equal(statusLabel(decision('send_failed', CREATED), later), 'send failed')
})

test('delivery unknown: a reserved row with an unreadable timestamp is flagged', () => {
  assert.equal(isDeliveryUnknown(decision('reserved', 'not a time'), createdMs), true)
})

// ── Hash shortening ────────────────────────────────────────────────────────────

test('shortHash: 12 characters plus an ellipsis; short or missing values are unchanged', () => {
  assert.equal(shortHash('0123456789abcdef0123456789abcdef01234567'), '0123456789ab…')
  assert.equal(shortHash('0123456789abcdef', 8), '01234567…')
  assert.equal(shortHash('0123456789ab'), '0123456789ab')
  assert.equal(shortHash(''), '')
  assert.equal(shortHash(null), '')
  assert.equal(shortHash(undefined), '')
})
