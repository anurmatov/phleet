import type { ActiveView, ReprovisionAllState } from '../types'

interface SidenavProps {
  activeView: ActiveView
  onNavigate: (view: ActiveView) => void
  agentCount: number
  activeWorkflowCount: number
  attentionWorkflowCount: number
  instructionCount: number
  projectContextCount: number
  wfDefinitionCount: number
  namespaceCount: number
  unreadAlertCount: number
  reprovisionAllState: ReprovisionAllState
  reprovisionAllMsg: string
  onReprovisionAll: () => void
  onReprovisionAllConfirm: () => void
  onReprovisionAllCancel: () => void
  onNewAgent: () => void
  navOpen: boolean
  onNavClose: () => void
}

export default function Sidenav({
  activeView, onNavigate,
  agentCount, activeWorkflowCount, attentionWorkflowCount, instructionCount, projectContextCount, wfDefinitionCount, namespaceCount, unreadAlertCount,
  reprovisionAllState, reprovisionAllMsg,
  onReprovisionAll, onReprovisionAllConfirm, onReprovisionAllCancel,
  onNewAgent,
  navOpen, onNavClose,
}: SidenavProps) {
  return (
    <>
      {/* Mobile overlay */}
      {navOpen && (
        <div className="sidenav-overlay" onClick={onNavClose} />
      )}

      <nav className={`sidenav${navOpen ? ' sidenav-open' : ''}`}>
        <div className="sidenav-section-label">Fleet</div>

        <button
          className={`sidenav-item${activeView === 'agents' ? ' active' : ''}`}
          onClick={() => { onNavigate('agents'); onNavClose() }}
        >
          <span className="sidenav-item-label">Agents</span>
          <span className="sidenav-badge">{agentCount}</span>
        </button>

        <button
          className={`sidenav-item${activeView === 'workflows' ? ' active' : ''}`}
          onClick={() => { onNavigate('workflows'); onNavClose() }}
        >
          <span className="sidenav-item-label">Workflows</span>
          {attentionWorkflowCount > 0
            ? <span className="sidenav-badge sidenav-badge-attention">{attentionWorkflowCount}</span>
            : <span className="sidenav-badge">{activeWorkflowCount}</span>
          }
        </button>

        <div className="sidenav-section-label" style={{ marginTop: 16 }}>Agent Config</div>

        <button
          className={`sidenav-item${activeView === 'instructions' ? ' active' : ''}`}
          onClick={() => { onNavigate('instructions'); onNavClose() }}
        >
          <span className="sidenav-item-label">Instructions</span>
          <span className="sidenav-badge">{instructionCount}</span>
        </button>

        <button
          className={`sidenav-item${activeView === 'project-contexts' ? ' active' : ''}`}
          onClick={() => { onNavigate('project-contexts'); onNavClose() }}
        >
          <span className="sidenav-item-label">Project Contexts</span>
          <span className="sidenav-badge">{projectContextCount}</span>
        </button>

        <button
          className={`sidenav-item${activeView === 'repositories' ? ' active' : ''}`}
          onClick={() => { onNavigate('repositories'); onNavClose() }}
        >
          <span className="sidenav-item-label">Repositories</span>
        </button>

        <button
          className={`sidenav-item${activeView === 'credentials' ? ' active' : ''}`}
          onClick={() => { onNavigate('credentials'); onNavClose() }}
        >
          <span className="sidenav-item-label">Credentials</span>
        </button>

        <button
          className={`sidenav-item${activeView === 'memory' ? ' active' : ''}`}
          onClick={() => { onNavigate('memory'); onNavClose() }}
        >
          <span className="sidenav-item-label">Memory</span>
        </button>

        <div className="sidenav-section-label" style={{ marginTop: 16 }}>Workflows</div>

        <button
          className={`sidenav-item${activeView === 'wf-definitions' ? ' active' : ''}`}
          onClick={() => { onNavigate('wf-definitions'); onNavClose() }}
        >
          <span className="sidenav-item-label">WF Definitions</span>
          <span className="sidenav-badge">{wfDefinitionCount}</span>
        </button>

        <button
          className={`sidenav-item${activeView === 'schedules' ? ' active' : ''}`}
          onClick={() => { onNavigate('schedules'); onNavClose() }}
        >
          <span className="sidenav-item-label">Schedules</span>
        </button>

        <button
          className={`sidenav-item${activeView === 'namespaces' ? ' active' : ''}`}
          onClick={() => { onNavigate('namespaces'); onNavClose() }}
        >
          <span className="sidenav-item-label">Namespaces</span>
          <span className="sidenav-badge">{namespaceCount}</span>
        </button>

        <button
          className={`sidenav-item${activeView === 'alerts' ? ' active' : ''}`}
          onClick={() => { onNavigate('alerts'); onNavClose() }}
        >
          <span className="sidenav-item-label">🔔 Alerts</span>
          {unreadAlertCount > 0 && <span className="sidenav-badge sidenav-badge-alert">{unreadAlertCount}</span>}
        </button>

        <div className="sidenav-divider" />
        <div className="sidenav-section-label">Actions</div>

        {/* Reprovision All — inline two-step */}
        {reprovisionAllState === 'idle' && (
          <button className="sidenav-action" onClick={onReprovisionAll}>
            ⟳ Reprovision All
          </button>
        )}
        {reprovisionAllState === 'confirming' && (
          <div className="sidenav-action-confirm">
            <span className="sidenav-confirm-text">⚠ Confirm?</span>
            <button className="sidenav-confirm-yes" onClick={onReprovisionAllConfirm}>Yes</button>
            <button className="sidenav-confirm-no" onClick={onReprovisionAllCancel}>No</button>
          </div>
        )}
        {reprovisionAllState === 'running' && (
          <div className="sidenav-action running">⟳ Reprovisioning…</div>
        )}
        {(reprovisionAllState === 'success' || reprovisionAllState === 'error') && (
          <div className={`sidenav-action ${reprovisionAllState}`}>
            {reprovisionAllMsg || (reprovisionAllState === 'success' ? 'Done' : 'Error')}
          </div>
        )}

        <button className="sidenav-action new-agent" onClick={onNewAgent}>
          + New Agent
        </button>
      </nav>
    </>
  )
}
