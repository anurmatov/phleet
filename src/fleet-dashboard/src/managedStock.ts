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
  if (!applied && agent.runtimeKind !== 'openshell') return { label: null, title: '', startBlocked: false, canRevoke: false }
  const state = agent.managedState ?? (applied ? 'unknown' : 'pending apply')
  const held = ['needs_attention', 'revoked', 'busy', 'applying', 'unreadable', 'unknown'].includes(state)
  return {
    label: `openshell · ${state.replace(/_/g, ' ')}`,
    title: (agent.managedErrors ?? []).join(', '),
    // Start targets the applied binding; a pending desired change still starts the applied Docker runtime.
    startBlocked: applied && held,
    canRevoke: applied && state !== 'busy' && state !== 'revoked',
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
  const pending = revoking || !!status?.pending
  const pendingLabel = revoking || status?.operation === 'revoke' ? 'revoking…'
    : pending ? `${status?.operation ?? status?.state ?? 'pending'}…` : null
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
  return { pending, pendingLabel, admission: status.admissionState.replace(/_/g, ' '), binding, errors }
}
