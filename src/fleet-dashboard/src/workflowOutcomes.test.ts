// Run with `npm test` (node --test, Node ≥ 22.18 strips the types). Type-checked by `tsc -b`.
// #476 dashboard suite: every describe title starts with "dashboard" so the integration runner can
// select it with `--test-name-pattern dashboard`. Synthetic fixtures only (placeholder names).
import { describe, test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import { createElement } from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import { createServer } from 'vite'
import type {
  WorkflowOutcomeCoverage, WorkflowOutcomeDetail, WorkflowOutcomeHealth, WorkflowOutcomeListPage, WorkflowOutcomeReceipt,
  WorkflowOutcomeView,
} from './types'
import {
  ackableMembers, applyOutcomePage, asCoverage, asHealth, asListPage, asOutcomeDetail, asOutcomeView, asReceipt,
  batchReviewBlocked, buildAckRequest, buildDecisionRequest, causeLabel, coverageGapRows, currentStageLabel,
  DEFAULT_PAGE_LIMIT, deliveryVisual, detailUrl, dueLabel, EMPTY_DECISION_DRAFT, EMPTY_PAGING, fetchBatchReceipt,
  fetchCoverage, fetchHealth, fetchOutcomeDetail, fetchOutcomeList, isEvidenceRef, listUrl, loadNavSummary,
  MALFORMED_ERROR, MAX_BATCH_ITEMS, navBadges, navSummaryFrom, navSummaryNote, NO_BEARER_ERROR, OPERATOR_DECISIONS,
  outcomeBanner, outcomeErrorMessage, pagingComplete, pagingFailed, pagingSummary, parseDueAt, postAck, postDecision,
  postRecoveryPlan, recoveryCandidates, recoveryPlanSummary, stageRows, STAGE_LABELS,
} from './workflowOutcomes.ts'
import type { ApiResult, BannerInput, DecisionDraft, OutcomeHttp, OutcomePaging } from './workflowOutcomes.ts'

// ── Synthetic fixtures ──────────────────────────────────────────────────────────

const NOW = Date.parse('2026-10-24T12:00:00Z')
const ID_A = '11111111-1111-4111-8111-111111111111'
const ID_B = '22222222-2222-4222-8222-222222222222'
const ID_C = '33333333-3333-4333-8333-333333333333'
const BATCH = 'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa'

function view(overrides: Partial<WorkflowOutcomeView> = {}): WorkflowOutcomeView {
  return {
    id: ID_A, version: 2, namespace: 'namespace-a', workflowId: 'wf-example-1', runId: 'run-0001-example',
    workflowType: 'ExampleWorkflow', status: 'Failed', classification: 'confirmed_failure', reasonCode: 'workflow_failed',
    terminalEventType: 'WorkflowExecutionFailed', terminalEventId: 42,
    startTime: '2026-10-24T10:00:00Z', closeTime: '2026-10-24T11:00:00Z',
    project: 'project-a', accessNeeded: false, coordinator: 'coordinator', taskOwner: 'agent1', evidenceState: 'complete',
    failureIncidentId: null, parentWorkflowId: null, parentRunId: null, previousRunId: null, nextRunId: null,
    stage: 'stored',
    stages: { storedAt: '2026-10-24T11:00:05Z', enqueuedAt: null, deliveredAt: null, acknowledgedAt: null, decisionMadeAt: null },
    deliveryState: 'none', turnAmbiguous: false, lastDecision: null, recovery: null, dueAt: null, attentionOverdue: false,
    ...overrides,
  }
}

const ALL_STAGES = {
  storedAt: '2026-10-24T11:00:05Z', enqueuedAt: '2026-10-24T11:00:20Z', deliveredAt: '2026-10-24T11:01:00Z',
  acknowledgedAt: '2026-10-24T11:05:00Z', decisionMadeAt: '2026-10-24T11:06:00Z',
}

function page(items: WorkflowOutcomeView[], nextCursor: string | null = null, overrides: Partial<WorkflowOutcomeListPage> = {}): WorkflowOutcomeListPage {
  return { items, nextCursor, namespaceErrors: [], counts: { failures: items.length, unresolvedCauses: 0, coverageGaps: 0 }, ...overrides }
}

function coverage(overrides: Partial<WorkflowOutcomeCoverage> = {}): WorkflowOutcomeCoverage {
  return {
    enabled: true, degraded: false,
    namespaces: [{
      namespace: 'namespace-a', state: 'healthy', retentionSeconds: 7 * 86400, enrolledAt: '2026-10-01T00:00:00Z',
      lastRecentPassAt: '2026-10-24T11:59:00Z', lastFullPassAt: '2026-10-24T11:55:00Z', knownPending: 0, gaps: [],
    }],
    ...overrides,
  }
}

const HEALTHY: WorkflowOutcomeHealth = { state: 'healthy', incidents: [] }

function bannerInput(overrides: Partial<BannerInput> = {}): BannerInput {
  return {
    listErrors: [], namespaceErrors: [], coverage: coverage(), coverageError: null, health: HEALTHY, healthError: null,
    itemCount: 0, counts: { failures: 0, unresolvedCauses: 0, coverageGaps: 0 }, hasMore: false, ...overrides,
  }
}

function receipt(overrides: Partial<WorkflowOutcomeReceipt> = {}): WorkflowOutcomeReceipt {
  return {
    batchId: BATCH, generation: 3, nonce: 'nonce-example', itemVersionsHash: 'f'.repeat(64),
    items: [{ id: ID_A, version: 2 }, { id: ID_B, version: 1 }, { id: ID_C, version: 4 }],
    deliveredVersions: [{ id: ID_A, version: 2 }, { id: ID_B, version: 1 }, { id: ID_C, version: 4 }],
    ackedVersions: [{ id: ID_C, version: 4 }],
    receiptHash: 'e'.repeat(64), deliveredAt: '2026-10-24T11:01:00Z', acknowledgedAt: null,
    ...overrides,
  }
}

function draft(overrides: Partial<DecisionDraft> = {}): DecisionDraft {
  return { ...EMPTY_DECISION_DRAFT, operation: 'hold', owner: 'coordinator', reason: 'waiting for the task owner', dueAt: '2026-10-25T12:00:00Z', ...overrides }
}

function detail(overrides: Partial<WorkflowOutcomeDetail> = {}): WorkflowOutcomeDetail {
  return {
    ...view({
      stage: 'delivered', deliveryState: 'delivered',
      stages: { ...ALL_STAGES, acknowledgedAt: null, decisionMadeAt: null },
      parentWorkflowId: 'wf-parent-example', parentRunId: 'run-parent-0001', previousRunId: 'run-0000-example', nextRunId: null,
    }),
    decisions: [{
      decisionId: 'dddddddd-dddd-4ddd-8ddd-dddddddddddd', version: 2, operation: 'retry', owner: 'coordinator',
      reason: 'retry after the owner stops the task', dueAt: null, status: 'accepted', decidedVia: 'coordinator',
      createdAt: '2026-10-24T11:10:00Z', evidenceRefs: ['task:example-task'],
    }],
    batches: [{ batchId: BATCH, generation: 3, state: 'delivered', deliveredAt: '2026-10-24T11:01:00Z', ackedAt: null, nonce: 'nonce-example', version: 2 }],
    observations: [{ eventId: 42, eventType: 'WorkflowExecutionFailed', classification: 'confirmed_failure', reasonCode: 'workflow_failed', observedAt: '2026-10-24T11:00:04Z' }],
    evidenceRefs: ['event:42', 'artifact:example-artifact', 'https://example.invalid/owner/repo/pull/1'],
    ...overrides,
  }
}

/** A fake bearer-carrying fetch: records every call and answers from `respond`. */
function fakeHttp(respond: (url: string, init?: RequestInit) => { status: number; body: unknown }) {
  const calls: { url: string; init?: RequestInit }[] = []
  const http: OutcomeHttp = async (url, init) => {
    calls.push({ url, init })
    const r = respond(url, init)
    return new Response(typeof r.body === 'string' ? r.body : JSON.stringify(r.body), { status: r.status })
  }
  return { http, calls }
}

// ── Labels ──────────────────────────────────────────────────────────────────────

describe('dashboard: transport vs business cause labels', () => {
  test('workflow-level reason codes are transport failures, business_* codes are business failures', () => {
    for (const code of ['workflow_failed', 'workflow_timed_out', 'failure_caused_cancel', 'failure_caused_terminate'])
      assert.equal(causeLabel('confirmed_failure', code).kind, 'transport', code)
    for (const code of ['business_task_failed', 'business_deadline', 'business_review_cap', 'business_artifact_missing'])
      assert.equal(causeLabel('confirmed_failure', code).kind, 'business', code)
    const c = causeLabel('confirmed_failure', 'business_review_cap')
    assert.equal(c.kindLabel, 'Business failure')
    assert.equal(c.label, 'Review cap reached')
    assert.equal(c.code, 'business_review_cap')
  })

  test('unresolved causes and pending classification are reviews or gaps, and an unknown code is kept verbatim', () => {
    assert.equal(causeLabel('cause_unresolved', 'abort_cause_unknown').kind, 'cause_review')
    assert.equal(causeLabel('cause_unresolved', null).kind, 'cause_review')
    assert.equal(causeLabel('classification_pending', null).kind, 'coverage')
    const unknown = causeLabel('confirmed_failure', 'some_future_code')
    assert.equal(unknown.label, 'some_future_code')
    assert.equal(unknown.kind, 'other')
    assert.equal(currentStageLabel('escalated'), 'Escalated')
    assert.match(currentStageLabel('weird'), /Unknown stage/)
  })
})

// ── Stages and delivery ─────────────────────────────────────────────────────────

describe('dashboard: six handoff stages', () => {
  test('all six stages are shown in order with their own timestamps', () => {
    const rows = stageRows(view({ stage: 'decision_made', deliveryState: 'acknowledged', stages: ALL_STAGES }))
    assert.deepEqual(rows.map(r => r.key), ['stored', 'enqueued', 'delivered', 'acknowledged', 'decision_made', 'unresolved'])
    assert.deepEqual(rows.map(r => r.label), Object.values(STAGE_LABELS))
    assert.deepEqual(rows.slice(0, 5).map(r => r.at), Object.values(ALL_STAGES))
    assert.deepEqual(rows.slice(0, 5).map(r => r.state), ['done', 'done', 'done', 'done', 'done'])
    assert.equal(rows[5].state, 'not_reached')
  })

  test('a stored-only failure is waiting for a batch and later stages are not reached', () => {
    const rows = stageRows(view())
    assert.deepEqual(rows.map(r => r.state), ['done', 'pending', 'not_reached', 'not_reached', 'not_reached', 'not_reached'])
  })

  test('unknown delivery is its own state and does not imply delivered or acknowledged', () => {
    const v = view({ stage: 'unresolved', deliveryState: 'unknown', stages: { ...ALL_STAGES, deliveredAt: null, acknowledgedAt: null, decisionMadeAt: null } })
    const rows = stageRows(v)
    assert.equal(rows[2].state, 'unknown')
    assert.equal(rows[3].state, 'not_reached')
    assert.equal(rows[5].state, 'attention')
    assert.equal(deliveryVisual(v).state, 'unknown')
  })

  test('pending, failed and unknown delivery are distinct', () => {
    const enq = { ...ALL_STAGES, deliveredAt: null, acknowledgedAt: null, decisionMadeAt: null }
    const pending = deliveryVisual(view({ stage: 'enqueued', deliveryState: 'pending', stages: enq }))
    const failed = deliveryVisual(view({ stage: 'escalated', deliveryState: 'pending', stages: enq }))
    const overdue = deliveryVisual(view({ stage: 'enqueued', deliveryState: 'pending', stages: enq, attentionOverdue: true }))
    const unknown = deliveryVisual(view({ deliveryState: 'unknown' }))
    assert.equal(pending.state, 'pending')
    assert.equal(failed.state, 'failed')
    assert.equal(overdue.state, 'failed')
    assert.equal(unknown.state, 'unknown')
    assert.equal(new Set([pending.label, failed.label, unknown.label]).size, 3)
    const failedRows = stageRows(view({ stage: 'escalated', deliveryState: 'pending', stages: enq }))
    assert.equal(failedRows[2].state, 'failed')
    assert.equal(failedRows[5].state, 'failed')
  })

  test('an unrecognised delivery value is shown as unknown, never as delivered', () => {
    const d = deliveryVisual(view({ deliveryState: 'maybe' }))
    assert.equal(d.state, 'unknown')
    assert.match(d.label, /not recognised/)
    // A missing deliveryState in the response parses as unknown too.
    const raw = { ...view() } as Record<string, unknown>
    delete raw.deliveryState
    assert.equal(asOutcomeView(raw)?.deliveryState, 'unknown')
  })

  test('coordinator-turn ambiguity marks acknowledgement unknown instead of inferring it', () => {
    const v = view({ stage: 'delivered', deliveryState: 'delivered', turnAmbiguous: true, stages: { ...ALL_STAGES, acknowledgedAt: null, decisionMadeAt: null } })
    const ack = stageRows(v)[3]
    assert.equal(ack.state, 'unknown')
    assert.match(ack.note, /turn ambiguous/)
    assert.equal(deliveryVisual(v).state, 'delivered')
  })

  test('a hold or wait decision keeps the item visible as unresolved with its due time', () => {
    const v = view({
      stage: 'decision_made', deliveryState: 'acknowledged', stages: ALL_STAGES,
      lastDecision: { operation: 'hold', owner: 'coordinator', reason: 'r', dueAt: '2026-10-25T12:00:00Z', status: 'accepted', decidedVia: 'operator', createdAt: '2026-10-24T11:06:00Z' },
    })
    const last = stageRows(v)[5]
    assert.equal(last.state, 'attention')
    assert.match(last.note, /hold until 2026-10-25T12:00:00Z/)
    assert.match(stageRows(v)[4].note, /hold by coordinator/)
  })

  test('acknowledgement is labelled as not an approval', () => {
    assert.match(deliveryVisual(view({ deliveryState: 'acknowledged' })).label, /not an approval/)
  })
})

describe('dashboard: due labels', () => {
  test('future, overdue, missing and unreadable due times', () => {
    assert.deepEqual(dueLabel('2026-10-24T14:30:00Z', false, NOW), { text: 'due in 2h 30m', overdue: false })
    assert.deepEqual(dueLabel('2026-10-24T11:45:00Z', false, NOW), { text: 'overdue by 15m', overdue: true })
    assert.deepEqual(dueLabel(null, false, NOW), { text: 'no due time', overdue: false })
    assert.deepEqual(dueLabel(null, true, NOW), { text: 'attention overdue', overdue: true })
    assert.equal(dueLabel('not-a-date', false, NOW).overdue, true)
  })
})

// ── Parsing ─────────────────────────────────────────────────────────────────────

describe('dashboard: response parsing never turns a malformed body into an empty list', () => {
  test('a well-formed page parses; an empty-string cursor means no next page', () => {
    const parsed = asListPage({ ...page([view()]), nextCursor: '' })
    assert.ok(parsed)
    assert.equal(parsed.items.length, 1)
    assert.equal(parsed.nextCursor, null)
    assert.equal(asListPage(page([view()], 'cursor-2'))?.nextCursor, 'cursor-2')
  })

  test('missing counts, missing namespaceErrors, a non-array items, a bad item or a numeric cursor are malformed', () => {
    const good = page([view()])
    assert.equal(asListPage({ ...good, counts: undefined }), null)
    assert.equal(asListPage({ ...good, counts: { failures: 1 } }), null)
    assert.equal(asListPage({ ...good, namespaceErrors: undefined }), null)
    assert.equal(asListPage({ ...good, items: {} }), null)
    assert.equal(asListPage({ ...good, items: [{ id: ID_A }] }), null)
    assert.equal(asListPage({ ...good, nextCursor: 5 }), null)
    assert.equal(asListPage(null), null)
    assert.equal(asListPage('<html>'), null)
  })

  test('detail keeps batch nonce/version, decision ids and evidence refs, and rejects a malformed batch', () => {
    const d = detail()
    const parsed = asOutcomeDetail(JSON.parse(JSON.stringify(d)))
    assert.ok(parsed)
    assert.equal(parsed.batches[0].nonce, 'nonce-example')
    assert.equal(parsed.batches[0].version, 2)
    assert.equal(parsed.decisions[0].decisionId, 'dddddddd-dddd-4ddd-8ddd-dddddddddddd')
    assert.deepEqual(parsed.evidenceRefs, d.evidenceRefs)
    const legacyId = asOutcomeDetail({ ...d, decisions: [{ ...d.decisions[0], decisionId: undefined, id: 'dec-1' }] })
    assert.equal(legacyId?.decisions[0].decisionId, 'dec-1')
    assert.equal(asOutcomeDetail({ ...d, batches: [{ batchId: BATCH }] }), null)
  })

  test('coverage, health and receipt require their core fields', () => {
    assert.ok(asCoverage(coverage()))
    assert.equal(asCoverage({ enabled: true, namespaces: [] }), null)
    assert.equal(asCoverage({ enabled: true, degraded: false, namespaces: [{ state: 'healthy' }] }), null)
    assert.ok(asHealth(HEALTHY))
    assert.equal(asHealth({ incidents: [] }), null)
    assert.ok(asReceipt(receipt()))
    assert.equal(asReceipt({ ...receipt(), deliveredVersions: [{ id: ID_A }] }), null)
    assert.equal(asReceipt({ ...receipt(), nonce: undefined }), null)
  })
})

// ── API client ──────────────────────────────────────────────────────────────────

describe('dashboard: API client uses the dashboard bearer and never requests payloads', () => {
  test('without a bearer nothing is requested and every call reports why', async () => {
    const decision = buildDecisionRequest(draft(), { version: 2 }, NOW)
    assert.ok(decision.ok)
    const results: ApiResult<unknown>[] = await Promise.all([
      fetchOutcomeList(null, { state: 'failures' }),
      fetchOutcomeDetail(null, ID_A),
      fetchCoverage(null),
      fetchHealth(null),
      fetchBatchReceipt(null, BATCH, 'n'),
      postAck(null, BATCH, { generation: 1, nonce: 'n', items: [{ id: ID_A, version: 1 }], taskId: null }),
      postDecision(null, ID_A, decision.body),
      postRecoveryPlan(null, ID_A, 'dec-1'),
    ])
    for (const r of results) {
      assert.equal(r.ok, false)
      if (!r.ok) { assert.equal(r.status, 0); assert.equal(r.error, NO_BEARER_ERROR) }
    }
  })

  test('list URLs always carry an explicit, clamped limit and encode namespace and cursor', () => {
    assert.equal(listUrl({ state: 'failures' }), `/api/workflow-outcomes?state=failures&limit=${DEFAULT_PAGE_LIMIT}`)
    assert.match(listUrl({ state: 'unresolved_cause', limit: 0 }), /limit=1(&|$)/)
    assert.match(listUrl({ state: 'failures', limit: 500 }), /limit=100(&|$)/)
    const u = new URL(listUrl({ state: 'all_attention', namespace: 'namespace a', cursor: 'c/+=', limit: 25 }), 'http://x')
    assert.equal(u.searchParams.get('namespace'), 'namespace a')
    assert.equal(u.searchParams.get('cursor'), 'c/+=')
    assert.equal(u.searchParams.get('limit'), '25')
    assert.equal(detailUrl('id/with?chars'), '/api/workflow-outcomes/id%2Fwith%3Fchars')
    assert.ok(!detailUrl(ID_A).includes('detail='))
  })

  test('requests go through the injected fetch; GETs have no body and no payload detail query', async () => {
    const { http, calls } = fakeHttp(url => {
      if (url.includes('/coverage')) return { status: 200, body: coverage() }
      if (url.includes('/health')) return { status: 200, body: HEALTHY }
      if (url.includes('/receipt')) return { status: 200, body: receipt() }
      if (url.startsWith('/api/workflow-outcomes?')) return { status: 200, body: page([view()], 'next-1') }
      return { status: 200, body: detail() }
    })
    const list = await fetchOutcomeList(http, { state: 'failures', cursor: 'c1' })
    assert.ok(list.ok && list.data.nextCursor === 'next-1')
    assert.ok((await fetchOutcomeDetail(http, ID_A)).ok)
    assert.ok((await fetchCoverage(http)).ok)
    assert.ok((await fetchHealth(http)).ok)
    assert.ok((await fetchBatchReceipt(http, BATCH, 'nonce example')).ok)
    assert.equal(calls.length, 5)
    for (const c of calls) {
      assert.equal(c.init?.method ?? 'GET', 'GET')
      assert.equal(c.init?.body, undefined)
      assert.ok(!c.url.includes('detail=payload'), c.url)
    }
    assert.equal(calls[4].url, `/api/workflow-outcomes/batches/${BATCH}/receipt?nonce=nonce%20example`)
  })

  test('POSTs send exactly the built JSON body to the exact route', async () => {
    const { http, calls } = fakeHttp(url => {
      if (url.endsWith('/ack')) return { status: 200, body: receipt() }
      if (url.endsWith('/decisions')) return { status: 201, body: { decisionId: 'dec-9', status: 'accepted' } }
      return { status: 200, body: { allowed: false, operation: 'retry', blockedReason: 'task_still_running', missingProofs: ['stop_proof'], checkedAt: '2026-10-24T12:00:00Z' } }
    })
    const ack = buildAckRequest(receipt(), [{ id: ID_B, version: 1 }])
    assert.ok(ack.ok)
    assert.ok((await postAck(http, BATCH, ack.body)).ok)
    const dec = buildDecisionRequest(draft(), { version: 2 }, NOW)
    assert.ok(dec.ok)
    const decRes = await postDecision(http, ID_A, dec.body)
    assert.ok(decRes.ok && decRes.data.decisionId === 'dec-9')
    const plan = await postRecoveryPlan(http, ID_A, 'dec-1')
    assert.ok(plan.ok && plan.data.allowed === false)
    assert.deepEqual(calls.map(c => [c.url, c.init?.method]), [
      [`/api/workflow-outcomes/batches/${BATCH}/ack`, 'POST'],
      [`/api/workflow-outcomes/${ID_A}/decisions`, 'POST'],
      [`/api/workflow-outcomes/${ID_A}/recovery-plan`, 'POST'],
    ])
    assert.deepEqual(JSON.parse(String(calls[0].init?.body)), ack.body)
    assert.deepEqual(JSON.parse(String(calls[1].init?.body)), dec.body)
    assert.deepEqual(JSON.parse(String(calls[2].init?.body)), { decisionId: 'dec-1' })
    assert.equal((calls[0].init?.headers as Record<string, string>)['Content-Type'], 'application/json')
  })

  test('error statuses explain themselves; a malformed 200 or a network error is an error, not an empty list', async () => {
    assert.match(outcomeErrorMessage(401, null), /bearer was refused/)
    assert.match(outcomeErrorMessage(403, null), /403/)
    assert.match(outcomeErrorMessage(409, { error: 'expected version 2, current 3' }), /Stale \(409\).*expected version 2/)
    assert.match(outcomeErrorMessage(422, { error: 'dueAt must be in the future' }), /422.*dueAt/)
    assert.match(outcomeErrorMessage(503, null), /disabled/)
    assert.match(outcomeErrorMessage(500, null, 'boom'), /HTTP 500: boom/)

    const malformed = fakeHttp(() => ({ status: 200, body: { items: [] } }))
    const r1 = await fetchOutcomeList(malformed.http, { state: 'failures' })
    assert.ok(!r1.ok && r1.error === MALFORMED_ERROR)
    const html = fakeHttp(() => ({ status: 200, body: '<html>proxy</html>' }))
    assert.ok(!(await fetchOutcomeList(html.http, { state: 'failures' })).ok)
    const down: OutcomeHttp = async () => { throw new Error('connection refused') }
    const r2 = await fetchOutcomeList(down, { state: 'failures' })
    assert.ok(!r2.ok && /connection refused/.test(r2.error))
    const stale = fakeHttp(() => ({ status: 409, body: { error: 'stale' } }))
    const decision = buildDecisionRequest(draft(), { version: 2 }, NOW)
    assert.ok(decision.ok)
    const r3 = await postDecision(stale.http, ID_A, decision.body)
    assert.ok(!r3.ok && r3.status === 409)
  })
})

// ── Paging ──────────────────────────────────────────────────────────────────────

describe('dashboard: keyset pagination never silently truncates', () => {
  test('first page replaces, next pages append, a repeated id is replaced by its newer version', () => {
    let p = applyOutcomePage(EMPTY_PAGING, page([view({ id: ID_A }), view({ id: ID_B })], 'c2'), null, 'failures')
    assert.equal(p.items.length, 2)
    assert.equal(p.nextCursor, 'c2')
    assert.equal(pagingComplete(p), false)
    assert.match(pagingSummary(p, 3), /Showing 2 of 3 — more pages available/)
    p = applyOutcomePage(p, page([view({ id: ID_B, version: 5 }), view({ id: ID_C })], null), 'c2', 'failures')
    assert.deepEqual(p.items.map(i => [i.id, i.version]), [[ID_A, 2], [ID_B, 5], [ID_C, 2]])
    assert.equal(p.loadedPages, 2)
    assert.equal(pagingComplete(p), true)
    assert.equal(pagingSummary(p, 3), 'All 3 shown')
    const restarted = applyOutcomePage(p, page([view({ id: ID_C })]), null, 'failures')
    assert.deepEqual(restarted.items.map(i => i.id), [ID_C])
  })

  test('traversing every page of a 120-item list keeps every id exactly once', async () => {
    const all = Array.from({ length: 120 }, (_, i) => view({ id: `00000000-0000-4000-8000-${String(i).padStart(12, '0')}` }))
    const { http, calls } = fakeHttp(url => {
      const q = new URL(url, 'http://x').searchParams
      const start = Number(q.get('cursor') ?? '0')
      const limit = Number(q.get('limit'))
      const next = start + limit < all.length ? String(start + limit) : null
      return { status: 200, body: { ...page(all.slice(start, start + limit), next), counts: { failures: 120, unresolvedCauses: 0, coverageGaps: 0 } } }
    })
    let p: OutcomePaging = EMPTY_PAGING
    let cursor: string | null = null
    do {
      const r = await fetchOutcomeList(http, { state: 'failures', cursor, limit: DEFAULT_PAGE_LIMIT })
      assert.ok(r.ok)
      p = applyOutcomePage(p, r.data, cursor, 'failures')
      cursor = p.nextCursor
    } while (cursor)
    assert.equal(calls.length, 3)
    assert.equal(p.items.length, 120)
    assert.equal(new Set(p.items.map(i => i.id)).size, 120)
    assert.equal(pagingSummary(p, 120), 'All 120 shown')
  })

  test('a server that repeats the cursor stops paging with a visible incomplete state', () => {
    const p = applyOutcomePage(applyOutcomePage(EMPTY_PAGING, page([view()], 'same'), null, 'failures'), page([view({ id: ID_B })], 'same'), 'same', 'failures')
    assert.equal(p.nextCursor, null)
    assert.ok(p.error)
    assert.equal(pagingComplete(p), false)
    assert.match(pagingSummary(p, null), /incomplete/)
  })

  test('a failed page keeps loaded items and the cursor, and is never reported as complete', () => {
    const loaded = applyOutcomePage(EMPTY_PAGING, page([view()], 'c2'), null, 'failures')
    const failed = pagingFailed(loaded, 'HTTP 500')
    assert.equal(failed.items.length, 1)
    assert.equal(failed.nextCursor, 'c2')
    assert.equal(pagingComplete(failed), false)
    assert.match(pagingSummary(failed, 10), /incomplete: HTTP 500/)
    assert.match(pagingSummary(pagingFailed(EMPTY_PAGING, 'HTTP 503'), null), /Not loaded: HTTP 503/)
    assert.doesNotMatch(pagingSummary(failed, 10), /All/)
  })

  test('the failure list shows only confirmed failures; the cause review list only unresolved causes', () => {
    const mixed = page([
      view({ id: ID_A, classification: 'confirmed_failure' }),
      view({ id: ID_B, classification: 'excluded_nonfailure', reasonCode: 'normal_completion' }),
      view({ id: ID_C, classification: 'cause_unresolved', reasonCode: 'abort_cause_unknown' }),
    ])
    const failures = applyOutcomePage(EMPTY_PAGING, mixed, null, 'failures')
    assert.deepEqual(failures.items.map(i => i.id), [ID_A])
    assert.equal(failures.hidden, 2)
    const causes = applyOutcomePage(EMPTY_PAGING, mixed, null, 'unresolved_cause')
    assert.deepEqual(causes.items.map(i => i.id), [ID_C])
    assert.equal(causes.hidden, 2)
    for (const c of ['insufficient_evidence', 'classification_pending'])
      assert.equal(applyOutcomePage(EMPTY_PAGING, page([view({ classification: c })]), null, 'unresolved_cause').items.length, 1, c)
  })
})

// ── Banner ──────────────────────────────────────────────────────────────────────

describe('dashboard: zero results while degraded is an error banner, never all clear', () => {
  const degradedCases: [string, Partial<BannerInput>][] = [
    ['a degraded namespace', { coverage: coverage({ namespaces: [{ ...coverage().namespaces[0], state: 'degraded' }] }) }],
    ['a pending namespace', { coverage: coverage({ namespaces: [{ ...coverage().namespaces[0], state: 'pending', knownPending: 3 }] }) }],
    ['observer-reported degradation', { coverage: coverage({ degraded: true }) }],
    ['a namespace read error', { namespaceErrors: [{ namespace: 'namespace-b', error: 'visibility unavailable' }] }],
    ['monitoring disabled', { coverage: coverage({ enabled: false }) }],
    ['no enrolled namespace', { coverage: coverage({ namespaces: [] }) }],
    ['coverage request failed', { coverage: null, coverageError: 'HTTP 503' }],
    ['coverage not loaded', { coverage: null }],
    ['a coverage gap', { coverage: coverage({ namespaces: [{ ...coverage().namespaces[0], gaps: [{ start: '2026-10-01T00:00:00Z', end: '2026-10-02T00:00:00Z', reason: 'retention_expired', unknownRunCount: 7 }] }] }) }],
    ['a gap count without gap rows', { counts: { failures: 0, unresolvedCauses: 0, coverageGaps: 2 } }],
    ['degraded health', { health: { state: 'degraded', incidents: [{ component: 'observer', outageKey: 'k', state: 'open', reason: 'db unavailable', namespace: null, firstSeenAt: null, lastSeenAt: null }] } }],
    ['health request failed', { health: null, healthError: 'HTTP 500' }],
    ['the list failed', { listErrors: ['HTTP 500'] }],
  ]
  for (const [name, overrides] of degradedCases) {
    test(`zero items with ${name} is an error banner`, () => {
      const b = outcomeBanner(bannerInput(overrides))
      assert.equal(b.level, 'error')
      assert.match(b.title, /NOT all clear/)
      assert.ok(b.details.length > 0)
    })
  }

  test('gap details carry the namespace and unknown-run count', () => {
    const cov = coverage({ namespaces: [{ ...coverage().namespaces[0], gaps: [{ start: null, end: null, reason: 'outage', unknownRunCount: 7 }] }] })
    const b = outcomeBanner(bannerInput({ coverage: cov }))
    assert.ok(b.details.some(d => /namespace-a: 1 coverage gap\(s\), 7 unknown run\(s\)/.test(d)))
    assert.deepEqual(coverageGapRows(cov).map(g => [g.namespace, g.unknownRunCount]), [['namespace-a', 7]])
  })

  test('items with degradation still show an error banner about possibly missing items', () => {
    const b = outcomeBanner(bannerInput({ itemCount: 2, coverage: coverage({ degraded: true }) }))
    assert.equal(b.level, 'error')
    assert.match(b.title, /may be missing items/)
  })

  test('all clear only when everything is loaded, healthy, gap-free and zero', () => {
    assert.equal(outcomeBanner(bannerInput()).level, 'clear')
    assert.equal(outcomeBanner(bannerInput({ hasMore: true })).level, 'none')
    assert.equal(outcomeBanner(bannerInput({ counts: null })).level, 'none')
    assert.equal(outcomeBanner(bannerInput({ itemCount: 1, counts: { failures: 1, unresolvedCauses: 0, coverageGaps: 0 } })).level, 'none')
    assert.equal(outcomeBanner(bannerInput({ health: null })).level, 'clear')
  })
})

// ── Side-nav ────────────────────────────────────────────────────────────────────

describe('dashboard: side-nav counts failure items and separately causes and gaps, never success', () => {
  const okList = (over: Partial<WorkflowOutcomeListPage> = {}): ApiResult<WorkflowOutcomeListPage> =>
    ({ ok: true, status: 200, data: page([], null, { counts: { failures: 4, unresolvedCauses: 2, coverageGaps: 1 }, ...over }) })
  const okCov: ApiResult<WorkflowOutcomeCoverage> = { ok: true, status: 200, data: coverage() }

  test('counts come from the server totals, not the page length', () => {
    const s = navSummaryFrom(okList(), okCov)
    assert.deepEqual(s, { status: 'ok', failures: 4, causes: 2, gaps: 1, degraded: false })
    const badges = navBadges(s)
    assert.deepEqual(badges.map(b => [b.key, b.label, b.value, b.attention]), [
      ['failures', '', '4', true], ['causes', 'cause', '2', true], ['gaps', 'gap', '1', true],
    ])
    for (const b of badges) assert.doesNotMatch(`${b.label} ${b.value} ${b.title}`, /success|receipt|delivered|acknowledged/i)
    assert.equal(navSummaryNote(s), null)
  })

  test('namespace errors or missing coverage mark the counts degraded', () => {
    const s1 = navSummaryFrom(okList({ namespaceErrors: [{ namespace: 'namespace-b', error: 'e' }] }), okCov)
    assert.ok(s1.status === 'ok' && s1.degraded)
    const s2 = navSummaryFrom(okList(), { ok: false, status: 500, error: 'HTTP 500' })
    assert.ok(s2.status === 'ok' && s2.degraded)
    const zero = navSummaryFrom({ ok: true, status: 200, data: page([]) }, { ok: true, status: 200, data: coverage({ degraded: true }) })
    assert.equal(navBadges(zero).find(b => b.key === 'gaps')?.value, '0!')
    assert.equal(navBadges(zero).find(b => b.key === 'gaps')?.attention, true)
    assert.match(navSummaryNote(zero) ?? '', /zero is not all clear/)
  })

  test('disabled monitoring shows "off"; an auth or server error shows "!"', () => {
    const off = navSummaryFrom({ ok: false, status: 503, error: 'disabled' }, null)
    assert.equal(off.status, 'off')
    assert.deepEqual(navBadges(off).map(b => b.value), ['off'])
    const err = navSummaryFrom({ ok: false, status: 401, error: 'refused' }, null)
    assert.equal(err.status, 'error')
    assert.deepEqual(navBadges(err).map(b => [b.value, b.attention]), [['!', true]])
    assert.deepEqual(navBadges(null), [])
  })

  test('the side-nav poll asks for one row plus coverage, and nothing without a bearer', async () => {
    const { http, calls } = fakeHttp(url => url.includes('/coverage')
      ? { status: 200, body: coverage() }
      : { status: 200, body: page([view()], 'more', { counts: { failures: 9, unresolvedCauses: 1, coverageGaps: 0 } }) })
    const s = await loadNavSummary(http)
    assert.deepEqual(s, { status: 'ok', failures: 9, causes: 1, gaps: 0, degraded: false })
    assert.deepEqual(calls.map(c => c.url).sort(), ['/api/workflow-outcomes/coverage', '/api/workflow-outcomes?state=failures&limit=1'])
    const none = await loadNavSummary(null)
    assert.equal(none.status, 'error')
  })
})

// ── Acknowledgement ─────────────────────────────────────────────────────────────

describe('dashboard: acknowledge selected exact versions only', () => {
  test('ackable members are delivered and not yet acknowledged at that version', () => {
    assert.deepEqual(ackableMembers(receipt()), [{ id: ID_A, version: 2 }, { id: ID_B, version: 1 }])
    assert.deepEqual(ackableMembers(receipt({ ackedVersions: [{ id: ID_C, version: 3 }] })).map(r => r.id), [ID_A, ID_B, ID_C])
  })

  test('the body holds only the selected ids and versions, in delivered order, with generation and nonce', () => {
    const r = buildAckRequest(receipt(), [{ id: ID_B, version: 1 }, { id: ID_A, version: 2 }])
    assert.ok(r.ok)
    assert.deepEqual(r.body, { generation: 3, nonce: 'nonce-example', items: [{ id: ID_A, version: 2 }, { id: ID_B, version: 1 }], taskId: null })
    const one = buildAckRequest(receipt(), [{ id: ID_B, version: 1 }])
    assert.ok(one.ok)
    assert.deepEqual(one.body.items, [{ id: ID_B, version: 1 }])
  })

  test('stale, foreign, already acknowledged, duplicate, empty and oversized selections are refused', () => {
    const cases: [string, Parameters<typeof buildAckRequest>[1], RegExp][] = [
      ['stale', [{ id: ID_A, version: 1 }], /delivered at version 2, not 1/],
      ['foreign', [{ id: '99999999-9999-4999-8999-999999999999', version: 1 }], /not delivered in this batch/],
      ['already acked', [{ id: ID_C, version: 4 }], /already acknowledged/],
      ['duplicate', [{ id: ID_A, version: 2 }, { id: ID_A, version: 2 }], /selected twice/],
      ['empty', [], /Select at least one/],
      ['oversized', Array.from({ length: MAX_BATCH_ITEMS + 1 }, (_, i) => ({ id: `id-${i}`, version: 1 })), /At most 20/],
    ]
    for (const [name, sel, re] of cases) {
      const r = buildAckRequest(receipt(), sel)
      assert.equal(r.ok, false, name)
      if (!r.ok) assert.match(r.error, re, name)
    }
  })

  test('a batch without a delivered receipt or nonce cannot be acknowledged', () => {
    const undelivered = buildAckRequest(receipt({ deliveredAt: null, deliveredVersions: [] }), [{ id: ID_A, version: 2 }])
    assert.ok(!undelivered.ok && /no delivered receipt/.test(undelivered.error))
    const batch = detail().batches[0]
    assert.equal(batchReviewBlocked(batch), null)
    assert.match(batchReviewBlocked({ ...batch, nonce: null }) ?? '', /nonce/)
    assert.match(batchReviewBlocked({ ...batch, deliveredAt: null }) ?? '', /Not delivered/)
  })
})

// ── Decisions ───────────────────────────────────────────────────────────────────

describe('dashboard: record-only decisions mirror the server limits', () => {
  test('the dashboard offers exactly hold, wait, accept late, finish and close', () => {
    assert.deepEqual(OPERATOR_DECISIONS.map(d => d.operation), ['hold', 'wait', 'accept_late', 'finish', 'close'])
    for (const op of ['retry', 'resume', 'reassign', 'cancel', 'approve', '']) {
      const r = buildDecisionRequest(draft({ operation: op, evidenceRefsText: 'task:t1' }), { version: 2 }, NOW)
      assert.ok(!r.ok && r.errors.some(e => /not a decision this dashboard records/.test(e)), op)
    }
  })

  test('hold and wait require a future due time', () => {
    for (const operation of ['hold', 'wait']) {
      const missing = buildDecisionRequest(draft({ operation, dueAt: '' }), { version: 2 }, NOW)
      assert.ok(!missing.ok && missing.errors.some(e => /needs a future due time/.test(e)), operation)
      const past = buildDecisionRequest(draft({ operation, dueAt: '2026-10-24T11:59:00Z' }), { version: 2 }, NOW)
      assert.ok(!past.ok && past.errors.includes('Due time must be in the future.'), operation)
      const exactlyNow = buildDecisionRequest(draft({ operation, dueAt: '2026-10-24T12:00:00Z' }), { version: 2 }, NOW)
      assert.equal(exactlyNow.ok, false, operation)
      const future = buildDecisionRequest(draft({ operation, dueAt: '2026-10-24T12:01:00+00:00' }), { version: 2 }, NOW)
      assert.ok(future.ok, operation)
      assert.equal(future.body.dueAt, '2026-10-24T12:01:00.000Z')
    }
  })

  test('the built body is exact: expected version, trimmed text, refs, no batch binding', () => {
    const r = buildDecisionRequest(draft({ owner: '  agent1 ', reason: ' owner is reviewing \n', evidenceRefsText: 'task:t1\n\n  artifact:a1  \n', taskRef: 'task:t1', artifactRef: '' }), { version: 7 }, NOW)
    assert.ok(r.ok)
    assert.deepEqual(r.body, {
      expectedVersion: 7, operation: 'hold', owner: 'agent1', reason: 'owner is reviewing', evidenceRefs: ['task:t1', 'artifact:a1'],
      dueAt: '2026-10-25T12:00:00.000Z', taskRef: 'task:t1', artifactRef: null, batchId: null, generation: null,
    })
  })

  test('owner is 1..200 characters and reason 1..2000 characters', () => {
    assert.ok(buildDecisionRequest(draft({ owner: 'o'.repeat(200) }), { version: 1 }, NOW).ok)
    assert.ok(!buildDecisionRequest(draft({ owner: 'o'.repeat(201) }), { version: 1 }, NOW).ok)
    assert.ok(!buildDecisionRequest(draft({ owner: '   ' }), { version: 1 }, NOW).ok)
    assert.ok(buildDecisionRequest(draft({ reason: 'r'.repeat(2000) }), { version: 1 }, NOW).ok)
    const long = buildDecisionRequest(draft({ reason: 'r'.repeat(2001) }), { version: 1 }, NOW)
    assert.ok(!long.ok && long.errors.some(e => /2001 characters; the limit is 2000/.test(e)))
    assert.ok(!buildDecisionRequest(draft({ reason: '' }), { version: 1 }, NOW).ok)
  })

  test('evidence refs follow the server rule: known prefix, ≤500 chars, no control chars, ≤10 refs', () => {
    assert.ok(isEvidenceRef('late-result:abc'))
    assert.ok(isEvidenceRef('https://example.invalid/x'))
    for (const bad of ['https://', 'ftp://x', 'note: hi', ' task:x', 'task:x\u0007', `task:${'x'.repeat(496)}`, ''])
      assert.equal(isEvidenceRef(bad), false, JSON.stringify(bad))
    assert.ok(isEvidenceRef(`task:${'x'.repeat(495)}`))
    const eleven = Array.from({ length: 11 }, (_, i) => `task:t${i}`).join('\n')
    assert.ok(!buildDecisionRequest(draft({ evidenceRefsText: eleven }), { version: 1 }, NOW).ok)
    const ten = Array.from({ length: 10 }, (_, i) => `task:t${i}`).join('\n')
    assert.ok(buildDecisionRequest(draft({ evidenceRefsText: ten }), { version: 1 }, NOW).ok)
    assert.ok(!buildDecisionRequest(draft({ evidenceRefsText: 'task:a\ntask:a' }), { version: 1 }, NOW).ok)
    assert.ok(!buildDecisionRequest(draft({ taskRef: 'not-a-ref' }), { version: 1 }, NOW).ok)
  })

  test('accept late needs a late-result ref; finish and close need evidence', () => {
    const late = buildDecisionRequest(draft({ operation: 'accept_late', dueAt: '', evidenceRefsText: 'artifact:a1' }), { version: 1 }, NOW)
    assert.ok(!late.ok && late.errors.some(e => /late-result:/.test(e)))
    assert.ok(buildDecisionRequest(draft({ operation: 'accept_late', dueAt: '', evidenceRefsText: 'late-result:r1' }), { version: 1 }, NOW).ok)
    for (const operation of ['finish', 'close']) {
      assert.ok(!buildDecisionRequest(draft({ operation, dueAt: '' }), { version: 1 }, NOW).ok, operation)
      const ok = buildDecisionRequest(draft({ operation, dueAt: '', evidenceRefsText: 'artifact:a1' }), { version: 1 }, NOW)
      assert.ok(ok.ok && ok.body.dueAt === null, operation)
    }
  })

  test('due-time parsing accepts local datetime-local values and explicit offsets only', () => {
    const local = parseDueAt('2026-10-25T09:30')
    assert.ok(local)
    assert.equal(local, new Date('2026-10-25T09:30').toISOString())
    assert.equal(parseDueAt('2026-10-25T09:30:00-05:00'), '2026-10-25T14:30:00.000Z')
    for (const bad of ['tomorrow', '2026-10-25', '2026-13-01T00:00', '2026-10-25 09:30'])
      assert.equal(parseDueAt(bad), null, bad)
    assert.equal(parseDueAt('  '), null)
  })

  test('a decision needs a valid item version', () => {
    assert.ok(!buildDecisionRequest(draft(), { version: 0 }, NOW).ok)
  })
})

// ── Recovery ────────────────────────────────────────────────────────────────────

describe('dashboard: plan recovery is a read-only check', () => {
  test('only accepted recovery decisions with an id can be planned', () => {
    const base = detail().decisions[0]
    const ds = [
      base,
      { ...base, decisionId: 'd2', operation: 'hold' },
      { ...base, decisionId: 'd3', status: 'rejected' },
      { ...base, decisionId: null, operation: 'cancel' },
      { ...base, decisionId: 'd5', operation: 'accept_late' },
    ]
    assert.deepEqual(recoveryCandidates(ds).map(d => d.decisionId), [base.decisionId, 'd5'])
  })

  test('allowed and blocked results both say nothing was reserved, approved or started', () => {
    const allowed = recoveryPlanSummary({ allowed: true, operation: 'retry', blockedReason: null, missingProofs: [], checkedAt: '2026-10-24T12:00:00Z' })
    assert.equal(allowed.allowed, true)
    assert.match(allowed.title, /Read-only check passed for retry/)
    const blocked = recoveryPlanSummary({ allowed: false, operation: 'retry', blockedReason: 'task_still_running', missingProofs: ['stop_proof', 'late_reply_check'], checkedAt: null })
    assert.match(blocked.title, /Blocked: task_still_running/)
    assert.ok(blocked.lines.some(l => /stop_proof, late_reply_check/.test(l)))
    for (const s of [allowed, blocked]) assert.ok(s.lines.some(l => /reserved, approved and started nothing/.test(l)))
  })
})

// ── Rendering (actual components through the installed JSX compiler; no HTTP) ──

const ROOT = fileURLToPath(new URL('..', import.meta.url))

function buttons(markup: string): string[] {
  return [...markup.matchAll(/<button[^>]*>(.*?)<\/button>/g)].map(m => m[1].replace(/<[^>]+>/g, ''))
}

describe('dashboard: rendered panel', () => {
  test('lists, banner, detail, ack members and plan result render with distinct states and no execution control', async () => {
    const server = await createServer({ root: ROOT, configFile: false, server: { middlewareMode: true, hmr: false, watch: null }, appType: 'custom' })
    try {
      const mod = await server.ssrLoadModule('/src/components/WorkflowOutcomesPanel.tsx')
      const { default: Panel, OutcomeList, OutcomeBannerView, OutcomeDetailView, BatchMembers, RecoveryPlan, CountChips, CoverageSection } = mod

      // Panel shell renders before any data (effects do not run on the server).
      const shell = renderToStaticMarkup(createElement(Panel, { http: null }))
      assert.match(shell, /Workflow Failures/)
      assert.match(shell, /Confirmed failures/)
      assert.match(shell, /Unresolved-cause review/)
      // Nothing loaded yet is reported as incomplete, never as all clear.
      assert.match(shell, /NOT all clear/)
      assert.doesNotMatch(shell, /class="wo-banner wo-banner-clear"/)
      assert.match(shell, /class="wo-banner wo-banner-error"/)

      // List with a next page: explicit "Load next", no evidence refs or payloads in the list.
      const items = [
        view({ stage: 'escalated', deliveryState: 'pending', attentionOverdue: true, stages: { ...ALL_STAGES, deliveredAt: null, acknowledgedAt: null, decisionMadeAt: null } }),
        view({ id: ID_B, reasonCode: 'business_review_cap', deliveryState: 'unknown', stage: 'unresolved' }),
        view({ id: ID_C, deliveryState: 'delivered', turnAmbiguous: true, stage: 'delivered', stages: { ...ALL_STAGES, acknowledgedAt: null, decisionMadeAt: null } }),
      ]
      const paging = applyOutcomePage(EMPTY_PAGING, page(items, 'c2'), null, 'failures')
      const list = renderToStaticMarkup(createElement(OutcomeList, {
        title: 'Confirmed failures', note: 'n', paging, serverCount: 7, loading: false, nowMs: NOW,
        onOpen: () => {}, onMore: () => {}, onReload: () => {},
      }))
      assert.match(list, /Showing 3 of 7 — more pages available/)
      assert.match(list, new RegExp(`Load next ${DEFAULT_PAGE_LIMIT}`))
      assert.match(list, /wo-delivery-failed/)
      assert.match(list, /wo-delivery-unknown/)
      assert.match(list, /wo-turn-ambiguous/)
      assert.match(list, /wo-cause-transport/)
      assert.match(list, /wo-cause-business/)
      assert.match(list, /task owner agent1/)
      assert.equal((list.match(/data-outcome-id=/g) ?? []).length, 3)
      assert.doesNotMatch(list, /artifact:|event:|payload|success/i)

      const banner = renderToStaticMarkup(createElement(OutcomeBannerView, { banner: outcomeBanner(bannerInput({ coverage: coverage({ degraded: true }) })) }))
      assert.match(banner, /role="alert"/)
      assert.match(banner, /NOT all clear/)
      assert.equal(renderToStaticMarkup(createElement(OutcomeBannerView, { banner: { level: 'none', title: '', details: [] } })), '')

      const chips = renderToStaticMarkup(createElement(CountChips, { counts: { failures: 2, unresolvedCauses: 1, coverageGaps: 3 } }))
      assert.match(chips, /2 failure item\(s\).*1 cause review\(s\).*3 coverage gap\(s\)/)
      assert.doesNotMatch(chips, /success|receipt/i)

      const gapCov = coverage({ namespaces: [{ ...coverage().namespaces[0], state: 'degraded', gaps: [{ start: null, end: null, reason: 'retention_expired', unknownRunCount: 4 }] }] })
      const cov = renderToStaticMarkup(createElement(CoverageSection, { coverage: gapCov }))
      assert.match(cov, /retention_expired/)
      assert.match(cov, /1 gap\(s\)/)

      // Detail: six stages, chain links, batch membership, refs (not payloads), decision audit.
      const d = detail()
      const det = renderToStaticMarkup(createElement(OutcomeDetailView, { detail: d, nowMs: NOW, http: null, onChanged: () => {} }))
      assert.deepEqual([...det.matchAll(/data-stage="([a-z_]+)"/g)].map(m => m[1]), ['stored', 'enqueued', 'delivered', 'acknowledged', 'decision_made', 'unresolved'])
      assert.match(det, new RegExp(`data-batch-id="${BATCH}"`))
      assert.match(det, /wf-parent-example \/ run-parent-0001/)
      assert.match(det, /wf-example-1 \/ run-0000-example/)
      assert.match(det, /artifact:example-artifact/)
      assert.match(det, /href="https:\/\/example.invalid\/owner\/repo\/pull\/1"[^>]*rel="noreferrer"/)
      assert.match(det, /retry after the owner stops the task/)
      assert.match(det, /Owner \(coordinator\)/)
      assert.match(det, /Task owner/)
      for (const op of ['Hold', 'Wait', 'Accept late', 'Finish', 'Close']) assert.match(det, new RegExp(`<option value="[a-z_]+"[^>]*>${op}</option>`))
      const detButtons = buttons(det)
      assert.ok(detButtons.includes('Review members'))
      assert.ok(detButtons.includes('Plan recovery'))
      for (const b of detButtons) assert.doesNotMatch(b, /approv|execut|dispatch|reserve|start|retry|cancel workflow/i, b)

      // Members of a delivered batch: only delivered, unacknowledged exact versions are selectable.
      const members = renderToStaticMarkup(createElement(BatchMembers, {
        batch: d.batches[0], outcomeId: ID_A, http: null, onAcked: () => {}, initialReceipt: { ok: true, status: 200, data: receipt() },
      }))
      const boxes = [...members.matchAll(/<input type="checkbox"([^>]*)\/>/g)].map(m => m[1])
      assert.equal(boxes.length, 3)
      assert.deepEqual(boxes.map(b => b.includes('disabled')), [false, false, true])
      assert.match(members, /this run/)
      assert.match(members, /Acknowledge selected \(0\)/)

      // Plan result: blocked reason and missing proofs; still no execution control.
      const plan = renderToStaticMarkup(createElement(RecoveryPlan, {
        outcomeId: ID_A, decision: d.decisions[0], http: null,
        initialResult: { ok: true, status: 200, data: { allowed: false, operation: 'retry', blockedReason: 'task_still_running', missingProofs: ['stop_proof'], checkedAt: null } },
      }))
      assert.match(plan, /Blocked: task_still_running/)
      assert.match(plan, /stop_proof/)
      assert.deepEqual(buttons(plan), ['Plan recovery'])
    } finally { await server.close() }
  })

  test('the Workflows view links to the failures panel with separate failure, cause and gap counts', async () => {
    const server = await createServer({ root: ROOT, configFile: false, server: { middlewareMode: true, hmr: false, watch: null }, appType: 'custom' })
    try {
      const { default: WorkflowsView } = await server.ssrLoadModule('/src/components/WorkflowsView.tsx')
      const noop = () => {}
      const props = {
        workflows: [], completedWorkflows: [], completedCollapsed: true, completedLoading: false, nsFilter: 'all', namespaces: [],
        filteredWorkflows: [], filteredCompletedWorkflows: [], agentByWorkflow: {}, wfActionStates: {}, wfActionMsg: {},
        signalStates: {}, signalMsg: {}, sentSignals: new Set(), signalRegistry: {}, wfMenuOpen: {}, selectedWf: null,
        wfHistory: [], wfHistoryLoading: false, wfHistoryError: null, expandedEvents: new Set(),
        onNsFilter: noop, onToggleCompleted: noop, onRefreshCompleted: noop, onWfClick: noop, onSignalClick: noop,
        onToggleMenu: noop, onToggleEvent: noop, onCloseDetail: noop, onRefreshDetail: noop, getSignalDefs: () => [],
        wfActionState: () => 'idle', signalKey: () => '', wfKey: () => '', wfStatusClass: () => '', onSendSignal: noop,
        onExecuteWfAction: noop, onStartWorkflow: noop, onRefreshWorkflows: noop, workflowsLoading: false,
      }
      const without = renderToStaticMarkup(createElement(WorkflowsView, props))
      assert.doesNotMatch(without, /workflow-failures-strip/)
      const withStrip = renderToStaticMarkup(createElement(WorkflowsView, {
        ...props, onOpenFailures: noop, failureSummary: { status: 'ok', failures: 3, causes: 1, gaps: 0, degraded: true },
      }))
      assert.match(withStrip, /data-testid="workflow-failures-strip"/)
      assert.match(withStrip, />3<\/span>/)
      assert.match(withStrip, />cause 1<\/span>/)
      assert.match(withStrip, />gap 0!<\/span>/)
      assert.match(withStrip, /zero is not all clear/)
      assert.ok(buttons(withStrip).includes('Open Workflow Failures'))
      assert.doesNotMatch(withStrip, /success/i)
    } finally { await server.close() }
  })
})

// ── Hooks order (repo rule: every hook runs before any early return) ─────────────

describe('dashboard: hooks are called before any return in every component', () => {
  // Components here use 2-space bodies: a top-level hook call or return sits at exactly 2 spaces;
  // returns inside nested callbacks are indented further and do not count.
  for (const file of ['components/WorkflowOutcomesPanel.tsx', 'components/WorkflowsView.tsx', 'App.tsx']) {
    test(`${file}: no hook after a top-level return`, () => {
      const lines = readFileSync(new URL(`./${file}`, import.meta.url), 'utf8').split('\n')
      let checked = 0
      for (let i = 0; i < lines.length; i++) {
        const m = /^(?:export )?(?:default )?function ([A-Z]\w*)\(/.exec(lines[i])
        if (!m) continue
        let firstReturn = -1
        let lastHook = -1
        for (let j = i + 1; j < lines.length && lines[j] !== '}'; j++) {
          if (/^ {2}(?:const [^=]+= )?use[A-Z]\w*\(/.test(lines[j])) lastHook = j
          if (firstReturn === -1 && /^ {2}(?:if \(.*\) )?return\b/.test(lines[j])) firstReturn = j
        }
        if (lastHook !== -1 && firstReturn !== -1)
          assert.ok(lastHook < firstReturn, `${file} ${m[1]}: hook on line ${lastHook + 1} after return on line ${firstReturn + 1}`)
        checked++
      }
      assert.ok(checked > 0, `${file}: no component found`)
    })
  }
})
