import { useCallback, useState } from 'react'
import type { ConfigSaveState, PromptSizeReport } from '../types'
import type { KeyedTimers } from '../reconnect'
import { apiGet, apiSend, errorMessage } from '../utils'
import { useKeyedConfirm, useKeyedStatus } from './useKeyedStatus'

// Instructions and project contexts are the same thing to the dashboard: a named text with
// versions, one of them current, that can be edited into a new version, rolled back, switched off
// and created. This is that one editor's state and its requests, for either route.

interface VersionedDetail {
  currentVersion: number
  versions: { versionNumber: number; content: string }[]
}

/** A write that may answer with the #346 size report. Null when the answer has no body. */
type SizedWrite = { size?: PromptSizeReport } | null

export type ToggleState = 'idle' | 'pending' | 'success' | 'error'
export type NewFormState = 'idle' | 'saving' | 'success' | 'error'

export interface VersionedResource<TSummary, TDetail> {
  items: TSummary[]
  loading: boolean
  /** Re-reads the list, showing it as loading. On failure the list held so far stays. */
  reload: () => void
  expanded: string | null
  toggleExpanded: (name: string) => void
  detail: Record<string, TDetail>
  detailLoading: Record<string, boolean>
  edits: Record<string, string>
  setEdit: (name: string, content: string) => void
  reasons: Record<string, string>
  setReason: (name: string, reason: string) => void
  saveState: Record<string, ConfigSaveState>
  saveMsg: Record<string, string>
  save: (name: string) => void
  selectedVersion: Record<string, number | null>
  selectVersion: (name: string, version: number | null) => void
  rollbackConfirm: Record<string, boolean>
  rollbackClick: (e: React.MouseEvent, name: string, version: number, saveState: ConfigSaveState) => void
  toggleConfirm: Record<string, boolean>
  toggleState: Record<string, ToggleState>
  toggleMsg: Record<string, string>
  toggleConfirmClick: (name: string) => void
  toggleActive: (name: string, isActive: boolean) => void
  showNew: boolean
  setShowNew: (show: boolean) => void
  newForm: { name: string; content: string; reason: string }
  changeNewForm: (field: string, value: string) => void
  newState: NewFormState
  newMsg: string
  create: () => void
  /** #346: the last write's size warning per row; a later write with none clears it. */
  sizeWarnings: Record<string, string>
}

/**
 * `basePath` is `/api/instructions` or `/api/project-contexts`; `scope` names this resource's
 * timers so the two never share one.
 */
export function useVersionedResource<TSummary, TDetail extends VersionedDetail>(
  basePath: string, scope: string, timers: KeyedTimers,
): VersionedResource<TSummary, TDetail> {
  const [items, setItems] = useState<TSummary[]>([])
  const [loading, setLoading] = useState(false)
  const [expanded, setExpanded] = useState<string | null>(null)
  const [detail, setDetail] = useState<Record<string, TDetail>>({})
  const [detailLoading, setDetailLoading] = useState<Record<string, boolean>>({})
  const [edits, setEdits] = useState<Record<string, string>>({})
  const [reasons, setReasons] = useState<Record<string, string>>({})
  const [selectedVersion, setSelectedVersion] = useState<Record<string, number | null>>({})
  const [showNew, setShowNew] = useState(false)
  const [newForm, setNewForm] = useState({ name: '', content: '', reason: '' })
  const [newState, setNewState] = useState<NewFormState>('idle')
  const [newMsg, setNewMsg] = useState('')
  const [sizeWarnings, setSizeWarnings] = useState<Record<string, string>>({})
  const saving = useKeyedStatus<ConfigSaveState>(timers, `${scope}-save`, 'idle')
  const toggling = useKeyedStatus<ToggleState>(timers, `${scope}-toggle-state`, 'idle')
  const rollback = useKeyedConfirm(timers, `${scope}-rollback`)
  const toggleConfirm = useKeyedConfirm(timers, `${scope}-toggle`)

  const itemUrl = (name: string) => `${basePath}/${encodeURIComponent(name)}`

  const reload = useCallback(() => {
    setLoading(true)
    apiGet<TSummary[]>(basePath)
      .then(setItems)
      .catch(() => {})
      .finally(() => setLoading(false))
  }, [basePath])

  /** After a write: the list follows, without flashing "Loading…" over the row being edited. */
  function refreshItems() {
    apiGet<TSummary[]>(basePath).then(setItems).catch(() => {})
  }

  /**
   * A write's `size.warning` sets the row's ⚠ badge and notice; a write with no warning clears it.
   * A response without `size` (an older orchestrator) changes nothing.
   */
  function applySizeWarning(name: string, size: PromptSizeReport | undefined) {
    if (!size) return
    setSizeWarnings(prev => {
      const next = { ...prev }
      if (size.warning) next[name] = size.warning
      else delete next[name]
      return next
    })
  }

  function loadDetail(name: string) {
    setDetailLoading(prev => ({ ...prev, [name]: true }))
    apiGet<TDetail>(itemUrl(name))
      .then(loaded => {
        setDetail(prev => ({ ...prev, [name]: loaded }))
        const current = loaded.versions.find(v => v.versionNumber === loaded.currentVersion)
        if (current) setEdits(prev => ({ ...prev, [name]: current.content }))
      })
      .catch(() => saving.set(name, 'error', 'Failed to load'))
      .finally(() => setDetailLoading(prev => ({ ...prev, [name]: false })))
  }

  function toggleExpanded(name: string) {
    if (expanded === name) { setExpanded(null); return }
    setExpanded(name)
    if (!detail[name]) loadDetail(name)
  }

  function save(name: string) {
    const content = edits[name]
    if (!content?.trim()) return
    const reason = reasons[name]?.trim() || undefined
    saving.set(name, 'saving')
    apiSend<SizedWrite>(`${itemUrl(name)}/versions`, 'POST', { content, reason })
      .then(res => {
        applySizeWarning(name, res?.size)
        saving.settle(name, 'success', 'Saved', 3000)
        setReasons(prev => ({ ...prev, [name]: '' }))
        loadDetail(name)
        refreshItems()
      })
      .catch((err: unknown) => saving.settle(name, 'error', errorMessage(err), 5000))
  }

  function rollbackTo(name: string, targetVersion: number) {
    saving.set(name, 'saving')
    apiSend<SizedWrite>(`${itemUrl(name)}/rollback/${targetVersion}`, 'POST')
      .then(res => {
        applySizeWarning(name, res?.size)
        saving.settle(name, 'success', `Rolled back to v${targetVersion}`, 3000)
        setSelectedVersion(prev => ({ ...prev, [name]: null }))
        loadDetail(name)
        refreshItems()
      })
      .catch((err: unknown) => saving.settle(name, 'error', errorMessage(err), 5000))
  }

  function rollbackClick(e: React.MouseEvent, name: string, version: number, saveState: ConfigSaveState) {
    e.stopPropagation()
    if (saveState === 'saving') return
    const key = `${name}:${version}`
    if (rollback.armed[key]) {
      rollback.disarm(key)
      rollbackTo(name, version)
    } else {
      rollback.arm(key)
    }
  }

  function toggleActive(name: string, isActive: boolean) {
    toggleConfirm.disarm(name)
    toggling.set(name, 'pending')
    apiSend(`${itemUrl(name)}/toggle-active`, 'POST', { isActive })
      .then(() => {
        toggling.settle(name, 'success', isActive ? 'Enabled' : 'Disabled', 3000)
        refreshItems()
      })
      .catch((err: unknown) => toggling.settle(name, 'error', errorMessage(err), 5000))
  }

  function create() {
    const name = newForm.name
    setNewState('saving')
    // `reason` is offered for instructions only; unset, it is left out of the body.
    apiSend<SizedWrite>(basePath, 'POST', { name, content: newForm.content, reason: newForm.reason || undefined })
      .then(res => {
        applySizeWarning(name, res?.size)
        setNewState('success')
        setNewMsg('Created')
        setShowNew(false)
        setNewForm({ name: '', content: '', reason: '' })
        refreshItems()
        timers.after(`${scope}-new`, 3000, () => setNewState('idle'))
      })
      .catch((err: unknown) => {
        setNewState('error')
        setNewMsg(errorMessage(err))
        timers.after(`${scope}-new`, 5000, () => setNewState('idle'))
      })
  }

  return {
    items, loading, reload,
    expanded, toggleExpanded,
    detail, detailLoading,
    edits, setEdit: (name, content) => setEdits(prev => ({ ...prev, [name]: content })),
    reasons, setReason: (name, reason) => setReasons(prev => ({ ...prev, [name]: reason })),
    saveState: saving.states, saveMsg: saving.messages, save,
    selectedVersion,
    selectVersion: (name, version) => setSelectedVersion(prev => ({ ...prev, [name]: prev[name] === version ? null : version })),
    rollbackConfirm: rollback.armed, rollbackClick,
    toggleConfirm: toggleConfirm.armed, toggleState: toggling.states, toggleMsg: toggling.messages,
    toggleConfirmClick: toggleConfirm.arm, toggleActive,
    showNew, setShowNew,
    newForm, changeNewForm: (field, value) => setNewForm(prev => ({ ...prev, [field]: value })),
    newState, newMsg, create,
    sizeWarnings,
  }
}
