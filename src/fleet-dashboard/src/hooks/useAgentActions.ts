import { useState } from 'react'
import type { CancelState, ReprovisionAllState, ReprovisionState, RestartState, StopStartState, TaskRecord } from '../types'
import type { KeyedTimers } from '../reconnect'
import { apiGet, apiSend, errorMessage } from '../utils'
import { useKeyedStatus } from './useKeyedStatus'

export type DeleteState = 'idle' | 'confirming' | 'deleting' | 'success' | 'error'
export type BgCancelState = 'idle' | 'cancelling' | 'done' | 'error'

/** What the Agents page can do to an agent, and what it has open: one status per action per agent. */
export interface AgentActions {
  expandedAgent: string | null
  /** Opens or closes an agent's detail; opening reads its task history. */
  toggleExpanded: (agentName: string) => void
  taskHistory: Record<string, TaskRecord[]>
  historyLoading: Record<string, boolean>
  expandedTasks: Set<string>
  toggleTask: (key: string) => void
  copiedContainer: string | null
  copyContainer: (name: string) => void

  restartStates: Record<string, RestartState>
  restartMsg: Record<string, string>
  restart: (agentName: string) => void
  restartConfirm: (agentName: string) => void
  restartCancel: (agentName: string) => void

  reprovisionStates: Record<string, ReprovisionState>
  reprovisionMsg: Record<string, string>
  reprovision: (agentName: string) => void
  reprovisionConfirm: (agentName: string) => void
  reprovisionCancel: (agentName: string) => void

  cancelStates: Record<string, CancelState>
  cancelMsg: Record<string, string>
  cancel: (agentName: string) => void
  cancelConfirm: (agentName: string) => void
  cancelCancel: (agentName: string) => void
  bgCancelStates: Record<string, BgCancelState>
  bgCancel: (agentName: string, taskId: string) => void

  stopStates: Record<string, StopStartState>
  stopMsg: Record<string, string>
  /** First click asks, second stops. */
  stop: (agentName: string) => void
  startStates: Record<string, StopStartState>
  startMsg: Record<string, string>
  start: (agentName: string) => void

  deleteStates: Record<string, DeleteState>
  deleteMsg: Record<string, string>
  deleteClick: (agentName: string) => void
  /** Shows "delete?" for `ms`; a `?delete=<agent>` link arms it on load. */
  armDelete: (agentName: string, ms: number) => void
  deleteConfirm: (agentName: string) => void
  deleteCancel: (agentName: string) => void

  reprovisionAllState: ReprovisionAllState
  reprovisionAllMsg: string
  reprovisionAll: () => void
  reprovisionAllConfirm: () => void
  reprovisionAllCancel: () => void
}

/**
 * `refreshAgents` re-reads the list after a delete; `onReprovisionAll` is told when every agent was
 * asked to reprovision, so the page can raise its alert.
 */
export function useAgentActions(timers: KeyedTimers, refreshAgents: () => void, onReprovisionAll: () => void): AgentActions {
  const [expandedAgent, setExpandedAgent] = useState<string | null>(null)
  const [taskHistory, setTaskHistory] = useState<Record<string, TaskRecord[]>>({})
  const [historyLoading, setHistoryLoading] = useState<Record<string, boolean>>({})
  const [expandedTasks, setExpandedTasks] = useState<Set<string>>(new Set())
  const [copiedContainer, setCopiedContainer] = useState<string | null>(null)
  const [reprovisionAllState, setReprovisionAllState] = useState<ReprovisionAllState>('idle')
  const [reprovisionAllMsg, setReprovisionAllMsg] = useState('')
  const restarting = useKeyedStatus<RestartState>(timers, 'restart', 'idle')
  const reprovisioning = useKeyedStatus<ReprovisionState>(timers, 'reprovision', 'idle')
  const cancelling = useKeyedStatus<CancelState>(timers, 'cancel', 'idle')
  const bgCancelling = useKeyedStatus<BgCancelState>(timers, 'bg-cancel', 'idle')
  const stopping = useKeyedStatus<StopStartState>(timers, 'stop', 'idle')
  const starting = useKeyedStatus<StopStartState>(timers, 'start', 'idle')
  const deleting = useKeyedStatus<DeleteState>(timers, 'delete', 'idle')

  const agentUrl = (agentName: string) => `/api/agents/${encodeURIComponent(agentName)}`

  function toggleExpanded(agentName: string) {
    if (expandedAgent === agentName) { setExpandedAgent(null); return }
    setExpandedAgent(agentName)
    setHistoryLoading(prev => ({ ...prev, [agentName]: true }))
    apiGet<TaskRecord[]>(`${agentUrl(agentName)}/history`)
      .then(records => setTaskHistory(prev => ({ ...prev, [agentName]: records })))
      .catch(() => setTaskHistory(prev => ({ ...prev, [agentName]: [] })))
      .finally(() => setHistoryLoading(prev => ({ ...prev, [agentName]: false })))
  }

  function toggleTask(key: string) {
    setExpandedTasks(prev => {
      const next = new Set(prev)
      if (next.has(key)) next.delete(key)
      else next.add(key)
      return next
    })
  }

  function copyContainer(name: string) {
    navigator.clipboard.writeText(name).then(() => {
      setCopiedContainer(name)
      setTimeout(() => setCopiedContainer(null), 2000)
    }).catch(() => {})
  }

  // Restart, reprovision and cancel share one shape: a click shows the question (it stays until
  // answered), the answer sends one request, and its result goes back to idle by itself.
  function ask<S extends string>(status: { states: Record<string, S>; set: (key: string, state: S, message?: string) => void }, busy: S, agentName: string) {
    const state = status.states[agentName] ?? 'idle'
    if (state === busy) return
    status.set(agentName, (state === 'confirming' ? 'idle' : 'confirming') as S, '')
  }

  function restartConfirm(agentName: string) {
    restarting.set(agentName, 'restarting', '')
    apiSend(`${agentUrl(agentName)}/restart`, 'POST')
      .then(() => restarting.settle(agentName, 'success', 'Restart triggered', 4000))
      .catch((e: unknown) => restarting.settle(agentName, 'error', errorMessage(e), 4000))
  }

  function reprovisionConfirm(agentName: string) {
    reprovisioning.set(agentName, 'provisioning', '')
    apiSend(`${agentUrl(agentName)}/reprovision`, 'POST')
      .then(() => reprovisioning.settle(agentName, 'success', 'Reprovisioning...', 5000))
      .catch((e: unknown) => reprovisioning.settle(agentName, 'error', errorMessage(e), 5000))
  }

  function cancelConfirm(agentName: string) {
    cancelling.set(agentName, 'cancelling', '')
    apiSend(`${agentUrl(agentName)}/cancel`, 'POST')
      .then(() => cancelling.settle(agentName, 'success', 'Cancelled', 4000))
      .catch((e: unknown) => cancelling.settle(agentName, 'error', errorMessage(e), 4000))
  }

  function bgCancel(agentName: string, taskId: string) {
    const key = `${agentName}/${taskId}`
    if (bgCancelling.states[key] === 'cancelling') return
    bgCancelling.set(key, 'cancelling')
    apiSend(`${agentUrl(agentName)}/cancel_bg/${encodeURIComponent(taskId)}`, 'POST')
      .then(() => bgCancelling.settle(key, 'done', undefined, 4000))
      .catch(() => bgCancelling.settle(key, 'error', undefined, 4000))
  }

  function stop(agentName: string) {
    const state = stopping.states[agentName] ?? 'idle'
    if (state === 'pending') return
    if (state !== 'confirming') { stopping.settle(agentName, 'confirming', undefined, 5000); return }
    stopping.set(agentName, 'pending')
    apiSend(`${agentUrl(agentName)}/stop`, 'POST')
      .then(() => stopping.settle(agentName, 'success', 'Stopped', 4000))
      .catch((e: unknown) => stopping.settle(agentName, 'error', errorMessage(e), 4000))
  }

  function start(agentName: string) {
    if ((starting.states[agentName] ?? 'idle') === 'pending') return
    starting.set(agentName, 'pending')
    apiSend(`${agentUrl(agentName)}/start`, 'POST')
      .then(() => starting.settle(agentName, 'success', 'Started', 4000))
      .catch((e: unknown) => starting.settle(agentName, 'error', errorMessage(e), 4000))
  }

  // "confirm delete?" goes back to idle by itself, like every other confirmation here.
  function armDelete(agentName: string, ms: number) {
    deleting.settle(agentName, 'confirming', undefined, ms)
  }

  function deleteClick(agentName: string) {
    const state = deleting.states[agentName] ?? 'idle'
    if (state === 'deleting') return
    if (state === 'confirming') { deleting.set(agentName, 'idle'); return }
    armDelete(agentName, 5000)
  }

  function deleteConfirm(agentName: string) {
    deleting.set(agentName, 'deleting')
    apiSend<{ message?: string } | null>(agentUrl(agentName), 'DELETE')
      .then(d => { deleting.settle(agentName, 'success', d?.message ?? 'Deleted', 5000); refreshAgents() })
      .catch((e: unknown) => deleting.settle(agentName, 'error', errorMessage(e), 5000))
  }

  function reprovisionAll() {
    setReprovisionAllState('confirming')
    timers.after('reprovision-all', 5000, () => setReprovisionAllState(s => s === 'confirming' ? 'idle' : s))
  }

  function reprovisionAllConfirm() {
    setReprovisionAllState('running')
    apiSend('/api/agents/reprovision-running', 'POST')
      .then(() => {
        setReprovisionAllState('success')
        setReprovisionAllMsg('All agents reprovisioning…')
        onReprovisionAll()
      })
      .catch((e: unknown) => { setReprovisionAllState('error'); setReprovisionAllMsg(errorMessage(e)) })
      .finally(() => timers.after('reprovision-all', 5000, () => { setReprovisionAllState('idle'); setReprovisionAllMsg('') }))
  }

  function reprovisionAllCancel() {
    timers.cancel('reprovision-all')
    setReprovisionAllState('idle')
  }

  return {
    expandedAgent, toggleExpanded, taskHistory, historyLoading,
    expandedTasks, toggleTask, copiedContainer, copyContainer,
    restartStates: restarting.states, restartMsg: restarting.messages,
    restart: name => ask(restarting, 'restarting', name), restartConfirm, restartCancel: name => restarting.set(name, 'idle', ''),
    reprovisionStates: reprovisioning.states, reprovisionMsg: reprovisioning.messages,
    reprovision: name => ask(reprovisioning, 'provisioning', name), reprovisionConfirm, reprovisionCancel: name => reprovisioning.set(name, 'idle', ''),
    cancelStates: cancelling.states, cancelMsg: cancelling.messages,
    cancel: name => ask(cancelling, 'cancelling', name), cancelConfirm, cancelCancel: name => cancelling.set(name, 'idle', ''),
    bgCancelStates: bgCancelling.states, bgCancel,
    stopStates: stopping.states, stopMsg: stopping.messages, stop,
    startStates: starting.states, startMsg: starting.messages, start,
    deleteStates: deleting.states, deleteMsg: deleting.messages,
    deleteClick, armDelete, deleteConfirm, deleteCancel: name => deleting.set(name, 'idle'),
    reprovisionAllState, reprovisionAllMsg, reprovisionAll, reprovisionAllConfirm, reprovisionAllCancel,
  }
}
