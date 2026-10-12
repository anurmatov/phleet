import test from 'node:test'
import assert from 'node:assert/strict'
import { buildSignalRequest, signalErrorMessage } from './signalRequest.ts'

test('signal request binds the rendered run and gate, not a newer snapshot', () => {
  const rendered = { workflowType: 'UwePrImplementationWorkflow', runId: 'run-old', gateRef: 'merge:head:2' }
  assert.deepEqual(buildSignalRequest(rendered, 'merge-approval', '{"Decision":"approved"}'), {
    signalName: 'merge-approval', workflowType: rendered.workflowType,
    payload: '{"Decision":"approved"}', runId: 'run-old', expectedGateRef: 'merge:head:2',
  })
})
test('legacy rendered gate remains null and comments preserve the decision', () => {
  const request = buildSignalRequest({ workflowType: 'Legacy', runId: 'run-1', gateRef: null },
    'merge-approval', '{"Decision":"changes_requested"}', 'revise')
  assert.equal(request.expectedGateRef, null)
  assert.deepEqual(JSON.parse(request.payload), { Decision: 'changes_requested', Comment: 'revise' })
})
test('409 reports a refresh, never a retry or a different gate submission', () => {
  assert.equal(signalErrorMessage(409, { error: 'stale_gate' }), 'This gate changed. Refresh before deciding.')
  assert.equal(signalErrorMessage(503, { error: 'describe_unavailable' }), 'describe_unavailable')
  assert.equal(signalErrorMessage(500, {}), 'Error 500')
  assert.equal(signalErrorMessage(500, null), 'Error 500')
})
