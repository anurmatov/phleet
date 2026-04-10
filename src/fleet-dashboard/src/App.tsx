import { useEffect, useRef, useState, useCallback, useMemo } from 'react'
import type {
  AgentState,
  TaskRecord,
  WorkflowSummary,
  WorkflowEvent,
  SignalDef,
  SignalButton,
  SignalModalState,
  Alert,
  McpEndpointEntry,
  CreateForm,
  AgentConfig,
  ConfigEdits,
  InstructionSummary,
  InstructionDetail,
  ProjectContextSummary,
  ProjectContextDetail,
  WsStatus,
  RestartState,
  ReprovisionState,
  StopStartState,
  WfActionState,
  CancelState,
  ConfigSaveState,
  ActiveView,
  ReprovisionAllState,
  WorkflowDefinitionSummary,
  WorkflowDefinitionDetail,
  WorkflowTypeInfo,
  ScheduleSummary,
} from './types'
import { apiFetch, heartbeatAge } from './utils'
import { PROVIDER_DEFAULT_MODEL } from './constants'
import AppHeader from './components/AppHeader'
import AppFooter from './components/AppFooter'
import Sidenav from './components/Sidenav'
import AgentsView from './components/AgentsView'
import WorkflowsView from './components/WorkflowsView'
import InstructionsView from './components/InstructionsView'
import ProjectContextsView from './components/ProjectContextsView'
import WorkflowDefinitionsView from './components/WorkflowDefinitionsView'
import WorkflowDefinitionEditor from './components/WorkflowEditor/WorkflowDefinitionEditor'
import AlertsView from './components/AlertsView'
import LogViewer from './components/LogViewer'
import SignalModal from './components/SignalModal'
import CreateAgentModal from './components/CreateAgentModal'
import AgentConfigModal from './components/AgentConfigModal'
import StartWorkflowModal from './components/StartWorkflowModal'
import SchedulesView from './components/SchedulesView'
import NamespacesView from './components/NamespacesView'
import RepositoriesView from './components/RepositoriesView'

type WsMessage =
  | { type: 'workflows'; data: WorkflowSummary[] }
  | (AgentState & { type?: undefined })

const STUCK_TASK_MS = 30 * 60 * 1000

const DEFAULT_CREATE_FORM: CreateForm = {
  name: '', displayName: '', role: '', model: PROVIDER_DEFAULT_MODEL['claude'],
  containerName: '', memoryLimitMb: '4096', isEnabled: true, image: '',
  permissionMode: 'acceptEdits', maxTurns: '50', workDir: '/workspace',
  proactiveIntervalMinutes: '0',
  groupListenMode: 'mention', groupDebounceSeconds: '15', shortName: '',
  ttsServiceUrl: '', effort: '', jsonSchema: '', agentsJson: '', autoMemoryEnabled: true,
  showStats: true, prefixMessages: false, suppressToolMessages: false, telegramSendOnly: false, provider: 'claude',
  codexSandboxMode: '',
  tools: '', projects: '', networks: '', envRefs: '',
  mcpEndpoints: [], telegramUsers: '', telegramGroups: '',
}

export default function App() {
  const [agents, setAgents] = useState<Record<string, AgentState>>({})
  const [workflows, setWorkflows] = useState<WorkflowSummary[]>([])
  const [workflowsLoading, setWorkflowsLoading] = useState(false)
  const [completedWorkflows, setCompletedWorkflows] = useState<WorkflowSummary[]>([])
  const [completedCollapsed, setCompletedCollapsed] = useState(true)
  const [completedLoading, setCompletedLoading] = useState(false)
  const [nsFilter, setNsFilter] = useState<string>('all')
  const [wsStatus, setWsStatus] = useState<WsStatus>('connecting')
  const [lastUpdated, setLastUpdated] = useState('')
  const wsRef = useRef<WebSocket | null>(null)
  const reconnectTimer = useRef<ReturnType<typeof setTimeout> | null>(null)
  const unmounted = useRef(false)

  const [expandedAgent, setExpandedAgent] = useState<string | null>(null)
  const [taskHistory, setTaskHistory] = useState<Record<string, TaskRecord[]>>({})
  const [historyLoading, setHistoryLoading] = useState<Record<string, boolean>>({})

  const [restartStates, setRestartStates] = useState<Record<string, RestartState>>({})
  const [restartMsg, setRestartMsg] = useState<Record<string, string>>({})

  const [reprovisionStates, setReprovisionStates] = useState<Record<string, ReprovisionState>>({})
  const [reprovisionMsg, setReprovisionMsg] = useState<Record<string, string>>({})
  const [cancelStates, setCancelStates] = useState<Record<string, CancelState>>({})
  const [cancelMsg, setCancelMsg] = useState<Record<string, string>>({})
  const [bgCancelStates, setBgCancelStates] = useState<Record<string, 'idle' | 'cancelling' | 'done' | 'error'>>({})

  const [wfActionStates, setWfActionStates] = useState<Record<string, WfActionState>>({})
  const [wfActionMsg, setWfActionMsg] = useState<Record<string, string>>({})

  const [stopStates, setStopStates] = useState<Record<string, StopStartState>>({})
  const [stopMsg, setStopMsg] = useState<Record<string, string>>({})
  const [startStates, setStartStates] = useState<Record<string, StopStartState>>({})
  const [startMsg, setStartMsg] = useState<Record<string, string>>({})

  const [signalRegistry, setSignalRegistry] = useState<Record<string, SignalDef[]>>({})
  const [workflowTypes, setWorkflowTypes] = useState<WorkflowTypeInfo[]>([])
  const [apiNamespaces, setApiNamespaces] = useState<string[]>([])
  const [startWfModalOpen, setStartWfModalOpen] = useState(false)
  const [signalModal, setSignalModal] = useState<SignalModalState | null>(null)
  const [signalStates, setSignalStates] = useState<Record<string, 'idle' | 'pending' | 'success' | 'error'>>({})
  const [signalMsg, setSignalMsg] = useState<Record<string, string>>({})
  const [sentSignals, setSentSignals] = useState<Set<string>>(new Set())
  const [signalConfirm, setSignalConfirm] = useState<string | null>(null)

  const [expandedTasks, setExpandedTasks] = useState<Set<string>>(new Set())
  function toggleTaskExpand(key: string) {
    setExpandedTasks(prev => {
      const next = new Set(prev)
      if (next.has(key)) next.delete(key)
      else next.add(key)
      return next
    })
  }

  const [configAgent, setConfigAgent] = useState<string | null>(null)
  const [configData, setConfigData] = useState<AgentConfig | null>(null)
  const [configEdits, setConfigEdits] = useState<ConfigEdits | null>(null)
  const [configSaveState, setConfigSaveState] = useState<ConfigSaveState>('idle')
  const [configSaveMsg, setConfigSaveMsg] = useState('')
  const [configLoading, setConfigLoading] = useState(false)
  const [configReprovisionConfirm, setConfigReprovisionConfirm] = useState(false)
  const [reprovisionAllState, setReprovisionAllState] = useState<ReprovisionAllState>('idle')
  const [reprovisionAllMsg, setReprovisionAllMsg] = useState('')

  const [instructions, setInstructions] = useState<InstructionSummary[]>([])
  const [instructionsLoading, setInstructionsLoading] = useState(false)
  const [expandedInstruction, setExpandedInstruction] = useState<string | null>(null)
  const [instructionDetail, setInstructionDetail] = useState<Record<string, InstructionDetail>>({})
  const [instructionDetailLoading, setInstructionDetailLoading] = useState<Record<string, boolean>>({})
  const [instructionEdits, setInstructionEdits] = useState<Record<string, string>>({})
  const [instructionReason, setInstructionReason] = useState<Record<string, string>>({})
  const [instructionSaveState, setInstructionSaveState] = useState<Record<string, 'idle' | 'saving' | 'success' | 'error'>>({})
  const [instructionSaveMsg, setInstructionSaveMsg] = useState<Record<string, string>>({})
  const [instrToggleConfirm, setInstrToggleConfirm] = useState<Record<string, boolean>>({})
  const [instrToggleState, setInstrToggleState] = useState<Record<string, 'idle' | 'pending' | 'success' | 'error'>>({})
  const [instrToggleMsg, setInstrToggleMsg] = useState<Record<string, string>>({})
  const [instrShowNew, setInstrShowNew] = useState(false)
  const [instrNewForm, setInstrNewForm] = useState({ name: '', content: '', reason: '' })
  const [instrNewState, setInstrNewState] = useState<'idle' | 'saving' | 'success' | 'error'>('idle')
  const [instrNewMsg, setInstrNewMsg] = useState('')

  // Project contexts
  const [projectContexts, setProjectContexts] = useState<ProjectContextSummary[]>([])
  const [projectContextsLoading, setProjectContextsLoading] = useState(false)
  const [expandedContext, setExpandedContext] = useState<string | null>(null)
  const [contextDetail, setContextDetail] = useState<Record<string, ProjectContextDetail>>({})
  const [contextDetailLoading, setContextDetailLoading] = useState<Record<string, boolean>>({})
  const [contextEdits, setContextEdits] = useState<Record<string, string>>({})
  const [contextReason, setContextReason] = useState<Record<string, string>>({})
  const [contextSaveState, setContextSaveState] = useState<Record<string, 'idle' | 'saving' | 'success' | 'error'>>({})
  const [contextSaveMsg, setContextSaveMsg] = useState<Record<string, string>>({})
  const [contextSelectedVersion, setContextSelectedVersion] = useState<Record<string, number | null>>({})
  const [contextRollbackConfirm, setContextRollbackConfirm] = useState<Record<string, boolean>>({})
  const [ctxToggleConfirm, setCtxToggleConfirm] = useState<Record<string, boolean>>({})
  const [ctxToggleState, setCtxToggleState] = useState<Record<string, 'idle' | 'pending' | 'success' | 'error'>>({})
  const [ctxToggleMsg, setCtxToggleMsg] = useState<Record<string, string>>({})
  const [ctxShowNew, setCtxShowNew] = useState(false)
  const [ctxNewForm, setCtxNewForm] = useState({ name: '', content: '' })
  const [ctxNewState, setCtxNewState] = useState<'idle' | 'saving' | 'success' | 'error'>('idle')
  const [ctxNewMsg, setCtxNewMsg] = useState('')
  const [selectedVersion, setSelectedVersion] = useState<Record<string, number | null>>({})
  const [rollbackConfirm, setRollbackConfirm] = useState<Record<string, boolean>>({})
  const [deployConfirm, setDeployConfirm] = useState<Record<string, boolean>>({})
  const [deployState, setDeployState] = useState<Record<string, 'idle' | 'deploying' | 'success' | 'error'>>({})
  const [deployMsg, setDeployMsg] = useState<Record<string, string>>({})

  const [logViewer, setLogViewer] = useState<string | null>(null)
  const [logLines, setLogLines] = useState<string[]>([])
  const [logAutoScroll, setLogAutoScroll] = useState(true)
  const [logPaused, setLogPaused] = useState(false)
  const [logFilter, setLogFilter] = useState<'all' | 'error' | 'warn' | 'info'>('all')
  const logWsRef = useRef<WebSocket | null>(null)
  const logPausedRef = useRef(false)

  const [alerts, setAlerts] = useState<Alert[]>([])
  const alertIdRef = useRef(0)
  const prevAgentsRef = useRef<Record<string, AgentState>>({})
  const taskFirstSeenRef = useRef<Record<string, { task: string; since: number }>>({})
  const stuckAlertedRef = useRef<Record<string, string>>({})
  const seenFailedWorkflowsRef = useRef<Set<string>>(new Set())

  const [copiedContainer, setCopiedContainer] = useState<string | null>(null)
  const [wfMenuOpen, setWfMenuOpen] = useState<Record<string, boolean>>({})

  const [selectedWf, setSelectedWf] = useState<WorkflowSummary | null>(null)
  const [wfHistory, setWfHistory] = useState<WorkflowEvent[]>([])
  const [wfHistoryLoading, setWfHistoryLoading] = useState(false)
  const [wfHistoryError, setWfHistoryError] = useState<string | null>(null)
  const [expandedEvents, setExpandedEvents] = useState<Set<number>>(new Set())

  const [createModalOpen, setCreateModalOpen] = useState(false)
  const [createForm, setCreateForm] = useState<CreateForm>(DEFAULT_CREATE_FORM)
  const [createState, setCreateState] = useState<'idle' | 'creating' | 'success' | 'error'>('idle')
  const [createMsg, setCreateMsg] = useState('')
  const [copyFrom, setCopyFrom] = useState('')
  const [copyFromLoading, setCopyFromLoading] = useState(false)

  const [deleteStates, setDeleteStates] = useState<Record<string, 'idle' | 'confirming' | 'deleting' | 'success' | 'error'>>({})
  const [deleteMsg, setDeleteMsg] = useState<Record<string, string>>({})

  // Navigation state — synced with URL hash
  const VALID_VIEWS: ActiveView[] = ['agents', 'workflows', 'instructions', 'project-contexts', 'wf-definitions', 'alerts', 'schedules', 'namespaces', 'repositories']
  const [activeView, setActiveViewState] = useState<ActiveView>(() => {
    const hash = window.location.hash.slice(1) as ActiveView
    return VALID_VIEWS.includes(hash) ? hash : 'agents'
  })
  const setActiveView = useCallback((view: ActiveView) => {
    setActiveViewState(view)
    window.history.replaceState(null, '', `#${view}`)
  }, [])
  useEffect(() => {
    function onHashChange() {
      const hash = window.location.hash.slice(1) as ActiveView
      if (VALID_VIEWS.includes(hash)) setActiveViewState(hash)
    }
    window.addEventListener('hashchange', onHashChange)
    return () => window.removeEventListener('hashchange', onHashChange)
  }, [])
  useEffect(() => {
    if (activeView === 'wf-definitions' && !wfDefsLoaded) loadWfDefs()
    if ((activeView === 'schedules' || activeView === 'namespaces') && !schedulesLoaded) loadSchedules()
  }, [activeView])
  const [navOpen, setNavOpen] = useState(false)

  // Visual editor state: undefined=closed, null=new definition, string=editing named def
  const [wfEditorDef, setWfEditorDef] = useState<string | null | undefined>(undefined)

  // Workflow Definitions state
  const [wfDefs, setWfDefs] = useState<WorkflowDefinitionSummary[]>([])
  const [wfDefsLoading, setWfDefsLoading] = useState(false)
  const [wfDefsLoaded, setWfDefsLoaded] = useState(false)
  const [expandedWfDef, setExpandedWfDef] = useState<string | null>(null)
  const [wfDefDetails, setWfDefDetails] = useState<Record<string, WorkflowDefinitionDetail>>({})
  const [wfDefDetailLoading, setWfDefDetailLoading] = useState<Record<string, boolean>>({})
  const [wfDefEdits, setWfDefEdits] = useState<Record<string, string>>({})
  const [wfDefReasons, setWfDefReasons] = useState<Record<string, string>>({})
  const [wfDefSaveState, setWfDefSaveState] = useState<Record<string, ConfigSaveState>>({})
  const [wfDefSaveMsg, setWfDefSaveMsg] = useState<Record<string, string>>({})
  const [wfDefSelectedVersion, setWfDefSelectedVersion] = useState<Record<string, number | null>>({})
  const [wfDefRollbackConfirm, setWfDefRollbackConfirm] = useState<Record<string, string | null>>({})
  const [wfDefToggleConfirm, setWfDefToggleConfirm] = useState<Record<string, boolean>>({})
  const [wfDefToggleState, setWfDefToggleState] = useState<Record<string, 'idle' | 'pending' | 'success' | 'error'>>({})
  const [wfDefToggleMsg, setWfDefToggleMsg] = useState<Record<string, string>>({})
  const [wfDefNsFilter, setWfDefNsFilter] = useState('all')
  const [wfDefSearch, setWfDefSearch] = useState('')
  const [wfDefShowNew, setWfDefShowNew] = useState(false)
  const [wfDefNewForm, setWfDefNewForm] = useState({ name: '', namespace: '', taskQueue: '', description: '', definition: '' })
  const [wfDefNewState, setWfDefNewState] = useState<'idle' | 'saving' | 'success' | 'error'>('idle')
  const [wfDefNewMsg, setWfDefNewMsg] = useState('')


  // Schedules state
  const [schedules, setSchedules] = useState<ScheduleSummary[]>([])
  const [schedulesLoading, setSchedulesLoading] = useState(false)
  const [schedulesLoaded, setSchedulesLoaded] = useState(false)

  function loadSchedules() {
    setSchedulesLoading(true)
    apiFetch('/api/schedules')
      .then(r => r.json())
      .then((list: ScheduleSummary[]) => { setSchedules(list); setSchedulesLoaded(true) })
      .catch(() => {})
      .finally(() => setSchedulesLoading(false))
  }

  const [highlightedEntityId, setHighlightedEntityId] = useState<string | null>(null)
  const onClearHighlight = useCallback(() => setHighlightedEntityId(null), [])
  const onHighlight = useCallback((id: string) => setHighlightedEntityId(id), [])

  const addAlert = useCallback((
    type: Alert['type'],
    message: string,
    meta?: { workflowId?: string; workflowNamespace?: string; agentName?: string }
  ) => {
    const id = String(++alertIdRef.current)
    const newAlert: Alert = {
      id,
      type,
      message,
      timestamp: new Date().toISOString(),
      dismissed: false,
      showToast: true,
      ...meta,
    }
    setAlerts(prev => [newAlert, ...prev].slice(0, 100))
    setTimeout(() => {
      setAlerts(prev => prev.map(a => a.id === id ? { ...a, showToast: false } : a))
    }, 8000)
  }, [])

  const handleAlertEntityLink = useCallback((alert: Alert) => {
    if (alert.workflowId) {
      const found = workflows.find(w => w.workflowId === alert.workflowId)
      if (!found) {
        addAlert('info', 'Workflow no longer in active list (may have completed or been terminated)')
        return
      }
      setActiveView('workflows')
      setTimeout(() => setHighlightedEntityId(alert.workflowId!), 50)
    } else if (alert.agentName) {
      setActiveView('agents')
      setTimeout(() => setHighlightedEntityId(alert.agentName!), 50)
    }
  }, [workflows, addAlert])

  const connect = useCallback(() => {
    if (unmounted.current) return
    const proto = location.protocol === 'https:' ? 'wss' : 'ws'
    const ws = new WebSocket(`${proto}://${location.host}/ws`)
    wsRef.current = ws
    setWsStatus('connecting')
    ws.onopen = () => { setWsStatus('connected') }
    ws.onmessage = (event) => {
      try {
        const msg: WsMessage = JSON.parse(event.data)
        if (msg.type === 'workflows') {
          setWorkflows(msg.data)
          setSentSignals(prev => {
            const next = new Set(prev)
            for (const entry of prev) {
              const sepIdx = entry.lastIndexOf('::')
              const wfId = entry.slice(0, sepIdx)
              const oldPhase = entry.slice(sepIdx + 2)
              const updated = (msg.data as WorkflowSummary[]).find((w: WorkflowSummary) => w.workflowId === wfId)
              if (!updated || updated.phase !== oldPhase || updated.status !== 'Running') {
                next.delete(entry)
              }
            }
            return next.size !== prev.size ? next : prev
          })
        } else {
          const agent = msg as AgentState
          setAgents(prev => ({ ...prev, [agent.agentName]: agent }))
          setLastUpdated(new Date().toLocaleTimeString())
        }
      } catch { /* ignore malformed */ }
    }
    ws.onclose = () => {
      setWsStatus('disconnected')
      if (!unmounted.current) reconnectTimer.current = setTimeout(connect, 3000)
    }
    ws.onerror = () => ws.close()
  }, [])

  function fetchWorkflows() {
    setWorkflowsLoading(true)
    apiFetch('/api/workflows')
      .then(r => r.json())
      .then((list: WorkflowSummary[]) => setWorkflows(list))
      .catch(() => {})
      .finally(() => setWorkflowsLoading(false))
  }

  function fetchCompleted() {
    setCompletedLoading(true)
    apiFetch('/api/workflows/completed?hours=24')
      .then(r => r.json())
      .then((list: WorkflowSummary[]) => setCompletedWorkflows(list))
      .catch(() => {})
      .finally(() => setCompletedLoading(false))
  }

  function loadInstructions() {
    setInstructionsLoading(true)
    apiFetch('/api/instructions')
      .then(r => r.json())
      .then((list: InstructionSummary[]) => setInstructions(list))
      .catch(() => {})
      .finally(() => setInstructionsLoading(false))
  }

  function loadProjectContexts() {
    setProjectContextsLoading(true)
    apiFetch('/api/project-contexts')
      .then(r => r.json())
      .then((list: ProjectContextSummary[]) => setProjectContexts(list))
      .catch(() => {})
      .finally(() => setProjectContextsLoading(false))
  }

  useEffect(() => {
    apiFetch('/api/agents')
      .then(r => r.json())
      .then((list: AgentState[]) => {
        const map: Record<string, AgentState> = {}
        for (const a of list) map[a.agentName] = a
        setAgents(map)
        setLastUpdated(new Date().toLocaleTimeString())
      })
      .catch(() => {})

    fetchWorkflows()

    apiFetch('/api/signals')
      .then(r => r.json())
      .then((reg: Record<string, SignalDef[]>) => setSignalRegistry(reg))
      .catch(() => {})

    apiFetch('/api/workflow-types')
      .then(r => r.json())
      .then((list: WorkflowTypeInfo[]) => setWorkflowTypes(list))
      .catch(() => {})

    apiFetch('/api/namespaces')
      .then(r => r.json())
      .then((list: string[]) => setApiNamespaces(list))
      .catch(() => {})

    loadInstructions()
    loadProjectContexts()
    fetchCompleted()
  }, [])

  useEffect(() => {
    unmounted.current = false
    connect()
    return () => {
      unmounted.current = true
      if (reconnectTimer.current) clearTimeout(reconnectTimer.current)
      wsRef.current?.close()
    }
  }, [connect])

  const [tick, setTick] = useState(0)
  useEffect(() => {
    const id = setInterval(() => setTick(t => t + 1), 5000)
    return () => clearInterval(id)
  }, [])
  void tick

  useEffect(() => {
    if (logWsRef.current) {
      logWsRef.current.close()
      logWsRef.current = null
    }
    if (!logViewer) return
    setLogLines([])
    setLogPaused(false)
    logPausedRef.current = false
    const proto = location.protocol === 'https:' ? 'wss' : 'ws'
    const ws = new WebSocket(`${proto}://${location.host}/ws/logs/${encodeURIComponent(logViewer)}`)
    logWsRef.current = ws
    ws.onmessage = (event) => {
      if (logPausedRef.current) return
      const line = event.data as string
      setLogLines(prev => {
        const next = [...prev, line]
        return next.length > 2000 ? next.slice(-2000) : next
      })
    }
    ws.onerror = () => ws.close()
    return () => { ws.close(); logWsRef.current = null }
  }, [logViewer])

  useEffect(() => {
    const prev = prevAgentsRef.current
    for (const [name, agent] of Object.entries(agents)) {
      const prevAgent = prev[name]
      if (prevAgent) {
        const wasHealthy = prevAgent.effectiveStatus !== 'dead' && prevAgent.effectiveStatus !== 'stale'
        if (wasHealthy && agent.effectiveStatus === 'dead') addAlert('agent-dead', `${name} is down`, { agentName: name })
        else if (wasHealthy && agent.effectiveStatus === 'stale') addAlert('agent-stale', `${name} is unresponsive (stale heartbeat)`, { agentName: name })
      }
      if (agent.currentTask) {
        const ts = taskFirstSeenRef.current[name]
        if (!ts || ts.task !== agent.currentTask) {
          taskFirstSeenRef.current[name] = { task: agent.currentTask, since: Date.now() }
          delete stuckAlertedRef.current[name]
        }
      } else {
        delete taskFirstSeenRef.current[name]
        delete stuckAlertedRef.current[name]
      }
    }
    prevAgentsRef.current = agents
  }, [agents, addAlert])

  useEffect(() => {
    const id = setInterval(() => {
      const nowMs = Date.now()
      for (const [name, ts] of Object.entries(taskFirstSeenRef.current)) {
        const ageMs = nowMs - ts.since
        if (ageMs > STUCK_TASK_MS && stuckAlertedRef.current[name] !== ts.task) {
          stuckAlertedRef.current[name] = ts.task
          const mins = Math.round(ageMs / 60_000)
          const taskPreview = ts.task.length > 60 ? ts.task.slice(0, 60) + '…' : ts.task
          addAlert('task-stuck', `${name} task stuck (${mins}m): ${taskPreview}`, { agentName: name })
        }
      }
    }, 60_000)
    return () => clearInterval(id)
  }, [addAlert])

  useEffect(() => {
    function checkFailures() {
      apiFetch('/api/workflows/failures')
        .then(r => r.json())
        .then((list: WorkflowSummary[]) => {
          for (const wf of list) {
            const key = `${wf.namespace}/${wf.workflowId}/${wf.runId}`
            if (!seenFailedWorkflowsRef.current.has(key)) {
              seenFailedWorkflowsRef.current.add(key)
              if (wf.status === 'Failed') addAlert('workflow-failed', `${wf.workflowType} failed in ${wf.namespace}`, { workflowId: wf.workflowId, workflowNamespace: wf.namespace })
            }
          }
        })
        .catch(() => {})
    }
    checkFailures()
    const id = setInterval(checkFailures, 60_000)
    return () => clearInterval(id)
  }, [addAlert])

  const namespaces = useMemo(() => Array.from(new Set(workflows.map(w => w.namespace))).sort(), [workflows])
  const filteredWorkflows = useMemo(() => nsFilter === 'all' ? workflows : workflows.filter(w => w.namespace === nsFilter), [workflows, nsFilter])
  const filteredCompletedWorkflows = useMemo(() => nsFilter === 'all' ? completedWorkflows : completedWorkflows.filter(w => w.namespace === nsFilter), [completedWorkflows, nsFilter])

  const { agentByWorkflow, workflowByAgent } = useMemo(() => {
    const agentByWorkflow: Record<string, string> = {}
    const workflowByAgent: Record<string, string> = {}
    for (const agent of Object.values(agents)) {
      if (!agent.currentTaskId) continue
      for (const wf of workflows) {
        if (agent.currentTaskId === wf.workflowId || agent.currentTaskId.startsWith(wf.workflowId + '/')) {
          agentByWorkflow[wf.workflowId] = agent.agentName
          workflowByAgent[agent.agentName] = wf.workflowId
          break
        }
      }
    }
    return { agentByWorkflow, workflowByAgent }
  }, [agents, workflows])

  const unreadAlerts = alerts.filter(a => !a.dismissed)
  const toastAlerts = alerts.filter(a => a.showToast)

  function toggleHistory(agentName: string) {
    if (expandedAgent === agentName) { setExpandedAgent(null); return }
    setExpandedAgent(agentName)
    setHistoryLoading(prev => ({ ...prev, [agentName]: true }))
    apiFetch(`/api/agents/${encodeURIComponent(agentName)}/history`)
      .then(r => r.json())
      .then((records: TaskRecord[]) => setTaskHistory(prev => ({ ...prev, [agentName]: records })))
      .catch(() => setTaskHistory(prev => ({ ...prev, [agentName]: [] })))
      .finally(() => setHistoryLoading(prev => ({ ...prev, [agentName]: false })))
  }

  function setAgentRestartState(agentName: string, state: RestartState, msg = '') {
    setRestartStates(prev => ({ ...prev, [agentName]: state }))
    setRestartMsg(prev => ({ ...prev, [agentName]: msg }))
  }

  function handleRestart(agentName: string) {
    const state = restartStates[agentName] ?? 'idle'
    if (state === 'restarting') return
    if (state === 'confirming') { setAgentRestartState(agentName, 'idle'); return }
    setAgentRestartState(agentName, 'confirming')
  }

  function handleRestartConfirm(agentName: string) {
    setAgentRestartState(agentName, 'restarting')
    apiFetch(`/api/agents/${encodeURIComponent(agentName)}/restart`, { method: 'POST' })
      .then(async r => {
        if (r.ok) setAgentRestartState(agentName, 'success', 'Restart triggered')
        else { const b = await r.json().catch(() => ({})); setAgentRestartState(agentName, 'error', b?.error ?? `Error ${r.status}`) }
      })
      .catch(() => setAgentRestartState(agentName, 'error', 'Request failed'))
      .finally(() => setTimeout(() => setAgentRestartState(agentName, 'idle'), 4000))
  }

  function setAgentCancelState(agentName: string, state: CancelState, msg = '') {
    setCancelStates(prev => ({ ...prev, [agentName]: state }))
    setCancelMsg(prev => ({ ...prev, [agentName]: msg }))
  }

  function handleCancel(agentName: string) {
    const state = cancelStates[agentName] ?? 'idle'
    if (state === 'cancelling') return
    if (state === 'confirming') { setAgentCancelState(agentName, 'idle'); return }
    setAgentCancelState(agentName, 'confirming')
  }

  function handleCancelConfirm(agentName: string) {
    setAgentCancelState(agentName, 'cancelling')
    apiFetch(`/api/agents/${encodeURIComponent(agentName)}/cancel`, { method: 'POST' })
      .then(async r => {
        if (r.ok) setAgentCancelState(agentName, 'success', 'Cancelled')
        else { const b = await r.json().catch(() => ({})); setAgentCancelState(agentName, 'error', b?.error ?? `Error ${r.status}`) }
      })
      .catch(() => setAgentCancelState(agentName, 'error', 'Request failed'))
      .finally(() => setTimeout(() => setAgentCancelState(agentName, 'idle'), 4000))
  }

  function handleBgCancel(agentName: string, taskId: string) {
    const key = `${agentName}/${taskId}`
    if (bgCancelStates[key] === 'cancelling') return
    setBgCancelStates(prev => ({ ...prev, [key]: 'cancelling' }))
    apiFetch(`/api/agents/${encodeURIComponent(agentName)}/cancel_bg/${encodeURIComponent(taskId)}`, { method: 'POST' })
      .then(r => setBgCancelStates(prev => ({ ...prev, [key]: r.ok ? 'done' : 'error' })))
      .catch(() => setBgCancelStates(prev => ({ ...prev, [key]: 'error' })))
      .finally(() => setTimeout(() => setBgCancelStates(prev => ({ ...prev, [key]: 'idle' })), 4000))
  }

  function setAgentReprovisionState(agentName: string, state: ReprovisionState, msg = '') {
    setReprovisionStates(prev => ({ ...prev, [agentName]: state }))
    setReprovisionMsg(prev => ({ ...prev, [agentName]: msg }))
  }

  function handleReprovision(agentName: string) {
    const state = reprovisionStates[agentName] ?? 'idle'
    if (state === 'provisioning') return
    if (state === 'confirming') { setAgentReprovisionState(agentName, 'idle'); return }
    setAgentReprovisionState(agentName, 'confirming')
  }

  function handleReprovisionConfirm(agentName: string) {
    setAgentReprovisionState(agentName, 'provisioning')
    apiFetch(`/api/agents/${encodeURIComponent(agentName)}/reprovision`, { method: 'POST' })
      .then(async r => {
        if (r.ok) setAgentReprovisionState(agentName, 'success', 'Reprovisioning...')
        else { const b = await r.json().catch(() => ({})); setAgentReprovisionState(agentName, 'error', b?.error ?? `Error ${r.status}`) }
      })
      .catch(() => setAgentReprovisionState(agentName, 'error', 'Request failed'))
      .finally(() => setTimeout(() => setAgentReprovisionState(agentName, 'idle'), 5000))
  }

  function handleReprovisionAll() {
    setReprovisionAllState('confirming')
    setTimeout(() => setReprovisionAllState(s => s === 'confirming' ? 'idle' : s), 5000)
  }

  function handleReprovisionAllConfirm() {
    setReprovisionAllState('running')
    apiFetch('/api/agents/reprovision-running', { method: 'POST' })
      .then(async r => {
        if (r.ok) {
          setReprovisionAllState('success')
          setReprovisionAllMsg('All agents reprovisioning…')
          addAlert('agent-stale', 'Reprovision all triggered — agents restarting')
        } else {
          const b = await r.json().catch(() => ({}))
          setReprovisionAllState('error')
          setReprovisionAllMsg(b?.error ?? `Error ${r.status}`)
        }
      })
      .catch(() => { setReprovisionAllState('error'); setReprovisionAllMsg('Request failed') })
      .finally(() => setTimeout(() => { setReprovisionAllState('idle'); setReprovisionAllMsg('') }, 5000))
  }

  function handleReprovisionAllCancel() {
    setReprovisionAllState('idle')
  }

  function handleStop(agentName: string) {
    const ss = stopStates[agentName] ?? 'idle'
    if (ss === 'pending') return
    if (ss === 'confirming') {
      setStopStates(prev => ({ ...prev, [agentName]: 'pending' }))
      apiFetch(`/api/agents/${encodeURIComponent(agentName)}/stop`, { method: 'POST' })
        .then(async r => {
          if (r.ok) { setStopStates(prev => ({ ...prev, [agentName]: 'success' })); setStopMsg(prev => ({ ...prev, [agentName]: 'Stopped' })) }
          else { const b = await r.json().catch(() => ({})); setStopStates(prev => ({ ...prev, [agentName]: 'error' })); setStopMsg(prev => ({ ...prev, [agentName]: b?.error ?? `Error ${r.status}` })) }
        })
        .catch(() => { setStopStates(prev => ({ ...prev, [agentName]: 'error' })); setStopMsg(prev => ({ ...prev, [agentName]: 'Request failed' })) })
        .finally(() => setTimeout(() => setStopStates(prev => ({ ...prev, [agentName]: 'idle' })), 4000))
      return
    }
    setStopStates(prev => ({ ...prev, [agentName]: 'confirming' }))
    setTimeout(() => setStopStates(prev => prev[agentName] === 'confirming' ? { ...prev, [agentName]: 'idle' } : prev), 5000)
  }

  function handleStart(agentName: string) {
    const ss = startStates[agentName] ?? 'idle'
    if (ss === 'pending') return
    setStartStates(prev => ({ ...prev, [agentName]: 'pending' }))
    apiFetch(`/api/agents/${encodeURIComponent(agentName)}/start`, { method: 'POST' })
      .then(async r => {
        if (r.ok) { setStartStates(prev => ({ ...prev, [agentName]: 'success' })); setStartMsg(prev => ({ ...prev, [agentName]: 'Started' })) }
        else { const b = await r.json().catch(() => ({})); setStartStates(prev => ({ ...prev, [agentName]: 'error' })); setStartMsg(prev => ({ ...prev, [agentName]: b?.error ?? `Error ${r.status}` })) }
      })
      .catch(() => { setStartStates(prev => ({ ...prev, [agentName]: 'error' })); setStartMsg(prev => ({ ...prev, [agentName]: 'Request failed' })) })
      .finally(() => setTimeout(() => setStartStates(prev => ({ ...prev, [agentName]: 'idle' })), 4000))
  }

  function handleDeleteClick(agentName: string) {
    const state = deleteStates[agentName] ?? 'idle'
    if (state === 'deleting') return
    if (state === 'confirming') { setDeleteStates(prev => ({ ...prev, [agentName]: 'idle' })); return }
    setDeleteStates(prev => ({ ...prev, [agentName]: 'confirming' }))
  }

  function handleDeleteConfirm(agentName: string) {
    setDeleteStates(prev => ({ ...prev, [agentName]: 'deleting' }))
    apiFetch(`/api/agents/${encodeURIComponent(agentName)}`, { method: 'DELETE' })
      .then(async r => {
        const d = await r.json().catch(() => ({}))
        if (r.ok) { setDeleteStates(prev => ({ ...prev, [agentName]: 'success' })); setDeleteMsg(prev => ({ ...prev, [agentName]: d.message ?? 'Deleted' })) }
        else { setDeleteStates(prev => ({ ...prev, [agentName]: 'error' })); setDeleteMsg(prev => ({ ...prev, [agentName]: d.error ?? `Error ${r.status}` })) }
      })
      .catch(() => { setDeleteStates(prev => ({ ...prev, [agentName]: 'error' })); setDeleteMsg(prev => ({ ...prev, [agentName]: 'Request failed' })) })
      .finally(() => setTimeout(() => setDeleteStates(prev => ({ ...prev, [agentName]: 'idle' })), 5000))
  }

  function openConfig(agentName: string) {
    if (configAgent === agentName) {
      setConfigAgent(null); setConfigData(null); setConfigEdits(null); setConfigReprovisionConfirm(false)
      return
    }
    setConfigAgent(agentName)
    setConfigData(null); setConfigEdits(null)
    setConfigSaveState('idle'); setConfigReprovisionConfirm(false)
    setConfigLoading(true)
    apiFetch(`/api/agents/${encodeURIComponent(agentName)}/config`)
      .then(r => r.ok ? r.json() : Promise.reject(r.status))
      .then((cfg: AgentConfig) => {
        setConfigData(cfg)
        setConfigEdits({
          model: cfg.model,
          memoryLimitMb: String(cfg.memoryLimitMb),
          isEnabled: cfg.isEnabled,
          image: cfg.image ?? '',
          permissionMode: cfg.permissionMode,
          maxTurns: String(cfg.maxTurns),
          workDir: cfg.workDir,
          proactiveIntervalMinutes: String(cfg.proactiveIntervalMinutes),
          groupListenMode: cfg.groupListenMode,
          groupDebounceSeconds: String(cfg.groupDebounceSeconds),
          shortName: cfg.shortName,
          displayName: cfg.displayName,
          showStats: cfg.showStats,
          prefixMessages: cfg.prefixMessages,
          suppressToolMessages: cfg.suppressToolMessages ?? false,
          telegramSendOnly: cfg.telegramSendOnly,
          provider: cfg.provider ?? 'claude',
          codexSandboxMode: cfg.codexSandboxMode ?? '',
          ttsServiceUrl: cfg.ttsServiceUrl ?? '',
          effort: cfg.effort ?? '',
          jsonSchema: cfg.jsonSchema ?? '',
          agentsJson: cfg.agentsJson ?? '',
          autoMemoryEnabled: cfg.autoMemoryEnabled,
          hostPort: cfg.hostPort != null ? String(cfg.hostPort) : '',
          tools: cfg.tools.map(t => t.toolName).join(', '),
          projects: cfg.projects.join(', '),
          mcpEndpoints: cfg.mcpEndpoints,
          networks: cfg.networks.join(', '),
          envRefs: cfg.envRefs.join(', '),
          telegramUsers: cfg.telegramUsers.join(', '),
          telegramGroups: cfg.telegramGroups.join(', '),
        })
      })
      .catch(() => { setConfigSaveMsg('Failed to load config'); setConfigSaveState('error') })
      .finally(() => setConfigLoading(false))
  }

  function closeConfig() {
    setConfigAgent(null); setConfigData(null); setConfigEdits(null)
    setConfigReprovisionConfirm(false); setConfigSaveState('idle')
  }

  function saveConfig(agentName: string, andReprovision: boolean) {
    if (!configEdits) return
    const provider = (configEdits.provider ?? 'claude').trim()
    const model = configEdits.model.trim() || PROVIDER_DEFAULT_MODEL[provider] || ''
    if (!model) { setConfigSaveMsg('Model is required'); setConfigSaveState('error'); return }
    const memoryLimitMb = parseInt(configEdits.memoryLimitMb, 10)
    if (isNaN(memoryLimitMb) || memoryLimitMb < 128) { setConfigSaveMsg('Memory must be ≥ 128 MB'); setConfigSaveState('error'); return }
    const maxTurns = parseInt(configEdits.maxTurns, 10)
    const tools = configEdits.tools.split(',').map(t => t.trim()).filter(Boolean)
    const projects = configEdits.projects.split(',').map(p => p.trim()).filter(Boolean)
    const networks = configEdits.networks.split(',').map(n => n.trim()).filter(Boolean)
    const envRefs = configEdits.envRefs.split(',').map(r => r.trim()).filter(Boolean)
    const telegramUsers = configEdits.telegramUsers.split(',').map(s => s.trim()).filter(Boolean).map(Number)
    const telegramGroups = configEdits.telegramGroups.split(',').map(s => s.trim()).filter(Boolean).map(Number)
    setConfigSaveState('saving')
    apiFetch(`/api/agents/${encodeURIComponent(agentName)}/config`, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        model, memoryLimitMb,
        isEnabled: configEdits.isEnabled,
        image: configEdits.image,
        permissionMode: configEdits.permissionMode,
        maxTurns: isNaN(maxTurns) ? undefined : maxTurns,
        workDir: configEdits.workDir,
        proactiveIntervalMinutes: parseInt(configEdits.proactiveIntervalMinutes, 10) || 0,
        groupListenMode: configEdits.groupListenMode,
        groupDebounceSeconds: parseInt(configEdits.groupDebounceSeconds, 10) || 15,
        shortName: configEdits.shortName,
        displayName: configEdits.displayName,
        showStats: configEdits.showStats,
        prefixMessages: configEdits.prefixMessages,
        suppressToolMessages: configEdits.suppressToolMessages,
        telegramSendOnly: configEdits.telegramSendOnly,
        provider: configEdits.provider,
        ttsServiceUrl: configEdits.ttsServiceUrl,
        effort: configEdits.effort,
        jsonSchema: configEdits.jsonSchema,
        agentsJson: configEdits.agentsJson,
        autoMemoryEnabled: configEdits.autoMemoryEnabled,
        codexSandboxMode: configEdits.codexSandboxMode || undefined,
        hostPort: configEdits.hostPort ? parseInt(configEdits.hostPort, 10) : null,
        tools, projects, mcpEndpoints: configEdits.mcpEndpoints, networks, envRefs, telegramUsers, telegramGroups,
      }),
    })
      .then(async r => {
        if (!r.ok) { const b = await r.json().catch(() => ({})); throw new Error(b?.error ?? `Error ${r.status}`) }
        if (andReprovision) return apiFetch(`/api/agents/${encodeURIComponent(agentName)}/reprovision`, { method: 'POST' }).then(r2 => { if (!r2.ok) throw new Error('Reprovision failed') })
      })
      .then(() => {
        setConfigSaveState('success')
        setConfigSaveMsg(andReprovision ? 'Saved & reprovisioning…' : 'Saved')
        setTimeout(() => setConfigSaveState('idle'), 4000)
      })
      .catch((err: Error) => {
        setConfigSaveState('error'); setConfigSaveMsg(err.message)
        setTimeout(() => setConfigSaveState('idle'), 5000)
      })
  }

  function loadInstructionDetail(name: string) {
    setInstructionDetailLoading(prev => ({ ...prev, [name]: true }))
    apiFetch(`/api/instructions/${encodeURIComponent(name)}`)
      .then(r => r.ok ? r.json() : Promise.reject(r.status))
      .then((detail: InstructionDetail) => {
        setInstructionDetail(prev => ({ ...prev, [name]: detail }))
        const current = detail.versions.find(v => v.versionNumber === detail.currentVersion)
        if (current) setInstructionEdits(prev => ({ ...prev, [name]: current.content }))
      })
      .catch(() => { setInstructionSaveState(prev => ({ ...prev, [name]: 'error' })); setInstructionSaveMsg(prev => ({ ...prev, [name]: 'Failed to load' })) })
      .finally(() => setInstructionDetailLoading(prev => ({ ...prev, [name]: false })))
  }

  function toggleInstruction(name: string) {
    if (expandedInstruction === name) { setExpandedInstruction(null); return }
    setExpandedInstruction(name)
    if (!instructionDetail[name]) loadInstructionDetail(name)
  }

  function saveInstruction(name: string) {
    const content = instructionEdits[name]
    if (!content?.trim()) return
    const reason = instructionReason[name]?.trim() || undefined
    setInstructionSaveState(prev => ({ ...prev, [name]: 'saving' }))
    apiFetch(`/api/instructions/${encodeURIComponent(name)}/versions`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ content, reason }),
    })
      .then(async r => { if (!r.ok) { const b = await r.json().catch(() => ({})); throw new Error(b?.error ?? `Error ${r.status}`) }; return r.json() })
      .then(() => {
        setInstructionSaveState(prev => ({ ...prev, [name]: 'success' }))
        setInstructionSaveMsg(prev => ({ ...prev, [name]: 'Saved' }))
        setInstructionReason(prev => ({ ...prev, [name]: '' }))
        loadInstructionDetail(name)
        apiFetch('/api/instructions').then(r => r.json()).then((list: InstructionSummary[]) => setInstructions(list)).catch(() => {})
        setTimeout(() => setInstructionSaveState(prev => ({ ...prev, [name]: 'idle' })), 3000)
      })
      .catch((err: Error) => {
        setInstructionSaveState(prev => ({ ...prev, [name]: 'error' })); setInstructionSaveMsg(prev => ({ ...prev, [name]: err.message }))
        setTimeout(() => setInstructionSaveState(prev => ({ ...prev, [name]: 'idle' })), 5000)
      })
  }

  function rollbackInstruction(name: string, targetVersion: number) {
    setInstructionSaveState(prev => ({ ...prev, [name]: 'saving' }))
    apiFetch(`/api/instructions/${encodeURIComponent(name)}/rollback/${targetVersion}`, { method: 'POST' })
      .then(async r => { if (!r.ok) { const b = await r.json().catch(() => ({})); throw new Error(b?.error ?? `Error ${r.status}`) }; return r.json() })
      .then(() => {
        setInstructionSaveState(prev => ({ ...prev, [name]: 'success' }))
        setInstructionSaveMsg(prev => ({ ...prev, [name]: `Rolled back to v${targetVersion}` }))
        setSelectedVersion(prev => ({ ...prev, [name]: null }))
        loadInstructionDetail(name)
        apiFetch('/api/instructions').then(r => r.json()).then((list: InstructionSummary[]) => setInstructions(list)).catch(() => {})
        setTimeout(() => setInstructionSaveState(prev => ({ ...prev, [name]: 'idle' })), 3000)
      })
      .catch((err: Error) => {
        setInstructionSaveState(prev => ({ ...prev, [name]: 'error' })); setInstructionSaveMsg(prev => ({ ...prev, [name]: err.message }))
        setTimeout(() => setInstructionSaveState(prev => ({ ...prev, [name]: 'idle' })), 5000)
      })
  }

  function handleRollbackClick(e: React.MouseEvent, instrName: string, versionNumber: number, saveState: ConfigSaveState) {
    e.stopPropagation()
    if (saveState === 'saving') return
    const key = `${instrName}:${versionNumber}`
    if (rollbackConfirm[key]) {
      setRollbackConfirm(prev => ({ ...prev, [key]: false }))
      rollbackInstruction(instrName, versionNumber)
    } else {
      setRollbackConfirm(prev => ({ ...prev, [key]: true }))
      setTimeout(() => setRollbackConfirm(prev => prev[key] ? { ...prev, [key]: false } : prev), 5000)
    }
  }

  function handleDeployClick(e: React.MouseEvent, instrName: string, versionNumber: number, agentName: string) {
    e.stopPropagation()
    const deployKey = `${instrName}:${versionNumber}:${agentName}`
    if (deployState[deployKey] === 'deploying') return
    if (deployConfirm[deployKey]) {
      setDeployConfirm(prev => ({ ...prev, [deployKey]: false }))
      setDeployState(prev => ({ ...prev, [deployKey]: 'deploying' }))
      apiFetch(`/api/agents/${encodeURIComponent(agentName)}/restart-with-version`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ instructionName: instrName, versionNumber }),
      })
        .then(async r => {
          if (r.ok) { setDeployState(prev => ({ ...prev, [deployKey]: 'success' })); setDeployMsg(prev => ({ ...prev, [deployKey]: `Deployed to ${agentName}` })) }
          else { const b = await r.json().catch(() => ({})); setDeployState(prev => ({ ...prev, [deployKey]: 'error' })); setDeployMsg(prev => ({ ...prev, [deployKey]: b?.error ?? `Error ${r.status}` })) }
        })
        .catch(() => { setDeployState(prev => ({ ...prev, [deployKey]: 'error' })); setDeployMsg(prev => ({ ...prev, [deployKey]: 'Request failed' })) })
        .finally(() => setTimeout(() => setDeployState(prev => ({ ...prev, [deployKey]: 'idle' })), 4000))
    } else {
      setDeployConfirm(prev => ({ ...prev, [deployKey]: true }))
      setTimeout(() => setDeployConfirm(prev => prev[deployKey] ? { ...prev, [deployKey]: false } : prev), 5000)
    }
  }

  function handleInstrToggleConfirmClick(name: string) {
    setInstrToggleConfirm(prev => ({ ...prev, [name]: true }))
    setTimeout(() => setInstrToggleConfirm(prev => prev[name] ? { ...prev, [name]: false } : prev), 5000)
  }

  function toggleInstrActive(name: string, isActive: boolean) {
    setInstrToggleConfirm(prev => ({ ...prev, [name]: false }))
    setInstrToggleState(prev => ({ ...prev, [name]: 'pending' }))
    apiFetch(`/api/instructions/${encodeURIComponent(name)}/toggle-active`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ isActive }),
    })
      .then(async r => {
        if (!r.ok) { const b = await r.json().catch(() => ({})); throw new Error(b?.error ?? `Error ${r.status}`) }
        return r.json()
      })
      .then(() => {
        setInstrToggleState(prev => ({ ...prev, [name]: 'success' }))
        setInstrToggleMsg(prev => ({ ...prev, [name]: isActive ? 'Enabled' : 'Disabled' }))
        apiFetch('/api/instructions').then(r => r.json()).then((list: InstructionSummary[]) => setInstructions(list)).catch(() => {})
        setTimeout(() => setInstrToggleState(prev => ({ ...prev, [name]: 'idle' })), 3000)
      })
      .catch((err: Error) => {
        setInstrToggleState(prev => ({ ...prev, [name]: 'error' }))
        setInstrToggleMsg(prev => ({ ...prev, [name]: err.message }))
        setTimeout(() => setInstrToggleState(prev => ({ ...prev, [name]: 'idle' })), 5000)
      })
  }

  function createInstruction() {
    setInstrNewState('saving')
    apiFetch('/api/instructions', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        name: instrNewForm.name,
        content: instrNewForm.content,
        reason: instrNewForm.reason || undefined,
      }),
    })
      .then(async r => {
        if (!r.ok) { const b = await r.json().catch(() => ({})); throw new Error(b?.error ?? `Error ${r.status}`) }
        return r.json()
      })
      .then(() => {
        setInstrNewState('success')
        setInstrNewMsg('Created')
        setInstrShowNew(false)
        setInstrNewForm({ name: '', content: '', reason: '' })
        apiFetch('/api/instructions').then(r => r.json()).then((list: InstructionSummary[]) => setInstructions(list)).catch(() => {})
        setTimeout(() => setInstrNewState('idle'), 3000)
      })
      .catch((err: Error) => {
        setInstrNewState('error')
        setInstrNewMsg(err.message)
        setTimeout(() => setInstrNewState('idle'), 5000)
      })
  }

  // ─── Project Context handlers ───────────────────────────────────────────────

  function loadContextDetail(name: string) {
    setContextDetailLoading(prev => ({ ...prev, [name]: true }))
    apiFetch(`/api/project-contexts/${encodeURIComponent(name)}`)
      .then(r => r.ok ? r.json() : Promise.reject(r.status))
      .then((detail: ProjectContextDetail) => {
        setContextDetail(prev => ({ ...prev, [name]: detail }))
        const current = detail.versions.find(v => v.versionNumber === detail.currentVersion)
        if (current) setContextEdits(prev => ({ ...prev, [name]: current.content }))
      })
      .catch(() => { setContextSaveState(prev => ({ ...prev, [name]: 'error' })); setContextSaveMsg(prev => ({ ...prev, [name]: 'Failed to load' })) })
      .finally(() => setContextDetailLoading(prev => ({ ...prev, [name]: false })))
  }

  function toggleContext(name: string) {
    if (expandedContext === name) { setExpandedContext(null); return }
    setExpandedContext(name)
    if (!contextDetail[name]) loadContextDetail(name)
  }

  function saveContext(name: string) {
    const content = contextEdits[name]
    if (!content?.trim()) return
    const reason = contextReason[name]?.trim() || undefined
    setContextSaveState(prev => ({ ...prev, [name]: 'saving' }))
    apiFetch(`/api/project-contexts/${encodeURIComponent(name)}/versions`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ content, reason }),
    })
      .then(async r => { if (!r.ok) { const b = await r.json().catch(() => ({})); throw new Error(b?.error ?? `Error ${r.status}`) }; return r.json() })
      .then(() => {
        setContextSaveState(prev => ({ ...prev, [name]: 'success' }))
        setContextSaveMsg(prev => ({ ...prev, [name]: 'Saved' }))
        setContextReason(prev => ({ ...prev, [name]: '' }))
        loadContextDetail(name)
        apiFetch('/api/project-contexts').then(r => r.json()).then((list: ProjectContextSummary[]) => setProjectContexts(list)).catch(() => {})
        setTimeout(() => setContextSaveState(prev => ({ ...prev, [name]: 'idle' })), 3000)
      })
      .catch((err: Error) => {
        setContextSaveState(prev => ({ ...prev, [name]: 'error' })); setContextSaveMsg(prev => ({ ...prev, [name]: err.message }))
        setTimeout(() => setContextSaveState(prev => ({ ...prev, [name]: 'idle' })), 5000)
      })
  }

  function rollbackContext(name: string, targetVersion: number) {
    setContextSaveState(prev => ({ ...prev, [name]: 'saving' }))
    apiFetch(`/api/project-contexts/${encodeURIComponent(name)}/rollback/${targetVersion}`, { method: 'POST' })
      .then(async r => { if (!r.ok) { const b = await r.json().catch(() => ({})); throw new Error(b?.error ?? `Error ${r.status}`) }; return r.json() })
      .then(() => {
        setContextSaveState(prev => ({ ...prev, [name]: 'success' }))
        setContextSaveMsg(prev => ({ ...prev, [name]: `Rolled back to v${targetVersion}` }))
        setContextSelectedVersion(prev => ({ ...prev, [name]: null }))
        loadContextDetail(name)
        apiFetch('/api/project-contexts').then(r => r.json()).then((list: ProjectContextSummary[]) => setProjectContexts(list)).catch(() => {})
        setTimeout(() => setContextSaveState(prev => ({ ...prev, [name]: 'idle' })), 3000)
      })
      .catch((err: Error) => {
        setContextSaveState(prev => ({ ...prev, [name]: 'error' })); setContextSaveMsg(prev => ({ ...prev, [name]: err.message }))
        setTimeout(() => setContextSaveState(prev => ({ ...prev, [name]: 'idle' })), 5000)
      })
  }

  function handleContextRollbackClick(e: React.MouseEvent, ctxName: string, versionNumber: number, saveState: ConfigSaveState) {
    e.stopPropagation()
    if (saveState === 'saving') return
    const key = `${ctxName}:${versionNumber}`
    if (contextRollbackConfirm[key]) {
      setContextRollbackConfirm(prev => ({ ...prev, [key]: false }))
      rollbackContext(ctxName, versionNumber)
    } else {
      setContextRollbackConfirm(prev => ({ ...prev, [key]: true }))
      setTimeout(() => setContextRollbackConfirm(prev => prev[key] ? { ...prev, [key]: false } : prev), 5000)
    }
  }

  function handleCtxToggleConfirmClick(name: string) {
    setCtxToggleConfirm(prev => ({ ...prev, [name]: true }))
    setTimeout(() => setCtxToggleConfirm(prev => prev[name] ? { ...prev, [name]: false } : prev), 5000)
  }

  function toggleCtxActive(name: string, isActive: boolean) {
    setCtxToggleConfirm(prev => ({ ...prev, [name]: false }))
    setCtxToggleState(prev => ({ ...prev, [name]: 'pending' }))
    apiFetch(`/api/project-contexts/${encodeURIComponent(name)}/toggle-active`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ isActive }),
    })
      .then(async r => {
        if (!r.ok) { const b = await r.json().catch(() => ({})); throw new Error(b?.error ?? `Error ${r.status}`) }
        return r.json()
      })
      .then(() => {
        setCtxToggleState(prev => ({ ...prev, [name]: 'success' }))
        setCtxToggleMsg(prev => ({ ...prev, [name]: isActive ? 'Enabled' : 'Disabled' }))
        apiFetch('/api/project-contexts').then(r => r.json()).then((list: ProjectContextSummary[]) => setProjectContexts(list)).catch(() => {})
        setTimeout(() => setCtxToggleState(prev => ({ ...prev, [name]: 'idle' })), 3000)
      })
      .catch((err: Error) => {
        setCtxToggleState(prev => ({ ...prev, [name]: 'error' }))
        setCtxToggleMsg(prev => ({ ...prev, [name]: err.message }))
        setTimeout(() => setCtxToggleState(prev => ({ ...prev, [name]: 'idle' })), 5000)
      })
  }

  function createContext() {
    setCtxNewState('saving')
    apiFetch('/api/project-contexts', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        name: ctxNewForm.name,
        content: ctxNewForm.content,
      }),
    })
      .then(async r => {
        if (!r.ok) { const b = await r.json().catch(() => ({})); throw new Error(b?.error ?? `Error ${r.status}`) }
        return r.json()
      })
      .then(() => {
        setCtxNewState('success')
        setCtxNewMsg('Created')
        setCtxShowNew(false)
        setCtxNewForm({ name: '', content: '' })
        apiFetch('/api/project-contexts').then(r => r.json()).then((list: ProjectContextSummary[]) => setProjectContexts(list)).catch(() => {})
        setTimeout(() => setCtxNewState('idle'), 3000)
      })
      .catch((err: Error) => {
        setCtxNewState('error')
        setCtxNewMsg(err.message)
        setTimeout(() => setCtxNewState('idle'), 5000)
      })
  }

  // ─── Workflow Definitions handlers ────────────────────────────────────────

  function loadWfDefs() {
    setWfDefsLoading(true)
    apiFetch('/api/workflow-definitions?includeInactive=true')
      .then(r => r.json())
      .then((list: WorkflowDefinitionSummary[]) => { setWfDefs(list); setWfDefsLoaded(true) })
      .catch(() => {})
      .finally(() => setWfDefsLoading(false))
  }

  function loadWfDefDetail(name: string) {
    setWfDefDetailLoading(prev => ({ ...prev, [name]: true }))
    apiFetch(`/api/workflow-definitions/${encodeURIComponent(name)}`)
      .then(r => r.json())
      .then((detail: WorkflowDefinitionDetail) => {
        setWfDefDetails(prev => ({ ...prev, [name]: detail }))
        setWfDefEdits(prev => ({ ...prev, [name]: detail.definition }))
      })
      .catch(() => {})
      .finally(() => setWfDefDetailLoading(prev => ({ ...prev, [name]: false })))
  }

  function toggleWfDefExpand(name: string) {
    setExpandedWfDef(prev => prev === name ? null : name)
    if (expandedWfDef !== name && !wfDefDetails[name]) loadWfDefDetail(name)
  }

  function saveWfDef(name: string) {
    const content = wfDefEdits[name]
    const reason = wfDefReasons[name]?.trim() || undefined
    setWfDefSaveState(prev => ({ ...prev, [name]: 'saving' }))
    apiFetch(`/api/workflow-definitions/${encodeURIComponent(name)}`, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ definition: content, reason }),
    })
      .then(async r => {
        if (!r.ok) { const b = await r.json().catch(() => ({})); throw new Error(b?.error ?? `Error ${r.status}`) }
        return r.json()
      })
      .then(() => {
        setWfDefSaveState(prev => ({ ...prev, [name]: 'success' }))
        setWfDefSaveMsg(prev => ({ ...prev, [name]: 'Saved' }))
        loadWfDefDetail(name)
        loadWfDefs()
        setTimeout(() => setWfDefSaveState(prev => ({ ...prev, [name]: 'idle' })), 3000)
      })
      .catch((err: Error) => {
        setWfDefSaveState(prev => ({ ...prev, [name]: 'error' }))
        setWfDefSaveMsg(prev => ({ ...prev, [name]: err.message }))
        setTimeout(() => setWfDefSaveState(prev => ({ ...prev, [name]: 'idle' })), 5000)
      })
  }

  function handleWfDefRollback(name: string, targetVersion: number) {
    const key = `${name}:${targetVersion}`
    if (wfDefRollbackConfirm[name] === key) {
      // Confirmed — execute rollback
      setWfDefRollbackConfirm(prev => ({ ...prev, [name]: null }))
      const verDef = wfDefDetails[name]?.versions.find(v => v.version === targetVersion)?.definition
      if (!verDef) return
      const reason = `Rollback to v${targetVersion}`
      setWfDefSaveState(prev => ({ ...prev, [name]: 'saving' }))
      apiFetch(`/api/workflow-definitions/${encodeURIComponent(name)}`, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ definition: verDef, reason }),
      })
        .then(async r => {
          if (!r.ok) { const b = await r.json().catch(() => ({})); throw new Error(b?.error ?? `Error ${r.status}`) }
          return r.json()
        })
        .then(() => {
          setWfDefSaveState(prev => ({ ...prev, [name]: 'success' }))
          setWfDefSaveMsg(prev => ({ ...prev, [name]: `Rolled back to v${targetVersion}` }))
          setWfDefSelectedVersion(prev => ({ ...prev, [name]: null }))
          loadWfDefDetail(name)
          loadWfDefs()
          setTimeout(() => setWfDefSaveState(prev => ({ ...prev, [name]: 'idle' })), 3000)
        })
        .catch((err: Error) => {
          setWfDefSaveState(prev => ({ ...prev, [name]: 'error' }))
          setWfDefSaveMsg(prev => ({ ...prev, [name]: err.message }))
          setTimeout(() => setWfDefSaveState(prev => ({ ...prev, [name]: 'idle' })), 5000)
        })
    } else {
      setWfDefRollbackConfirm(prev => ({ ...prev, [name]: key }))
      setTimeout(() => setWfDefRollbackConfirm(prev => prev[name] === key ? { ...prev, [name]: null } : prev), 5000)
    }
  }

  function handleWfDefToggleConfirmClick(name: string) {
    setWfDefToggleConfirm(prev => ({ ...prev, [name]: true }))
    setTimeout(() => setWfDefToggleConfirm(prev => prev[name] ? { ...prev, [name]: false } : prev), 5000)
  }

  function toggleWfDefActive(name: string, isActive: boolean) {
    setWfDefToggleConfirm(prev => ({ ...prev, [name]: false }))
    setWfDefToggleState(prev => ({ ...prev, [name]: 'pending' }))
    apiFetch(`/api/workflow-definitions/${encodeURIComponent(name)}/toggle-active`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ isActive }),
    })
      .then(async r => {
        if (!r.ok) { const b = await r.json().catch(() => ({})); throw new Error(b?.error ?? `Error ${r.status}`) }
        return r.json()
      })
      .then(() => {
        setWfDefToggleState(prev => ({ ...prev, [name]: 'success' }))
        setWfDefToggleMsg(prev => ({ ...prev, [name]: isActive ? 'Enabled' : 'Disabled' }))
        loadWfDefs()
        setTimeout(() => setWfDefToggleState(prev => ({ ...prev, [name]: 'idle' })), 3000)
      })
      .catch((err: Error) => {
        setWfDefToggleState(prev => ({ ...prev, [name]: 'error' }))
        setWfDefToggleMsg(prev => ({ ...prev, [name]: err.message }))
        setTimeout(() => setWfDefToggleState(prev => ({ ...prev, [name]: 'idle' })), 5000)
      })
  }

  function createWfDef() {
    setWfDefNewState('saving')
    apiFetch('/api/workflow-definitions', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        name: wfDefNewForm.name,
        namespace: wfDefNewForm.namespace,
        taskQueue: wfDefNewForm.taskQueue,
        definition: wfDefNewForm.definition,
        description: wfDefNewForm.description || undefined,
      }),
    })
      .then(async r => {
        if (!r.ok) { const b = await r.json().catch(() => ({})); throw new Error(b?.error ?? `Error ${r.status}`) }
        return r.json()
      })
      .then(() => {
        setWfDefNewState('success')
        setWfDefNewMsg('Created')
        setWfDefShowNew(false)
        setWfDefNewForm({ name: '', namespace: '', taskQueue: '', description: '', definition: '' })
        loadWfDefs()
        setTimeout(() => setWfDefNewState('idle'), 3000)
      })
      .catch((err: Error) => {
        setWfDefNewState('error')
        setWfDefNewMsg(err.message)
        setTimeout(() => setWfDefNewState('idle'), 5000)
      })
  }

  function wfKey(wf: WorkflowSummary): string { return `${wf.namespace}/${wf.workflowId}` }

  async function handleWfClick(wf: WorkflowSummary) {
    setSelectedWf(wf)
    setWfHistory([])
    setWfHistoryError(null)
    setExpandedEvents(new Set())
    setWfHistoryLoading(true)
    try {
      const res = await apiFetch(`/api/workflows/${encodeURIComponent(wf.namespace)}/history/${encodeURIComponent(wf.workflowId)}`)
      if (!res.ok) throw new Error(`HTTP ${res.status}`)
      const events: WorkflowEvent[] = await res.json()
      const sorted = [...events].reverse()
      setWfHistory(sorted)
      if (sorted.length > 0) setExpandedEvents(new Set([sorted[0].eventId]))
    } catch (e: unknown) {
      setWfHistoryError(e instanceof Error ? e.message : 'Unknown error')
    } finally {
      setWfHistoryLoading(false)
    }
  }

  function getWfActionState(wf: WorkflowSummary): WfActionState { return wfActionStates[wfKey(wf)] ?? 'idle' }

  function setWfState(wf: WorkflowSummary, state: WfActionState, msg = '') {
    const k = wfKey(wf)
    setWfActionStates(prev => ({ ...prev, [k]: state }))
    setWfActionMsg(prev => ({ ...prev, [k]: msg }))
  }

  function handleWfAction(wf: WorkflowSummary, action: 'cancel' | 'restart' | 'terminate') {
    const state = getWfActionState(wf)
    if (state === 'pending') return
    if (state === `confirming-${action}`) {
      setWfState(wf, 'pending')
      const url = action === 'restart'
        ? `/api/workflows/${encodeURIComponent(wf.namespace)}/restart/${encodeURIComponent(wf.workflowId)}?runId=${encodeURIComponent(wf.runId)}&terminateExisting=true`
        : `/api/workflows/${encodeURIComponent(wf.namespace)}/${action}/${encodeURIComponent(wf.workflowId)}?runId=${encodeURIComponent(wf.runId)}`
      apiFetch(url, { method: 'POST' })
        .then(async r => {
          if (r.ok) { const label = action === 'cancel' ? 'Cancel sent' : action === 'restart' ? 'Restarted' : 'Terminated'; setWfState(wf, 'success', label) }
          else { const b = await r.json().catch(() => ({})); setWfState(wf, 'error', b?.error ?? b?.title ?? `Error ${r.status}`) }
        })
        .catch(() => setWfState(wf, 'error', 'Request failed'))
        .finally(() => setTimeout(() => setWfState(wf, 'idle'), 4000))
      return
    }
    setWfState(wf, `confirming-${action}`)
    setTimeout(() => {
      setWfActionStates(prev => {
        const cur = prev[wfKey(wf)]
        if (cur === `confirming-${action}`) return { ...prev, [wfKey(wf)]: 'idle' }
        return prev
      })
    }, 5000)
  }

  function signalKey(wf: WorkflowSummary): string { return `${wf.namespace}/${wf.workflowId}` }

  function handleWfActionDirect(wf: WorkflowSummary, action: 'cancel' | 'restart' | 'terminate') {
    setWfState(wf, 'pending')
    const url = action === 'restart'
      ? `/api/workflows/${encodeURIComponent(wf.namespace)}/restart/${encodeURIComponent(wf.workflowId)}?runId=${encodeURIComponent(wf.runId)}&terminateExisting=true`
      : `/api/workflows/${encodeURIComponent(wf.namespace)}/${action}/${encodeURIComponent(wf.workflowId)}?runId=${encodeURIComponent(wf.runId)}`
    apiFetch(url, { method: 'POST' })
      .then(async r => {
        if (r.ok) { const label = action === 'cancel' ? 'Cancel sent' : action === 'restart' ? 'Restarted' : 'Terminated'; setWfState(wf, 'success', label) }
        else { const b = await r.json().catch(() => ({})); setWfState(wf, 'error', b?.error ?? b?.title ?? `Error ${r.status}`) }
      })
      .catch(() => setWfState(wf, 'error', 'Request failed'))
      .finally(() => setTimeout(() => setWfState(wf, 'idle'), 4000))
  }

  function getSignalDefs(wf: WorkflowSummary): SignalDef[] {
    const defs = signalRegistry[wf.workflowType] ?? []
    if (!wf.phase) return []
    return defs.filter(sig => !sig.validPhases || sig.validPhases.includes(wf.phase!))
  }

  function signalConfirmKey(wf: WorkflowSummary, sigName: string, btnLabel: string): string {
    return `${signalKey(wf)}|${sigName}|${btnLabel}`
  }

  function handleSignalClick(wf: WorkflowSummary, sig: SignalDef, btn: SignalButton) {
    if (btn.requiresComment) {
      setSignalModal({ wf, signalName: sig.name, button: btn, comment: '' })
      return
    }
    const ck = signalConfirmKey(wf, sig.name, btn.label)
    if (signalConfirm === ck) {
      setSignalConfirm(null)
      sendSignal(wf, sig.name, btn.payload)
    } else {
      setSignalConfirm(ck)
      setTimeout(() => setSignalConfirm(prev => prev === ck ? null : prev), 5000)
    }
  }

  function sendSignal(wf: WorkflowSummary, signalName: string, payload: string, comment?: string) {
    const finalPayload = comment ? JSON.stringify({ ...JSON.parse(payload), Comment: comment }) : payload
    const key = signalKey(wf)
    setSignalStates(prev => ({ ...prev, [key]: 'pending' }))
    apiFetch(`/api/workflows/${encodeURIComponent(wf.namespace)}/signal/${encodeURIComponent(wf.workflowId)}`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ signalName, workflowType: wf.workflowType, payload: finalPayload, runId: wf.runId }),
    })
      .then(async r => {
        if (r.ok) { setSignalStates(prev => ({ ...prev, [key]: 'success' })); setSignalMsg(prev => ({ ...prev, [key]: 'Signal sent' })); setSentSignals(prev => new Set(prev).add(`${wf.workflowId}::${wf.phase}`)) }
        else { const b = await r.json().catch(() => ({})); setSignalStates(prev => ({ ...prev, [key]: 'error' })); setSignalMsg(prev => ({ ...prev, [key]: b?.error ?? `Error ${r.status}` })) }
      })
      .catch(() => { setSignalStates(prev => ({ ...prev, [key]: 'error' })); setSignalMsg(prev => ({ ...prev, [key]: 'Request failed' })) })
      .finally(() => setTimeout(() => setSignalStates(prev => ({ ...prev, [key]: 'idle' })), 4000))
  }

  function applyLogFilter(lines: string[]): string[] {
    if (logFilter === 'all') return lines
    return lines.filter(l => {
      const low = l.toLowerCase()
      if (logFilter === 'error') return low.includes('error') || low.includes(' err ') || low.includes('[err') || low.includes('exception')
      if (logFilter === 'warn') return low.includes('warn')
      if (logFilter === 'info') return low.includes('info')
      return true
    })
  }

  function handleContainerCopy(name: string) {
    navigator.clipboard.writeText(name).then(() => {
      setCopiedContainer(name)
      setTimeout(() => setCopiedContainer(null), 2000)
    }).catch(() => {})
  }

  function wfStatusClass(wf: WorkflowSummary, signalDefs: SignalDef[]): string {
    const s = wf.status
    if (s === 'TimedOut' || s === 'Canceled' || s === 'Terminated' || s === 'Completed' || s === 'Failed') return 'wf-row-muted'
    if (s === 'Running' && signalDefs.length > 0) return 'wf-row-signal-waiting'
    if (s === 'Running') return 'wf-row-running'
    return ''
  }

  async function handleCopyFrom(sourceName: string) {
    setCopyFrom(sourceName)
    if (!sourceName) return
    setCopyFromLoading(true)
    try {
      const r = await apiFetch(`/api/agents/${encodeURIComponent(sourceName)}/config`)
      if (!r.ok) return
      const cfg: AgentConfig = await r.json()
      setCreateForm(prev => ({
        ...prev,
        role: (cfg as unknown as { role?: string }).role ?? prev.role,
        model: cfg.model,
        memoryLimitMb: String(cfg.memoryLimitMb),
        image: cfg.image ?? '',
        permissionMode: cfg.permissionMode,
        maxTurns: String(cfg.maxTurns),
        workDir: cfg.workDir,
        proactiveIntervalMinutes: String(cfg.proactiveIntervalMinutes),
        groupListenMode: cfg.groupListenMode,
        groupDebounceSeconds: String(cfg.groupDebounceSeconds),
        showStats: cfg.showStats,
        prefixMessages: cfg.prefixMessages,
        suppressToolMessages: cfg.suppressToolMessages ?? false,
        telegramSendOnly: cfg.telegramSendOnly,
        provider: cfg.provider ?? 'claude',
        codexSandboxMode: cfg.codexSandboxMode ?? '',
        ttsServiceUrl: cfg.ttsServiceUrl ?? '',
        effort: cfg.effort ?? '',
        jsonSchema: cfg.jsonSchema ?? '',
        agentsJson: cfg.agentsJson ?? '',
        autoMemoryEnabled: cfg.autoMemoryEnabled,
        tools: cfg.tools.filter(t => t.isEnabled).map(t => t.toolName).join('\n'),
        projects: cfg.projects.join('\n'),
        networks: cfg.networks.join('\n'),
        envRefs: cfg.envRefs.join('\n'),
        mcpEndpoints: cfg.mcpEndpoints,
        telegramUsers: cfg.telegramUsers.join('\n'),
        telegramGroups: cfg.telegramGroups.join('\n'),
      }))
    } finally {
      setCopyFromLoading(false)
    }
  }

  async function handleCreateSubmit() {
    if (!createForm.name.trim()) { setCreateMsg('Name is required'); return }
    const createProvider = createForm.provider || 'claude'
    const createModel = createForm.model.trim() || PROVIDER_DEFAULT_MODEL[createProvider] || ''
    if (!createModel) { setCreateMsg('Model is required'); return }
    if (!createForm.role.trim()) { setCreateMsg('Role is required'); return }
    setCreateState('creating'); setCreateMsg('')
    try {
      const body = {
        name: createForm.name.trim(),
        displayName: createForm.displayName.trim() || createForm.name.trim(),
        role: createForm.role.trim(),
        model: createModel,
        containerName: createForm.containerName.trim() || `fleet-${createForm.name.trim()}`,
        memoryLimitMb: parseInt(createForm.memoryLimitMb) || 4096,
        isEnabled: createForm.isEnabled,
        image: createForm.image.trim() || null,
        permissionMode: createForm.permissionMode || 'acceptEdits',
        maxTurns: parseInt(createForm.maxTurns) || 50,
        workDir: createForm.workDir || '/workspace',
        proactiveIntervalMinutes: parseInt(createForm.proactiveIntervalMinutes) || 0,
        groupListenMode: createForm.groupListenMode || 'mention',
        groupDebounceSeconds: parseInt(createForm.groupDebounceSeconds) || 15,
        shortName: createForm.shortName,
        showStats: createForm.showStats,
        prefixMessages: createForm.prefixMessages,
        suppressToolMessages: createForm.suppressToolMessages,
        telegramSendOnly: createForm.telegramSendOnly,
        ttsServiceUrl: createForm.ttsServiceUrl.trim() || null,
        effort: createForm.effort.trim() || null,
        jsonSchema: createForm.jsonSchema.trim() || null,
        agentsJson: createForm.agentsJson.trim() || null,
        autoMemoryEnabled: createForm.autoMemoryEnabled,
        provider: createForm.provider || 'claude',
        codexSandboxMode: createForm.codexSandboxMode.trim() || null,
        tools: createForm.tools.split('\n').map(s => s.trim()).filter(Boolean),
        projects: createForm.projects.split('\n').map(s => s.trim()).filter(Boolean),
        networks: createForm.networks.split('\n').map(s => s.trim()).filter(Boolean),
        envRefs: createForm.envRefs.split('\n').map(s => s.trim()).filter(Boolean),
        mcpEndpoints: createForm.mcpEndpoints,
        telegramUsers: createForm.telegramUsers.split('\n').map(s => parseInt(s.trim())).filter(n => !isNaN(n)),
        telegramGroups: createForm.telegramGroups.split('\n').map(s => parseInt(s.trim())).filter(n => !isNaN(n)),
        provision: false,
      }
      const r = await apiFetch('/api/agents', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) })
      const data = await r.json().catch(() => ({}))
      if (r.ok || r.status === 201 || r.status === 207) {
        setCreateState('success')
        setCreateMsg(data.message ?? 'Agent created — configure and provision when ready')
        setTimeout(() => { setCreateModalOpen(false); setCreateState('idle'); setCreateMsg(''); setCreateForm(DEFAULT_CREATE_FORM); setCopyFrom('') }, 3000)
      } else {
        setCreateState('error'); setCreateMsg(data.error ?? `Error ${r.status}`)
      }
    } catch {
      setCreateState('error'); setCreateMsg('Request failed')
    }
  }

  useEffect(() => {
    const params = new URLSearchParams(window.location.search)
    const deleteAgent = params.get('delete')
    if (deleteAgent) setDeleteStates(prev => ({ ...prev, [deleteAgent]: 'confirming' }))
    const hasCreate = params.has('create') || params.has('name')
    if (hasCreate) {
      const form = { ...DEFAULT_CREATE_FORM }
      const p = (k: string) => params.get(k) ?? ''
      if (p('name')) form.name = p('name')
      if (p('displayName')) form.displayName = p('displayName')
      if (p('role')) form.role = p('role')
      if (p('model')) form.model = p('model')
      if (p('memory')) form.memoryLimitMb = p('memory')
      setCreateForm(form)
      setCreateModalOpen(true)
    }
  }, [])

  const sorted = Object.values(agents).sort((a, b) => a.agentName.localeCompare(b.agentName))

  return (
    <div className="app-layout">
      <AppHeader
        wsStatus={wsStatus}
        unreadAlertCount={unreadAlerts.length}
        onAlertsClick={() => setActiveView('alerts')}
        navOpen={navOpen}
        onHamburgerClick={() => setNavOpen(o => !o)}
        agents={agents}
        workflows={workflows}
        onNavigate={view => setActiveView(view)}
        onHighlight={onHighlight}
      />

      <Sidenav
        activeView={activeView}
        onNavigate={view => {
          setActiveView(view)
          if (view === 'wf-definitions' && !wfDefsLoaded) loadWfDefs()
        }}
        agentCount={sorted.length}
        activeWorkflowCount={workflows.length}
        attentionWorkflowCount={workflows.filter(wf => getSignalDefs(wf).length > 0).length}
        instructionCount={instructions.length}
        projectContextCount={projectContexts.length}
        wfDefinitionCount={wfDefs.length}
        namespaceCount={apiNamespaces.length}
        unreadAlertCount={unreadAlerts.length}
        reprovisionAllState={reprovisionAllState}
        reprovisionAllMsg={reprovisionAllMsg}
        onReprovisionAll={handleReprovisionAll}
        onReprovisionAllConfirm={handleReprovisionAllConfirm}
        onReprovisionAllCancel={handleReprovisionAllCancel}
        onNewAgent={() => { setCreateForm(DEFAULT_CREATE_FORM); setCopyFrom(''); setCreateState('idle'); setCreateMsg(''); setCreateModalOpen(true) }}
        navOpen={navOpen}
        onNavClose={() => setNavOpen(false)}
      />

      <main className="app-main">
        {activeView === 'agents' && (
          <AgentsView
            sorted={sorted}
            expandedAgent={expandedAgent}
            taskHistory={taskHistory}
            historyLoading={historyLoading}
            restartStates={restartStates}
            restartMsg={restartMsg}
            reprovisionStates={reprovisionStates}
            reprovisionMsg={reprovisionMsg}
            stopStates={stopStates}
            stopMsg={stopMsg}
            startStates={startStates}
            startMsg={startMsg}
            cancelStates={cancelStates}
            cancelMsg={cancelMsg}
            deleteStates={deleteStates}
            deleteMsg={deleteMsg}
            bgCancelStates={bgCancelStates}
            expandedTasks={expandedTasks}
            copiedContainer={copiedContainer}
            configAgent={configAgent}
            logViewer={logViewer}
            workflowByAgent={workflowByAgent}
            workflows={workflows}
            agentByWorkflow={agentByWorkflow}
            wfActionStates={wfActionStates}
            wfActionMsg={wfActionMsg}
            signalStates={signalStates}
            signalMsg={signalMsg}
            signalConfirm={signalConfirm}
            signalRegistry={signalRegistry}
            wfMenuOpen={wfMenuOpen}
            selectedWf={selectedWf}
            wfHistory={wfHistory}
            wfHistoryLoading={wfHistoryLoading}
            wfHistoryError={wfHistoryError}
            expandedEvents={expandedEvents}
            onExpandAgent={toggleHistory}
            onRestart={handleRestart}
            onRestartConfirm={handleRestartConfirm}
            onRestartCancel={name => setAgentRestartState(name, 'idle')}
            onReprovision={handleReprovision}
            onReprovisionConfirm={handleReprovisionConfirm}
            onReprovisionCancel={name => setAgentReprovisionState(name, 'idle')}
            onStop={handleStop}
            onStart={handleStart}
            onCancel={handleCancel}
            onCancelConfirm={handleCancelConfirm}
            onCancelCancel={name => setAgentCancelState(name, 'idle')}
            onDeleteClick={handleDeleteClick}
            onDeleteConfirm={handleDeleteConfirm}
            onDeleteCancel={name => setDeleteStates(prev => ({ ...prev, [name]: 'idle' }))}
            onBgCancel={handleBgCancel}
            onViewLogs={name => setLogViewer(logViewer === name ? null : name)}
            onEditConfig={openConfig}
            onToggleTask={toggleTaskExpand}
            onCopyContainer={handleContainerCopy}
            onWfClick={handleWfClick}
            onWfAction={handleWfAction}
            onSignalClick={handleSignalClick}
            onToggleMenu={k => setWfMenuOpen(prev => ({ ...prev, [k]: !prev[k] }))}
            onToggleEvent={id => setExpandedEvents(prev => { const next = new Set(prev); prev.has(id) ? next.delete(id) : next.add(id); return next })}
            onCloseDetail={() => setSelectedWf(null)}
            onRefreshDetail={() => selectedWf && handleWfClick(selectedWf)}
            getSignalDefs={getSignalDefs}
            wfActionState={getWfActionState}
            signalKey={signalKey}
            wfKey={wfKey}
            wfStatusClass={wfStatusClass}
            signalConfirmKey={signalConfirmKey}
            highlightedEntityId={highlightedEntityId}
            onClearHighlight={onClearHighlight}
          />
        )}

        {activeView === 'workflows' && (
          <WorkflowsView
            workflows={workflows}
            completedWorkflows={completedWorkflows}
            completedCollapsed={completedCollapsed}
            completedLoading={completedLoading}
            nsFilter={nsFilter}
            namespaces={namespaces}
            filteredWorkflows={filteredWorkflows}
            filteredCompletedWorkflows={filteredCompletedWorkflows}
            agentByWorkflow={agentByWorkflow}
            wfActionStates={wfActionStates}
            wfActionMsg={wfActionMsg}
            signalStates={signalStates}
            signalMsg={signalMsg}
            sentSignals={sentSignals}
            signalRegistry={signalRegistry}
            wfMenuOpen={wfMenuOpen}
            selectedWf={selectedWf}
            wfHistory={wfHistory}
            wfHistoryLoading={wfHistoryLoading}
            wfHistoryError={wfHistoryError}
            expandedEvents={expandedEvents}
            onNsFilter={setNsFilter}
            onToggleCompleted={() => setCompletedCollapsed(c => !c)}
            onRefreshCompleted={fetchCompleted}
            onWfClick={handleWfClick}
            onSignalClick={handleSignalClick}
            onToggleMenu={k => setWfMenuOpen(prev => {
              // Support close-only key (from handleWfActionLocal)
              const realKey = k.endsWith('__close__') ? k.slice(0, -9) : k
              if (k.endsWith('__close__')) return { ...prev, [realKey]: false }
              return { ...prev, [realKey]: !prev[realKey] }
            })}
            onToggleEvent={id => setExpandedEvents(prev => { const next = new Set(prev); prev.has(id) ? next.delete(id) : next.add(id); return next })}
            onCloseDetail={() => setSelectedWf(null)}
            onRefreshDetail={() => selectedWf && handleWfClick(selectedWf)}
            getSignalDefs={getSignalDefs}
            wfActionState={getWfActionState}
            signalKey={signalKey}
            wfKey={wfKey}
            wfStatusClass={wfStatusClass}
            onSendSignal={sendSignal}
            onExecuteWfAction={handleWfActionDirect}
            highlightedEntityId={highlightedEntityId}
            onClearHighlight={onClearHighlight}
            onStartWorkflow={() => setStartWfModalOpen(true)}
            onRefreshWorkflows={fetchWorkflows}
            workflowsLoading={workflowsLoading}
          />
        )}

        {activeView === 'instructions' && (
          <InstructionsView
            instructions={instructions}
            instructionsLoading={instructionsLoading}
            expandedInstruction={expandedInstruction}
            instructionDetail={instructionDetail}
            instructionDetailLoading={instructionDetailLoading}
            instructionEdits={instructionEdits}
            instructionReason={instructionReason}
            instructionSaveState={instructionSaveState as Record<string, ConfigSaveState>}
            instructionSaveMsg={instructionSaveMsg}
            selectedVersion={selectedVersion}
            rollbackConfirm={rollbackConfirm}
            deployConfirm={deployConfirm}
            deployState={deployState}
            deployMsg={deployMsg}
            instrToggleConfirm={instrToggleConfirm}
            instrToggleState={instrToggleState}
            instrToggleMsg={instrToggleMsg}
            showNewForm={instrShowNew}
            newForm={instrNewForm}
            newFormState={instrNewState}
            newFormMsg={instrNewMsg}
            onToggleInstruction={toggleInstruction}
            onSetEdits={(name, content) => setInstructionEdits(prev => ({ ...prev, [name]: content }))}
            onSetReason={(name, reason) => setInstructionReason(prev => ({ ...prev, [name]: reason }))}
            onSave={saveInstruction}
            onSelectVersion={(name, version) => setSelectedVersion(prev => ({ ...prev, [name]: prev[name] === version ? null : version }))}
            onRollbackClick={handleRollbackClick}
            onDeployClick={handleDeployClick}
            onToggleActive={toggleInstrActive}
            onToggleConfirmClick={handleInstrToggleConfirmClick}
            onShowNewForm={setInstrShowNew}
            onNewFormChange={(field, value) => setInstrNewForm(prev => ({ ...prev, [field]: value }))}
            onNewFormSubmit={createInstruction}
            onRefresh={loadInstructions}
          />
        )}

        {activeView === 'project-contexts' && (
          <ProjectContextsView
            contexts={projectContexts}
            contextsLoading={projectContextsLoading}
            expandedContext={expandedContext}
            contextDetail={contextDetail}
            contextDetailLoading={contextDetailLoading}
            contextEdits={contextEdits}
            contextReason={contextReason}
            contextSaveState={contextSaveState as Record<string, ConfigSaveState>}
            contextSaveMsg={contextSaveMsg}
            selectedVersion={contextSelectedVersion}
            rollbackConfirm={contextRollbackConfirm}
            ctxToggleConfirm={ctxToggleConfirm}
            ctxToggleState={ctxToggleState}
            ctxToggleMsg={ctxToggleMsg}
            showNewForm={ctxShowNew}
            newForm={ctxNewForm}
            newFormState={ctxNewState}
            newFormMsg={ctxNewMsg}
            onToggleContext={toggleContext}
            onSetEdits={(name, content) => setContextEdits(prev => ({ ...prev, [name]: content }))}
            onSetReason={(name, reason) => setContextReason(prev => ({ ...prev, [name]: reason }))}
            onSave={saveContext}
            onSelectVersion={(name, version) => setContextSelectedVersion(prev => ({ ...prev, [name]: prev[name] === version ? null : version }))}
            onRollbackClick={handleContextRollbackClick}
            onToggleActive={toggleCtxActive}
            onToggleConfirmClick={handleCtxToggleConfirmClick}
            onShowNewForm={setCtxShowNew}
            onNewFormChange={(field, value) => setCtxNewForm(prev => ({ ...prev, [field]: value }))}
            onNewFormSubmit={createContext}
            onRefresh={loadProjectContexts}
          />
        )}

        {activeView === 'wf-definitions' && (
          <WorkflowDefinitionsView
            definitions={wfDefs}
            loading={wfDefsLoading}
            namespaces={apiNamespaces}
            expandedDef={expandedWfDef}
            defDetails={wfDefDetails}
            defDetailLoading={wfDefDetailLoading}
            defEdits={wfDefEdits}
            defReasons={wfDefReasons}
            defSaveState={wfDefSaveState}
            defSaveMsg={wfDefSaveMsg}
            defSelectedVersion={wfDefSelectedVersion}
            defRollbackConfirm={wfDefRollbackConfirm}
            defToggleConfirm={wfDefToggleConfirm}
            defToggleState={wfDefToggleState}
            defToggleMsg={wfDefToggleMsg}
            nsFilter={wfDefNsFilter}
            searchQuery={wfDefSearch}
            showNewForm={wfDefShowNew}
            newForm={wfDefNewForm}
            newFormState={wfDefNewState}
            newFormMsg={wfDefNewMsg}
            onToggleExpand={toggleWfDefExpand}
            onSetEdit={(name, value) => setWfDefEdits(prev => ({ ...prev, [name]: value }))}
            onSetReason={(name, value) => setWfDefReasons(prev => ({ ...prev, [name]: value }))}
            onSave={saveWfDef}
            onSelectVersion={(name, ver) => setWfDefSelectedVersion(prev => ({ ...prev, [name]: ver }))}
            onRollbackClick={handleWfDefRollback}
            onToggleActive={toggleWfDefActive}
            onToggleConfirmClick={handleWfDefToggleConfirmClick}
            onNsFilter={setWfDefNsFilter}
            onSearch={setWfDefSearch}
            onShowNewForm={setWfDefShowNew}
            onNewFormChange={(field, value) => setWfDefNewForm(prev => ({ ...prev, [field]: value }))}
            onNewFormSubmit={createWfDef}
            onEditVisual={name => setWfEditorDef(name)}
            onNewVisual={() => setWfEditorDef(null)}
            onRefresh={loadWfDefs}
          />
        )}

        {activeView === 'schedules' && (
          <SchedulesView
            schedules={schedules}
            loading={schedulesLoading}
            workflowTypes={workflowTypes}
            namespaces={apiNamespaces}
            onRefresh={loadSchedules}
          />
        )}

        {activeView === 'namespaces' && (
          <NamespacesView
            namespaces={apiNamespaces}
            workflows={workflows}
            schedules={schedules}
            workflowTypes={workflowTypes}
            schedulesLoading={schedulesLoading}
          />
        )}

        {activeView === 'repositories' && (
          <RepositoriesView />
        )}

        {activeView === 'alerts' && (
          <AlertsView
            alerts={alerts}
            onDismiss={id => setAlerts(prev => prev.map(a => a.id === id ? { ...a, dismissed: true } : a))}
            onDismissAll={() => setAlerts(prev => prev.map(a => ({ ...a, dismissed: true })))}
            onAlertEntityLink={handleAlertEntityLink}
          />
        )}
      </main>

      {/* Visual Workflow Editor overlay */}
      {wfEditorDef !== undefined && (
        <div className="wfe-overlay">
          <WorkflowDefinitionEditor
            defName={wfEditorDef}
            onBack={() => setWfEditorDef(undefined)}
            onSaved={() => { setWfEditorDef(undefined); loadWfDefs() }}
            workflowTypes={workflowTypes}
            namespaces={apiNamespaces}
          />
        </div>
      )}

      <AppFooter wsStatus={wsStatus} lastUpdated={lastUpdated} />

      {/* Overlay modals */}
      {logViewer && (
        <LogViewer
          agentName={logViewer}
          logLines={logLines}
          logFilter={logFilter}
          logPaused={logPaused}
          logAutoScroll={logAutoScroll}
          onSetFilter={setLogFilter}
          onTogglePause={() => {
            const next = !logPaused
            setLogPaused(next)
            logPausedRef.current = next
          }}
          onToggleAutoScroll={() => setLogAutoScroll(a => !a)}
          onClear={() => setLogLines([])}
          onClose={() => setLogViewer(null)}
          applyLogFilter={applyLogFilter}
        />
      )}

      {signalModal && (
        <SignalModal
          modal={signalModal}
          onClose={() => setSignalModal(null)}
          onCommentChange={comment => setSignalModal(m => m ? { ...m, comment } : null)}
          onSend={() => {
            sendSignal(signalModal.wf, signalModal.signalName, signalModal.button.payload, signalModal.comment || undefined)
            setSignalModal(null)
          }}
        />
      )}

      {createModalOpen && (
        <CreateAgentModal
          createForm={createForm}
          createState={createState}
          createMsg={createMsg}
          agentNames={sorted.map(a => a.agentName)}
          copyFrom={copyFrom}
          copyFromLoading={copyFromLoading}
          onCopyFrom={handleCopyFrom}
          onFormChange={patch => setCreateForm(f => ({ ...f, ...patch }))}
          onSubmit={handleCreateSubmit}
          onClose={() => setCreateModalOpen(false)}
        />
      )}

      {configAgent && (
        <AgentConfigModal
          agentName={configAgent}
          configData={configData}
          configEdits={configEdits}
          configSaveState={configSaveState}
          configSaveMsg={configSaveMsg}
          configLoading={configLoading}
          configReprovisionConfirm={configReprovisionConfirm}
          onEditsChange={patch => setConfigEdits(prev => prev ? { ...prev, ...patch } : prev)}
          onSave={andReprovision => saveConfig(configAgent, andReprovision)}
          onReprovisionConfirmToggle={() => setConfigReprovisionConfirm(c => !c)}
          onClose={closeConfig}
        />
      )}

      {startWfModalOpen && (
        <StartWorkflowModal
          workflowTypes={workflowTypes}
          agents={Object.values(agents)}
          onClose={() => setStartWfModalOpen(false)}
        />
      )}

      {/* Toast notifications */}
      {toastAlerts.length > 0 && (
        <div className="toast-area">
          {toastAlerts.slice(0, 5).map(alert => (
            <div key={alert.id} className={`toast toast-${alert.type}`}>
              <span className="toast-msg">
                {alert.message}
                {(alert.workflowId || alert.agentName) && (
                  <button className="toast-entity-link" onClick={() => {
                    setAlerts(prev => prev.map(a => a.id === alert.id ? { ...a, showToast: false } : a))
                    handleAlertEntityLink(alert)
                  }}>view</button>
                )}
              </span>
              <button className="toast-close" onClick={() => setAlerts(prev => prev.map(a => a.id === alert.id ? { ...a, showToast: false } : a))}>✕</button>
            </div>
          ))}
        </div>
      )}
    </div>
  )
}
