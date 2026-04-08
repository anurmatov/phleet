import { useState, useEffect, useRef } from 'react'
import { apiFetch } from '../utils'
import FieldHint from './FieldHint'

interface RepoRow {
  name: string
  fullName: string
  isActive: boolean
}

export default function RepositoriesView() {
  const [repos, setRepos] = useState<RepoRow[]>([])
  const [loading, setLoading] = useState(true)
  const [showArchived, setShowArchived] = useState(false)

  const [showNewForm, setShowNewForm] = useState(false)
  const [newName, setNewName] = useState('')
  const [newFullName, setNewFullName] = useState('')
  const [newFormState, setNewFormState] = useState<'idle' | 'saving' | 'success' | 'error'>('idle')
  const [newFormMsg, setNewFormMsg] = useState('')

  const [toggleConfirm, setToggleConfirm] = useState<Record<string, boolean>>({})
  const [toggleState, setToggleState] = useState<Record<string, 'idle' | 'pending' | 'success' | 'error'>>({})
  const [toggleMsg, setToggleMsg] = useState<Record<string, string>>({})
  const confirmTimers = useRef<Record<string, ReturnType<typeof setTimeout>>>({})

  function load() {
    setLoading(true)
    apiFetch('/api/repositories?includeInactive=true')
      .then(r => r.json())
      .then((data: RepoRow[]) => setRepos(data))
      .catch(() => setRepos([]))
      .finally(() => setLoading(false))
  }

  useEffect(() => { load() }, [])

  const active = repos.filter(r => r.isActive)
  const inactive = repos.filter(r => !r.isActive)
  const displayed = showArchived ? repos : active

  function handleToggleConfirmClick(name: string) {
    if (toggleConfirm[name]) {
      // second click — execute
      handleToggleActive(name, !repos.find(r => r.name === name)!.isActive)
    } else {
      setToggleConfirm(prev => ({ ...prev, [name]: true }))
      confirmTimers.current[name] = setTimeout(() => {
        setToggleConfirm(prev => ({ ...prev, [name]: false }))
      }, 5000)
    }
  }

  async function handleToggleActive(name: string, isActive: boolean) {
    clearTimeout(confirmTimers.current[name])
    setToggleConfirm(prev => ({ ...prev, [name]: false }))
    setToggleState(prev => ({ ...prev, [name]: 'pending' }))
    try {
      const res = await apiFetch(`/api/repositories/${name}/toggle-active`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ isActive }),
      })
      if (!res.ok) throw new Error(await res.text())
      setToggleState(prev => ({ ...prev, [name]: 'success' }))
      setToggleMsg(prev => ({ ...prev, [name]: isActive ? 'Activated' : 'Archived' }))
      load()
      setTimeout(() => setToggleState(prev => ({ ...prev, [name]: 'idle' })), 2000)
    } catch (e) {
      setToggleState(prev => ({ ...prev, [name]: 'error' }))
      setToggleMsg(prev => ({ ...prev, [name]: String(e) }))
      setTimeout(() => setToggleState(prev => ({ ...prev, [name]: 'idle' })), 3000)
    }
  }

  const nameValid = /^[a-zA-Z0-9_-]+$/.test(newName)
  const fullNameValid = newFullName.includes('/')

  async function handleCreate() {
    if (!nameValid || !fullNameValid) return
    setNewFormState('saving')
    try {
      const res = await apiFetch('/api/repositories', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ name: newName, fullName: newFullName }),
      })
      if (!res.ok) throw new Error(await res.text())
      setNewFormState('success')
      setNewFormMsg('Created')
      setNewName('')
      setNewFullName('')
      setShowNewForm(false)
      load()
      setTimeout(() => setNewFormState('idle'), 2000)
    } catch (e) {
      setNewFormState('error')
      setNewFormMsg(String(e))
      setTimeout(() => setNewFormState('idle'), 3000)
    }
  }

  return (
    <div className="view-page">
      <div className="view-page-header">
        <h1 className="view-page-title">
          Repositories
          {repos.length > 0 && (
            <span className="section-count">
              {active.length} active · {inactive.length} inactive
            </span>
          )}
        </h1>
        <div style={{ display: 'flex', gap: 8, alignItems: 'center' }}>
          {inactive.length > 0 && (
            <button className="wfd-cancel-btn" onClick={() => setShowArchived(s => !s)}>
              {showArchived ? 'Hide archived' : `Show archived (${inactive.length})`}
            </button>
          )}
          <button
            className="view-page-action"
            onClick={() => { setShowNewForm(s => !s); setNewFormState('idle') }}
          >
            {showNewForm ? 'Cancel' : '+ New Repository'}
          </button>
        </div>
      </div>

      {showNewForm && (
        <div className="wfd-new-form">
          <div className="wfd-new-form-title">New Repository</div>
          <div className="wfd-new-form-fields">
            <div className="wfd-form-row">
              <label className="config-label">Name <span className="wfd-required">*</span></label>
              <FieldHint>Short identifier used internally (e.g. <code>fleet</code>).</FieldHint>
              <input
                className="config-input"
                placeholder="e.g. myrepo"
                value={newName}
                onChange={e => setNewName(e.target.value)}
              />
              {newName && !nameValid && (
                <div className="wfd-field-error">No spaces or special characters except - _</div>
              )}
            </div>
            <div className="wfd-form-row">
              <label className="config-label">Full Name <span className="wfd-required">*</span></label>
              <FieldHint>GitHub repo in <code>org/name</code> format (e.g. <code>your-org/your-repo</code>). Used as the source for the repo picker in workflow start forms.</FieldHint>
              <input
                className="config-input"
                placeholder="e.g. org/repo"
                value={newFullName}
                onChange={e => setNewFullName(e.target.value)}
              />
              {newFullName && !fullNameValid && (
                <div className="wfd-field-error">Must be in org/repo format</div>
              )}
            </div>
          </div>
          <div className="wfd-new-form-actions">
            <button
              className="config-save-btn"
              disabled={newFormState === 'saving' || !newName || !nameValid || !newFullName || !fullNameValid}
              onClick={handleCreate}
            >
              {newFormState === 'saving' ? '…' : 'Create'}
            </button>
            <button className="wfd-cancel-btn" onClick={() => setShowNewForm(false)}>Cancel</button>
            {(newFormState === 'success' || newFormState === 'error') && (
              <span className={`config-feedback config-feedback-${newFormState}`}>{newFormMsg}</span>
            )}
          </div>
        </div>
      )}

      {loading && <div className="view-empty">Loading…</div>}
      {!loading && displayed.length === 0 && (
        <div className="view-empty">
          {repos.length === 0
            ? 'No repositories found.'
            : 'No active repositories. Use "Show archived" to see inactive ones.'}
        </div>
      )}

      <div className="instructions-list">
        {displayed.map(repo => {
          const confirming = toggleConfirm[repo.name] ?? false
          const state = toggleState[repo.name] ?? 'idle'
          const msg = toggleMsg[repo.name] ?? ''
          return (
            <div key={repo.name} className={`instr-row${!repo.isActive ? ' wfd-row-inactive' : ''}`}>
              <div className="instr-header">
                <span className="instr-name">{repo.name}</span>
                <span className="instr-meta">
                  <span className="instr-total">{repo.fullName}</span>
                  <span className={`wfd-active-badge${repo.isActive ? ' active' : ' inactive'}`}>
                    {repo.isActive ? 'active' : 'inactive'}
                  </span>
                </span>
                <div className="wfd-row-actions">
                  {state === 'idle' || state === 'pending' ? (
                    <button
                      className={`wfd-toggle-btn${confirming ? ' confirming' : ''}`}
                      disabled={state === 'pending'}
                      onClick={() => handleToggleConfirmClick(repo.name)}
                    >
                      {state === 'pending'
                        ? '…'
                        : confirming
                          ? `confirm ${repo.isActive ? 'disable' : 'enable'}?`
                          : repo.isActive ? 'disable' : 'enable'}
                    </button>
                  ) : (
                    <span className={`config-feedback config-feedback-${state}`}>{msg}</span>
                  )}
                </div>
              </div>
            </div>
          )
        })}
      </div>
    </div>
  )
}
