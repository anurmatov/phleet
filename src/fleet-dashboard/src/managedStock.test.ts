import { test } from 'node:test'
import assert from 'node:assert/strict'
import { savedRuntimeSelection, policyResponse, managedRuntimeView, revokeOutcome, managedStatusLines } from './managedStock.ts'

test('save pins exact desired version and contains no applied fields', () => {
  assert.deepEqual(savedRuntimeSelection({ runtimeKind: 'openshell', sandboxPolicyVersionId: '17', nativeAuthMode: 'api_key', providerApiKeyEnvRef: 'TEST_API_KEY' }), {
    runtimeKind: 'openshell', sandboxPolicyVersionId: 17, nativeAuthMode: 'api_key', providerApiKeyEnvRef: 'TEST_API_KEY',
  })
})
test('empty version clears rather than picks latest', () => {
  assert.equal(savedRuntimeSelection({ sandboxPolicyVersionId: '' }).sandboxPolicyVersionId, 0)
})
test('API errors stay errors, not empty policy lists', async () => {
  await assert.rejects(policyResponse(new Response(JSON.stringify({ error: 'policy_version_conflict' }), { status: 409 })), /policy_version_conflict/)
  await assert.rejects(policyResponse(new Response('unavailable', { status: 503 })), /503/)
  assert.deepEqual(await policyResponse(new Response('[]')), [])
})

test('managed card view blocks start on unresolved holds and offers revoke only for applied bindings', () => {
  assert.deepEqual(managedRuntimeView({ runtimeKind: 'docker', appliedRuntimeKind: 'docker' }), { label: null, title: '', startBlocked: false, canRevoke: false })
  assert.deepEqual(managedRuntimeView({ runtimeKind: 'openshell', appliedRuntimeKind: 'docker' }),
    { label: 'openshell · pending apply', title: '', startBlocked: false, canRevoke: false })
  assert.deepEqual(managedRuntimeView({ appliedRuntimeKind: 'openshell', managedState: 'needs_attention', managedErrors: ['broker:x', 'stop:y'] }),
    { label: 'openshell · needs attention', title: 'broker:x, stop:y', startBlocked: true, canRevoke: true })
  assert.equal(managedRuntimeView({ appliedRuntimeKind: 'openshell', managedState: 'applied' }).startBlocked, false)
  assert.equal(managedRuntimeView({ appliedRuntimeKind: 'openshell', managedState: 'busy' }).canRevoke, false)
  assert.equal(managedRuntimeView({ appliedRuntimeKind: 'openshell', managedState: 'revoked' }).startBlocked, true)
})

test('revoke outcome reports success, the named code and every unresolved leg', () => {
  assert.deepEqual(revokeOutcome(200, { message: 'revoked' }), { ok: true, text: 'Runtime revoked' })
  assert.deepEqual(revokeOutcome(502, { error: 'revoke_unresolved', errors: ['broker:x', 'stop:y'] }), { ok: false, text: 'revoke_unresolved: broker:x, stop:y' })
  assert.deepEqual(revokeOutcome(503, null), { ok: false, text: 'Error 503' })
})

test('managed status lines summarize the applied binding, admission, pending operation and errors', () => {
  const status = {
    state: 'needs_attention', errors: ['broker:x', 'stop:y'], admissionState: 'revoking', pending: true, operation: 'revoke',
    appliedSandboxId: 'sb-0123456789abcdef', appliedSandboxName: 'test-agent-sandbox', epoch: '11111111-2222-3333-4444-555555555555',
    policyVersionId: 17, policySha256: 'abcdef0123456789abcdef', gatewayGeneration: 'gen-3', providers: ['test-agent-api', 'test-agent-telegram'],
  }
  assert.deepEqual(managedStatusLines(status), {
    pending: true, pendingLabel: 'revoking…', admission: 'revoking',
    binding: ['sandbox test-agent-sandbox (sb0123456789)', 'epoch 11111111', 'policy v17 abcdef012345', 'gateway gen gen-3', 'providers test-agent-api, test-agent-telegram'],
    errors: ['broker:x', 'stop:y'],
  })
  const idle = managedStatusLines({ ...status, state: 'applied', errors: [], admissionState: 'active', pending: false, operation: null })
  assert.equal(idle.pending, false)
  assert.equal(idle.pendingLabel, null)
  assert.equal(idle.admission, 'active')
  assert.deepEqual(managedStatusLines(null, true), { pending: true, pendingLabel: 'revoking…', admission: null, binding: [], errors: [] })
  assert.deepEqual(managedStatusLines(undefined), { pending: false, pendingLabel: null, admission: null, binding: [], errors: [] })
})
