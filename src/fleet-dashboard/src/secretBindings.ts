// Secret bindings: which .env keys agents hold only as placeholders, and the hosts fleet-secrets
// may send each one to. The orchestrator validates every field; these helpers only shape the form.

export const STATIC_KIND = 'static'
export const GITHUB_APP_KIND = 'github_app'
// An SSH private key: never sent anywhere. fleet-secrets signs with it, for the listed hosts only.
export const SSH_KEY_KIND = 'ssh_key'

export interface SecretBindingAgent {
  agent: string
  placeholderHeld: boolean
}

export interface SecretBinding {
  envKeyName: string
  kind: string
  hosts: string[]
  headerName: string
  exposeAs: string[]
  appIdEnvKey: string | null
  knownHosts?: string[]
  agentAttachable: boolean
  valuePresent: boolean
  agents: SecretBindingAgent[]
  pendingReprovision: string[]
}

export interface SecretBindingsStatus {
  enabled: boolean
  fault: string | null
  subnet: string | null
  address: string | null
  caPresent: boolean
  bindings: SecretBinding[]
}

export interface SecretBindingForm {
  envKeyName: string
  kind: string
  hosts: string
  headerName: string
  exposeAs: string
  appIdEnvKey: string
  knownHosts: string
  agentAttachable: boolean
}

export const GITHUB_APP_DEFAULTS = {
  hosts: 'api.github.com, github.com, uploads.github.com',
  exposeAs: 'GH_TOKEN, GITHUB_TOKEN',
  appIdEnvKey: 'GITHUB_APP_ID',
}

export function emptyForm(): SecretBindingForm {
  return { envKeyName: '', kind: STATIC_KIND, hosts: '', headerName: 'Authorization', exposeAs: '', appIdEnvKey: '', knownHosts: '', agentAttachable: false }
}

export function formFor(binding: SecretBinding): SecretBindingForm {
  // The orchestrator reports the key itself when no other name is set; the form shows that as blank.
  // An SSH key reports none at all.
  const custom = binding.exposeAs.length > 0
    && !(binding.exposeAs.length === 1 && binding.exposeAs[0] === binding.envKeyName)
  return {
    envKeyName: binding.envKeyName,
    kind: binding.kind,
    hosts: binding.hosts.join(', '),
    headerName: binding.headerName,
    exposeAs: custom ? binding.exposeAs.join(', ') : '',
    appIdEnvKey: binding.appIdEnvKey ?? '',
    knownHosts: (binding.knownHosts ?? []).join('\n'),
    agentAttachable: binding.agentAttachable,
  }
}

/** Splits a comma, space or newline separated list, dropping blanks. */
export function splitList(text: string): string[] {
  return text.split(/[\s,]+/).map(part => part.trim()).filter(part => part.length > 0)
}

/** Hosts are lowercased for the operator; everything else is sent as typed and judged by the server. */
export function bindingBody(form: SecretBindingForm) {
  if (form.kind === SSH_KEY_KIND) {
    // Destinations are user@host; host key lines go as typed, one per line (the server drops
    // blank and comment lines and refuses anything that is not a plain host key).
    return {
      kind: form.kind,
      hosts: splitList(form.hosts).map(host => host.toLowerCase()),
      knownHosts: form.knownHosts.split('\n').map(line => line.trim()).filter(line => line.length > 0),
      agentAttachable: form.agentAttachable,
    }
  }
  return {
    kind: form.kind,
    hosts: splitList(form.hosts).map(host => host.toLowerCase()),
    headerName: form.headerName.trim() || 'Authorization',
    exposeAs: splitList(form.exposeAs),
    appIdEnvKey: form.kind === GITHUB_APP_KIND ? form.appIdEnvKey.trim() || null : null,
    agentAttachable: form.agentAttachable,
  }
}

/** Switching to a GitHub App fills the usual hosts and token names when those fields are empty. */
export function withKind(form: SecretBindingForm, kind: string): SecretBindingForm {
  // An SSH key has no header, no variable and no App id; the other kinds have no host keys.
  if (kind === SSH_KEY_KIND) return { ...form, kind, headerName: 'Authorization', exposeAs: '', appIdEnvKey: '' }
  if (kind !== GITHUB_APP_KIND) return { ...form, kind, appIdEnvKey: '', knownHosts: '' }
  return {
    ...form,
    kind,
    headerName: 'Authorization',
    hosts: form.hosts.trim() || GITHUB_APP_DEFAULTS.hosts,
    exposeAs: form.exposeAs.trim() || GITHUB_APP_DEFAULTS.exposeAs,
    appIdEnvKey: form.appIdEnvKey.trim() || GITHUB_APP_DEFAULTS.appIdEnvKey,
    knownHosts: '',
  }
}

/** One line on whether placeholders can work at all right now, or null when they can. */
export function statusProblem(status: SecretBindingsStatus): string | null {
  if (status.fault) return status.fault
  if (!status.enabled) return 'Secret placeholders are off (FLEET_SECRETS_ENABLED). An agent that references a bound key will not provision until they are on.'
  if (!status.caPresent) return 'fleet-secrets has not created its CA yet. Start it before provisioning an agent that references a bound key.'
  return null
}

/** What the agents column says for one binding. */
export function agentSummary(binding: SecretBinding): string {
  if (binding.agents.length === 0) return 'no agent references it'
  const pending = binding.pendingReprovision.length
  const names = binding.agents.map(a => a.agent).join(', ')
  return pending === 0 ? names : `${names} (${pending} to reprovision)`
}
