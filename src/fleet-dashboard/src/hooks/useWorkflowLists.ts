import { useCallback, useMemo, useRef, useState } from 'react'
import type { WorkflowSummary } from '../types'
import { apiGet } from '../utils'

/**
 * The Workflows page's lists: the namespace filter, the recently completed runs it reads on demand,
 * and a manual re-read of the running ones. The running list itself is the socket's, so it is
 * passed in.
 */
export interface WorkflowLists {
  workflowsLoading: boolean
  /** Re-reads the running workflows over HTTP; the socket keeps them current otherwise. */
  refreshWorkflows: () => void
  /** Applies a live snapshot and supersedes HTTP reads already in flight. */
  receiveWorkflows: (list: WorkflowSummary[]) => void
  completedCollapsed: boolean
  toggleCompleted: () => void
  completedLoading: boolean
  refreshCompleted: () => void
  nsFilter: string
  setNsFilter: (ns: string) => void
  namespaces: string[]
  filteredWorkflows: WorkflowSummary[]
  filteredCompletedWorkflows: WorkflowSummary[]
  startOpen: boolean
  setStartOpen: (open: boolean) => void
}

export function useWorkflowLists(workflows: WorkflowSummary[], setWorkflows: (list: WorkflowSummary[]) => void): WorkflowLists {
  const [workflowsLoading, setWorkflowsLoading] = useState(false)
  const [completed, setCompleted] = useState<WorkflowSummary[]>([])
  const [completedCollapsed, setCompletedCollapsed] = useState(true)
  const [completedLoading, setCompletedLoading] = useState(false)
  const [nsFilter, setNsFilter] = useState('all')
  const [startOpen, setStartOpen] = useState(false)

  const socketRevision = useRef(0)
  const receiveWorkflows = useCallback((list: WorkflowSummary[]) => {
    socketRevision.current++
    setWorkflows(list)
  }, [setWorkflows])

  const refreshWorkflows = useCallback(() => {
    const revision = socketRevision.current
    setWorkflowsLoading(true)
    apiGet<WorkflowSummary[]>('/api/workflows')
      .then(list => {
        // A newer socket snapshot wins over an earlier HTTP read, including initial load.
        if (socketRevision.current === revision) setWorkflows(list)
      })
      .catch(() => {})
      .finally(() => setWorkflowsLoading(false))
  }, [setWorkflows])

  const refreshCompleted = useCallback(() => {
    setCompletedLoading(true)
    apiGet<WorkflowSummary[]>('/api/workflows/completed?hours=24')
      .then(setCompleted)
      .catch(() => {})
      .finally(() => setCompletedLoading(false))
  }, [])

  const namespaces = useMemo(() => Array.from(new Set(workflows.map(w => w.namespace))).sort(), [workflows])
  const filteredWorkflows = useMemo(() => nsFilter === 'all' ? workflows : workflows.filter(w => w.namespace === nsFilter), [workflows, nsFilter])
  const filteredCompletedWorkflows = useMemo(() => nsFilter === 'all' ? completed : completed.filter(w => w.namespace === nsFilter), [completed, nsFilter])

  return {
    workflowsLoading, refreshWorkflows, receiveWorkflows,
    completedCollapsed, toggleCompleted: () => setCompletedCollapsed(c => !c),
    completedLoading, refreshCompleted,
    nsFilter, setNsFilter, namespaces, filteredWorkflows, filteredCompletedWorkflows,
    startOpen, setStartOpen,
  }
}
