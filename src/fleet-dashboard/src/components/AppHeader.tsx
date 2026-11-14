import { useState, useMemo, useRef, useEffect } from 'react'
import type { WsStatus, AgentState, WorkflowSummary } from '../types'
import { statusDot } from '../utils'
import { applyTheme, nextTheme, readTheme, themeLabel, type ThemeChoice } from '../theme'
import Icon from './Icon'

interface SearchResult {
  type: 'agent' | 'workflow'
  id: string
  label: string
  sub: string
  status?: string
}

interface AppHeaderProps {
  wsStatus: WsStatus
  unreadAlertCount: number
  onAlertsClick: () => void
  /** The page on screen, as the navigation names it. Shown where the sidebar is not. */
  agents: Record<string, AgentState>
  workflows: WorkflowSummary[]
  onNavigate: (view: 'agents' | 'workflows') => void
  onHighlight: (entityId: string) => void
}

export default function AppHeader({
  wsStatus, unreadAlertCount, onAlertsClick,
  agents, workflows, onNavigate, onHighlight,
}: AppHeaderProps) {
  const [theme, setTheme] = useState<ThemeChoice>(() => readTheme(window.localStorage))
  function cycleTheme() {
    const next = nextTheme(theme)
    applyTheme(next, document.documentElement, window.localStorage)
    setTheme(next)
  }

  const [query, setQuery] = useState('')
  const [open, setOpen] = useState(false)
  const [mobileExpanded, setMobileExpanded] = useState(false)
  const inputRef = useRef<HTMLInputElement>(null)
  const containerRef = useRef<HTMLDivElement>(null)

  // Cmd+K / Ctrl+K to focus
  useEffect(() => {
    function handler(e: KeyboardEvent) {
      if ((e.metaKey || e.ctrlKey) && e.key === 'k') {
        e.preventDefault()
        setMobileExpanded(true)
        inputRef.current?.focus()
        inputRef.current?.select()
      }
    }
    window.addEventListener('keydown', handler)
    return () => window.removeEventListener('keydown', handler)
  }, [])

  // Close dropdown on outside click
  useEffect(() => {
    function handler(e: MouseEvent) {
      if (containerRef.current && !containerRef.current.contains(e.target as Node)) {
        setOpen(false)
      }
    }
    document.addEventListener('mousedown', handler)
    return () => document.removeEventListener('mousedown', handler)
  }, [])

  const results = useMemo<SearchResult[]>(() => {
    const q = query.trim().toLowerCase()
    if (!q || q === '#') return []

    const agentResults: SearchResult[] = Object.values(agents)
      .filter(a =>
        a.agentName.toLowerCase().includes(q) ||
        (a.displayName?.toLowerCase().includes(q) ?? false) ||
        (a.shortName?.toLowerCase().includes(q) ?? false))
      .slice(0, 5)
      .map(a => ({
        type: 'agent',
        id: a.agentName,
        label: a.displayName || a.agentName,
        sub: a.agentName,
        status: a.effectiveStatus,
      }))

    const wfResults: SearchResult[] = workflows
      .filter(w =>
        w.workflowType.toLowerCase().includes(q) ||
        w.workflowId.toLowerCase().includes(q) ||
        (w.issueNumber != null && (`#${w.issueNumber}`.includes(q) || `${w.issueNumber}`.includes(q))) ||
        (w.prNumber != null && (`#${w.prNumber}`.includes(q) || `${w.prNumber}`.includes(q))))
      .slice(0, 10)
      .map(w => ({
        type: 'workflow',
        id: w.workflowId,
        label: w.workflowType,
        sub: w.issueNumber ? `#${w.issueNumber}` : w.workflowId.slice(0, 30),
      }))

    return [...agentResults, ...wfResults]
  }, [query, agents, workflows])

  function handleSelect(r: SearchResult) {
    setOpen(false)
    setQuery('')
    setMobileExpanded(false)
    onNavigate(r.type === 'agent' ? 'agents' : 'workflows')
    // Defer highlight so the view has time to render
    setTimeout(() => onHighlight(r.id), 50)
  }

  function handleInputChange(v: string) {
    setQuery(v)
    setOpen(v.trim().length > 0 && v.trim() !== '#')
  }

  function handleKeyDown(e: React.KeyboardEvent) {
    if (e.key === 'Escape') {
      setOpen(false)
      setQuery('')
      setMobileExpanded(false)
      inputRef.current?.blur()
    }
  }

  const searchBox = (
    <div className="header-search-wrap" ref={containerRef}>
      <Icon name="search" size={16} className="header-search-icon" />
      <input
        ref={inputRef}
        className="header-search-input"
        placeholder="Search agents and workflows"
        value={query}
        onChange={e => handleInputChange(e.target.value)}
        onFocus={() => { if (query.trim() && query.trim() !== '#') setOpen(true) }}
        onKeyDown={handleKeyDown}
        aria-label="Global search"
        autoComplete="off"
        spellCheck={false}
      />
      {open && results.length > 0 && (
        <div className="header-search-dropdown">
          {results.map(r => (
            <div
              key={`${r.type}:${r.id}`}
              className="header-search-result"
              onMouseDown={() => handleSelect(r)}
            >
              {r.type === 'agent' ? (
                <>
                  <span className="hsr-dot">{statusDot(r.status ?? 'idle')}</span>
                  <span className="hsr-label">{r.label}</span>
                  <span className="hsr-sub hsr-agent-tag">agent</span>
                </>
              ) : (
                <>
                  <span className="hsr-wf-icon"><Icon name="workflows" size={14} /></span>
                  <span className="hsr-label">{r.label}</span>
                  <span className="hsr-sub">{r.sub}</span>
                </>
              )}
            </div>
          ))}
        </div>
      )}
      {open && results.length === 0 && query.trim().length > 0 && (
        <div className="header-search-dropdown">
          <div className="hsr-empty">No results</div>
        </div>
      )}
    </div>
  )

  return (
    <header className="app-header">
      {/* On a phone there is no sidebar to carry the name; each page says its own title. */}
      <span className="app-header-brand">
        <span className="brand-mark" aria-hidden="true" />
        <span className="brand-name">fleet</span>
      </span>

      <div className="header-search-desktop">
        {searchBox}
        <kbd className="header-search-kbd" aria-hidden="true">⌘K</kbd>
      </div>

      <div className="app-header-right">
        {wsStatus !== 'connected' && (
          <span className={`conn-chip conn-${wsStatus}`} role="status">
            <span className="conn-dot" aria-hidden="true" />
            {wsStatus === 'connecting' ? 'Connecting…' : 'Disconnected'}
          </span>
        )}

        <button
          className="icon-btn header-search-mobile-btn"
          onClick={() => { setMobileExpanded(e => !e); if (!mobileExpanded) setTimeout(() => inputRef.current?.focus(), 50) }}
          title="Search"
          aria-label="Search"
        ><Icon name="search" /></button>

        <button className="icon-btn" onClick={cycleTheme} title={themeLabel(theme)} aria-label={themeLabel(theme)}>
          <Icon name={theme === 'system' ? 'system' : theme === 'light' ? 'sun' : 'moon'} />
        </button>

        <button
          className={`icon-btn app-alerts-btn${unreadAlertCount > 0 ? ' has-alerts' : ''}`}
          onClick={onAlertsClick}
          title={unreadAlertCount > 0 ? `${unreadAlertCount} unread alert${unreadAlertCount > 1 ? 's' : ''}` : 'Alerts'}
          aria-label={unreadAlertCount > 0 ? `${unreadAlertCount} unread alert${unreadAlertCount > 1 ? 's' : ''}` : 'Alerts'}
        >
          <Icon name="alerts" />
          {unreadAlertCount > 0 && <span className="app-alerts-badge">{unreadAlertCount}</span>}
        </button>
      </div>

      {mobileExpanded && (
        <div className="header-search-mobile-overlay">
          {searchBox}
        </div>
      )}
    </header>
  )
}
