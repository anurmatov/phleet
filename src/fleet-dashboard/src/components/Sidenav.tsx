import type { ActiveView, ReprovisionAllState, WsStatus } from '../types'
import { NAV_GROUPS } from '../navigation'
import Icon, { type IconName } from './Icon'

declare const __APP_VERSION__: string

interface SidenavProps {
  activeView: ActiveView
  onNavigate: (view: ActiveView) => void
  /** What each page's count is, by view. A page with no entry shows none. */
  counts: Partial<Record<ActiveView, number>>
  /** Runs waiting for a person. Shown on Workflows in place of its count when there are any. */
  attentionWorkflowCount: number
  unreadAlertCount: number
  reprovisionAllState: ReprovisionAllState
  reprovisionAllMsg: string
  onReprovisionAll: () => void
  onReprovisionAllConfirm: () => void
  onReprovisionAllCancel: () => void
  onNewAgent: () => void
  wsStatus: WsStatus
  lastUpdated: string
  navOpen: boolean
  onNavClose: () => void
}

/**
 * Where everything is. A sidebar beside the page on a wide screen; on a phone the same list,
 * opened from the tab bar's More and closed by choosing something.
 *
 * The pages are grouped by what they are for rather than listed flat: the fleet as it runs,
 * what agents are told, what runs on its own, and the settings.
 */
export default function Sidenav({
  activeView, onNavigate, counts, attentionWorkflowCount, unreadAlertCount,
  reprovisionAllState, reprovisionAllMsg,
  onReprovisionAll, onReprovisionAllConfirm, onReprovisionAllCancel,
  onNewAgent, wsStatus, lastUpdated, navOpen, onNavClose,
}: SidenavProps) {
  function badge(view: ActiveView) {
    if (view === 'workflows' && attentionWorkflowCount > 0) {
      return <span className="sidenav-badge sidenav-badge-attention" title="Waiting for you">{attentionWorkflowCount}</span>
    }
    if (view === 'alerts') {
      return unreadAlertCount > 0
        ? <span className="sidenav-badge sidenav-badge-alert" title="Unread alerts">{unreadAlertCount}</span>
        : null
    }
    const count = counts[view]
    // A zero is not news, and for a list not read yet it would be wrong.
    return count ? <span className="sidenav-badge">{count}</span> : null
  }

  return (
    <>
      {navOpen && <div className="sidenav-overlay" onClick={onNavClose} />}

      <nav className={`sidenav${navOpen ? ' sidenav-open' : ''}`} aria-label="Pages">
        <div className="sidenav-brand">
          <span className="brand-mark" aria-hidden="true" />
          <span className="brand-name">fleet</span>
          <button className="icon-btn sidenav-close" onClick={onNavClose} aria-label="Close" title="Close">
            <Icon name="close" />
          </button>
        </div>

        <div className="sidenav-scroll">
          {NAV_GROUPS.map(group => (
            <div className="sidenav-group" key={group.label}>
              <div className="sidenav-section-label">{group.label}</div>
              {group.items.map(item => (
                <button
                  key={item.view}
                  className={`sidenav-item${activeView === item.view ? ' active' : ''}`}
                  aria-current={activeView === item.view ? 'page' : undefined}
                  onClick={() => { onNavigate(item.view); onNavClose() }}
                >
                  <Icon name={item.icon as IconName} />
                  <span className="sidenav-item-label">{item.label}</span>
                  {badge(item.view)}
                </button>
              ))}
            </div>
          ))}
        </div>

        <div className="sidenav-foot">
          <button className="btn btn-primary sidenav-new" onClick={() => { onNewAgent(); onNavClose() }}>
            <Icon name="plus" size={16} />
            New agent
          </button>

          {/* Reprovisioning every agent interrupts all of them, so it asks first, in place. */}
          {reprovisionAllState === 'idle' && (
            <button className="btn btn-quiet sidenav-action" onClick={onReprovisionAll}>
              <Icon name="reprovision" size={16} />
              Reprovision all
            </button>
          )}
          {reprovisionAllState === 'confirming' && (
            <div className="sidenav-action-confirm">
              <span className="sidenav-confirm-text">Reprovision every agent?</span>
              <button className="btn btn-danger btn-s sidenav-confirm-yes" onClick={onReprovisionAllConfirm}>Yes</button>
              <button className="btn btn-s sidenav-confirm-no" onClick={onReprovisionAllCancel}>No</button>
            </div>
          )}
          {reprovisionAllState === 'running' && (
            <div className="sidenav-action running">Reprovisioning…</div>
          )}
          {(reprovisionAllState === 'success' || reprovisionAllState === 'error') && (
            <div className={`sidenav-action ${reprovisionAllState}`}>
              {reprovisionAllMsg || (reprovisionAllState === 'success' ? 'Done' : 'Error')}
            </div>
          )}

          <div className={`sidenav-status conn-${wsStatus}`}>
            <span className="conn-dot" aria-hidden="true" />
            <span className="sidenav-status-text">
              {wsStatus === 'connected' ? 'Live' : wsStatus === 'connecting' ? 'Connecting…' : 'Disconnected'}
              {lastUpdated && wsStatus === 'connected' ? ` · ${lastUpdated}` : ''}
            </span>
            <span className="sidenav-version">v{__APP_VERSION__}</span>
          </div>
        </div>
      </nav>
    </>
  )
}
