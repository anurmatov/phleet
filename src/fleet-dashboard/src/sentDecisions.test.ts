import { test } from 'node:test'
import assert from 'node:assert/strict'
import type { WorkflowSummary } from './types.ts'
import { pruneDecisions, sentDecision, withDecision, withoutDecision } from './sentDecisions.ts'

function wf(over: Partial<WorkflowSummary> = {}): WorkflowSummary {
  return {
    workflowId: 'wf-1', runId: 'run-1', workflowType: 'ReviewWorkflow', namespace: 'fleet', taskQueue: null,
    status: 'Running', startTime: '2026-01-01T00:00:00Z', closeTime: null, issueNumber: null, prNumber: null,
    repo: null, docPrs: null, phase: 'design-review', gateRef: null, ...over,
  }
}

test('a sent decision is known for that run in that phase only', () => {
  const sent = withDecision({}, wf(), 'Approve')
  assert.equal(sentDecision(sent, wf()), 'Approve')
  assert.equal(sentDecision(sent, wf({ phase: 'merge-review' })), null)
  assert.equal(sentDecision(sent, wf({ runId: 'run-2' })), null)
  assert.equal(sentDecision(sent, wf({ namespace: 'other' })), null)
  assert.equal(sentDecision(sent, wf({ workflowId: 'wf-2' })), null)
})

test('a later decision for the same run replaces the earlier one', () => {
  const sent = withDecision(withDecision({}, wf(), 'Approve'), wf({ phase: 'merge-review' }), 'Reject')
  assert.equal(sentDecision(sent, wf()), null)
  assert.equal(sentDecision(sent, wf({ phase: 'merge-review' })), 'Reject')
})

test('taking a decision back offers the buttons again, and leaves the others', () => {
  const sent = withDecision(withDecision({}, wf(), 'Approve'), wf({ workflowId: 'wf-2' }), 'Reject')
  const next = withoutDecision(sent, wf())
  assert.equal(sentDecision(next, wf()), null)
  assert.equal(sentDecision(next, wf({ workflowId: 'wf-2' })), 'Reject')
  assert.equal(withoutDecision(next, wf()), next)
})

test('a decision lasts while the run stays in that phase, and the object is kept when nothing ends', () => {
  const sent = withDecision({}, wf(), 'Approve')
  assert.equal(pruneDecisions(sent, [wf(), wf({ workflowId: 'wf-2' })]), sent)
})

test('a decision ends when the run moves on, is replaced, stops or leaves the list', () => {
  const sent = withDecision({}, wf(), 'Approve')
  assert.deepEqual(pruneDecisions(sent, [wf({ phase: 'building' })]), {})
  assert.deepEqual(pruneDecisions(sent, [wf({ runId: 'run-2' })]), {})
  assert.deepEqual(pruneDecisions(sent, [wf({ status: 'Completed' })]), {})
  assert.deepEqual(pruneDecisions(sent, []), {})
  assert.deepEqual(pruneDecisions(sent, [wf({ namespace: 'other' })]), {})
})

test('only the ended decisions are dropped', () => {
  const sent = withDecision(withDecision({}, wf(), 'Approve'), wf({ workflowId: 'wf-2' }), 'Reject')
  const next = pruneDecisions(sent, [wf({ phase: 'building' }), wf({ workflowId: 'wf-2' })])
  assert.equal(sentDecision(next, wf({ workflowId: 'wf-2' })), 'Reject')
  assert.equal(Object.keys(next).length, 1)
})
