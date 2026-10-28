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
