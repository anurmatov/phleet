import { useCallback, useEffect, useState } from 'react'
import type { OutputStyleSummary, PromptSizeLimits, ScheduleSummary, SignalDef, WorkflowTypeInfo } from '../types'
import { apiGet } from '../utils'

export interface SetupStatus {
  telegram: { configured: boolean; groupChatEnabled: boolean }
  gitHub: { configured: boolean }
}

/**
 * Reference data several pages read and none of them owns: which signals a workflow type accepts,
 * the workflow types and namespaces, output styles, the prompt size limits, schedules and whether
 * setup is complete. Read once on load, except schedules, which are read on first visit.
 */
export interface FleetCatalog {
  signalRegistry: Record<string, SignalDef[]>
  workflowTypes: WorkflowTypeInfo[]
  namespaces: string[]
  outputStyles: OutputStyleSummary[]
  /** Re-read when a style is added or removed, and each time an agent's config opens. */
  loadOutputStyles: () => void
  /** #346: null until loaded, or when the policy route is unavailable (the meters then show bytes only). */
  promptSizeLimits: PromptSizeLimits | null
  schedules: ScheduleSummary[]
  schedulesLoading: boolean
  schedulesLoaded: boolean
  loadSchedules: () => void
  setupStatus: SetupStatus | null
  loadSetupStatus: () => void
}

export function useFleetCatalog(): FleetCatalog {
  const [signalRegistry, setSignalRegistry] = useState<Record<string, SignalDef[]>>({})
  const [workflowTypes, setWorkflowTypes] = useState<WorkflowTypeInfo[]>([])
  const [namespaces, setNamespaces] = useState<string[]>([])
  const [outputStyles, setOutputStyles] = useState<OutputStyleSummary[]>([])
  const [promptSizeLimits, setPromptSizeLimits] = useState<PromptSizeLimits | null>(null)
  const [schedules, setSchedules] = useState<ScheduleSummary[]>([])
  const [schedulesLoading, setSchedulesLoading] = useState(false)
  const [schedulesLoaded, setSchedulesLoaded] = useState(false)
  const [setupStatus, setSetupStatus] = useState<SetupStatus | null>(null)

  // On failure the list stays empty and the select still offers "none", so a styled agent can
  // always be switched back off.
  const loadOutputStyles = useCallback(() => {
    apiGet<OutputStyleSummary[]>('/api/output-styles').then(setOutputStyles).catch(() => setOutputStyles([]))
  }, [])

  const loadSchedules = useCallback(() => {
    setSchedulesLoading(true)
    apiGet<ScheduleSummary[]>('/api/schedules')
      .then(list => { setSchedules(list); setSchedulesLoaded(true) })
      .catch(() => {})
      .finally(() => setSchedulesLoading(false))
  }, [])

  // On failure the banner keeps what it last knew (nothing, on a first load).
  const loadSetupStatus = useCallback(() => {
    apiGet<SetupStatus>('/api/setup/status').then(setSetupStatus).catch(() => {})
  }, [])

  useEffect(() => {
    apiGet<Record<string, SignalDef[]>>('/api/signals').then(setSignalRegistry).catch(() => {})
    apiGet<WorkflowTypeInfo[]>('/api/workflow-types').then(setWorkflowTypes).catch(() => {})
    apiGet<string[]>('/api/namespaces').then(setNamespaces).catch(() => {})
    apiGet<PromptSizeLimits>('/api/prompt-size-policy').then(setPromptSizeLimits).catch(() => setPromptSizeLimits(null))
    loadOutputStyles()
    loadSetupStatus()
  }, [loadOutputStyles, loadSetupStatus])

  return {
    signalRegistry, workflowTypes, namespaces, outputStyles, loadOutputStyles, promptSizeLimits,
    schedules, schedulesLoading, schedulesLoaded, loadSchedules, setupStatus, loadSetupStatus,
  }
}
