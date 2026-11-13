import { test } from 'node:test'
import assert from 'node:assert/strict'
import { humanInputRequestFromHistory, humanInputHistoryUrl, humanInputIdentity } from './humanInputRequest.ts'

const gate = { namespace: 'fleet', workflowId: 'human-input-example', runId: 'run-1', gateRef: 'run-1',
  workflowType: 'UweHumanInputWorkflow', status: 'Running', phase: 'request-input' }
const request = { Question: ' "read"\nКыргыз \\ {{config.Secret}} ', Context: 'report only', AnswerMode: 'approve-reject' as const, Origin: { Kind: 'agent' as const, RequesterAgent: 'agent1' } }
const history = [{ eventType: 'WorkflowStarted', inputSummary: JSON.stringify(request) }]
test('exact literal request comes only from the rendered run history', () => {
  assert.deepEqual(humanInputRequestFromHistory(gate, history), request)
  assert.match(humanInputHistoryUrl(gate), /\?runId=run-1$/)
  assert.notEqual(humanInputIdentity(gate), humanInputIdentity({ ...gate, runId: 'run-2' }))
})
test('closed, wrong type, unbound and malformed histories block confirmation', () => {
  for (const changed of [{ status: 'Completed' }, { gateRef: null }, { gateRef: 'run-2' }, { runId: '' }, { phase: '' }, { workflowType: 'Other' }])
    assert.throws(() => humanInputRequestFromHistory({ ...gate, ...changed }, history))
  for (const events of [[], [{ eventType: 'WorkflowStarted', inputSummary: '{}' }],
    [{ eventType: 'WorkflowStarted', inputSummary: JSON.stringify(request).slice(0, 20) + '…' }],
    [{ eventType: 'WorkflowStarted', inputSummary: JSON.stringify({ ...request, Origin: null }) }]])
    assert.throws(() => humanInputRequestFromHistory(gate, events))
})

test('all modes require an explicit valid answer and literal text stays untrimmed', async () => {
  const { validHumanAnswer } = await import('./humanInputRequest.ts')
  for (const mode of ['text', 'choice', 'approve-reject'] as const) {
    const r = { ...request, AnswerMode: mode, ...(mode === 'choice' ? { Choices: [{ Id: 'first', Label: 'First' }, { Id: 'other', Label: 'Other' }] } : {}) }
    assert.equal(validHumanAnswer(r, ''), false)
    assert.equal(validHumanAnswer(r, mode === 'text' ? ' \nliteral ' : mode === 'choice' ? 'first' : 'approved'), true)
    assert.equal(validHumanAnswer(r, mode === 'text' ? 'x'.repeat(2001) : 'undeclared'), false)
  }
})
