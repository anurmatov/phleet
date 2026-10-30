import { useEffect, useState } from 'react'
import type { AgentConfig } from '../types'
import { apiFetch } from '../utils'
import { authModeChange, policyResponse, type RuntimeEdits } from '../managedStock'

interface Policy { id: number; name: string }
interface Version { id: number; version: number; yaml: string; sha256: string; providerProfileHashes: string }
const initialYaml = 'version: 1\nfilesystem_policy:\n  include_workdir: true\n  read_only: [/usr]\n  read_write: [/workspace]\nlandlock:\n  compatibility: hard_requirement\nnetwork_policies: {}\n'

export default function SandboxPoliciesView() {
  const [policies, setPolicies] = useState<Policy[]>([])
  const [selected, setSelected] = useState('')
  const [versions, setVersions] = useState<Version[]>([])
  const [name, setName] = useState('')
  const [yaml, setYaml] = useState(initialYaml)
  const [hashes, setHashes] = useState('{}')
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(false)
  const [loading, setLoading] = useState(true)
  const [message, setMessage] = useState('')
  const [generation, setGeneration] = useState(0)
  useEffect(() => {
    const abort = new AbortController()
    setLoading(true); setError('')
    apiFetch('/api/sandbox-policies', { signal: abort.signal }).then(policyResponse<Policy[]>).then(setPolicies)
      .catch(e => { if (!abort.signal.aborted) setError(e.message) }).finally(() => { if (!abort.signal.aborted) setLoading(false) })
    return () => abort.abort()
  }, [generation])
  useEffect(() => {
    const abort = new AbortController()
    setVersions([])
    if (selected) apiFetch(`/api/sandbox-policies/${selected}/versions`, { signal: abort.signal })
      .then(policyResponse<Version[]>).then(setVersions).catch(e => { if (!abort.signal.aborted) setError(e.message) })
    return () => abort.abort()
  }, [selected, generation])
  async function save() {
    setBusy(true); setError(''); setMessage('')
    try {
      const providerProfileHashes = JSON.parse(hashes)
      const response = await apiFetch(selected ? `/api/sandbox-policies/${selected}/versions` : '/api/sandbox-policies', {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ name, version: Math.max(0, ...versions.map(v => v.version)) + 1, yaml, providerProfileHashes }),
      })
      const result = await policyResponse<{ id: number }>(response)
      if (!selected) setSelected(String(result.id))
      setGeneration(n => n + 1); setMessage('New immutable version saved. No runtime or grants changed.')
    } catch (e) { setError(e instanceof Error ? e.message : 'Save failed') }
    finally { setBusy(false) }
  }
  return <section style={{ padding: 20, minWidth: 0, maxWidth: 900, overflowWrap: 'anywhere' }}>
    <h2>Sandbox policies</h2>
    <p>Save a version, then select it in an agent’s Model &amp; runtime settings. Managed activation is unavailable.</p>
    {loading && <p role="status">Loading policies…</p>}
    {error && <p role="alert">{error}</p>}
    {message && <p role="status">{message}</p>}
    {!loading && !error && policies.length === 0 && <p>No saved policies yet.</p>}
    <label className="config-field">Policy
      <select className="config-input" style={{ maxWidth: '100%', minWidth: 0 }} value={selected} onChange={e => { setSelected(e.target.value); setError(''); setMessage('') }}>
        <option value="">Create new policy</option>
        {policies.map(p => <option key={p.id} value={p.id}>{p.name}</option>)}
      </select>
    </label>
    {!selected && <label className="config-field">Policy name<input className="config-input" maxLength={100} value={name} onChange={e => setName(e.target.value)} /></label>}
    {selected && <label className="config-field">Read saved version
      <select className="config-input" value="" onChange={e => {
        const v = versions.find(v => String(v.id) === e.target.value)
        if (v) { setYaml(v.yaml); setHashes(v.providerProfileHashes) }
      }}><option value="">Copy a saved version into editor…</option>{versions.map(v => <option key={v.id} value={v.id}>Version {v.version} · {v.sha256.slice(0, 12)}</option>)}</select>
    </label>}
    <label className="config-field">Stock policy YAML
      <textarea className="config-input" rows={15} style={{ width: '100%', minWidth: 0, boxSizing: 'border-box' }} value={yaml} onChange={e => setYaml(e.target.value)} />
    </label>
    <p>Save-supported subset: version 1, filesystem_policy, process, landlock, network_policies with binary paths and host/port endpoints. Middleware and advanced rules are refused, never dropped.</p>
    <label className="config-field">Provider profile content hashes (JSON names to SHA256, no credentials)
      <textarea className="config-input" rows={3} value={hashes} onChange={e => setHashes(e.target.value)} />
    </label>
    <button className="config-save-btn" disabled={busy || loading || (!!selected && versions.length === 0)} onClick={save}>{busy ? 'Saving…' : selected ? 'Save new version' : 'Create policy and version 1'}</button>
  </section>
}

export function RuntimeSelection({ config, edits, onChange }: { config?: AgentConfig | null; edits: RuntimeEdits; onChange: (patch: RuntimeEdits) => void }) {
  const [policies, setPolicies] = useState<Policy[]>([])
  const [versions, setVersions] = useState<(Version & { policyName: string })[]>([])
  const [error, setError] = useState('')
  const [loading, setLoading] = useState(true)
  useEffect(() => {
    const abort = new AbortController()
    apiFetch('/api/sandbox-policies', { signal: abort.signal }).then(policyResponse<Policy[]>).then(async rows => {
      setPolicies(rows)
      const groups = await Promise.all(rows.map(async p => (await apiFetch(`/api/sandbox-policies/${p.id}/versions`, { signal: abort.signal }).then(policyResponse<Version[]>)).map(v => ({ ...v, policyName: p.name }))))
      if (!abort.signal.aborted) setVersions(groups.flat())
    }).catch(e => { if (!abort.signal.aborted) setError(e.message) }).finally(() => { if (!abort.signal.aborted) setLoading(false) })
    return () => abort.abort()
  }, [])
  return <div style={{ minWidth: 0, overflowWrap: 'anywhere' }}>
    <p>Applied: {config?.appliedRuntimeKind ?? 'docker'} · policy version ID {config?.appliedSandboxPolicyVersionId ?? 'none'}</p>
    <p>Desired: {edits.runtimeKind ?? 'docker'} · {config?.runtimePending ? 'Pending selection' : 'Save only, no activation'}</p>
    <label className="config-field">Desired runtime<select className="config-input" value={edits.runtimeKind ?? 'docker'} onChange={e => onChange({ runtimeKind: e.target.value })}><option value="docker">Docker (existing runtime)</option><option value="openshell">OpenShell (save only)</option></select></label>
    {loading && <p role="status">Loading policy versions…</p>}
    {error && <p role="alert">{error}</p>}
    {!loading && !error && policies.length === 0 && <p>No policies. Create one on the Sandbox policies page.</p>}
    <label className="config-field">Desired immutable policy version<select className="config-input" style={{ maxWidth: '100%', minWidth: 0 }} disabled={loading || !!error} value={edits.sandboxPolicyVersionId ?? ''} onChange={e => onChange({ sandboxPolicyVersionId: e.target.value })}>
      <option value="">None</option>{versions.map(v => <option key={v.id} value={v.id}>{v.policyName} · v{v.version} · {v.sha256.slice(0, 12)}</option>)}
      {edits.sandboxPolicyVersionId && !versions.some(v => String(v.id) === edits.sandboxPolicyVersionId) && <option value={edits.sandboxPolicyVersionId}>Saved version ID {edits.sandboxPolicyVersionId} (not loaded)</option>}
    </select></label>
    <label className="config-field">Native authentication<select className="config-input" value={edits.nativeAuthMode ?? ''} onChange={e => onChange(authModeChange(e.target.value))}><option value="">Not selected (existing auth unchanged)</option><option value="api_key">API key (usage-based billing)</option><option value="subscription">Subscription (unqualified compatibility only)</option></select></label>
    {edits.nativeAuthMode === 'subscription'
      ? <p>Requires an outside-owned provider and separate trial approval; not production-qualified.</p>
      : <>
        <label className="config-field">API key environment reference name<input className="config-input" value={edits.providerApiKeyEnvRef ?? ''} placeholder="EXAMPLE_API_KEY" onChange={e => onChange({ providerApiKeyEnvRef: e.target.value })} /></label>
        <p>API-key use incurs separate usage-based billing. OAuth migration is unsupported. Enter a reference name only, never a secret. Selection does not grant tools or project access.</p>
      </>}
    <p role="status">Managed runtime apply/reprovision is unavailable. Saving never starts, stops or migrates an agent.</p>
  </div>
}
