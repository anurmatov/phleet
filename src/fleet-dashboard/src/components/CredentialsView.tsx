import { useState, useEffect, useRef } from 'react'
import { apiFetch } from '../utils'

// ── Types ─────────────────────────────────────────────────────────────────────

interface RichTelegramStatus {
  configured: boolean
  groupChatEnabled: boolean
  maskedCtoBotToken: string | null
  maskedNotifierBotToken: string | null
  groupChatId: string | null
  userId: string | null
  lastValidatedUtc: string | null
}

interface RichGitHubStatus {
  configured: boolean
  maskedAppId: string | null
  maskedPrivateKey: string | null
  lastValidatedUtc: string | null
}

interface ConnectionsStatus {
  telegram: RichTelegramStatus
  gitHub: RichGitHubStatus
}

interface EnvVar {
  key: string
  maskedValue: string
  isSensitive: boolean
  usedBy: string[]
}

interface CredentialFile {
  id: number
  name: string
  type: string
  fileName: string
  sizeBytes: number
  createdAt: string
  mounts: Array<{ id: number; agentName: string; mountPath: string; mode: string }>
}

interface CredentialsViewProps {
  onSetupConnected: () => void
}

// ── Connection Cards ──────────────────────────────────────────────────────────

function StatusBadge({ configured }: { configured: boolean }) {
  return (
    <span className={`cred-status-badge ${configured ? 'cred-status-badge--ok' : 'cred-status-badge--missing'}`}>
      {configured ? '✓ Configured' : '○ Not configured'}
    </span>
  )
}

function MaskedRow({ label, value }: { label: string; value: string | null }) {
  if (!value) return null
  return (
    <div className="cred-masked-row">
      <span className="cred-masked-label">{label}</span>
      <span className="cred-masked-value">{value}</span>
    </div>
  )
}

// ── Env Var Modal ─────────────────────────────────────────────────────────────

interface EnvVarEditModalProps {
  editKey: string | null  // null = new
  onClose: () => void
  onSaved: () => void
}

function EnvVarEditModal({ editKey, onClose, onSaved }: EnvVarEditModalProps) {
  const [key, setKey] = useState(editKey ?? '')
  const [value, setValue] = useState('')
  const [saving, setSaving] = useState(false)
  const [err, setErr] = useState('')

  async function handleSave() {
    if (!key.trim() || !value.trim()) { setErr('Key and value are required'); return }
    setSaving(true)
    setErr('')
    try {
      const res = await apiFetch(`/api/env-vars/${encodeURIComponent(key.trim())}`, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ value: value.trim() }),
      })
      if (!res.ok) {
        const data = await res.json().catch(() => ({}))
        setErr((data as { error?: string }).error ?? `Error ${res.status}`)
      } else {
        onSaved()
        onClose()
      }
    } catch (e) { setErr(String(e)) }
    finally { setSaving(false) }
  }

  return (
    <div className="config-modal-overlay" onClick={onClose}>
      <div className="config-modal" style={{ maxWidth: 440 }} onClick={e => e.stopPropagation()}>
        <div className="config-modal-header">
          <span className="config-modal-title">{editKey ? 'Edit' : 'Add'} Variable</span>
          <button className="config-modal-close" onClick={onClose}>✕ close</button>
        </div>
        <div className="config-modal-body">
          {!editKey && (
            <div className="config-row">
              <label className="config-label">Key <span style={{ color: 'var(--red)' }}>*</span></label>
              <input className="config-input" placeholder="MY_SECRET_KEY" value={key}
                onChange={e => setKey(e.target.value.toUpperCase().replace(/[^A-Z0-9_]/g, ''))} />
              <div className="setup-field-hint">Uppercase letters, digits, underscores.</div>
            </div>
          )}
          <div className="config-row">
            <label className="config-label">Value <span style={{ color: 'var(--red)' }}>*</span></label>
            <input className="config-input" type="password" placeholder="Enter value..."
              value={value} onChange={e => setValue(e.target.value)} />
            <div className="setup-field-hint">Stored in .env. Sensitive keys are masked in the UI.</div>
          </div>
          {err && <div className="config-feedback" style={{ color: 'var(--red)' }}>{err}</div>}
          <div style={{ display: 'flex', gap: 8, marginTop: 8 }}>
            <button className="config-save-btn" disabled={saving} onClick={handleSave}>
              {saving ? 'Saving…' : 'Save'}
            </button>
            <button className="wfd-cancel-btn" onClick={onClose}>Cancel</button>
          </div>
        </div>
      </div>
    </div>
  )
}

// ── Mount Modal ───────────────────────────────────────────────────────────────

interface MountModalProps {
  fileId: number
  fileName: string
  onClose: () => void
  onMounted: () => void
}

function MountModal({ fileId, fileName, onClose, onMounted }: MountModalProps) {
  const [agentName, setAgentName] = useState('')
  const [mountPath, setMountPath] = useState('/workspace/.ssh/')
  const [mode, setMode] = useState('ro')
  const [saving, setSaving] = useState(false)
  const [err, setErr] = useState('')

  async function handleMount() {
    if (!agentName.trim() || !mountPath.trim()) { setErr('Agent name and mount path are required'); return }
    setSaving(true)
    setErr('')
    try {
      const res = await apiFetch(`/api/credential-files/${fileId}/mount`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ agentName: agentName.trim(), mountPath: mountPath.trim(), mode }),
      })
      if (!res.ok) {
        const data = await res.json().catch(() => ({}))
        setErr((data as { error?: string }).error ?? `Error ${res.status}`)
      } else {
        onMounted()
        onClose()
      }
    } catch (e) { setErr(String(e)) }
    finally { setSaving(false) }
  }

  return (
    <div className="config-modal-overlay" onClick={onClose}>
      <div className="config-modal" style={{ maxWidth: 420 }} onClick={e => e.stopPropagation()}>
        <div className="config-modal-header">
          <span className="config-modal-title">Mount "{fileName}"</span>
          <button className="config-modal-close" onClick={onClose}>✕ close</button>
        </div>
        <div className="config-modal-body">
          <div className="config-row">
            <label className="config-label">Agent name</label>
            <input className="config-input" placeholder="aops" value={agentName} onChange={e => setAgentName(e.target.value)} />
          </div>
          <div className="config-row">
            <label className="config-label">Mount path in container</label>
            <input className="config-input" placeholder="/workspace/.ssh/server.key" value={mountPath} onChange={e => setMountPath(e.target.value)} />
          </div>
          <div className="config-row">
            <label className="config-label">Mode</label>
            <select className="config-input" value={mode} onChange={e => setMode(e.target.value)}>
              <option value="ro">Read-only (ro)</option>
              <option value="rw">Read-write (rw)</option>
            </select>
          </div>
          {err && <div className="config-feedback" style={{ color: 'var(--red)' }}>{err}</div>}
          <div className="setup-field-hint">Changes take effect on next reprovision of the agent.</div>
          <div style={{ display: 'flex', gap: 8, marginTop: 8 }}>
            <button className="config-save-btn" disabled={saving} onClick={handleMount}>
              {saving ? 'Mounting…' : 'Mount'}
            </button>
            <button className="wfd-cancel-btn" onClick={onClose}>Cancel</button>
          </div>
        </div>
      </div>
    </div>
  )
}

// ── Main View ─────────────────────────────────────────────────────────────────

export default function CredentialsView({ onSetupConnected }: CredentialsViewProps) {
  const [connections, setConnections] = useState<ConnectionsStatus | null>(null)
  const [envVars, setEnvVars] = useState<EnvVar[]>([])
  const [credFiles, setCredFiles] = useState<CredentialFile[]>([])
  const [envSearch, setEnvSearch] = useState('')
  const [editingVar, setEditingVar] = useState<string | null | 'new'>()
  const [deletingVar, setDeletingVar] = useState<string | null>(null)
  const [mountingFile, setMountingFile] = useState<CredentialFile | null>(null)
  const [unmountConfirm, setUnmountConfirm] = useState<{ fileId: number; mountId: number } | null>(null)
  const [uploading, setUploading] = useState(false)
  const [uploadErr, setUploadErr] = useState('')
  const [testState, setTestState] = useState<Record<string, 'idle' | 'testing' | 'ok' | 'error'>>({})
  const [testMsg, setTestMsg] = useState<Record<string, string>>({})
  const fileInputRef = useRef<HTMLInputElement>(null)
  const [uploadForm, setUploadForm] = useState({ name: '', type: 'generic' })
  const [revealKey, setRevealKey] = useState<string | null>(null)
  const [revealValue, setRevealValue] = useState<string | null>(null)
  const [revealLoading, setRevealLoading] = useState(false)
  const [toast, setToast] = useState<string | null>(null)
  const [deleteFileConfirm, setDeleteFileConfirm] = useState<number | null>(null)

  void onSetupConnected // used by parent to refresh setup status after connection changes

  function showToast(msg: string) {
    setToast(msg)
    setTimeout(() => setToast(null), 4000)
  }

  async function loadConnections() {
    try {
      const res = await apiFetch('/api/credentials/connections')
      if (res.ok) setConnections(await res.json())
    } catch { /* ignore */ }
  }

  async function loadEnvVars() {
    try {
      const res = await apiFetch('/api/env-vars')
      if (res.ok) setEnvVars(await res.json())
    } catch { /* ignore */ }
  }

  async function loadCredFiles() {
    try {
      const res = await apiFetch('/api/credential-files')
      if (res.ok) setCredFiles(await res.json())
    } catch { /* ignore */ }
  }

  useEffect(() => {
    loadConnections()
    loadEnvVars()
    loadCredFiles()
  }, [])

  async function handleTest(provider: 'telegram' | 'github') {
    setTestState(s => ({ ...s, [provider]: 'testing' }))
    setTestMsg(s => ({ ...s, [provider]: '' }))
    try {
      const res = await apiFetch(`/api/setup/${provider}/validate`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({}),
      })
      const data = await res.json()
      if (res.ok && (data as { valid?: boolean }).valid) {
        setTestState(s => ({ ...s, [provider]: 'ok' }))
        setTestMsg(s => ({ ...s, [provider]: 'Connection OK' }))
        await loadConnections()
      } else {
        setTestState(s => ({ ...s, [provider]: 'error' }))
        setTestMsg(s => ({ ...s, [provider]: (data as { errorDetail?: string; error?: string }).errorDetail ?? (data as { error?: string }).error ?? 'Validation failed' }))
      }
    } catch (e) {
      setTestState(s => ({ ...s, [provider]: 'error' }))
      setTestMsg(s => ({ ...s, [provider]: String(e) }))
    }
  }

  async function handleDeleteVar(key: string) {
    try {
      await apiFetch(`/api/env-vars/${encodeURIComponent(key)}`, { method: 'DELETE' })
      await loadEnvVars()
      setDeletingVar(null)
    } catch (e) {
      showToast(`Failed to delete: ${e}`)
    }
  }

  async function handleReveal(key: string) {
    if (revealKey === key) { setRevealKey(null); setRevealValue(null); return }
    setRevealLoading(true)
    setRevealKey(key)
    try {
      const res = await apiFetch(`/api/env-vars/${encodeURIComponent(key)}/reveal`)
      if (res.ok) {
        const data = await res.json()
        setRevealValue((data as { value: string }).value)
      } else {
        setRevealValue('(unauthorized)')
      }
    } catch { setRevealValue('(error)') }
    finally { setRevealLoading(false) }
  }

  async function handleDeleteFile(id: number) {
    try {
      await apiFetch(`/api/credential-files/${id}`, { method: 'DELETE' })
      await loadCredFiles()
      setDeleteFileConfirm(null)
      showToast('File deleted')
    } catch (e) { showToast(`Failed: ${e}`) }
  }

  async function handleUnmount(fileId: number, mountId: number) {
    try {
      await apiFetch(`/api/credential-files/${fileId}/mount/${mountId}`, { method: 'DELETE' })
      await loadCredFiles()
      setUnmountConfirm(null)
    } catch (e) { showToast(`Failed: ${e}`) }
  }

  async function handleUpload() {
    const file = fileInputRef.current?.files?.[0]
    if (!file) { setUploadErr('Select a file first'); return }
    if (!uploadForm.name.trim()) { setUploadErr('Name is required'); return }

    setUploading(true)
    setUploadErr('')
    const fd = new FormData()
    fd.append('file', file)
    fd.append('name', uploadForm.name.trim())
    fd.append('type', uploadForm.type)

    try {
      const res = await apiFetch('/api/credential-files', { method: 'POST', body: fd })
      if (res.ok) {
        await loadCredFiles()
        setUploadForm({ name: '', type: 'generic' })
        if (fileInputRef.current) fileInputRef.current.value = ''
        showToast('File uploaded')
      } else {
        const data = await res.json().catch(() => ({}))
        setUploadErr((data as { error?: string }).error ?? `Error ${res.status}`)
      }
    } catch (e) { setUploadErr(String(e)) }
    finally { setUploading(false) }
  }

  const filteredVars = envVars.filter(v =>
    !envSearch || v.key.toLowerCase().includes(envSearch.toLowerCase())
  )

  function formatTimestamp(ts: string | null) {
    if (!ts) return 'Never'
    try { return new Date(ts).toLocaleString() } catch { return ts }
  }

  function formatBytes(b: number) {
    if (b < 1024) return `${b} B`
    if (b < 1024 * 1024) return `${(b / 1024).toFixed(1)} KB`
    return `${(b / 1024 / 1024).toFixed(1)} MB`
  }

  return (
    <div className="cred-view">
      {toast && <div className="setup-toast">{toast}</div>}

      <div className="view-header">
        <h2 className="view-title">Credentials</h2>
        <p className="view-subtitle">Manage connections, environment variables, and credential files.</p>
      </div>

      {/* ── Connections ─────────────────────────────────────────────────────── */}
      <section className="cred-section">
        <div className="cred-section-title">Connections</div>
        <div className="cred-cards">

          {/* Telegram card */}
          <div className="cred-card">
            <div className="cred-card-header">
              <span className="cred-card-name">Telegram</span>
              <StatusBadge configured={connections?.telegram.configured ?? false} />
            </div>
            {connections && (
              <div className="cred-card-body">
                <MaskedRow label="CTO bot" value={connections.telegram.maskedCtoBotToken} />
                <MaskedRow label="Notifier bot" value={connections.telegram.maskedNotifierBotToken} />
                <MaskedRow label="Group chat" value={connections.telegram.groupChatId} />
                <MaskedRow label="Your user ID" value={connections.telegram.userId} />
                <div className="cred-last-validated">
                  Last validated: {formatTimestamp(connections.telegram.lastValidatedUtc)}
                </div>
              </div>
            )}
            <div className="cred-card-actions">
              <button
                className="wfd-cancel-btn"
                disabled={testState.telegram === 'testing'}
                onClick={() => handleTest('telegram')}
              >
                {testState.telegram === 'testing' ? 'Testing…' : 'Test connection'}
              </button>
              <span className="cred-change-hint">Use Setup Banner to change</span>
            </div>
            {testMsg.telegram && (
              <div className="cred-test-result" style={{ color: testState.telegram === 'ok' ? 'var(--accent)' : 'var(--red)' }}>
                {testMsg.telegram}
              </div>
            )}
          </div>

          {/* GitHub card */}
          <div className="cred-card">
            <div className="cred-card-header">
              <span className="cred-card-name">GitHub App</span>
              <StatusBadge configured={connections?.gitHub.configured ?? false} />
            </div>
            {connections && (
              <div className="cred-card-body">
                <MaskedRow label="App ID" value={connections.gitHub.maskedAppId} />
                <MaskedRow label="Private key" value={connections.gitHub.maskedPrivateKey} />
                <div className="cred-last-validated">
                  Last validated: {formatTimestamp(connections.gitHub.lastValidatedUtc)}
                </div>
              </div>
            )}
            <div className="cred-card-actions">
              <button
                className="wfd-cancel-btn"
                disabled={testState.github === 'testing'}
                onClick={() => handleTest('github')}
              >
                {testState.github === 'testing' ? 'Testing…' : 'Test connection'}
              </button>
              <span className="cred-change-hint">Use Setup Banner to change</span>
            </div>
            {testMsg.github && (
              <div className="cred-test-result" style={{ color: testState.github === 'ok' ? 'var(--accent)' : 'var(--red)' }}>
                {testMsg.github}
              </div>
            )}
          </div>

        </div>
      </section>

      {/* ── Environment Variables ────────────────────────────────────────────── */}
      <section className="cred-section">
        <div className="cred-section-header">
          <div className="cred-section-title">Environment Variables</div>
          <div style={{ display: 'flex', gap: 8, alignItems: 'center' }}>
            <input
              className="config-input"
              style={{ width: 200 }}
              placeholder="Search keys…"
              value={envSearch}
              onChange={e => setEnvSearch(e.target.value)}
            />
            <button className="config-save-btn" onClick={() => setEditingVar('new')}>
              + Add variable
            </button>
          </div>
        </div>

        <div className="cred-env-table">
          <div className="cred-env-header">
            <span>Key</span>
            <span>Value</span>
            <span>Used by</span>
            <span>Actions</span>
          </div>
          {filteredVars.length === 0 && (
            <div className="cred-env-empty">{envSearch ? 'No matches.' : 'No environment variables found.'}</div>
          )}
          {filteredVars.map(v => (
            <div key={v.key} className="cred-env-row">
              <span className="cred-env-key">{v.key}</span>
              <span className="cred-env-value">
                {revealKey === v.key
                  ? (revealLoading ? '…' : <span style={{ fontFamily: 'monospace', color: 'var(--accent)' }}>{revealValue}</span>)
                  : v.maskedValue
                }
              </span>
              <span className="cred-env-used">
                {v.usedBy.length > 0 ? v.usedBy.join(', ') : <span style={{ color: 'var(--muted)' }}>—</span>}
              </span>
              <span className="cred-env-actions">
                {v.isSensitive && (
                  <button className="cred-action-btn" onClick={() => handleReveal(v.key)} title="Reveal value">
                    {revealKey === v.key ? '◻ hide' : '◉ reveal'}
                  </button>
                )}
                <button className="cred-action-btn" onClick={() => setEditingVar(v.key)}>Edit</button>
                {deletingVar === v.key
                  ? <>
                    <button className="cred-action-btn cred-action-btn--danger" onClick={() => handleDeleteVar(v.key)}>Confirm delete</button>
                    <button className="cred-action-btn" onClick={() => setDeletingVar(null)}>Cancel</button>
                  </>
                  : <button className="cred-action-btn cred-action-btn--danger" onClick={() => setDeletingVar(v.key)}>Delete</button>
                }
              </span>
            </div>
          ))}
        </div>
      </section>

      {/* ── Files ────────────────────────────────────────────────────────────── */}
      <section className="cred-section">
        <div className="cred-section-title">Credential Files</div>
        <div className="cred-upload-form">
          <input ref={fileInputRef} type="file" className="config-input" style={{ padding: '6px 8px', flex: 1 }}
            onChange={e => {
              const f = e.target.files?.[0]
              if (f && !uploadForm.name) setUploadForm(u => ({ ...u, name: f.name.replace(/\.[^.]+$/, '') }))
            }} />
          <input className="config-input" style={{ width: 160 }} placeholder="Name (label)"
            value={uploadForm.name} onChange={e => setUploadForm(u => ({ ...u, name: e.target.value }))} />
          <select className="config-input" style={{ width: 160 }} value={uploadForm.type}
            onChange={e => setUploadForm(u => ({ ...u, type: e.target.value }))}>
            <option value="generic">Generic</option>
            <option value="ssh-private-key">SSH private key</option>
            <option value="certificate">Certificate</option>
          </select>
          <button className="config-save-btn" disabled={uploading} onClick={handleUpload}>
            {uploading ? 'Uploading…' : 'Upload'}
          </button>
        </div>
        {uploadErr && <div className="config-feedback" style={{ color: 'var(--red)', marginBottom: 8 }}>{uploadErr}</div>}

        {credFiles.length === 0 && (
          <div className="cred-empty">No credential files uploaded yet.</div>
        )}

        {credFiles.map(cf => (
          <div key={cf.id} className="cred-file-row">
            <div className="cred-file-header">
              <span className="cred-file-name">{cf.name}</span>
              <span className="cred-file-type">{cf.type}</span>
              <span className="cred-file-size">{formatBytes(cf.sizeBytes)}</span>
              <span className="cred-file-filename" style={{ color: 'var(--muted)', fontFamily: 'monospace', fontSize: 12 }}>
                {cf.fileName}
              </span>
              <div style={{ marginLeft: 'auto', display: 'flex', gap: 6 }}>
                <button className="cred-action-btn" onClick={() => setMountingFile(cf)}>+ Mount</button>
                {deleteFileConfirm === cf.id
                  ? <>
                    <button className="cred-action-btn cred-action-btn--danger" onClick={() => handleDeleteFile(cf.id)}>Confirm</button>
                    <button className="cred-action-btn" onClick={() => setDeleteFileConfirm(null)}>Cancel</button>
                  </>
                  : <button className="cred-action-btn cred-action-btn--danger" onClick={() => setDeleteFileConfirm(cf.id)}>Delete</button>
                }
              </div>
            </div>
            {cf.mounts.length > 0 && (
              <div className="cred-file-mounts">
                {cf.mounts.map(m => (
                  <div key={m.id} className="cred-mount-row">
                    <span className="cred-mount-agent">{m.agentName}</span>
                    <span className="cred-mount-path" style={{ fontFamily: 'monospace', fontSize: 12 }}>{m.mountPath}</span>
                    <span className="cred-mount-mode">{m.mode}</span>
                    {unmountConfirm?.mountId === m.id
                      ? <>
                        <button className="cred-action-btn cred-action-btn--danger" onClick={() => handleUnmount(cf.id, m.id)}>Confirm remove</button>
                        <button className="cred-action-btn" onClick={() => setUnmountConfirm(null)}>Cancel</button>
                      </>
                      : <button className="cred-action-btn" onClick={() => setUnmountConfirm({ fileId: cf.id, mountId: m.id })}>Remove</button>
                    }
                  </div>
                ))}
              </div>
            )}
          </div>
        ))}
      </section>

      {/* Modals */}
      {(editingVar === 'new' || (editingVar && editingVar !== 'new')) && (
        <EnvVarEditModal
          editKey={editingVar === 'new' ? null : editingVar as string}
          onClose={() => setEditingVar(undefined)}
          onSaved={loadEnvVars}
        />
      )}
      {mountingFile && (
        <MountModal
          fileId={mountingFile.id}
          fileName={mountingFile.name}
          onClose={() => setMountingFile(null)}
          onMounted={loadCredFiles}
        />
      )}
    </div>
  )
}
