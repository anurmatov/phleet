import { useCallback, useEffect, useState } from 'react'
import { apiFetch, apiGet, apiSend, errorMessage } from '../utils'
import {
  GITHUB_APP_KIND, SSH_KEY_KIND, STATIC_KIND, agentSummary, bindingBody, emptyForm, formFor, statusProblem, withKind,
  type SecretBinding, type SecretBindingForm, type SecretBindingsStatus,
} from '../secretBindings'

const cell: React.CSSProperties = { padding: '6px 8px', fontSize: 12, verticalAlign: 'top', textAlign: 'left' }
const label: React.CSSProperties = { fontSize: 11, color: 'var(--muted)', display: 'block', marginBottom: 2 }

/**
 * Secret bindings: a bound .env key never enters an agent container. The agent holds a placeholder,
 * and fleet-secrets swaps it for the real value only on a request to one of the binding's hosts.
 */
export default function SecretBindingsSection({ onToast }: { onToast: (message: string) => void }) {
  const [status, setStatus] = useState<SecretBindingsStatus | null>(null)
  const [loadError, setLoadError] = useState('')
  const [form, setForm] = useState<SecretBindingForm | null>(null)
  const [editing, setEditing] = useState<string | null>(null)
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState('')

  const load = useCallback(async () => {
    try {
      setStatus(await apiGet<SecretBindingsStatus>('/api/secret-bindings'))
      setLoadError('')
    } catch (e) { setLoadError(errorMessage(e)) }
  }, [])

  useEffect(() => { void load() }, [load])

  function open(binding: SecretBinding | null) {
    setEditing(binding?.envKeyName ?? null)
    setForm(binding ? formFor(binding) : emptyForm())
    setError('')
  }

  async function save() {
    if (!form) return
    const key = form.envKeyName.trim()
    if (!key) { setError('Name the .env key'); return }
    setSaving(true)
    setError('')
    try {
      const data = await apiSend<{ reprovision?: string[] } | null>(`/api/secret-bindings/${encodeURIComponent(key)}`, 'PUT', bindingBody(form))
      const agents = data?.reprovision ?? []
      onToast(agents.length > 0 ? `Saved ${key}. Reprovision: ${agents.join(', ')}` : `Saved ${key}`)
      setForm(null)
      await load()
    } catch (e) { setError(errorMessage(e)) }
    finally { setSaving(false) }
  }

  async function remove(binding: SecretBinding) {
    try {
      const res = await apiFetch(`/api/secret-bindings/${encodeURIComponent(binding.envKeyName)}`, { method: 'DELETE' })
      const data = await res.json().catch(() => ({})) as { error?: string; agents?: string[] }
      if (!res.ok) {
        onToast(data.agents?.length
          ? `Not deleted: remove this Env Ref from ${data.agents.join(', ')} first`
          : data.error ?? `Error ${res.status}`)
        return
      }
      onToast(`Deleted the binding for ${binding.envKeyName}`)
      await load()
    } catch (e) { onToast(String(e)) }
  }

  const problem = status ? statusProblem(status) : null

  return (
    <section className="cred-section">
      <div className="cred-section-header">
        <div className="cred-section-title">Secret bindings</div>
        <button className="config-save-btn" onClick={() => open(null)}>+ Bind a key</button>
      </div>
      <p style={{ fontSize: 12, color: 'var(--muted)', marginBottom: 12 }}>
        A bound key is never written into an agent container. The agent gets a placeholder, and fleet-secrets
        swaps it for the real value only on an HTTPS request to one of the hosts listed here, in the named header.
        An SSH key is never sent at all: the agent gets a signing socket, and fleet-secrets signs only for a session
        with one of the listed hosts, proven by its host key. Attach a bound key to an agent as an Env Ref, then
        reprovision the agent.
      </p>

      {loadError && <div style={{ color: 'var(--red)', fontSize: 12, marginBottom: 8 }}>{loadError}</div>}
      {problem && <div style={{ color: 'var(--red)', fontSize: 12, marginBottom: 8 }}>{problem}</div>}

      {status && (
        <table style={{ width: '100%', borderCollapse: 'collapse' }}>
          <thead>
            <tr style={{ color: 'var(--muted)' }}>
              <th style={cell}>Key</th>
              <th style={cell}>Used only for</th>
              <th style={cell}>In header</th>
              <th style={cell}>Agent sees it as</th>
              <th style={cell}>Agents</th>
              <th style={cell}>Actions</th>
            </tr>
          </thead>
          <tbody>
            {status.bindings.length === 0 && (
              <tr><td style={cell} colSpan={6}>No key is bound. Every Env Ref is delivered to its agent as a plain value.</td></tr>
            )}
            {status.bindings.map(binding => (
              <tr key={binding.envKeyName} style={{ borderTop: '1px solid var(--border)' }}>
                <td style={{ ...cell, fontFamily: 'monospace' }}>
                  {binding.envKeyName}
                  {binding.kind === GITHUB_APP_KIND && <div style={{ color: 'var(--muted)' }}>GitHub App ({binding.appIdEnvKey})</div>}
                  {binding.kind === SSH_KEY_KIND && <div style={{ color: 'var(--muted)' }}>SSH key ({binding.knownHosts?.length ?? 0} host keys)</div>}
                  {!binding.valuePresent && <div style={{ color: 'var(--red)' }}>no value in .env</div>}
                </td>
                <td style={{ ...cell, fontFamily: 'monospace' }}>{binding.hosts.map(host => <div key={host}>{host}</div>)}</td>
                <td style={{ ...cell, fontFamily: 'monospace' }}>{binding.kind === SSH_KEY_KIND ? 'signed, never sent' : binding.headerName}</td>
                <td style={{ ...cell, fontFamily: 'monospace' }}>{binding.kind === SSH_KEY_KIND ? 'SSH_AUTH_SOCK (a socket)' : binding.exposeAs.join(', ')}</td>
                <td style={cell}>
                  {agentSummary(binding)}
                  {binding.agentAttachable && <div style={{ color: 'var(--muted)' }}>agents may attach it</div>}
                </td>
                <td style={cell}>
                  <button className="cred-action-btn" onClick={() => open(binding)}>edit</button>{' '}
                  <button className="cred-action-btn" onClick={() => void remove(binding)}>delete</button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}

      {form && (
        <div style={{ marginTop: 12, padding: 12, border: '1px solid var(--border)', borderRadius: 6 }}>
          <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(220px, 1fr))', gap: 10 }}>
            <div>
              <span style={label}>.env key</span>
              <input className="config-input" style={{ width: '100%' }} value={form.envKeyName} disabled={editing !== null}
                placeholder="EXAMPLE_API_KEY" onChange={e => setForm({ ...form, envKeyName: e.target.value })} />
            </div>
            <div>
              <span style={label}>Kind</span>
              <select className="config-input" style={{ width: '100%' }} value={form.kind}
                onChange={e => setForm(withKind(form, e.target.value))}>
                <option value={STATIC_KIND}>Static value</option>
                <option value={GITHUB_APP_KIND}>GitHub App private key</option>
                <option value={SSH_KEY_KIND}>SSH private key</option>
              </select>
            </div>
            <div>
              <span style={label}>{form.kind === SSH_KEY_KIND ? 'Destinations (user@host, comma-separated)' : 'Hosts (exact names, comma-separated)'}</span>
              <input className="config-input" style={{ width: '100%' }} value={form.hosts}
                placeholder={form.kind === SSH_KEY_KIND ? 'deploy@build.example.test' : 'api.example.test'}
                onChange={e => setForm({ ...form, hosts: e.target.value })} />
            </div>
            {form.kind !== SSH_KEY_KIND && (
              <div>
                <span style={label}>Request header</span>
                <input className="config-input" style={{ width: '100%' }} value={form.headerName} disabled={form.kind === GITHUB_APP_KIND}
                  onChange={e => setForm({ ...form, headerName: e.target.value })} />
              </div>
            )}
            {form.kind !== SSH_KEY_KIND && (
              <div>
                <span style={label}>Variable names in the agent (blank: the key itself)</span>
                <input className="config-input" style={{ width: '100%' }} value={form.exposeAs}
                  onChange={e => setForm({ ...form, exposeAs: e.target.value })} />
              </div>
            )}
            {form.kind === GITHUB_APP_KIND && (
              <div>
                <span style={label}>.env key that holds the App id</span>
                <input className="config-input" style={{ width: '100%' }} value={form.appIdEnvKey}
                  onChange={e => setForm({ ...form, appIdEnvKey: e.target.value })} />
              </div>
            )}
          </div>
          {form.kind === SSH_KEY_KIND && (
            <div style={{ marginTop: 10 }}>
              <span style={label}>
                Host keys, one line per key (run <code>ssh-keyscan build.example.test</code> and check the result against the host).
                The key signs only for these.
              </span>
              <textarea className="config-input" style={{ width: '100%', minHeight: 70, fontFamily: 'monospace', fontSize: 11 }}
                value={form.knownHosts} placeholder="build.example.test ssh-ed25519 AAAA…"
                onChange={e => setForm({ ...form, knownHosts: e.target.value })} />
            </div>
          )}
          <label style={{ display: 'block', fontSize: 12, marginTop: 10 }}>
            <input type="checkbox" checked={form.agentAttachable}
              onChange={e => setForm({ ...form, agentAttachable: e.target.checked })} />{' '}
            Agents may attach this key to an agent themselves (manage_agent_env_refs). Off: only you can.
          </label>
          {error && <div style={{ color: 'var(--red)', fontSize: 12, marginTop: 8 }}>{error}</div>}
          <div style={{ display: 'flex', gap: 8, marginTop: 10 }}>
            <button className="config-save-btn" disabled={saving} onClick={() => void save()}>{saving ? 'Saving…' : 'Save binding'}</button>
            <button className="cred-action-btn" onClick={() => setForm(null)}>Cancel</button>
          </div>
        </div>
      )}
    </section>
  )
}
