import { useCallback, useState } from 'react'
import type { ConfigSaveState, WorkflowDefinitionDetail, WorkflowDefinitionSummary } from '../types'
import type { KeyedTimers } from '../reconnect'
import { apiGet, apiSend, errorMessage } from '../utils'
import { useKeyedConfirm, useKeyedStatus } from './useKeyedStatus'
import type { NewFormState, ToggleState } from './useVersionedResource'

const EMPTY_NEW_FORM = { name: '', namespace: '', taskQueue: '', description: '', definition: '' }

/** The workflow definitions page: the list, the JSON editor per row, and the visual editor's target. */
export interface WorkflowDefinitionsState {
  definitions: WorkflowDefinitionSummary[]
  loading: boolean
  /** True once the list has been read; it is read on the first visit, not on page load. */
  loaded: boolean
  load: () => void
  expanded: string | null
  toggleExpand: (name: string) => void
  details: Record<string, WorkflowDefinitionDetail>
  detailLoading: Record<string, boolean>
  edits: Record<string, string>
  setEdit: (name: string, value: string) => void
  reasons: Record<string, string>
  setReason: (name: string, value: string) => void
  saveState: Record<string, ConfigSaveState>
  saveMsg: Record<string, string>
  save: (name: string) => void
  selectedVersion: Record<string, number | null>
  selectVersion: (name: string, version: number | null) => void
  /** Per definition, the `name:version` whose rollback is armed, or null. */
  rollbackConfirm: Record<string, string | null>
  rollbackClick: (name: string, version: number) => void
  toggleConfirm: Record<string, boolean>
  toggleState: Record<string, ToggleState>
  toggleMsg: Record<string, string>
  toggleConfirmClick: (name: string) => void
  toggleActive: (name: string, isActive: boolean) => void
  nsFilter: string
  setNsFilter: (ns: string) => void
  search: string
  setSearch: (query: string) => void
  showNew: boolean
  setShowNew: (show: boolean) => void
  newForm: typeof EMPTY_NEW_FORM
  changeNewForm: (field: string, value: string) => void
  newState: NewFormState
  newMsg: string
  create: () => void
  /** The visual editor: undefined is closed, null a new definition, a string the one being edited. */
  editorDef: string | null | undefined
  openEditor: (name: string | null) => void
  closeEditor: () => void
}

export function useWorkflowDefinitions(timers: KeyedTimers): WorkflowDefinitionsState {
  const [definitions, setDefinitions] = useState<WorkflowDefinitionSummary[]>([])
  const [loading, setLoading] = useState(false)
  const [loaded, setLoaded] = useState(false)
  const [expanded, setExpanded] = useState<string | null>(null)
  const [details, setDetails] = useState<Record<string, WorkflowDefinitionDetail>>({})
  const [detailLoading, setDetailLoading] = useState<Record<string, boolean>>({})
  const [edits, setEdits] = useState<Record<string, string>>({})
  const [reasons, setReasons] = useState<Record<string, string>>({})
  const [selectedVersion, setSelectedVersion] = useState<Record<string, number | null>>({})
  const [rollbackConfirm, setRollbackConfirm] = useState<Record<string, string | null>>({})
  const [nsFilter, setNsFilter] = useState('all')
  const [search, setSearch] = useState('')
  const [showNew, setShowNew] = useState(false)
  const [newForm, setNewForm] = useState(EMPTY_NEW_FORM)
  const [newState, setNewState] = useState<NewFormState>('idle')
  const [newMsg, setNewMsg] = useState('')
  const [editorDef, setEditorDef] = useState<string | null | undefined>(undefined)
  const saving = useKeyedStatus<ConfigSaveState>(timers, 'wfdef-save', 'idle')
  const toggling = useKeyedStatus<ToggleState>(timers, 'wfdef-toggle-state', 'idle')
  const toggleConfirm = useKeyedConfirm(timers, 'wfdef-toggle')

  const itemUrl = (name: string) => `/api/workflow-definitions/${encodeURIComponent(name)}`

  const load = useCallback(() => {
    setLoading(true)
    apiGet<WorkflowDefinitionSummary[]>('/api/workflow-definitions?includeInactive=true')
      .then(list => { setDefinitions(list); setLoaded(true) })
      .catch(() => {})
      .finally(() => setLoading(false))
  }, [])

  function loadDetail(name: string) {
    setDetailLoading(prev => ({ ...prev, [name]: true }))
    apiGet<WorkflowDefinitionDetail>(itemUrl(name))
      .then(detail => {
        setDetails(prev => ({ ...prev, [name]: detail }))
        setEdits(prev => ({ ...prev, [name]: detail.definition }))
      })
      .catch(() => {})
      .finally(() => setDetailLoading(prev => ({ ...prev, [name]: false })))
  }

  function toggleExpand(name: string) {
    setExpanded(prev => prev === name ? null : name)
    if (expanded !== name && !details[name]) loadDetail(name)
  }

  /** Stores `definition` as the next version, then re-reads the row and the list. */
  function put(name: string, definition: string, reason: string | undefined, done: string, after?: () => void) {
    saving.set(name, 'saving')
    apiSend(itemUrl(name), 'PUT', { definition, reason })
      .then(() => {
        saving.settle(name, 'success', done, 3000)
        after?.()
        loadDetail(name)
        load()
      })
      .catch((err: unknown) => saving.settle(name, 'error', errorMessage(err), 5000))
  }

  function save(name: string) {
    put(name, edits[name], reasons[name]?.trim() || undefined, 'Saved')
  }

  /** First click arms that version, second stores its definition again as the newest. */
  function rollbackClick(name: string, version: number) {
    const key = `${name}:${version}`
    if (rollbackConfirm[name] !== key) {
      setRollbackConfirm(prev => ({ ...prev, [name]: key }))
      timers.after(`wfdef-rollback:${name}`, 5000, () => setRollbackConfirm(prev => prev[name] === key ? { ...prev, [name]: null } : prev))
      return
    }
    setRollbackConfirm(prev => ({ ...prev, [name]: null }))
    const definition = details[name]?.versions.find(v => v.version === version)?.definition
    if (!definition) return
    put(name, definition, `Rollback to v${version}`, `Rolled back to v${version}`,
      () => setSelectedVersion(prev => ({ ...prev, [name]: null })))
  }

  function toggleActive(name: string, isActive: boolean) {
    toggleConfirm.disarm(name)
    toggling.set(name, 'pending')
    apiSend(`${itemUrl(name)}/toggle-active`, 'POST', { isActive })
      .then(() => {
        toggling.settle(name, 'success', isActive ? 'Enabled' : 'Disabled', 3000)
        load()
      })
      .catch((err: unknown) => toggling.settle(name, 'error', errorMessage(err), 5000))
  }

  function create() {
    setNewState('saving')
    apiSend('/api/workflow-definitions', 'POST', {
      name: newForm.name,
      namespace: newForm.namespace,
      taskQueue: newForm.taskQueue,
      definition: newForm.definition,
      description: newForm.description || undefined,
    })
      .then(() => {
        setNewState('success')
        setNewMsg('Created')
        setShowNew(false)
        setNewForm(EMPTY_NEW_FORM)
        load()
        timers.after('wfdef-new', 3000, () => setNewState('idle'))
      })
      .catch((err: unknown) => {
        setNewState('error')
        setNewMsg(errorMessage(err))
        timers.after('wfdef-new', 5000, () => setNewState('idle'))
      })
  }

  return {
    definitions, loading, loaded, load,
    expanded, toggleExpand,
    details, detailLoading,
    edits, setEdit: (name, value) => setEdits(prev => ({ ...prev, [name]: value })),
    reasons, setReason: (name, value) => setReasons(prev => ({ ...prev, [name]: value })),
    saveState: saving.states, saveMsg: saving.messages, save,
    selectedVersion, selectVersion: (name, version) => setSelectedVersion(prev => ({ ...prev, [name]: version })),
    rollbackConfirm, rollbackClick,
    toggleConfirm: toggleConfirm.armed, toggleState: toggling.states, toggleMsg: toggling.messages,
    toggleConfirmClick: toggleConfirm.arm, toggleActive,
    nsFilter, setNsFilter, search, setSearch,
    showNew, setShowNew,
    newForm, changeNewForm: (field, value) => setNewForm(prev => ({ ...prev, [field]: value })),
    newState, newMsg, create,
    editorDef, openEditor: setEditorDef, closeEditor: () => setEditorDef(undefined),
  }
}
