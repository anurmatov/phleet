import { test } from 'node:test'
import assert from 'node:assert/strict'
import { savedRuntimeSelection, authModeChange, policyResponse, managedRuntimeView, revokeOutcome, managedStatusLines, backupLine, formatAge } from './managedStock.ts'

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

test('a failed authority retirement after a Docker handoff is surfaced and offers the revoke retry', () => {
  // Handoff path: Docker is applied, the managed record is held with authority_retire_failed.
  assert.deepEqual(managedRuntimeView({ runtimeKind: 'docker', appliedRuntimeKind: 'docker', managedState: 'needs_attention', managedErrors: ['authority_retire_failed'] }),
    { label: 'openshell · authority retirement failed', title: 'authority_retire_failed', startBlocked: true, canRevoke: true })
  // A failed revoke retry appends its code; the summary still names the retirement failure.
  assert.deepEqual(managedRuntimeView({ runtimeKind: 'docker', appliedRuntimeKind: 'docker', managedState: 'needs_attention', managedErrors: ['authority_retire_failed', 'docker_handoff_unresolved', 'authority_retire_failed'] }),
    { label: 'openshell · authority retirement failed', title: 'authority_retire_failed, docker_handoff_unresolved, authority_retire_failed', startBlocked: true, canRevoke: true })
  // While the retry is in flight the record reads busy: no second revoke.
  assert.deepEqual(managedRuntimeView({ appliedRuntimeKind: 'docker', managedState: 'busy', managedErrors: ['authority_retire_failed'] }),
    { label: 'openshell · busy', title: 'authority_retire_failed', startBlocked: true, canRevoke: false })
  // A resolved retirement clears the errors: an ordinary Docker agent again.
  assert.equal(managedRuntimeView({ runtimeKind: 'docker', appliedRuntimeKind: 'docker', managedState: 'revoked', managedErrors: [] }).label, null)
  assert.equal(managedRuntimeView({ runtimeKind: 'docker', appliedRuntimeKind: 'docker', managedState: 'revoked', managedErrors: [] }).startBlocked, false)
})

test('a committed Docker handoff with a pending authority retirement is held and revocable', () => {
  assert.deepEqual(managedRuntimeView({ runtimeKind: 'docker', appliedRuntimeKind: 'docker', managedState: 'needs_attention', managedErrors: ['authority_retire_pending', 'handoff_record_unfinalized'] }),
    { label: 'openshell · authority retirement pending', title: 'authority_retire_pending, handoff_record_unfinalized', startBlocked: true, canRevoke: true })
})

test('an unresolved Docker→OpenShell candidate blocks Start and offers revoke, whatever the desired selection', () => {
  // #492 F2: the apply failed and left a held candidate on the applied Docker agent; the backend refuses Start.
  const failed = { appliedRuntimeKind: 'docker', managedState: 'needs_attention', managedErrors: ['sandbox_create_failed'] }
  assert.deepEqual(managedRuntimeView({ ...failed, runtimeKind: 'openshell' }),
    { label: 'openshell · needs attention', title: 'sandbox_create_failed', startBlocked: true, canRevoke: true })
  // The operator switched the desired selection back to Docker: still held until revoke resolves it.
  assert.deepEqual(managedRuntimeView({ ...failed, runtimeKind: 'docker' }),
    { label: 'openshell · needs attention', title: 'sandbox_create_failed', startBlocked: true, canRevoke: true })
  // An interrupted apply (stale applying) and an unreadable record are held the same way.
  assert.equal(managedRuntimeView({ runtimeKind: 'docker', appliedRuntimeKind: 'docker', managedState: 'applying' }).startBlocked, true)
  assert.equal(managedRuntimeView({ runtimeKind: 'docker', appliedRuntimeKind: 'docker', managedState: 'unreadable' }).canRevoke, true)
  // An apply still in flight is held but not revocable twice.
  assert.deepEqual(managedRuntimeView({ runtimeKind: 'openshell', appliedRuntimeKind: 'docker', managedState: 'busy', managedErrors: [] }),
    { label: 'openshell · busy', title: '', startBlocked: true, canRevoke: false })
})

test('an ordinary pending Docker runtime selection stays startable (control for the retirement block)', () => {
  // Applied OpenShell, desired Docker, no held state: Start still targets the applied binding.
  assert.equal(managedRuntimeView({ runtimeKind: 'docker', appliedRuntimeKind: 'openshell', managedState: 'applied', managedErrors: [] }).startBlocked, false)
  // Applied Docker, desired OpenShell pending apply: Start runs the applied Docker runtime.
  assert.equal(managedRuntimeView({ runtimeKind: 'openshell', appliedRuntimeKind: 'docker' }).startBlocked, false)
  assert.equal(managedRuntimeView({ runtimeKind: 'docker', appliedRuntimeKind: 'docker', managedState: null, managedErrors: null }).startBlocked, false)
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

const baseStatus = {
  state: 'applied', errors: [] as string[], admissionState: 'active', pending: false, operation: null as string | null,
  appliedSandboxId: 'sb-1', appliedSandboxName: 'test-agent-sandbox', epoch: null, policyVersionId: null, policySha256: null,
  gatewayGeneration: null, providers: [] as string[],
}

test('pending clears when the record shows no operation, even for a held candidate', () => {
  // Older servers reported pending for a held candidate / stale applying with no operation: never a permanent badge.
  const held = managedStatusLines({ ...baseStatus, state: 'needs_attention', admissionState: 'needs_attention', pending: true, operation: null })
  assert.equal(held.pending, false)
  assert.equal(held.pendingLabel, null)
  const applying = managedStatusLines({ ...baseStatus, state: 'applying', pending: true, operation: 'apply', admissionState: 'applying' })
  assert.deepEqual([applying.pending, applying.pendingLabel], [true, 'apply…'])
  // The same record once the operation completes.
  const done = managedStatusLines({ ...baseStatus, pending: false, operation: null })
  assert.deepEqual([done.pending, done.pendingLabel], [false, null])
  // A local in-flight revoke still shows until its request settles.
  assert.equal(managedStatusLines({ ...baseStatus }, true).pendingLabel, 'revoking…')
})

test('workload/supervisor IDs are shortened and stopped/removed status and backup age are shown', () => {
  const lines = managedStatusLines({
    ...baseStatus, state: 'stopped', workloadContainerId: 'aaaaaaaaaaaa1111111111111111', supervisorContainerId: 'bbbbbbbbbbbb2222222222222222',
    containerStatus: 'stopped', backupState: 'recorded', backupAgeSeconds: 7260,
  })
  assert.deepEqual(lines.binding, ['sandbox test-agent-sandbox (sb1)', 'workload aaaaaaaaaaaa', 'supervisor bbbbbbbbbbbb', 'containers stopped', 'backup 2h ago'])
  const removed = managedStatusLines({ ...baseStatus, state: 'revoked', workloadContainerId: 'aaaaaaaaaaaa1111', containerStatus: 'removed', backupState: 'unknown', backupAgeSeconds: null })
  assert.deepEqual(removed.binding.slice(1), ['workload aaaaaaaaaaaa', 'containers removed', 'backup unknown'])
  // Older servers without the fields add no lines.
  assert.deepEqual(managedStatusLines(baseStatus).binding, ['sandbox test-agent-sandbox (sb1)'])
})

test('backup age is unknown without a last-success stamp and never reads as fresh', () => {
  assert.equal(backupLine({ backupState: 'unknown', backupAgeSeconds: null }), 'backup unknown')
  assert.equal(backupLine({ backupState: 'recorded' }), 'backup unknown')
  assert.equal(backupLine({ backupState: 'unmounted', backupAgeSeconds: null }), 'backup dir not mounted')
  assert.equal(backupLine({ backupState: 'failed', backupAgeSeconds: 90000 }), 'backup failed (last success 1d ago)')
  assert.deepEqual([formatAge(45), formatAge(720), formatAge(-5)], ['45s', '12m', '0s'])
})

// #502: explicit subscription save clears the API-key reference; API and legacy saves are unchanged.
test('subscription save clears the API-key reference and invents no defaults', () => {
  assert.deepEqual(savedRuntimeSelection({ runtimeKind: 'openshell', sandboxPolicyVersionId: '17', nativeAuthMode: 'subscription', providerApiKeyEnvRef: 'STALE_KEY' }), {
    runtimeKind: 'openshell', sandboxPolicyVersionId: 17, nativeAuthMode: 'subscription', providerApiKeyEnvRef: '',
  })
  assert.deepEqual(savedRuntimeSelection({ runtimeKind: 'openshell', sandboxPolicyVersionId: '17', nativeAuthMode: 'api_key', providerApiKeyEnvRef: 'TEST_API_KEY' }).providerApiKeyEnvRef, 'TEST_API_KEY')
  assert.deepEqual(savedRuntimeSelection({}), {})
  assert.deepEqual(savedRuntimeSelection({ runtimeKind: 'docker' }), { runtimeKind: 'docker', sandboxPolicyVersionId: 0, nativeAuthMode: '', providerApiKeyEnvRef: '' })
})
test('choosing subscription clears the reference edit; other modes leave it', () => {
  assert.deepEqual(authModeChange('subscription'), { nativeAuthMode: 'subscription', providerApiKeyEnvRef: '' })
  assert.deepEqual(authModeChange('api_key'), { nativeAuthMode: 'api_key' })
  assert.deepEqual(authModeChange(''), { nativeAuthMode: '' })
})
