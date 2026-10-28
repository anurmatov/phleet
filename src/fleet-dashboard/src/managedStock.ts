import type { ConfigEdits } from './types'

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
