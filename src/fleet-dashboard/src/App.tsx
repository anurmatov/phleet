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
  OutputStyleSummary,
  PromptSizeLimits,
  PromptSizeReport,
} from './types'
import { apiFetch, apiGet, apiSend, errorMessage, firstFailure } from './utils'
import { buildSignalRequest, signalErrorMessage } from './signalRequest'
import { humanInputIdentity } from './humanInputRequest'
import { PROVIDER_DEFAULT_MODEL } from './constants'
import { warmupTimeoutEditValue } from './warmupTimeout'
import { contextWindowEditValue } from './contextWindow'
import { localModelEditValues } from './localModel'
import { configSaveBody } from './agentConfigSave'
import { DEFAULT_CREATE_FORM, createAgentBody, createFormFromConfig } from './agentCreate'
import { parseViewHash } from './activeViews'
import { reconcileAgents } from './agentList'
import { KeyedTimers, afterClose } from './reconnect'
import { useInstructions } from './hooks/useInstructions'
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
import ChatView from './components/ChatView'
import LogViewer from './components/LogViewer'
import SignalModal from './components/SignalModal'
import CreateAgentModal from './components/CreateAgentModal'
import AgentConfigModal from './components/AgentConfigModal'
import StartWorkflowModal from './components/StartWorkflowModal'
import SchedulesView from './components/SchedulesView'
import NamespacesView from './components/NamespacesView'
import RepositoriesView from './components/RepositoriesView'
import EpicGrantsView from './components/EpicGrantsView'
import OutputStylesView from './components/OutputStylesView'
import CredentialsView from './components/CredentialsView'
import MemoryView from './components/MemoryView'
import { MemoryIdCacheProvider } from './context/MemoryIdCacheContext'
import SetupBanner from './components/SetupBanner'

type WsMessage =
  | { type: 'workflows'; data: WorkflowSummary[] }
  | (AgentState & { type?: undefined })

const STUCK_TASK_MS = 30 * 60 * 1000

interface SetupStatus {
  telegram: { configured: boolean; groupChatEnabled: boolean }
  gitHub: { configured: boolean }
}

/** A write that may answer with the #346 size report. Null when the answer has no body. */
type SizedWrite = { size?: PromptSizeReport } | null

/** `GET /api/agent-templates/{name}`: the template's create-form defaults. */
interface AgentTemplateDetail {
  displayName?: string
  role?: string
  model?: string
  provider?: string
  memoryLimitMb?: number
  permissionMode?: string
  maxTurns?: number
  workDir?: string
  proactiveIntervalMinutes?: number
  groupListenMode?: string
  groupDebounceSeconds?: number
  warmupTimeoutSeconds?: number
  showStats?: boolean
  prefixMessages?: boolean
  formattingMode?: number
  suppressToolMessages?: boolean
  telegramSendOnly?: boolean
  autoMemoryEnabled?: boolean
  tools?: { toolName: string }[]
  projects?: string[]
  networks?: string[]
  envRefs?: string[]
  mcpEndpoints?: McpEndpointEntry[]
  instructions?: { name: string; loadOrder: number }[]
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
  const reconnectFailures = useRef(0)
  const unmounted = useRef(false)

  // Confirmations ("confirm?") and result messages ("Stopped") go back to idle on timers. Each
  // action on each target owns one timer, so a timer left by an earlier click or an earlier
  // result cannot expire a confirmation armed after it (reconnect.ts, KeyedTimers).
  const [timers] = useState(() => new KeyedTimers())
  useEffect(() => () => timers.cancelAll(), [timers])

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
  const [projectAccess, setProjectAccess] = useState<string[] | null>(null)
  const [projectAccessLoading, setProjectAccessLoading] = useState(false)
  const [reprovisionAllState, setReprovisionAllState] = useState<ReprovisionAllState>('idle')
  const [reprovisionAllMsg, setReprovisionAllMsg] = useState('')

  const [outputStyles, setOutputStyles] = useState<OutputStyleSummary[]>([])
  const instr = useInstructions(timers)
  const instructions = instr.items
  const loadInstructions = instr.reload
  // #346: the active soft limits (null until loaded, or when the policy route is unavailable) and
  // the last write's size warning per row.
  const [promptSizeLimits, setPromptSizeLimits] = useState<PromptSizeLimits | null>(null)
  const [ctxSizeWarnings, setCtxSizeWarnings] = useState<Record<string, string>>({})

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

  const [setupStatus, setSetupStatus] = useState<SetupStatus | null>(null)
  // On failure the banner keeps what it last knew (nothing, on a first load).
  function loadSetupStatus() {
    apiGet<SetupStatus>('/api/setup/status').then(setSetupStatus).catch(() => {})
  }

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
  const [createTemplateName, setCreateTemplateName] = useState<string | undefined>(undefined)

  const [deleteStates, setDeleteStates] = useState<Record<string, 'idle' | 'confirming' | 'deleting' | 'success' | 'error'>>({})
  const [deleteMsg, setDeleteMsg] = useState<Record<string, string>>({})

  // Navigation state — synced with URL hash
  // The hash is `#view` or `#view/param` (activeViews.ts). Null for anything unrecognised, which
  // leaves the current view alone.
  const parseHash = () => parseViewHash(window.location.hash)
  const [activeView, setActiveViewState] = useState<ActiveView>(() => parseHash()?.view ?? 'agents')
  const [viewParam, setViewParam] = useState<string>(() => parseHash()?.param ?? '')
  const setActiveView = useCallback((view: ActiveView) => {
    setActiveViewState(view)
    setViewParam('')
    window.history.replaceState(null, '', `#${view}`)
  }, [])
  useEffect(() => {
    function onHashChange() {
      const parsed = parseHash()
      if (!parsed) return
      setActiveViewState(parsed.view)
      setViewParam(parsed.param)
    }
    window.addEventListener('hashchange', onHashChange)
    return () => window.removeEventListener('hashchange', onHashChange)
  }, [])
  useEffect(() => {
    if (activeView === 'wf-definitions' && !wfDefsLoaded) loadWfDefs()
    if ((activeView === 'schedules' || activeView === 'namespaces') && !schedulesLoaded) loadSchedules()
    // Runs when the view changes. The two flags only guard the calls, and are declared further
    // down this component, where a dependency list here cannot reach them.
    // eslint-disable-next-line react-hooks/exhaustive-deps
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
    apiGet<ScheduleSummary[]>('/api/schedules')
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
  }, [workflows, addAlert, setActiveView])

  // Replaces the list with what the server lists now (see reconcileAgents). On failure the rows
  // held so far stay.
  const refreshAgents = useCallback(() => {
    apiGet<AgentState[]>('/api/agents')
      .then(list => {
        setAgents(prev => reconcileAgents(prev, list))
        setLastUpdated(new Date().toLocaleTimeString())
      })
      .catch(() => {})
  }, [])

  const connect = useCallback(() => {
    if (unmounted.current) return
    const proto = location.protocol === 'https:' ? 'wss' : 'ws'
    const ws = new WebSocket(`${proto}://${location.host}/ws`)
    wsRef.current = ws
    setWsStatus('connecting')
    let openedAt: number | null = null
    ws.onopen = () => {
      openedAt = Date.now()
      setWsStatus('connected')
      // The socket only adds and updates rows. Re-reading the list on every (re)connect is what
      // drops an agent that was deleted while this page was not listening.
      refreshAgents()
    }
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
      if (unmounted.current) return
      // A handshake the orchestrator refuses (a session that lapsed) looks, from here, like any
      // other close: the browser hides the status. The list request is an ordinary HTTP read, so
      // a 401 on it reaches the sign-in gate; it also keeps the list honest while the socket is down.
      if (openedAt === null) refreshAgents()
      const next = afterClose(reconnectFailures.current, openedAt, Date.now(), Math.random())
      reconnectFailures.current = next.failures
      reconnectTimer.current = setTimeout(connect, next.delayMs)
    }
    ws.onerror = () => ws.close()
  }, [refreshAgents])

  function fetchWorkflows() {
    setWorkflowsLoading(true)
    apiGet<WorkflowSummary[]>('/api/workflows')
      .then((list: WorkflowSummary[]) => setWorkflows(list))
      .catch(() => {})
      .finally(() => setWorkflowsLoading(false))
  }

  function fetchCompleted() {
    setCompletedLoading(true)
    apiGet<WorkflowSummary[]>('/api/workflows/completed?hours=24')
      .then((list: WorkflowSummary[]) => setCompletedWorkflows(list))
      .catch(() => {})
      .finally(() => setCompletedLoading(false))
  }

  // Populates the output-style select and the sidenav count. On failure the list stays empty and
  // the select still offers "none", so a styled agent can always be switched back off.
  function loadOutputStyles() {
    apiGet<OutputStyleSummary[]>('/api/output-styles')
      .then((list: OutputStyleSummary[]) => setOutputStyles(list))
      .catch(() => setOutputStyles([]))
  }

  // #346: fetched once. On failure or 404 (an older orchestrator) the meters show bytes only.
  function loadPromptSizeLimits() {
    apiGet<PromptSizeLimits>('/api/prompt-size-policy')
      .then((policy: PromptSizeLimits) => setPromptSizeLimits(policy))
      .catch(() => setPromptSizeLimits(null))
  }

  /**
   * #346: a write's `size.warning` sets the row's ⚠ badge and notice; a write with no warning clears
   * it. A response without `size` (an older orchestrator) changes nothing.
   */
  function applySizeWarning(
    set: React.Dispatch<React.SetStateAction<Record<string, string>>>,
    name: string,
    size: PromptSizeReport | undefined,
  ) {
    if (!size) return
    set(prev => {
      const next = { ...prev }
      if (size.warning) next[name] = size.warning
      else delete next[name]
      return next
    })
  }

  function loadProjectContexts() {
    setProjectContextsLoading(true)
    apiGet<ProjectContextSummary[]>('/api/project-contexts')
      .then((list: ProjectContextSummary[]) => setProjectContexts(list))
      .catch(() => {})
      .finally(() => setProjectContextsLoading(false))
  }

  useEffect(() => {
    refreshAgents()

    fetchWorkflows()

    apiGet<Record<string, SignalDef[]>>('/api/signals')
      .then((reg: Record<string, SignalDef[]>) => setSignalRegistry(reg))
      .catch(() => {})

    apiGet<WorkflowTypeInfo[]>('/api/workflow-types')
      .then((list: WorkflowTypeInfo[]) => setWorkflowTypes(list))
      .catch(() => {})

    apiGet<string[]>('/api/namespaces')
      .then((list: string[]) => setApiNamespaces(list))
      .catch(() => {})

    loadInstructions()
    loadProjectContexts()
    loadPromptSizeLimits()
    loadOutputStyles()
    fetchCompleted()

    loadSetupStatus()
  }, [refreshAgents, loadInstructions])

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
    // A stream that ends (the container stopped, the orchestrator restarted, the session lapsed)
    // used to leave the last lines on screen looking live. It is not reopened by itself: the
    // orchestrator replays the last 200 lines on every connect, which would repeat them here.
    let closedHere = false
    ws.onclose = () => {
      if (closedHere) return
      setLogLines(prev => [...prev, '── log stream ended; close and reopen the viewer to follow again ──'])
    }
    return () => { closedHere = true; ws.close(); logWsRef.current = null }
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
      apiGet<WorkflowSummary[]>('/api/workflows/failures')
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
    apiGet<TaskRecord[]>(`/api/agents/${encodeURIComponent(agentName)}/history`)
      .then((records: TaskRecord[]) => setTaskHistory(prev => ({ ...prev, [agentName]: records })))
      .catch(() => setTaskHistory(prev => ({ ...prev, [agentName]: [] })))
      .finally(() => setHistoryLoading(prev => ({ ...prev, [agentName]: false })))
  }

  function setAgentRestartState(agentName: string, state: RestartState, msg = '') {
    if (state === 'confirming') timers.cancel(`restart:${agentName}`)
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
    apiSend(`/api/agents/${encodeURIComponent(agentName)}/restart`, 'POST')
      .then(() => setAgentRestartState(agentName, 'success', 'Restart triggered'))
      .catch((e: unknown) => { setAgentRestartState(agentName, 'error', errorMessage(e)) })
      .finally(() => timers.after(`restart:${agentName}`, 4000, () => setAgentRestartState(agentName, 'idle')))
  }

  function setAgentCancelState(agentName: string, state: CancelState, msg = '') {
    if (state === 'confirming') timers.cancel(`cancel:${agentName}`)
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
    apiSend(`/api/agents/${encodeURIComponent(agentName)}/cancel`, 'POST')
      .then(() => setAgentCancelState(agentName, 'success', 'Cancelled'))
      .catch((e: unknown) => { setAgentCancelState(agentName, 'error', errorMessage(e)) })
      .finally(() => timers.after(`cancel:${agentName}`, 4000, () => setAgentCancelState(agentName, 'idle')))
  }

  function handleBgCancel(agentName: string, taskId: string) {
    const key = `${agentName}/${taskId}`
    if (bgCancelStates[key] === 'cancelling') return
    setBgCancelStates(prev => ({ ...prev, [key]: 'cancelling' }))
    apiSend(`/api/agents/${encodeURIComponent(agentName)}/cancel_bg/${encodeURIComponent(taskId)}`, 'POST')
      .then(() => setBgCancelStates(prev => ({ ...prev, [key]: 'done' })))
      .catch(() => setBgCancelStates(prev => ({ ...prev, [key]: 'error' })))
      .finally(() => setTimeout(() => setBgCancelStates(prev => ({ ...prev, [key]: 'idle' })), 4000))
  }

  function setAgentReprovisionState(agentName: string, state: ReprovisionState, msg = '') {
    if (state === 'confirming') timers.cancel(`reprovision:${agentName}`)
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
    apiSend(`/api/agents/${encodeURIComponent(agentName)}/reprovision`, 'POST')
      .then(() => setAgentReprovisionState(agentName, 'success', 'Reprovisioning...'))
      .catch((e: unknown) => { setAgentReprovisionState(agentName, 'error', errorMessage(e)) })
      .finally(() => timers.after(`reprovision:${agentName}`, 5000, () => setAgentReprovisionState(agentName, 'idle')))
  }

  function handleReprovisionAll() {
    setReprovisionAllState('confirming')
    timers.after('reprovision-all', 5000, () => setReprovisionAllState(s => s === 'confirming' ? 'idle' : s))
  }

  function handleReprovisionAllConfirm() {
    setReprovisionAllState('running')
    apiSend('/api/agents/reprovision-running', 'POST')
      .then(() => {
        setReprovisionAllState('success')
        setReprovisionAllMsg('All agents reprovisioning…')
        addAlert('agent-stale', 'Reprovision all triggered — agents restarting')
      })
      .catch((e: unknown) => { setReprovisionAllState('error'); setReprovisionAllMsg(errorMessage(e)) })
      .finally(() => timers.after('reprovision-all', 5000, () => { setReprovisionAllState('idle'); setReprovisionAllMsg('') }))
  }

  function handleReprovisionAllCancel() {
    timers.cancel('reprovision-all')
    setReprovisionAllState('idle')
  }

  function handleStop(agentName: string) {
    const ss = stopStates[agentName] ?? 'idle'
    if (ss === 'pending') return
    if (ss === 'confirming') {
      setStopStates(prev => ({ ...prev, [agentName]: 'pending' }))
      apiSend(`/api/agents/${encodeURIComponent(agentName)}/stop`, 'POST')
        .then(() => { setStopStates(prev => ({ ...prev, [agentName]: 'success' })); setStopMsg(prev => ({ ...prev, [agentName]: 'Stopped' })) })
        .catch((e: unknown) => { setStopStates(prev => ({ ...prev, [agentName]: 'error' })); setStopMsg(prev => ({ ...prev, [agentName]: errorMessage(e) })) })
        .finally(() => timers.after(`stop:${agentName}`, 4000, () => setStopStates(prev => ({ ...prev, [agentName]: 'idle' }))))
      return
    }
    setStopStates(prev => ({ ...prev, [agentName]: 'confirming' }))
    timers.after(`stop:${agentName}`, 5000, () => setStopStates(prev => prev[agentName] === 'confirming' ? { ...prev, [agentName]: 'idle' } : prev))
  }

  function handleStart(agentName: string) {
    const ss = startStates[agentName] ?? 'idle'
    if (ss === 'pending') return
    setStartStates(prev => ({ ...prev, [agentName]: 'pending' }))
    apiSend(`/api/agents/${encodeURIComponent(agentName)}/start`, 'POST')
      .then(() => { setStartStates(prev => ({ ...prev, [agentName]: 'success' })); setStartMsg(prev => ({ ...prev, [agentName]: 'Started' })) })
      .catch((e: unknown) => { setStartStates(prev => ({ ...prev, [agentName]: 'error' })); setStartMsg(prev => ({ ...prev, [agentName]: errorMessage(e) })) })
      .finally(() => setTimeout(() => setStartStates(prev => ({ ...prev, [agentName]: 'idle' })), 4000))
  }

  function handleDeleteClick(agentName: string) {
    const state = deleteStates[agentName] ?? 'idle'
    if (state === 'deleting') return
    if (state === 'confirming') { timers.cancel(`delete:${agentName}`); setDeleteStates(prev => ({ ...prev, [agentName]: 'idle' })); return }
    armDeleteConfirm(agentName, 5000)
  }

  // "confirm delete?" goes back to idle by itself, like every other confirmation here. It used to
  // stay armed until clicked, and a `?delete=<agent>` link armed it on load with no limit at all.
  function armDeleteConfirm(agentName: string, ms: number) {
    setDeleteStates(prev => ({ ...prev, [agentName]: 'confirming' }))
    timers.after(`delete:${agentName}`, ms, () =>
      setDeleteStates(prev => prev[agentName] === 'confirming' ? { ...prev, [agentName]: 'idle' } : prev))
  }

  function handleDeleteConfirm(agentName: string) {
    setDeleteStates(prev => ({ ...prev, [agentName]: 'deleting' }))
    apiSend<{ message?: string } | null>(`/api/agents/${encodeURIComponent(agentName)}`, 'DELETE')
      .then(d => { setDeleteStates(prev => ({ ...prev, [agentName]: 'success' })); setDeleteMsg(prev => ({ ...prev, [agentName]: d?.message ?? 'Deleted' })); refreshAgents() })
      .catch((e: unknown) => { setDeleteStates(prev => ({ ...prev, [agentName]: 'error' })); setDeleteMsg(prev => ({ ...prev, [agentName]: errorMessage(e) })) })
      .finally(() => timers.after(`delete:${agentName}`, 5000, () => setDeleteStates(prev => ({ ...prev, [agentName]: 'idle' }))))
  }

  function openConfig(agentName: string) {
    if (configAgent === agentName) {
      setConfigAgent(null); setConfigData(null); setConfigEdits(null); setConfigReprovisionConfirm(false)
      return
    }
    setConfigAgent(agentName)
    setConfigData(null); setConfigEdits(null)
    setConfigSaveState('idle'); setConfigReprovisionConfirm(false)
    setProjectAccess(null)
    setConfigLoading(true)
    setProjectAccessLoading(true)
    apiGet<{ projects: string[] }>(`/api/agents/${encodeURIComponent(agentName)}/project-access`)
      .then((data: { projects: string[] }) => setProjectAccess(data.projects))
      .catch(() => setProjectAccess([]))
      .finally(() => setProjectAccessLoading(false))
    // Refresh in case a style was added or removed since the page loaded.
    loadOutputStyles()
    // Same for the project and instruction tables, whose Size columns show the current bytes.
    loadProjectContexts()
    loadInstructions()
    apiGet<AgentConfig>(`/api/agents/${encodeURIComponent(agentName)}/config`)
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
          warmupTimeoutSeconds: warmupTimeoutEditValue(cfg.warmupTimeoutSeconds),
          shortName: cfg.shortName,
          showStats: cfg.showStats,
          prefixMessages: cfg.prefixMessages,
          formattingMode: cfg.formattingMode,
          suppressToolMessages: cfg.suppressToolMessages ?? false,
          telegramSendOnly: cfg.telegramSendOnly,
          provider: cfg.provider ?? 'claude',
          codexSandboxMode: cfg.codexSandboxMode ?? '',
          effort: cfg.effort ?? '',
          jsonSchema: cfg.jsonSchema ?? '',
          agentsJson: cfg.agentsJson ?? '',
          autoMemoryEnabled: cfg.autoMemoryEnabled,
          journalEnabled: cfg.journalEnabled ?? false,
          journalCrossChatEnabled: cfg.journalCrossChatEnabled ?? false,
          chatEnabled: cfg.chatEnabled ?? false,
          hostPort: cfg.hostPort != null ? String(cfg.hostPort) : '',
          tools: cfg.tools.map(t => t.toolName).join(', '),
          projects: cfg.projects,
          mcpEndpoints: cfg.mcpEndpoints,
          networks: cfg.networks.join(', '),
          envRefs: cfg.envRefs.join(', '),
          telegramUsers: cfg.telegramUsers.join(', '),
          telegramGroups: cfg.telegramGroups.join(', '),
          canReceiveChatRequests: cfg.canReceiveChatRequests ?? false,
          requestReceivedMessage: cfg.requestReceivedMessage ?? '',
          mountDockerSock: cfg.mountDockerSock ?? false,
          outputStyle: cfg.outputStyle ?? '',
          ...localModelEditValues(cfg),
          contextWindow: contextWindowEditValue(cfg.contextWindow),
          harnessExtra: cfg.harnessExtra ?? '',
          instructions: cfg.instructions ?? [],
        })
      })
      .catch(() => { setConfigSaveMsg('Failed to load config'); setConfigSaveState('error') })
      .finally(() => setConfigLoading(false))
  }

  function closeConfig() {
    setConfigAgent(null); setConfigData(null); setConfigEdits(null)
    setConfigReprovisionConfirm(false); setConfigSaveState('idle')
    setProjectAccess(null)
  }

  // A project-access change is its own write, outside Save. A refusal is shown on the dialog's
  // status line, and the list is re-read either way so it shows what the server holds.
  async function changeProjectAccess(agentName: string, write: (url: string) => Promise<unknown>) {
    const url = `/api/agents/${encodeURIComponent(agentName)}/project-access`
    try {
      await write(url)
    } catch (e) {
      setConfigSaveState('error'); setConfigSaveMsg(`Project access not changed: ${errorMessage(e)}`)
      setTimeout(() => setConfigSaveState('idle'), 5000)
    }
    try {
      const data = await apiGet<{ projects: string[] }>(url)
      setProjectAccess(data.projects)
    } catch { /* the list shown stays as it was */ }
  }

  function addProjectAccess(agentName: string, project: string) {
    return changeProjectAccess(agentName, url => apiSend(url, 'POST', { project }))
  }

  function removeProjectAccess(agentName: string, project: string) {
    return changeProjectAccess(agentName, url => apiSend(`${url}/${encodeURIComponent(project)}`, 'DELETE'))
  }

  function toggleProjectAccessWildcard(agentName: string, enable: boolean) {
    return enable ? addProjectAccess(agentName, '*') : removeProjectAccess(agentName, '*')
  }

  function saveConfig(agentName: string, andReprovision: boolean) {
    if (!configEdits) return
    // The body is built in agentConfigSave.ts (#460 AC-D2: the same body the unsectioned dialog sent).
    const save = configSaveBody(configEdits, configData?.harnessExtra, configData?.chatEnabled)
    if (!save.ok) { setConfigSaveMsg(save.error); setConfigSaveState('error'); return }
    const projects = save.body.projects as string[]
    const newTelegramUsers = configEdits.telegramUsers.split(',').map(s => s.trim()).filter(Boolean).map(Number).filter(n => !isNaN(n))
    const newTelegramGroups = configEdits.telegramGroups.split(',').map(s => s.trim()).filter(Boolean).map(Number).filter(n => !isNaN(n))
    const prevUsers = configData?.telegramUsers ?? []
    const prevGroups = configData?.telegramGroups ?? []
    const addedUsers = newTelegramUsers.filter(id => !prevUsers.includes(id))
    const removedUsers = prevUsers.filter(id => !newTelegramUsers.includes(id))
    const addedGroups = newTelegramGroups.filter(id => !prevGroups.includes(id))
    const removedGroups = prevGroups.filter(id => !newTelegramGroups.includes(id))
    setConfigSaveState('saving')
    const agentUrl = `/api/agents/${encodeURIComponent(agentName)}`
    const failed = (message: string) => {
      setConfigSaveState('error'); setConfigSaveMsg(message)
      setTimeout(() => setConfigSaveState('idle'), 5000)
    }
    void (async () => {
      try {
        await apiSend(`${agentUrl}/config`, 'PUT', save.body)
      } catch (e) { failed(errorMessage(e)); return }
      // The config is stored from here on. A later step that is refused says so, and is never
      // reported as "Saved" nor as a save that did not happen.
      setConfigData(prev => prev ? { ...prev, projects, harnessExtra: configEdits.harnessExtra === '' ? null : configEdits.harnessExtra, chatEnabled: configEdits.chatEnabled } : prev)
      // Apply telegram user/group diffs via dedicated endpoints (live, no reprovision needed)
      const allowlist = firstFailure(await Promise.allSettled([
        ...addedUsers.map(id => apiSend(`${agentUrl}/telegram-users`, 'POST', { userId: id })),
        ...removedUsers.map(id => apiSend(`${agentUrl}/telegram-users/${id}`, 'DELETE')),
        ...addedGroups.map(id => apiSend(`${agentUrl}/telegram-groups`, 'POST', { groupId: id })),
        ...removedGroups.map(id => apiSend(`${agentUrl}/telegram-groups/${id}`, 'DELETE')),
      ]))
      if (allowlist) {
        failed(`Saved, but ${allowlist.failed} Telegram allowlist change${allowlist.failed === 1 ? '' : 's'} failed: ${allowlist.message}`)
        return
      }
      if (andReprovision) {
        try {
          await apiSend(`${agentUrl}/reprovision`, 'POST')
        } catch (e) { failed(`Saved, but reprovision failed: ${errorMessage(e)}`); return }
      }
      setConfigSaveState('success')
      setConfigSaveMsg(andReprovision ? 'Saved & reprovisioning…' : 'Saved')
      setTimeout(() => setConfigSaveState('idle'), 4000)
    })()
  }

  // ─── Project Context handlers ───────────────────────────────────────────────

  function loadContextDetail(name: string) {
    setContextDetailLoading(prev => ({ ...prev, [name]: true }))
    apiGet<ProjectContextDetail>(`/api/project-contexts/${encodeURIComponent(name)}`)
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
    apiSend<SizedWrite>(`/api/project-contexts/${encodeURIComponent(name)}/versions`, 'POST', { content, reason })
      .then(res => {
        applySizeWarning(setCtxSizeWarnings, name, res?.size)
        setContextSaveState(prev => ({ ...prev, [name]: 'success' }))
        setContextSaveMsg(prev => ({ ...prev, [name]: 'Saved' }))
        setContextReason(prev => ({ ...prev, [name]: '' }))
        loadContextDetail(name)
        apiGet<ProjectContextSummary[]>('/api/project-contexts').then((list: ProjectContextSummary[]) => setProjectContexts(list)).catch(() => {})
        setTimeout(() => setContextSaveState(prev => ({ ...prev, [name]: 'idle' })), 3000)
      })
      .catch((err: unknown) => {
        setContextSaveState(prev => ({ ...prev, [name]: 'error' })); setContextSaveMsg(prev => ({ ...prev, [name]: errorMessage(err) }))
        setTimeout(() => setContextSaveState(prev => ({ ...prev, [name]: 'idle' })), 5000)
      })
  }

  function rollbackContext(name: string, targetVersion: number) {
    setContextSaveState(prev => ({ ...prev, [name]: 'saving' }))
    apiSend<SizedWrite>(`/api/project-contexts/${encodeURIComponent(name)}/rollback/${targetVersion}`, 'POST')
      .then(res => {
        applySizeWarning(setCtxSizeWarnings, name, res?.size)
        setContextSaveState(prev => ({ ...prev, [name]: 'success' }))
        setContextSaveMsg(prev => ({ ...prev, [name]: `Rolled back to v${targetVersion}` }))
        setContextSelectedVersion(prev => ({ ...prev, [name]: null }))
        loadContextDetail(name)
        apiGet<ProjectContextSummary[]>('/api/project-contexts').then((list: ProjectContextSummary[]) => setProjectContexts(list)).catch(() => {})
        setTimeout(() => setContextSaveState(prev => ({ ...prev, [name]: 'idle' })), 3000)
      })
      .catch((err: unknown) => {
        setContextSaveState(prev => ({ ...prev, [name]: 'error' })); setContextSaveMsg(prev => ({ ...prev, [name]: errorMessage(err) }))
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
      timers.after(`ctx-rollback:${key}`, 5000, () => setContextRollbackConfirm(prev => prev[key] ? { ...prev, [key]: false } : prev))
    }
  }

  function handleCtxToggleConfirmClick(name: string) {
    setCtxToggleConfirm(prev => ({ ...prev, [name]: true }))
    timers.after(`ctx-toggle:${name}`, 5000, () => setCtxToggleConfirm(prev => prev[name] ? { ...prev, [name]: false } : prev))
  }

  function toggleCtxActive(name: string, isActive: boolean) {
    setCtxToggleConfirm(prev => ({ ...prev, [name]: false }))
    setCtxToggleState(prev => ({ ...prev, [name]: 'pending' }))
    apiSend(`/api/project-contexts/${encodeURIComponent(name)}/toggle-active`, 'POST', { isActive })
      .then(() => {
        setCtxToggleState(prev => ({ ...prev, [name]: 'success' }))
        setCtxToggleMsg(prev => ({ ...prev, [name]: isActive ? 'Enabled' : 'Disabled' }))
        apiGet<ProjectContextSummary[]>('/api/project-contexts').then((list: ProjectContextSummary[]) => setProjectContexts(list)).catch(() => {})
        setTimeout(() => setCtxToggleState(prev => ({ ...prev, [name]: 'idle' })), 3000)
      })
      .catch((err: unknown) => {
        setCtxToggleState(prev => ({ ...prev, [name]: 'error' }))
        setCtxToggleMsg(prev => ({ ...prev, [name]: errorMessage(err) }))
        setTimeout(() => setCtxToggleState(prev => ({ ...prev, [name]: 'idle' })), 5000)
      })
  }

  function createContext() {
    const name = ctxNewForm.name
    setCtxNewState('saving')
    apiSend<SizedWrite>('/api/project-contexts', 'POST', {
        name: ctxNewForm.name,
        content: ctxNewForm.content,
      })
      .then(res => {
        applySizeWarning(setCtxSizeWarnings, name, res?.size)
        setCtxNewState('success')
        setCtxNewMsg('Created')
        setCtxShowNew(false)
        setCtxNewForm({ name: '', content: '' })
        apiGet<ProjectContextSummary[]>('/api/project-contexts').then((list: ProjectContextSummary[]) => setProjectContexts(list)).catch(() => {})
        setTimeout(() => setCtxNewState('idle'), 3000)
      })
      .catch((err: unknown) => {
        setCtxNewState('error')
        setCtxNewMsg(errorMessage(err))
        setTimeout(() => setCtxNewState('idle'), 5000)
      })
  }

  // ─── Workflow Definitions handlers ────────────────────────────────────────

  function loadWfDefs() {
    setWfDefsLoading(true)
    apiGet<WorkflowDefinitionSummary[]>('/api/workflow-definitions?includeInactive=true')
      .then((list: WorkflowDefinitionSummary[]) => { setWfDefs(list); setWfDefsLoaded(true) })
      .catch(() => {})
      .finally(() => setWfDefsLoading(false))
  }

  function loadWfDefDetail(name: string) {
    setWfDefDetailLoading(prev => ({ ...prev, [name]: true }))
    apiGet<WorkflowDefinitionDetail>(`/api/workflow-definitions/${encodeURIComponent(name)}`)
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
    apiSend(`/api/workflow-definitions/${encodeURIComponent(name)}`, 'PUT', { definition: content, reason })
      .then(() => {
        setWfDefSaveState(prev => ({ ...prev, [name]: 'success' }))
        setWfDefSaveMsg(prev => ({ ...prev, [name]: 'Saved' }))
        loadWfDefDetail(name)
        loadWfDefs()
        setTimeout(() => setWfDefSaveState(prev => ({ ...prev, [name]: 'idle' })), 3000)
      })
      .catch((err: unknown) => {
        setWfDefSaveState(prev => ({ ...prev, [name]: 'error' }))
        setWfDefSaveMsg(prev => ({ ...prev, [name]: errorMessage(err) }))
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
      apiSend(`/api/workflow-definitions/${encodeURIComponent(name)}`, 'PUT', { definition: verDef, reason })
        .then(() => {
          setWfDefSaveState(prev => ({ ...prev, [name]: 'success' }))
          setWfDefSaveMsg(prev => ({ ...prev, [name]: `Rolled back to v${targetVersion}` }))
          setWfDefSelectedVersion(prev => ({ ...prev, [name]: null }))
          loadWfDefDetail(name)
          loadWfDefs()
          setTimeout(() => setWfDefSaveState(prev => ({ ...prev, [name]: 'idle' })), 3000)
        })
        .catch((err: unknown) => {
          setWfDefSaveState(prev => ({ ...prev, [name]: 'error' }))
          setWfDefSaveMsg(prev => ({ ...prev, [name]: errorMessage(err) }))
          setTimeout(() => setWfDefSaveState(prev => ({ ...prev, [name]: 'idle' })), 5000)
        })
    } else {
      setWfDefRollbackConfirm(prev => ({ ...prev, [name]: key }))
      timers.after(`wfdef-rollback:${name}`, 5000, () => setWfDefRollbackConfirm(prev => prev[name] === key ? { ...prev, [name]: null } : prev))
    }
  }

  function handleWfDefToggleConfirmClick(name: string) {
    setWfDefToggleConfirm(prev => ({ ...prev, [name]: true }))
    timers.after(`wfdef-toggle:${name}`, 5000, () => setWfDefToggleConfirm(prev => prev[name] ? { ...prev, [name]: false } : prev))
  }

  function toggleWfDefActive(name: string, isActive: boolean) {
    setWfDefToggleConfirm(prev => ({ ...prev, [name]: false }))
    setWfDefToggleState(prev => ({ ...prev, [name]: 'pending' }))
    apiSend(`/api/workflow-definitions/${encodeURIComponent(name)}/toggle-active`, 'POST', { isActive })
      .then(() => {
        setWfDefToggleState(prev => ({ ...prev, [name]: 'success' }))
        setWfDefToggleMsg(prev => ({ ...prev, [name]: isActive ? 'Enabled' : 'Disabled' }))
        loadWfDefs()
        setTimeout(() => setWfDefToggleState(prev => ({ ...prev, [name]: 'idle' })), 3000)
      })
      .catch((err: unknown) => {
        setWfDefToggleState(prev => ({ ...prev, [name]: 'error' }))
        setWfDefToggleMsg(prev => ({ ...prev, [name]: errorMessage(err) }))
        setTimeout(() => setWfDefToggleState(prev => ({ ...prev, [name]: 'idle' })), 5000)
      })
  }

  function createWfDef() {
    setWfDefNewState('saving')
    apiSend('/api/workflow-definitions', 'POST', {
        name: wfDefNewForm.name,
        namespace: wfDefNewForm.namespace,
        taskQueue: wfDefNewForm.taskQueue,
        definition: wfDefNewForm.definition,
        description: wfDefNewForm.description || undefined,
      })
      .then(() => {
        setWfDefNewState('success')
        setWfDefNewMsg('Created')
        setWfDefShowNew(false)
        setWfDefNewForm({ name: '', namespace: '', taskQueue: '', description: '', definition: '' })
        loadWfDefs()
        setTimeout(() => setWfDefNewState('idle'), 3000)
      })
      .catch((err: unknown) => {
        setWfDefNewState('error')
        setWfDefNewMsg(errorMessage(err))
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
      const events = await apiGet<WorkflowEvent[]>(`/api/workflows/${encodeURIComponent(wf.namespace)}/history/${encodeURIComponent(wf.workflowId)}`)
      const sorted = [...events].reverse()
      setWfHistory(sorted)
      if (sorted.length > 0) setExpandedEvents(new Set([sorted[0].eventId]))
    } catch (e: unknown) {
      setWfHistoryError(errorMessage(e))
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
      apiSend(url, 'POST')
        .then(() => { const label = action === 'cancel' ? 'Cancel sent' : action === 'restart' ? 'Restarted' : 'Terminated'; setWfState(wf, 'success', label) })
        .catch((e: unknown) => { setWfState(wf, 'error', errorMessage(e)) })
        .finally(() => timers.after(`wf:${wfKey(wf)}`, 4000, () => setWfState(wf, 'idle')))
      return
    }
    setWfState(wf, `confirming-${action}`)
    timers.after(`wf:${wfKey(wf)}`, 5000, () => {
      setWfActionStates(prev => {
        const cur = prev[wfKey(wf)]
        if (cur === `confirming-${action}`) return { ...prev, [wfKey(wf)]: 'idle' }
        return prev
      })
    })
  }

  function signalKey(wf: WorkflowSummary): string { return `${wf.namespace}/${wf.workflowId}` }

  function handleWfActionDirect(wf: WorkflowSummary, action: 'cancel' | 'restart' | 'terminate') {
    setWfState(wf, 'pending')
    const url = action === 'restart'
      ? `/api/workflows/${encodeURIComponent(wf.namespace)}/restart/${encodeURIComponent(wf.workflowId)}?runId=${encodeURIComponent(wf.runId)}&terminateExisting=true`
      : `/api/workflows/${encodeURIComponent(wf.namespace)}/${action}/${encodeURIComponent(wf.workflowId)}?runId=${encodeURIComponent(wf.runId)}`
    apiSend(url, 'POST')
      .then(() => { const label = action === 'cancel' ? 'Cancel sent' : action === 'restart' ? 'Restarted' : 'Terminated'; setWfState(wf, 'success', label) })
      .catch((e: unknown) => { setWfState(wf, 'error', errorMessage(e)) })
      .finally(() => timers.after(`wf:${wfKey(wf)}`, 4000, () => setWfState(wf, 'idle')))
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

  function sendSignal(wf: WorkflowSummary, signalName: string, payload: string, comment?: string) {
    const key = signalKey(wf)
    setSignalStates(prev => ({ ...prev, [key]: 'pending' }))
    apiFetch(`/api/workflows/${encodeURIComponent(wf.namespace)}/signal/${encodeURIComponent(wf.workflowId)}`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(buildSignalRequest(wf, signalName, payload, comment)),
    })
      .then(async r => {
        if (r.ok) { setSignalStates(prev => ({ ...prev, [key]: 'success' })); setSignalMsg(prev => ({ ...prev, [key]: 'Signal sent' })); setSentSignals(prev => new Set(prev).add(`${wf.workflowId}::${wf.phase}`)) }
        else { const b = await r.json().catch(() => ({})); setSignalStates(prev => ({ ...prev, [key]: 'error' })); setSignalMsg(prev => ({ ...prev, [key]: signalName === 'request-input' && r.status >= 500 ? 'Answer delivery is uncertain. Refresh this exact run before retrying.' : signalErrorMessage(r.status, b) })) }
      })
      .catch(() => { setSignalStates(prev => ({ ...prev, [key]: 'error' })); setSignalMsg(prev => ({ ...prev, [key]: signalName === 'request-input' ? 'Answer delivery is uncertain. Refresh this exact run before retrying.' : 'Request failed' })) })
      .finally(() => { if (signalName !== 'request-input') setTimeout(() => setSignalStates(prev => ({ ...prev, [key]: 'idle' })), 4000) })
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
      const cfg = await apiGet<AgentConfig>(`/api/agents/${encodeURIComponent(sourceName)}/config`)
      // The config route carries no role; the agent list does.
      setCreateForm(prev => createFormFromConfig(prev, cfg, agents[sourceName]?.role))
    } catch { /* the form keeps what it had */
    } finally {
      setCopyFromLoading(false)
    }
  }

  async function handleCreateSubmit() {
    // The body is built in agentCreate.ts, where a test holds it against the create route.
    const create = createAgentBody(createForm, !!createTemplateName)
    if (!create.ok) { setCreateMsg(create.error); return }
    setCreateState('creating'); setCreateMsg('')
    try {
      const data = await apiSend<{ message?: string } | null>('/api/agents', 'POST', create.body)
      // Apply instruction assignments if any were selected. Non-fatal (they can be set in the
      // config panel), but a refusal is said, not hidden behind "created".
      let instructionsNote = ''
      if (createForm.instructions.length > 0) {
        try {
          await apiSend(`/api/agents/${encodeURIComponent(createForm.name.trim())}/config`, 'PUT', {
            instructions: createForm.instructions.map(i => ({ instructionName: i.name, loadOrder: i.loadOrder })),
          })
        } catch (e) {
          instructionsNote = ` Instructions were not assigned (${errorMessage(e)}); set them in the config panel.`
        }
      }
      setCreateState('success')
      const defaultMsg = createTemplateName
        ? `✓ ${createForm.name.trim()} provisioned`
        : 'Agent created — configure and provision when ready'
      setCreateMsg((data?.message ?? defaultMsg) + instructionsNote)
      // A note about instructions stays up longer: it is the only place it is shown.
      setTimeout(() => { setCreateModalOpen(false); setCreateState('idle'); setCreateMsg(''); setCreateForm(DEFAULT_CREATE_FORM); setCopyFrom(''); setCreateTemplateName(undefined) }, instructionsNote ? 8000 : 3000)
    } catch (e) {
      setCreateState('error'); setCreateMsg(errorMessage(e))
    }
  }

  async function handleNewAgentFromTemplate(templateName: string) {
    try {
      const t = await apiGet<AgentTemplateDetail>(`/api/agent-templates/${encodeURIComponent(templateName)}`)
      const form: CreateForm = {
        ...DEFAULT_CREATE_FORM,
        role: t.role ?? '',
        model: t.model ?? PROVIDER_DEFAULT_MODEL['claude'],
        provider: t.provider ?? 'claude',
        memoryLimitMb: String(t.memoryLimitMb ?? 4096),
        permissionMode: t.permissionMode ?? 'acceptEdits',
        maxTurns: String(t.maxTurns ?? 50),
        workDir: t.workDir ?? '/workspace',
        proactiveIntervalMinutes: String(t.proactiveIntervalMinutes ?? 0),
        groupListenMode: t.groupListenMode ?? 'mention',
        groupDebounceSeconds: String(t.groupDebounceSeconds ?? 15),
        warmupTimeoutSeconds: warmupTimeoutEditValue(t.warmupTimeoutSeconds),
        showStats: t.showStats ?? false,
        prefixMessages: t.prefixMessages ?? false,
        formattingMode: t.formattingMode ?? 0,
        suppressToolMessages: t.suppressToolMessages ?? false,
        telegramSendOnly: t.telegramSendOnly ?? false,
        autoMemoryEnabled: t.autoMemoryEnabled ?? true,
        tools: (t.tools ?? []).map(x => x.toolName).join('\n'),
        projects: (t.projects ?? []).join('\n'),
        networks: (t.networks ?? []).join('\n'),
        envRefs: (t.envRefs ?? []).join('\n'),
        mcpEndpoints: (t.mcpEndpoints ?? []).map(e => ({
          mcpName: e.mcpName, url: e.url, transportType: e.transportType,
        })),
        instructions: (t.instructions ?? []).map(i => ({
          name: i.name, loadOrder: i.loadOrder,
        })),
      }
      setCreateTemplateName(t.displayName ?? templateName)
      setCreateForm(form)
      setCopyFrom('')
      setCreateState('idle')
      setCreateMsg('')
      setCreateModalOpen(true)
    } catch {/* ignore */}
  }

  useEffect(() => {
    const params = new URLSearchParams(window.location.search)
    const deleteAgent = params.get('delete')
    // Longer than a click's five seconds: the page is still loading when this is armed.
    if (deleteAgent) armDeleteConfirm(deleteAgent, 30_000)
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
    const templateParam = params.get('template')
    if (templateParam) handleNewAgentFromTemplate(templateParam)
    // Once, on load: this reads the URL the page was opened with, and must not run again when the
    // handlers it calls are re-created.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  const sorted = Object.values(agents).sort((a, b) => a.agentName.localeCompare(b.agentName))

  return (
  <MemoryIdCacheProvider>
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
        outputStyleCount={outputStyles.length}
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

      <main className={`app-main${activeView === 'memory' ? ' app-main--memory' : activeView === 'chat' ? ' app-main--chat' : ''}`}>
        {activeView === 'agents' && (
          <SetupBanner
            status={setupStatus}
            agentCount={Object.keys(agents).length}
            onConnected={loadSetupStatus}
            onNewAgentFromTemplate={handleNewAgentFromTemplate}
          />
        )}
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

        {activeView === 'chat' && (
          <ChatView initialAgent={viewParam} />
        )}

        {activeView === 'instructions' && (
          <InstructionsView
            state={instr}
            sizeLimit={promptSizeLimits ? promptSizeLimits.instruction.limitBytes : undefined}
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
            sizeLimit={promptSizeLimits ? promptSizeLimits.projectContext.limitBytes : undefined}
            sizeWarnings={ctxSizeWarnings}
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

        {activeView === 'output-styles' && (
          <OutputStylesView
            initialStyle={viewParam}
            // The sidenav badge and the agent config select both read App's copy of the list, so a
            // create or delete on this page has to say so or they stay at the count from mount.
            onStylesChanged={loadOutputStyles}
          />
        )}

        {activeView === 'repositories' && (
          <RepositoriesView />
        )}

        {activeView === 'epic-grants' && (
          <EpicGrantsView />
        )}

        {activeView === 'credentials' && (
          <CredentialsView onSetupConnected={loadSetupStatus} />
        )}

        {activeView === 'memory' && (
          <MemoryView />
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
          currentGate={signalModal.signalName !== 'request-input' || workflows.some(wf =>
            humanInputIdentity(wf) === humanInputIdentity(signalModal.wf))}
          onClose={() => setSignalModal(null)}
          onCommentChange={comment => setSignalModal(m => m ? { ...m, comment } : null)}
          onSend={(payload) => {
            sendSignal(signalModal.wf, signalModal.signalName, payload ?? signalModal.button.payload, signalModal.comment || undefined)
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
          allInstructions={instructions}
          templateName={createTemplateName}
          onCopyFrom={handleCopyFrom}
          onFormChange={patch => setCreateForm(f => ({ ...f, ...patch }))}
          onSubmit={handleCreateSubmit}
          onClose={() => { setCreateModalOpen(false); setCreateTemplateName(undefined) }}
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
          allInstructions={instructions}
          outputStyles={outputStyles}
          projectContexts={projectContexts}
          promptSizeLimits={promptSizeLimits}
          projectAccess={projectAccess}
          projectAccessLoading={projectAccessLoading}
          onEditsChange={patch => setConfigEdits(prev => prev ? { ...prev, ...patch } : prev)}
          onSave={andReprovision => saveConfig(configAgent, andReprovision)}
          onReprovisionConfirmToggle={() => setConfigReprovisionConfirm(c => !c)}
          onProjectAccessAdd={project => addProjectAccess(configAgent, project)}
          onProjectAccessRemove={project => removeProjectAccess(configAgent, project)}
          onProjectAccessToggleWildcard={enable => toggleProjectAccessWildcard(configAgent, enable)}
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
  </MemoryIdCacheProvider>
  )
}
