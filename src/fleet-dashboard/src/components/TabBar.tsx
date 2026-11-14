import type { ActiveView } from '../types'
import { TAB_VIEWS, navItem } from '../navigation'
import Icon, { type IconName } from './Icon'

interface TabBarProps {
  activeView: ActiveView
  onNavigate: (view: ActiveView) => void
  attentionWorkflowCount: number
  unreadAlertCount: number
  moreOpen: boolean
  onMore: () => void
}

/**
 * A phone's way around: the three pages opened most, and More for the rest.
 *
 * Along the bottom, where a thumb is. Hidden on a wide screen, where the sidebar is always there.
 * More is marked as the current place when the page on screen is one of those it holds, so there
 * is always one tab that says where you are.
 */
export default function TabBar({
  activeView, onNavigate, attentionWorkflowCount, unreadAlertCount, moreOpen, onMore,
}: TabBarProps) {
  const inMore = !TAB_VIEWS.includes(activeView)

  return (
    <nav className="tabbar" aria-label="Main pages">
      {TAB_VIEWS.map(view => {
        const item = navItem(view)
        const current = activeView === view && !moreOpen
        return (
          <button
            key={view}
            className={`tabbar-item${current ? ' active' : ''}`}
            aria-current={current ? 'page' : undefined}
            onClick={() => onNavigate(view)}
          >
            <span className="tabbar-icon">
              <Icon name={item.icon as IconName} size={22} />
              {view === 'workflows' && attentionWorkflowCount > 0 && (
                <span className="tabbar-badge" title="Waiting for you">{attentionWorkflowCount}</span>
              )}
            </span>
            <span className="tabbar-label">{item.label}</span>
          </button>
        )
      })}
      <button
        className={`tabbar-item${moreOpen || inMore ? ' active' : ''}`}
        aria-expanded={moreOpen}
        onClick={onMore}
      >
        <span className="tabbar-icon">
          <Icon name="menu" size={22} />
          {unreadAlertCount > 0 && <span className="tabbar-badge" title="Unread alerts">{unreadAlertCount}</span>}
        </span>
        <span className="tabbar-label">{inMore && !moreOpen ? navItem(activeView).label : 'More'}</span>
      </button>
    </nav>
  )
}
