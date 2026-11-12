import { test } from 'node:test'
import assert from 'node:assert/strict'
import { approvalRequestFromHistory, approvalHistoryUrl, approvalIdentity } from './approvalRequest.ts'

const gate = { namespace: 'fleet', workflowId: 'human-approval-example', runId: 'run-1', gateRef: 'run-1',
  workflowType: 'UweHumanApprovalRequestWorkflow', status: 'Running', phase: 'request-approval' }
const request = { Action: ' \"read\"\nКыргыз \\ {{config.Secret}} ', Scope: 'report only', RequesterAgent: 'agent1' }
const history = [{ eventType: 'WorkflowStarted', inputSummary: JSON.stringify(request) }]
test('exact literal request comes only from the rendered run history', () => {
  assert.deepEqual(approvalRequestFromHistory(gate, history), request)
  assert.match(approvalHistoryUrl(gate), /\?runId=run-1$/)
  assert.notEqual(approvalIdentity(gate), approvalIdentity({ ...gate, runId: 'run-2' }))
})
test('closed, wrong type, unbound and malformed histories block confirmation', () => {
  for (const changed of [{ status: 'Completed' }, { gateRef: null }, { gateRef: 'run-2' }, { runId: '' }, { phase: '' }, { workflowType: 'Other' }])
    assert.throws(() => approvalRequestFromHistory({ ...gate, ...changed }, history))
  for (const events of [[], [{ eventType: 'WorkflowStarted', inputSummary: '{}' }],
    [{ eventType: 'WorkflowStarted', inputSummary: JSON.stringify(request).slice(0, 20) + '…' }],
    [{ eventType: 'WorkflowStarted', inputSummary: JSON.stringify({ ...request, RequesterAgent: null }) }]])
    assert.throws(() => approvalRequestFromHistory(gate, events))
})
