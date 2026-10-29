import type { ConfigEdits, ManagedStatus } from './types'

export type RuntimeEdits = Pick<Partial<ConfigEdits>, 'runtimeKind' | 'sandboxPolicyVersionId' | 'nativeAuthMode' | 'providerApiKeyEnvRef'>

export function savedRuntimeSelection(edits: RuntimeEdits): Record<string, unknown> {
  // Older servers/configs have no selection fields. Never send an invented default back to them.
  return edits.runtimeKind === undefined && edits.sandboxPolicyVersionId === undefined ? {} : {
    runtimeKind: edits.runtimeKind,
    sandboxPolicyVersionId: Number(edits.sandboxPolicyVersionId || 0),
    nativeAuthMode: edits.nativeAuthMode ?? '',
    providerApiKeyEnvRef: edits.providerApiKeyEnvRef ?? '',
  }
}

export async function policyResponse<T>(response: Response): Promise<T> {
  if (!response.ok) {
    const body = await response.json().catch(() => null)
    throw new Error(body?.error || `Policy API unavailable (${response.status})`)
  }
  return response.json()
}

/** #492: what the card shows and allows for a managed runtime, from the persisted record only. */
export interface ManagedRuntimeView { label: string | null; title: string; startBlocked: boolean; canRevoke: boolean }

export function managedRuntimeView(agent: { runtimeKind?: string; appliedRuntimeKind?: string; managedState?: string | null; managedErrors?: string[] | null }): ManagedRuntimeView {
  const applied = agent.appliedRuntimeKind === 'openshell'
  // #492 W6: a Docker handoff whose authority retirement failed leaves a held managed record on an applied
  // Docker agent. Surface it and offer revoke, the supported retry, rather than hiding it as plain Docker.
  // A committed handoff whose authority is not yet retired carries authority_retire_pending: equally held.
  const retireErrors = agent.managedErrors ?? []
  const retireFailed = retireErrors.includes('authority_retire_failed') || retireErrors.includes('authority_retire_pending')
  if (!applied && agent.runtimeKind !== 'openshell' && !retireFailed) return { label: null, title: '', startBlocked: false, canRevoke: false }
  const state = agent.managedState ?? (applied ? 'unknown' : 'pending apply')
  const held = ['needs_attention', 'revoked', 'busy', 'applying', 'unreadable', 'unknown'].includes(state)
  return {
    label: `openshell · ${retireFailed && state === 'needs_attention'
      ? (retireErrors.includes('authority_retire_failed') ? 'authority retirement failed' : 'authority retirement pending')
      : state.replace(/_/g, ' ')}`,
    title: (agent.managedErrors ?? []).join(', '),
    // Start targets the applied binding; a pending desired change still starts the applied Docker runtime.
    // An unresolved Docker authority retirement keeps the backend's ordinary-admission hold, so Start is
    // refused there too until revoke resolves it (#492 U1).
    startBlocked: (applied || retireFailed) && held,
    canRevoke: (applied || retireFailed) && state !== 'busy' && state !== 'revoked',
  }
}

/** #492: the operator-visible result of POST /api/agents/{name}/revoke, including every unresolved leg. */
export function revokeOutcome(status: number, body: { message?: string; error?: string; errors?: unknown } | null): { ok: boolean; text: string } {
  if (status >= 200 && status < 300) return { ok: true, text: 'Runtime revoked' }
  const errors = Array.isArray(body?.errors) ? body!.errors.filter((e): e is string => typeof e === 'string') : []
  return { ok: false, text: `${body?.error ?? `Error ${status}`}${errors.length ? `: ${errors.join(', ')}` : ''}` }
}

/** #492: display lines for the applied binding and admission state; `revoking` is a local in-flight revoke. */
export interface ManagedStatusLines { pending: boolean; pendingLabel: string | null; admission: string | null; binding: string[]; errors: string[] }

export function managedStatusLines(status: ManagedStatus | null | undefined, revoking = false): ManagedStatusLines {
  const short = (v: string | null | undefined, n = 8) => (v ? v.replace(/-/g, '').slice(0, n) : null)
  const errors = status?.errors ?? []
  // Pending only while an operation is in flight: a record with no operation (completed, or a held
  // candidate / stale "applying" from an older server) never shows a permanent pending badge.
  const pending = revoking || (!!status?.pending && !!status?.operation)
  const pendingLabel = revoking || (pending && status?.operation === 'revoke') ? 'revoking…'
    : pending ? `${status?.operation}…` : null
  if (!status) return { pending, pendingLabel, admission: null, binding: [], errors }
  const binding: string[] = []
  const sandbox = status.appliedSandboxName && status.appliedSandboxId
    ? `${status.appliedSandboxName} (${short(status.appliedSandboxId, 12)})`
    : status.appliedSandboxName ?? short(status.appliedSandboxId, 12)
  if (sandbox) binding.push(`sandbox ${sandbox}`)
  if (status.epoch) binding.push(`epoch ${short(status.epoch)}`)
  if (status.policySha256 || status.policyVersionId) {
    binding.push(`policy ${status.policyVersionId ? `v${status.policyVersionId}` : ''}${status.policySha256 ? `${status.policyVersionId ? ' ' : ''}${short(status.policySha256, 12)}` : ''}`)
  }
  if (status.gatewayGeneration) binding.push(`gateway gen ${status.gatewayGeneration}`)
  if (status.providers?.length) binding.push(`providers ${status.providers.join(', ')}`)
  const containerId = (v: string | null | undefined) => (v ? v.replace(/^sha256:/, '').slice(0, 12) : null)
  const workload = containerId(status.workloadContainerId)
  const supervisor = containerId(status.supervisorContainerId)
  if (workload) binding.push(`workload ${workload}`)
  if (supervisor) binding.push(`supervisor ${supervisor}`)
  if (status.containerStatus) binding.push(`containers ${status.containerStatus}`)
  if (status.backupState !== undefined || status.backupAgeSeconds !== undefined) binding.push(backupLine(status))
  return { pending, pendingLabel, admission: status.admissionState.replace(/_/g, ' '), binding, errors }
}

/** Compact age: 45s, 12m, 5h, 3d. */
export function formatAge(seconds: number): string {
  const s = Math.max(0, Math.floor(seconds))
  return s < 60 ? `${s}s` : s < 3600 ? `${Math.floor(s / 60)}m` : s < 86400 ? `${Math.floor(s / 3600)}h` : `${Math.floor(s / 86400)}d`
}

/** Gateway backup age from the last-success stamp; a missing stamp is unknown, never fresh. */
export function backupLine(status: Pick<ManagedStatus, 'backupState' | 'backupAgeSeconds'>): string {
  if (status.backupState === 'unmounted') return 'backup dir not mounted'
  const age = typeof status.backupAgeSeconds === 'number' ? formatAge(status.backupAgeSeconds) : null
  if (!age) return 'backup unknown'
  return status.backupState === 'failed' ? `backup failed (last success ${age} ago)` : `backup ${age} ago`
}
