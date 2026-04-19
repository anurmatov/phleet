import { useState, useEffect, useRef, useCallback } from 'react'
import { apiFetch } from '../utils'

// ── Types ─────────────────────────────────────────────────────────────────────

interface RegistryEntry {
  key: string
  description: string
  category: string
  editable: boolean
  bootstrapOnly: boolean
  sensitive: boolean
  confirmRecreate: boolean
  consumers: string[]
}

interface PropagationPreview {
  key: string
  infra: string[]
  agents: string[]
  selfRecreate: boolean
  warnings: string[]
}

interface SaveResult {
  key: string
  changed: boolean
  selfRecreate?: boolean
  warning?: string
  infra?: { restarted: string[]; failed: string[] }
  agents?: { reprovisioned: string[]; failed: Array<{ name: string; error: string }> }
  warnings?: string[]
}

// ── Registry edit modal ───────────────────────────────────────────────────────

interface RegistryEditModalProps {
  entry: RegistryEntry
  onClose: () => void
  onSaved: (result: SaveResult) => void
}

function RegistryEditModal({ entry, onClose, onSaved }: RegistryEditModalProps) {
  const [value, setValue] = useState('')
  const [preview, setPreview] = useState<PropagationPreview | null>(null)
  const [previewLoading, setPreviewLoading] = useState(false)
  const [saving, setSaving] = useState(false)
  const [err, setErr] = useState('')
  const [confirmOpen, setConfirmOpen] = useState(false)

  // Fetch propagation preview when modal opens
  useEffect(() => {
    setPreviewLoading(true)
    apiFetch(`/api/credentials/${encodeURIComponent(entry.key)}/propagation-preview`)
      .then(r => r.ok ? r.json() : null)
      .then((d: PropagationPreview | null) => { if (d) setPreview(d) })
      .catch(() => {})
      .finally(() => setPreviewLoading(false))
  }, [entry.key])

  async function handleSave() {
    if (!value.trim()) { setErr('Value is required'); return }
    if (entry.confirmRecreate && !confirmOpen) {
      setConfirmOpen(true)
      return
    }
    setSaving(true)
    setErr('')
    try {
      const res = await apiFetch(`/api/credentials/${encodeURIComponent(entry.key)}`, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ value: value.trim() }),
      })
      const data: SaveResult = await res.json().catch(() => ({}))
      if (res.status === 200 || res.status === 202 || res.status === 207) {
        onSaved(data)
        onClose()
      } else {
        setErr((data as { error?: string }).error ?? `Error ${res.status}`)
      }
    } catch (e) { setErr(String(e)) }
    finally { setSaving(false) }
  }

  const affectedCount = (preview?.infra.length ?? 0) + (preview?.agents.length ?? 0)

  return (
    <div className="config-modal-overlay" onClick={onClose}>
      <div className="config-modal" style={{ maxWidth: 500 }} onClick={e => e.stopPropagation()}>
        <div className="config-modal-header">
          <span className="config-modal-title">Edit {entry.key}</span>
          <button className="config-modal-close" onClick={onClose}>✕ close</button>
        </div>
        <div className="config-modal-body">
          <div style={{ fontSize: 12, color: 'var(--muted)', marginBottom: 12 }}>{entry.description}</div>

          {/* Propagation preview */}
          {previewLoading && <div style={{ fontSize: 12, color: 'var(--muted)', marginBottom: 8 }}>Loading blast radius…</div>}
          {preview && !previewLoading && (
            <div style={{ background: 'var(--bg-alt)', borderRadius: 4, padding: '8px 12px', marginBottom: 12, fontSize: 12 }}>
              <div style={{ fontWeight: 600, marginBottom: 4 }}>
                {affectedCount === 0 ? 'No containers need restart' : `${affectedCount} container(s) will be restarted`}
              </div>
              {preview.infra.length > 0 && (
                <div style={{ color: 'var(--muted)' }}>Infra: {preview.infra.join(', ')}</div>
              )}
              {preview.agents.length > 0 && (
                <div style={{ color: 'var(--muted)' }}>Agents: {preview.agents.join(', ')}</div>
              )}
              {preview.selfRecreate && (
                <div style={{ color: 'var(--yellow)', marginTop: 4 }}>⚠ Orchestrator will restart — dashboard will disconnect briefly</div>
              )}
              {preview.warnings.length > 0 && (
                <div style={{ color: 'var(--muted)', marginTop: 4 }}>{preview.warnings.join('; ')}</div>
              )}
            </div>
          )}

          <div className="config-row">
            <label className="config-label">New value <span style={{ color: 'var(--red)' }}>*</span></label>
            <input
              className="config-input"
              type={entry.sensitive ? 'password' : 'text'}
              placeholder={entry.sensitive ? '(sensitive)' : 'Enter new value…'}
              value={value}
              onChange={e => setValue(e.target.value)}
              autoFocus
            />
          </div>

          {entry.bootstrapOnly && (
            <div className="setup-field-hint" style={{ color: 'var(--yellow)' }}>
              Bootstrap-only key — affected containers will be restarted to pick up the new value.
            </div>
          )}

          {confirmOpen && (
            <div style={{ background: 'var(--bg-alt)', borderRadius: 4, padding: '8px 12px', marginBottom: 8, fontSize: 12, color: 'var(--yellow)' }}>
              This change will restart affected containers. Are you sure?
            </div>
          )}

          {err && <div className="config-feedback" style={{ color: 'var(--red)' }}>{err}</div>}

          <div style={{ display: 'flex', gap: 8, marginTop: 8 }}>
            <button className="config-save-btn" disabled={saving} onClick={handleSave}>
              {saving ? 'Saving…' : confirmOpen ? 'Confirm restart' : 'Save'}
            </button>
            <button className="wfd-cancel-btn" onClick={confirmOpen ? () => setConfirmOpen(false) : onClose}>
              {confirmOpen ? 'Go back' : 'Cancel'}
            </button>
          </div>
        </div>
      </div>
    </div>
  )
}

// ── Registry section ──────────────────────────────────────────────────────────

interface RegistrySectionProps {
  onSaveResult: (result: SaveResult) => void
}

function RegistrySection({ onSaveResult }: RegistrySectionProps) {
  const [entries, setEntries] = useState<RegistryEntry[]>([])
  const [loading, setLoading] = useState(true)
  const [editEntry, setEditEntry] = useState<RegistryEntry | null>(null)
  const [revealKey, setRevealKey] = useState<string | null>(null)
  const [revealValue, setRevealValue] = useState<string | null>(null)
  const [revealLoading, setRevealLoading] = useState(false)
  const [categoryFilter, setCategoryFilter] = useState<string>('all')

  useEffect(() => {
    apiFetch('/api/credentials/registry')
      .then(r => r.ok ? r.json() : { entries: [] })
      .then((d: { entries: RegistryEntry[] }) => setEntries(d.entries ?? []))
      .catch(() => {})
      .finally(() => setLoading(false))
  }, [])

  const categories = ['all', ...Array.from(new Set(entries.map(e => e.category))).sort()]
  const filtered = categoryFilter === 'all' ? entries : entries.filter(e => e.category === categoryFilter)

  async function handleReveal(key: string) {
    if (revealKey === key) { setRevealKey(null); setRevealValue(null); return }
    setRevealLoading(true)
    setRevealKey(key)
    setRevealValue(null)
    try {
      const res = await apiFetch(`/api/credentials/${encodeURIComponent(key)}/value`)
      if (res.ok) {
        const d = await res.json()
        setRevealValue(d.value ?? '(not set)')
      } else {
        setRevealValue('(unauthorized)')
      }
    } catch { setRevealValue('(error)') }
    finally { setRevealLoading(false) }
  }

  if (loading) return <div style={{ color: 'var(--muted)', fontSize: 12, padding: 8 }}>Loading registry…</div>

  return (
    <>
      {/* Category filter pills */}
      <div style={{ display: 'flex', gap: 6, flexWrap: 'wrap', marginBottom: 12 }}>
        {categories.map(cat => (
          <button
            key={cat}
            className={categoryFilter === cat ? 'config-save-btn' : 'wfd-cancel-btn'}
            style={{ fontSize: 11, padding: '2px 10px' }}
            onClick={() => setCategoryFilter(cat)}
          >
            {cat}
          </button>
        ))}
      </div>

      <div className="cred-env-table">
        <div className="cred-env-header" style={{ gridTemplateColumns: '1fr 80px 80px 80px 1fr' }}>
          <span>Key</span>
          <span>Category</span>
          <span>Editable</span>
          <span>Sensitive</span>
          <span>Actions</span>
        </div>
        {filtered.map(entry => (
          <div key={entry.key} className="cred-env-row" style={{ gridTemplateColumns: '1fr 80px 80px 80px 1fr' }}>
            <span>
              <span className="cred-env-key">{entry.key}</span>
              <span style={{ fontSize: 11, color: 'var(--muted)', display: 'block' }}>{entry.description}</span>
              {revealKey === entry.key && (
                <span style={{ fontFamily: 'monospace', fontSize: 11, color: 'var(--accent)' }}>
                  {revealLoading ? '…' : (revealValue ?? '')}
                </span>
              )}
            </span>
            <span style={{ fontSize: 11 }}>{entry.category}</span>
            <span style={{ fontSize: 11, color: entry.editable ? 'var(--accent)' : 'var(--muted)' }}>
              {entry.editable ? 'yes' : 'read-only'}
            </span>
            <span style={{ fontSize: 11 }}>{entry.sensitive ? '🔒' : '—'}</span>
            <span className="cred-env-actions">
              <button className="cred-action-btn" onClick={() => handleReveal(entry.key)}>
                {revealKey === entry.key ? '◻ hide' : '◉ reveal'}
              </button>
              {entry.editable && (
                <button className="cred-action-btn" onClick={() => setEditEntry(entry)}>Edit</button>
              )}
            </span>
          </div>
        ))}
      </div>

      {editEntry && (
        <RegistryEditModal
          entry={editEntry}
          onClose={() => setEditEntry(null)}
          onSaved={result => { onSaveResult(result); setEditEntry(null) }}
        />
      )}
    </>
  )
}

// ── Save result banner ────────────────────────────────────────────────────────

interface SaveResultBannerProps {
  result: SaveResult
  onDismiss: () => void
  onRetry: (target: string, type: string) => void
}

function SaveResultBanner({ result, onDismiss, onRetry }: SaveResultBannerProps) {
  const infraFailed = result.infra?.failed ?? []
  const agentsFailed = result.agents?.failed ?? []
  const hasFailures = infraFailed.length > 0 || agentsFailed.length > 0

  if (result.selfRecreate) {
    return (
      <div className="cred-restart-banner" style={{ background: 'var(--bg-alt)', borderLeft: '3px solid var(--yellow)' }}>
        <span>⚠ {result.warning ?? 'Orchestrator will restart shortly.'}</span>
        {hasFailures && (
          <span style={{ fontSize: 12 }}>
            &nbsp;Some targets failed:
            {infraFailed.map(c => <button key={c} className="cred-action-btn" style={{ marginLeft: 6 }} onClick={() => onRetry(c, 'infra')}>Retry {c}</button>)}
            {agentsFailed.map(f => <button key={f.name} className="cred-action-btn" style={{ marginLeft: 6 }} onClick={() => onRetry(f.name, 'agent')}>Retry {f.name}</button>)}
          </span>
        )}
        <button className="wfd-cancel-btn" style={{ marginLeft: 'auto' }} onClick={onDismiss}>Dismiss</button>
      </div>
    )
  }

  if (hasFailures) {
    return (
      <div className="cred-restart-banner" style={{ background: 'var(--bg-alt)', borderLeft: '3px solid var(--red)' }}>
        <span>⚠ Partial propagation failure for <strong>{result.key}</strong>.</span>
        {infraFailed.map(c => <button key={c} className="cred-action-btn" style={{ marginLeft: 6 }} onClick={() => onRetry(c, 'infra')}>Retry {c}</button>)}
        {agentsFailed.map(f => <button key={f.name} className="cred-action-btn" style={{ marginLeft: 6 }} onClick={() => onRetry(f.name, 'agent')}>Retry {f.name}</button>)}
        <button className="wfd-cancel-btn" style={{ marginLeft: 'auto' }} onClick={onDismiss}>Dismiss</button>
      </div>
    )
  }

  return null
}

// ── Original types (below) ────────────────────────────────────────────────────

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
  onSaved: (affectedServices: string[]) => void
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
        const data = await res.json().catch(() => ({}))
        onSaved((data as { affectedServices?: string[] }).affectedServices ?? [])
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
  const [droppedFile, setDroppedFile] = useState<File | null>(null)
  const [isDragging, setIsDragging] = useState(false)
  const [revealKey, setRevealKey] = useState<string | null>(null)
  const [revealValue, setRevealValue] = useState<string | null>(null)
  const [revealLoading, setRevealLoading] = useState(false)
  const [toast, setToast] = useState<string | null>(null)
  const [deleteFileConfirm, setDeleteFileConfirm] = useState<number | null>(null)
  const [restartBanner, setRestartBanner] = useState<{ services: string[]; restarting: boolean } | null>(null)
  const [saveResult, setSaveResult] = useState<SaveResult | null>(null)

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

  function handleVarSaved(affectedServices: string[]) {
    loadEnvVars()
    if (affectedServices.length > 0)
      setRestartBanner({ services: affectedServices, restarting: false })
  }

  async function handleRestartServices() {
    if (!restartBanner) return
    setRestartBanner(b => b ? { ...b, restarting: true } : null)
    try {
      await apiFetch('/api/services/restart', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ services: restartBanner.services }),
      })
      showToast(`Restarted: ${restartBanner.services.join(', ')}`)
    } catch (e) { showToast(`Restart failed: ${e}`) }
    finally { setRestartBanner(null) }
  }

  const handleDragOver = useCallback((e: React.DragEvent) => {
    e.preventDefault()
    setIsDragging(true)
  }, [])

  const handleDragLeave = useCallback((e: React.DragEvent) => {
    e.preventDefault()
    setIsDragging(false)
  }, [])

  const handleDrop = useCallback((e: React.DragEvent) => {
    e.preventDefault()
    setIsDragging(false)
    const file = e.dataTransfer.files?.[0]
    if (!file) return
    setDroppedFile(file)
    if (!uploadForm.name)
      setUploadForm(u => ({ ...u, name: file.name.replace(/\.[^.]+$/, '') }))
  }, [uploadForm.name])

  async function handleDownload(id: number, fileName: string) {
    try {
      const res = await apiFetch(`/api/credential-files/${id}/download`)
      if (!res.ok) { showToast('Download failed'); return }
      const blob = await res.blob()
      const url = URL.createObjectURL(blob)
      const a = document.createElement('a')
      a.href = url; a.download = fileName; a.click()
      URL.revokeObjectURL(url)
    } catch (e) { showToast(`Download failed: ${e}`) }
  }

  async function handleUpload() {
    const file = droppedFile ?? fileInputRef.current?.files?.[0]
    if (!file) { setUploadErr('Select or drop a file first'); return }
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
        setDroppedFile(null)
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

  async function handleRetryPropagation(key: string, target: string, type: string) {
    try {
      await apiFetch(`/api/credentials/${encodeURIComponent(key)}/retry-propagation`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ target, type }),
      })
      showToast(`Retry for ${target} completed`)
      setSaveResult(null)
    } catch (e) { showToast(`Retry failed: ${e}`) }
  }

  return (
    <div className="cred-view">
      {toast && <div className="setup-toast">{toast}</div>}
      {saveResult && (
        <SaveResultBanner
          result={saveResult}
          onDismiss={() => setSaveResult(null)}
          onRetry={(target, type) => handleRetryPropagation(saveResult.key, target, type)}
        />
      )}
      {restartBanner && (
        <div className="cred-restart-banner">
          <span>⚠ Restart required — affected services: <strong>{restartBanner.services.join(', ')}</strong></span>
          <button className="config-save-btn" disabled={restartBanner.restarting} onClick={handleRestartServices}>
            {restartBanner.restarting ? 'Restarting…' : 'Restart now'}
          </button>
          <button className="wfd-cancel-btn" onClick={() => setRestartBanner(null)}>Dismiss</button>
        </div>
      )}

      <div className="view-header">
        <h2 className="view-title">Credentials</h2>
        <p className="view-subtitle">Manage connections, environment variables, and credential files.</p>
      </div>

      {/* ── Registry ────────────────────────────────────────────────────────── */}
      <section className="cred-section">
        <div className="cred-section-title">Managed Credentials</div>
        <p style={{ fontSize: 12, color: 'var(--muted)', marginBottom: 12 }}>
          All registered .env keys. Changes are written atomically, cache is invalidated immediately, and affected containers are restarted automatically.
        </p>
        <RegistrySection onSaveResult={result => setSaveResult(result)} />
      </section>

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
        <div
          className={`cred-upload-form${isDragging ? ' cred-upload-dragging' : ''}`}
          onDragOver={handleDragOver}
          onDragLeave={handleDragLeave}
          onDrop={handleDrop}
        >
          <input ref={fileInputRef} type="file" className="config-input" style={{ padding: '6px 8px', flex: 1 }}
            onChange={e => {
              const f = e.target.files?.[0]
              if (f) {
                setDroppedFile(null)
                if (!uploadForm.name) setUploadForm(u => ({ ...u, name: f.name.replace(/\.[^.]+$/, '') }))
              }
            }} />
          {droppedFile && (
            <span style={{ fontSize: 12, color: 'var(--accent)', whiteSpace: 'nowrap' }}>
              ↳ {droppedFile.name}
            </span>
          )}
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
        {!isDragging && !droppedFile && (
          <div style={{ fontSize: 11, color: 'var(--muted)', marginBottom: 8 }}>
            Drag and drop a file onto the row above to select it.
          </div>
        )}
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
                <button className="cred-action-btn" onClick={() => handleDownload(cf.id, cf.fileName)}>↓ Download</button>
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
          onSaved={handleVarSaved}
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
