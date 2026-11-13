import { useCallback, useState } from 'react'
import type { SignalButton, SignalDef, SignalModalState, WfActionState, WorkflowEvent, WorkflowSummary } from '../types'
import type { KeyedTimers } from '../reconnect'
import { apiFetch, apiGet, apiSend, errorMessage } from '../utils'
import { buildSignalRequest, signalErrorMessage } from '../signalRequest'
import { useKeyedStatus } from './useKeyedStatus'

// What can be done to a running workflow, wherever its row is drawn: the Agents page and the
// Workflows page both list workflows, and an action started on one is still showing its result
// when the other is opened.

export type WorkflowAction = 'cancel' | 'restart' | 'terminate'
export type SignalState = 'idle' | 'pending' | 'success' | 'error'

export function wfKey(wf: WorkflowSummary): string { return `${wf.namespace}/${wf.workflowId}` }

/** A workflow's signal status is kept under the same key as its action status. */
export const signalKey = wfKey

export function signalConfirmKey(wf: WorkflowSummary, sigName: string, btnLabel: string): string {
  return `${signalKey(wf)}|${sigName}|${btnLabel}`
}

export function wfStatusClass(wf: WorkflowSummary, signalDefs: SignalDef[]): string {
  const s = wf.status
  if (s === 'TimedOut' || s === 'Canceled' || s === 'Terminated' || s === 'Completed' || s === 'Failed') return 'wf-row-muted'
  if (s === 'Running' && signalDefs.length > 0) return 'wf-row-signal-waiting'
  if (s === 'Running') return 'wf-row-running'
  return ''
}

const UNCERTAIN_ANSWER = 'Answer delivery is uncertain. Refresh this exact run before retrying.'
const ACTION_DONE: Record<WorkflowAction, string> = { cancel: 'Cancel sent', restart: 'Restarted', terminate: 'Terminated' }

export interface WorkflowActions {
  wfKey: typeof wfKey
  signalKey: typeof signalKey
  signalConfirmKey: typeof signalConfirmKey
  wfStatusClass: typeof wfStatusClass
  /** The signals this workflow's current phase accepts. */
  getSignalDefs: (wf: WorkflowSummary) => SignalDef[]
  wfActionState: (wf: WorkflowSummary) => WfActionState
  wfActionMsg: Record<string, string>
  /** Click-again-to-confirm, as the Agents page offers it. */
  confirmAction: (wf: WorkflowSummary, action: WorkflowAction) => void
  /** Runs the action at once; the Workflows page asks through its own confirm bar first. */
  runAction: (wf: WorkflowSummary, action: WorkflowAction) => void
  signalStates: Record<string, SignalState>
  signalMsg: Record<string, string>
  /** `workflowId::phase` of signals already sent, so the buttons stay away until the phase moves. */
  sentSignals: Set<string>
  /** Drops the sent marks of workflows that left that phase or the list. */
  pruneSentSignals: (current: WorkflowSummary[]) => void
  signalConfirm: string | null
  /** Opens the dialog for a signal that needs one, otherwise click-again-to-confirm. */
  signalClick: (wf: WorkflowSummary, sig: SignalDef, btn: SignalButton) => void
  sendSignal: (wf: WorkflowSummary, signalName: string, payload: string, comment?: string) => void
  signalModal: SignalModalState | null
  setSignalModal: React.Dispatch<React.SetStateAction<SignalModalState | null>>
  menuOpen: Record<string, boolean>
  /** Toggles a row's menu; a key ending in `__close__` only closes it. */
  toggleMenu: (key: string) => void
  selectedWf: WorkflowSummary | null
  history: WorkflowEvent[]
  historyLoading: boolean
  historyError: string | null
  expandedEvents: Set<number>
  openDetail: (wf: WorkflowSummary) => void
  refreshDetail: () => void
  closeDetail: () => void
  toggleEvent: (eventId: number) => void
}

export function useWorkflowActions(timers: KeyedTimers, signalRegistry: Record<string, SignalDef[]>): WorkflowActions {
  const actions = useKeyedStatus<WfActionState>(timers, 'wf', 'idle')
  const signals = useKeyedStatus<SignalState>(timers, 'signal', 'idle')
  const [sentSignals, setSentSignals] = useState<Set<string>>(new Set())
  const [signalConfirm, setSignalConfirm] = useState<string | null>(null)
  const [signalModal, setSignalModal] = useState<SignalModalState | null>(null)
  const [menuOpen, setMenuOpen] = useState<Record<string, boolean>>({})
  const [selectedWf, setSelectedWf] = useState<WorkflowSummary | null>(null)
  const [history, setHistory] = useState<WorkflowEvent[]>([])
  const [historyLoading, setHistoryLoading] = useState(false)
  const [historyError, setHistoryError] = useState<string | null>(null)
  const [expandedEvents, setExpandedEvents] = useState<Set<number>>(new Set())

  function getSignalDefs(wf: WorkflowSummary): SignalDef[] {
    const defs = signalRegistry[wf.workflowType] ?? []
    if (!wf.phase) return []
    return defs.filter(sig => !sig.validPhases || sig.validPhases.includes(wf.phase!))
  }

  function wfActionState(wf: WorkflowSummary): WfActionState { return actions.states[wfKey(wf)] ?? 'idle' }

  function runAction(wf: WorkflowSummary, action: WorkflowAction) {
    const key = wfKey(wf)
    actions.set(key, 'pending', '')
    const base = `/api/workflows/${encodeURIComponent(wf.namespace)}`
    const url = action === 'restart'
      ? `${base}/restart/${encodeURIComponent(wf.workflowId)}?runId=${encodeURIComponent(wf.runId)}&terminateExisting=true`
      : `${base}/${action}/${encodeURIComponent(wf.workflowId)}?runId=${encodeURIComponent(wf.runId)}`
    apiSend(url, 'POST')
      .then(() => actions.settle(key, 'success', ACTION_DONE[action], 4000))
      .catch((e: unknown) => actions.settle(key, 'error', errorMessage(e), 4000))
  }

  function confirmAction(wf: WorkflowSummary, action: WorkflowAction) {
    const state = wfActionState(wf)
    if (state === 'pending') return
    if (state === `confirming-${action}`) { runAction(wf, action); return }
    actions.settle(wfKey(wf), `confirming-${action}`, '', 5000)
  }

  function sendSignal(wf: WorkflowSummary, signalName: string, payload: string, comment?: string) {
    const key = signalKey(wf)
    // An answer to a human-input request is not retried blind: its result stays on the row.
    const answer = signalName === 'request-input'
    const report = (state: SignalState, message: string) =>
      answer ? signals.set(key, state, message) : signals.settle(key, state, message, 4000)
    signals.set(key, 'pending')
    apiFetch(`/api/workflows/${encodeURIComponent(wf.namespace)}/signal/${encodeURIComponent(wf.workflowId)}`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(buildSignalRequest(wf, signalName, payload, comment)),
    })
      .then(async r => {
        if (r.ok) {
          report('success', 'Signal sent')
          setSentSignals(prev => new Set(prev).add(`${wf.workflowId}::${wf.phase}`))
          return
        }
        const body = await r.json().catch(() => ({}))
        report('error', answer && r.status >= 500 ? UNCERTAIN_ANSWER : signalErrorMessage(r.status, body))
      })
      .catch(() => report('error', answer ? UNCERTAIN_ANSWER : 'Request failed'))
  }

  function signalClick(wf: WorkflowSummary, sig: SignalDef, btn: SignalButton) {
    if (btn.requiresComment || sig.name === 'request-input') {
      setSignalModal({ wf, signalName: sig.name, button: btn, comment: '' })
      return
    }
    const ck = signalConfirmKey(wf, sig.name, btn.label)
    if (signalConfirm === ck) {
      setSignalConfirm(null)
      sendSignal(wf, sig.name, btn.payload)
    } else {
      setSignalConfirm(ck)
      timers.after('signal-confirm', 5000, () => setSignalConfirm(prev => prev === ck ? null : prev))
    }
  }

  const pruneSentSignals = useCallback((current: WorkflowSummary[]) => {
    setSentSignals(prev => {
      const next = new Set(prev)
      for (const entry of prev) {
        const sepIdx = entry.lastIndexOf('::')
        const wfId = entry.slice(0, sepIdx)
        const oldPhase = entry.slice(sepIdx + 2)
        const updated = current.find(w => w.workflowId === wfId)
        if (!updated || updated.phase !== oldPhase || updated.status !== 'Running') next.delete(entry)
      }
      return next.size !== prev.size ? next : prev
    })
  }, [])

  function toggleMenu(key: string) {
    setMenuOpen(prev => {
      if (key.endsWith('__close__')) return { ...prev, [key.slice(0, -9)]: false }
      return { ...prev, [key]: !prev[key] }
    })
  }

  async function openDetail(wf: WorkflowSummary) {
    setSelectedWf(wf)
    setHistory([])
    setHistoryError(null)
    setExpandedEvents(new Set())
    setHistoryLoading(true)
    try {
      const events = await apiGet<WorkflowEvent[]>(`/api/workflows/${encodeURIComponent(wf.namespace)}/history/${encodeURIComponent(wf.workflowId)}`)
      const sorted = [...events].reverse()
      setHistory(sorted)
      if (sorted.length > 0) setExpandedEvents(new Set([sorted[0].eventId]))
    } catch (e: unknown) {
      setHistoryError(errorMessage(e))
    } finally {
      setHistoryLoading(false)
    }
  }

  function toggleEvent(eventId: number) {
    setExpandedEvents(prev => {
      const next = new Set(prev)
      prev.has(eventId) ? next.delete(eventId) : next.add(eventId)
      return next
    })
  }

  return {
    wfKey, signalKey, signalConfirmKey, wfStatusClass, getSignalDefs,
    wfActionState, wfActionMsg: actions.messages, confirmAction, runAction,
    signalStates: signals.states, signalMsg: signals.messages,
    sentSignals, pruneSentSignals, signalConfirm, signalClick, sendSignal,
    signalModal, setSignalModal,
    menuOpen, toggleMenu,
    selectedWf, history, historyLoading, historyError, expandedEvents,
    openDetail, refreshDetail: () => { if (selectedWf) void openDetail(selectedWf) }, closeDetail: () => setSelectedWf(null), toggleEvent,
  }
}
