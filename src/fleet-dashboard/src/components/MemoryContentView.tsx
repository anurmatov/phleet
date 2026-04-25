import { useState, useEffect, useCallback } from 'react'
import { createPortal } from 'react-dom'
import ReactMarkdown from 'react-markdown'
import type { MemoryDoc } from '../types'
import { apiFetch } from '../utils'
import { useMemoryIdCache } from '../context/MemoryIdCacheContext'

interface MemoryContentViewProps {
  id: string
  /** Called when the memory is deleted so the parent can react (close modal, refresh tree). */
  onDeleted?: (id: string) => void
  /** Called when a save completes successfully (parent may refresh tree). */
  onSaved?: (id: string) => void
}

type ViewState = 'loading' | 'loaded' | 'editing' | 'saving' | 'not-found' | 'error'

/** S6: Modal-style delete confirmation to reduce accidental deletes. */
interface DeleteModalProps {
  onConfirm: () => void
  onCancel: () => void
}
function DeleteModal({ onConfirm, onCancel }: DeleteModalProps) {
  return createPortal(
    <div className="memory-modal-overlay" onClick={e => { if (e.target === e.currentTarget) onCancel() }}>
      <div className="memory-delete-modal">
        <div className="memory-delete-modal-title">Delete this memory?</div>
        <div className="memory-delete-modal-body">This cannot be undone.</div>
        <div className="memory-delete-modal-actions">
          <button className="memory-cv-btn" onClick={onCancel}>Cancel</button>
          <button className="memory-cv-btn memory-cv-btn-danger" onClick={onConfirm}>Delete</button>
        </div>
      </div>
    </div>,
    document.body
  )
}

export default function MemoryContentView({ id, onDeleted, onSaved }: MemoryContentViewProps) {
  const [doc, setDoc] = useState<MemoryDoc | null>(null)
  const [state, setState] = useState<ViewState>('loading')
  const [errorMsg, setErrorMsg] = useState('')
  const [editContent, setEditContent] = useState('')
  const [editTags, setEditTags] = useState('')
  const [saveError, setSaveError] = useState('')
  const [showDeleteModal, setShowDeleteModal] = useState(false)
  const [deleteError, setDeleteError] = useState('')
  const { refresh: refreshIdCache } = useMemoryIdCache()

  const load = useCallback(async () => {
    setState('loading')
    setErrorMsg('')
    setSaveError('')
    setDeleteError('')
    setShowDeleteModal(false)
    try {
      const resp = await apiFetch(`/api/memory/${encodeURIComponent(id)}`)
      if (resp.status === 404) { setState('not-found'); return }
      if (!resp.ok) throw new Error(`HTTP ${resp.status}`)
      const d: MemoryDoc = await resp.json()
      setDoc(d)
      setState('loaded')
    } catch (e) {
      setErrorMsg(e instanceof Error ? e.message : String(e))
      setState('error')
    }
  }, [id])

  useEffect(() => { load() }, [load])

  function startEdit() {
    if (!doc) return
    setEditContent(doc.content)
    setEditTags(doc.tags.join(', '))
    setSaveError('')
    setState('editing')
  }

  async function save() {
    if (!doc) return
    setState('saving')
    setSaveError('')
    try {
      const resp = await apiFetch(`/api/memory/${encodeURIComponent(doc.id)}`, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          content: editContent,
          tags: editTags.split(',').map(t => t.trim()).filter(Boolean),
          last_seen_updated_at: doc.updated_at,
        }),
      })
      if (resp.status === 409) {
        const body = await resp.json()
        setSaveError(`Someone else edited this memory (updated at ${body.current_updated_at}). Reload to see latest content.`)
        setState('editing')
        return
      }
      if (!resp.ok) throw new Error(`HTTP ${resp.status}`)
      await load()
      onSaved?.(doc.id)
    } catch (e) {
      setSaveError(e instanceof Error ? e.message : String(e))
      setState('editing')
    }
  }

  async function deleteMemory() {
    if (!doc) return
    setShowDeleteModal(false)
    try {
      const resp = await apiFetch(`/api/memory/${encodeURIComponent(doc.id)}`, { method: 'DELETE' })
      if (!resp.ok) throw new Error(`HTTP ${resp.status}`)
      refreshIdCache()
      onDeleted?.(doc.id)
    } catch (e) {
      setDeleteError(e instanceof Error ? e.message : String(e))
    }
  }

  if (state === 'loading') {
    return <div className="memory-cv-placeholder">Loading…</div>
  }

  if (state === 'not-found') {
    return <div className="memory-cv-placeholder memory-cv-error">Memory not found or was deleted.</div>
  }

  if (state === 'error') {
    return (
      <div className="memory-cv-placeholder memory-cv-error">
        Failed to load memory: {errorMsg}
        <button className="memory-cv-retry" onClick={load}>Retry</button>
      </div>
    )
  }

  if (!doc) return null

  return (
    <div className="memory-cv">
      {/* S6: Delete confirmation modal rendered via portal */}
      {showDeleteModal && (
        <DeleteModal
          onConfirm={deleteMemory}
          onCancel={() => setShowDeleteModal(false)}
        />
      )}

      <div className="memory-cv-header">
        <div className="memory-cv-title">{doc.title}</div>
        <div className="memory-cv-actions">
          {state === 'loaded' && (
            <>
              <button className="memory-cv-btn" onClick={startEdit}>Edit</button>
              <button className="memory-cv-btn memory-cv-btn-danger" onClick={() => setShowDeleteModal(true)}>Delete</button>
            </>
          )}
          {state === 'editing' && (
            <>
              <button className="memory-cv-btn memory-cv-btn-primary" onClick={save}>Save</button>
              <button className="memory-cv-btn" onClick={() => setState('loaded')}>Cancel</button>
            </>
          )}
          {state === 'saving' && <span className="memory-cv-saving">Saving…</span>}
        </div>
      </div>

      <div className="memory-cv-meta">
        <span className="memory-cv-chip">{doc.type}</span>
        {doc.project && <span className="memory-cv-chip">{doc.project}</span>}
        {doc.tags.map(t => <span key={t} className="memory-cv-chip memory-cv-chip-tag">{t}</span>)}
        <span className="memory-cv-meta-id" title={doc.id}>{doc.id.substring(0, 8)}</span>
        {doc.agent && <span className="memory-cv-meta-text">by {doc.agent}</span>}
        <span className="memory-cv-meta-text">updated {new Date(doc.updated_at).toLocaleDateString()}</span>
      </div>

      {deleteError && <div className="memory-cv-error-inline">Delete failed: {deleteError}</div>}
      {saveError && <div className="memory-cv-error-inline">{saveError}</div>}

      {(state === 'editing' || state === 'saving') ? (
        <div className="memory-cv-edit">
          <div className="memory-cv-edit-label">Tags (comma-separated)</div>
          <input
            className="memory-cv-tags-input"
            value={editTags}
            onChange={e => setEditTags(e.target.value)}
            disabled={state === 'saving'}
            placeholder="tag1, tag2, …"
          />
          <div className="memory-cv-edit-label">Content</div>
          <textarea
            className="memory-cv-textarea"
            value={editContent}
            onChange={e => setEditContent(e.target.value)}
            disabled={state === 'saving'}
          />
        </div>
      ) : (
        // S7: render content as markdown for readability.
        // S9 (MemoryText chips inside markdown body) requires a rehype plugin to safely walk
        // text nodes — deferred to a follow-up issue to avoid breaking inline formatting.
        <div className="memory-cv-content memory-cv-content--md">
          <ReactMarkdown>{doc.content}</ReactMarkdown>
        </div>
      )}
    </div>
  )
}
