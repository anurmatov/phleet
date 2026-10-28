import { test } from 'node:test'
import assert from 'node:assert/strict'
import { savedRuntimeSelection, policyResponse, managedRuntimeView, revokeOutcome } from './managedStock.ts'

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
