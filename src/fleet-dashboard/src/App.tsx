import { useEffect, useRef, useState, useCallback, useMemo } from 'react'
import type {
  AgentState,
  WorkflowSummary,
  Alert,
  ProjectContextSummary,
  ProjectContextDetail,
  WsStatus,
  ActiveView,
} from './types'
import { apiGet } from './utils'
import { humanInputIdentity } from './humanInputRequest'
import { DEFAULT_CREATE_FORM } from './agentCreate'
import { parseViewHash } from './activeViews'
import { reconcileAgents } from './agentList'
import { KeyedTimers, afterClose } from './reconnect'
import { useFleetCatalog } from './hooks/useFleetCatalog'
import { useInstructions } from './hooks/useInstructions'
import { useVersionedResource } from './hooks/useVersionedResource'
import { useWorkflowDefinitions } from './hooks/useWorkflowDefinitions'
import { useWorkflowActions } from './hooks/useWorkflowActions'
import { useWorkflowLists } from './hooks/useWorkflowLists'
import { useAgentActions } from './hooks/useAgentActions'
import { useAgentConfig } from './hooks/useAgentConfig'
import { useCreateAgent } from './hooks/useCreateAgent'
import { useLogViewer } from './hooks/useLogViewer'
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

// App holds what every page shares: which page is showing, the agent and workflow lists the socket
// keeps current, and the alerts raised from them. Each page's own state lives in a hook beside it
// (src/hooks). Those hooks are called here, not inside the pages, on purpose: a page is unmounted
// when another is shown, and an unsaved draft or an open row has to still be there on the way back.

type WsMessage =
  | { type: 'workflows'; data: WorkflowSummary[] }
  | (AgentState & { type?: undefined })

const STUCK_TASK_MS = 30 * 60 * 1000

export default function App() {
  const [agents, setAgents] = useState<Record<string, AgentState>>({})
  const [workflows, setWorkflows] = useState<WorkflowSummary[]>([])
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

  // ── Navigation, synced with the URL hash ────────────────────────────────────────────────────
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
  const [navOpen, setNavOpen] = useState(false)

  const [highlightedEntityId, setHighlightedEntityId] = useState<string | null>(null)
  const onClearHighlight = useCallback(() => setHighlightedEntityId(null), [])
  const onHighlight = useCallback((id: string) => setHighlightedEntityId(id), [])

  // ── Alerts ──────────────────────────────────────────────────────────────────────────────────
  const [alerts, setAlerts] = useState<Alert[]>([])
  const alertIdRef = useRef(0)
  const prevAgentsRef = useRef<Record<string, AgentState>>({})
  const taskFirstSeenRef = useRef<Record<string, { task: string; since: number }>>({})
  const stuckAlertedRef = useRef<Record<string, string>>({})
  const seenFailedWorkflowsRef = useRef<Set<string>>(new Set())

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

  // ── The agent list ──────────────────────────────────────────────────────────────────────────
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

  // ── Each page's state (see the note at the top of this file) ────────────────────────────────
  const catalog = useFleetCatalog()
  const wfLists = useWorkflowLists(workflows, setWorkflows)
  const wfActions = useWorkflowActions(timers, catalog.signalRegistry)
  const wfDefs = useWorkflowDefinitions(timers)
  const instr = useInstructions(timers)
  const ctx = useVersionedResource<ProjectContextSummary, ProjectContextDetail>('/api/project-contexts', 'ctx', timers)
  const agentActions = useAgentActions(timers, refreshAgents,
    () => addAlert('agent-stale', 'Reprovision all triggered — agents restarting'))
  // Opening a config re-reads the lists its dialog offers, in case one changed since page load:
  // the output styles, and the project and instruction tables whose Size columns show current bytes.
  const config = useAgentConfig(timers, () => { catalog.loadOutputStyles(); ctx.reload(); instr.reload() })
  const create = useCreateAgent(agents)
  const log = useLogViewer()

  const { pruneSentSignals, getSignalDefs, signalModal } = wfActions
  const { refreshWorkflows, refreshCompleted } = wfLists
  const loadInstructions = instr.reload
  const loadProjectContexts = ctx.reload

  // ── The socket ──────────────────────────────────────────────────────────────────────────────
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
          pruneSentSignals(msg.data)
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
  }, [refreshAgents, pruneSentSignals])

  useEffect(() => {
    refreshAgents()
    refreshWorkflows()
    loadInstructions()
    loadProjectContexts()
    refreshCompleted()
  }, [refreshAgents, refreshWorkflows, loadInstructions, loadProjectContexts, refreshCompleted])

  useEffect(() => {
    unmounted.current = false
    connect()
    return () => {
      unmounted.current = true
      if (reconnectTimer.current) clearTimeout(reconnectTimer.current)
      wsRef.current?.close()
    }
  }, [connect])

  // Workflow definitions and schedules are read the first time a page that shows them opens.
  useEffect(() => {
    if (activeView === 'wf-definitions' && !wfDefs.loaded) wfDefs.load()
    if ((activeView === 'schedules' || activeView === 'namespaces') && !catalog.schedulesLoaded) catalog.loadSchedules()
    // Runs when the view changes; the two `loaded` flags only guard the calls.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [activeView])

  const [tick, setTick] = useState(0)
  useEffect(() => {
    const id = setInterval(() => setTick(t => t + 1), 5000)
    return () => clearInterval(id)
  }, [])
  void tick

  // ── What raises an alert ────────────────────────────────────────────────────────────────────
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

  // ── Links that open the page already doing something ────────────────────────────────────────
  useEffect(() => {
    const params = new URLSearchParams(window.location.search)
    const deleteAgent = params.get('delete')
    // Longer than a click's five seconds: the page is still loading when this is armed.
    if (deleteAgent) agentActions.armDelete(deleteAgent, 30_000)
    const hasCreate = params.has('create') || params.has('name')
    if (hasCreate) {
      const form = { ...DEFAULT_CREATE_FORM }
      const p = (k: string) => params.get(k) ?? ''
      if (p('name')) form.name = p('name')
      if (p('displayName')) form.displayName = p('displayName')
      if (p('role')) form.role = p('role')
      if (p('model')) form.model = p('model')
      if (p('memory')) form.memoryLimitMb = p('memory')
      create.open(form)
    }
    const templateParam = params.get('template')
    if (templateParam) void create.openFromTemplate(templateParam)
    // Once, on load: this reads the URL the page was opened with, and must not run again when the
    // handlers it calls are re-created.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

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
  const sorted = Object.values(agents).sort((a, b) => a.agentName.localeCompare(b.agentName))
  const instructions = instr.items
  const projectContexts = ctx.items

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
          if (view === 'wf-definitions' && !wfDefs.loaded) wfDefs.load()
        }}
        agentCount={sorted.length}
        activeWorkflowCount={workflows.length}
        attentionWorkflowCount={workflows.filter(wf => getSignalDefs(wf).length > 0).length}
        instructionCount={instructions.length}
        projectContextCount={projectContexts.length}
        outputStyleCount={catalog.outputStyles.length}
        wfDefinitionCount={wfDefs.definitions.length}
        namespaceCount={catalog.namespaces.length}
        unreadAlertCount={unreadAlerts.length}
        reprovisionAllState={agentActions.reprovisionAllState}
        reprovisionAllMsg={agentActions.reprovisionAllMsg}
        onReprovisionAll={agentActions.reprovisionAll}
        onReprovisionAllConfirm={agentActions.reprovisionAllConfirm}
        onReprovisionAllCancel={agentActions.reprovisionAllCancel}
        onNewAgent={() => create.open()}
        navOpen={navOpen}
        onNavClose={() => setNavOpen(false)}
      />

      <main className={`app-main${activeView === 'memory' ? ' app-main--memory' : activeView === 'chat' ? ' app-main--chat' : ''}`}>
        {activeView === 'agents' && (
          <SetupBanner
            status={catalog.setupStatus}
            agentCount={Object.keys(agents).length}
            onConnected={catalog.loadSetupStatus}
            onNewAgentFromTemplate={create.openFromTemplate}
          />
        )}
        {activeView === 'agents' && (
          <AgentsView
            sorted={sorted}
            actions={agentActions}
            configAgent={config.agent}
            logViewer={log.agent}
            onViewLogs={log.toggle}
            onEditConfig={config.open}
            workflowByAgent={workflowByAgent}
            workflows={workflows}
            agentByWorkflow={agentByWorkflow}
            workflowActions={wfActions}
            highlightedEntityId={highlightedEntityId}
            onClearHighlight={onClearHighlight}
          />
        )}

        {activeView === 'workflows' && (
          <WorkflowsView
            workflows={workflows}
            lists={wfLists}
            actions={wfActions}
            agentByWorkflow={agentByWorkflow}
            highlightedEntityId={highlightedEntityId}
            onClearHighlight={onClearHighlight}
          />
        )}

        {activeView === 'chat' && (
          <ChatView initialAgent={viewParam} />
        )}

        {activeView === 'instructions' && (
          <InstructionsView
            state={instr}
            sizeLimit={catalog.promptSizeLimits ? catalog.promptSizeLimits.instruction.limitBytes : undefined}
          />
        )}

        {activeView === 'project-contexts' && (
          <ProjectContextsView
            state={ctx}
            sizeLimit={catalog.promptSizeLimits ? catalog.promptSizeLimits.projectContext.limitBytes : undefined}
          />
        )}

        {activeView === 'wf-definitions' && (
          <WorkflowDefinitionsView state={wfDefs} namespaces={catalog.namespaces} />
        )}

        {activeView === 'schedules' && (
          <SchedulesView
            schedules={catalog.schedules}
            loading={catalog.schedulesLoading}
            workflowTypes={catalog.workflowTypes}
            namespaces={catalog.namespaces}
            onRefresh={catalog.loadSchedules}
          />
        )}

        {activeView === 'namespaces' && (
          <NamespacesView
            namespaces={catalog.namespaces}
            workflows={workflows}
            schedules={catalog.schedules}
            workflowTypes={catalog.workflowTypes}
            schedulesLoading={catalog.schedulesLoading}
          />
        )}

        {activeView === 'output-styles' && (
          <OutputStylesView
            initialStyle={viewParam}
            // The sidenav badge and the agent config select both read the catalog's copy of the
            // list, so a create or delete on this page has to say so or they stay at the count
            // from page load.
            onStylesChanged={catalog.loadOutputStyles}
          />
        )}

        {activeView === 'repositories' && (
          <RepositoriesView />
        )}

        {activeView === 'epic-grants' && (
          <EpicGrantsView />
        )}

        {activeView === 'credentials' && (
          <CredentialsView onSetupConnected={catalog.loadSetupStatus} />
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
      {wfDefs.editorDef !== undefined && (
        <div className="wfe-overlay">
          <WorkflowDefinitionEditor
            defName={wfDefs.editorDef}
            onBack={wfDefs.closeEditor}
            onSaved={() => { wfDefs.closeEditor(); wfDefs.load() }}
            workflowTypes={catalog.workflowTypes}
            namespaces={catalog.namespaces}
          />
        </div>
      )}

      <AppFooter wsStatus={wsStatus} lastUpdated={lastUpdated} />

      {/* Overlay modals */}
      {log.agent && <LogViewer agentName={log.agent} log={log} />}

      {signalModal && (
        <SignalModal
          modal={signalModal}
          currentGate={signalModal.signalName !== 'request-input' || workflows.some(wf =>
            humanInputIdentity(wf) === humanInputIdentity(signalModal.wf))}
          onClose={() => wfActions.setSignalModal(null)}
          onCommentChange={comment => wfActions.setSignalModal(m => m ? { ...m, comment } : null)}
          onSend={(payload) => {
            wfActions.sendSignal(signalModal.wf, signalModal.signalName, payload ?? signalModal.button.payload, signalModal.comment || undefined)
            wfActions.setSignalModal(null)
          }}
        />
      )}

      {create.isOpen && (
        <CreateAgentModal create={create} agentNames={sorted.map(a => a.agentName)} allInstructions={instructions} />
      )}

      {config.agent && (
        // This dialog keeps its flat props: agentConfigSections.test.ts renders it with them.
        <AgentConfigModal
          agentName={config.agent}
          configData={config.data}
          configEdits={config.edits}
          configSaveState={config.saveState}
          configSaveMsg={config.saveMsg}
          configLoading={config.loading}
          configReprovisionConfirm={config.reprovisionConfirm}
          allInstructions={instructions}
          outputStyles={catalog.outputStyles}
          projectContexts={projectContexts}
          promptSizeLimits={catalog.promptSizeLimits}
          projectAccess={config.projectAccess}
          projectAccessLoading={config.projectAccessLoading}
          onEditsChange={config.change}
          onSave={config.save}
          onReprovisionConfirmToggle={config.toggleReprovisionConfirm}
          onProjectAccessAdd={config.addProjectAccess}
          onProjectAccessRemove={config.removeProjectAccess}
          onProjectAccessToggleWildcard={config.toggleProjectAccessWildcard}
          onClose={config.close}
        />
      )}

      {wfLists.startOpen && (
        <StartWorkflowModal
          workflowTypes={catalog.workflowTypes}
          agents={Object.values(agents)}
          onClose={() => wfLists.setStartOpen(false)}
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
