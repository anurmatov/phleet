import { useRef, useEffect } from 'react'
import type { LogViewerState } from '../hooks/useLogViewer'
import Icon from './Icon'

interface LogViewerProps {
  agentName: string
  /** The stream, its lines and how they are shown (hooks/useLogViewer.ts). */
  log: LogViewerState
}

export default function LogViewer({ agentName, log }: LogViewerProps) {
  const {
    lines: logLines,
    filter: logFilter,
    paused: logPaused,
    autoScroll: logAutoScroll,
    setFilter: onSetFilter,
    togglePause: onTogglePause,
    toggleAutoScroll: onToggleAutoScroll,
    clear: onClear,
    close: onClose,
    applyFilter: applyLogFilter,
  } = log
  const logBottomRef = useRef<HTMLDivElement | null>(null)

  useEffect(() => {
    if (logAutoScroll && logBottomRef.current) {
      logBottomRef.current.scrollIntoView({ behavior: 'instant' })
    }
  }, [logLines, logAutoScroll])

  const filtered = applyLogFilter(logLines)

  return (
    <div className="log-fullscreen-overlay">
      <div className="log-panel-header">
        <span className="log-panel-title">
          <span className="log-panel-agent">{agentName}</span>
          <span className="log-panel-sub">live logs</span>
        </span>
        <div className="log-panel-controls">
          <div className="seg log-filter-group" role="group" aria-label="Level">
            {(['all', 'error', 'warn', 'info'] as const).map(f => (
              <button
                key={f}
                className={`seg-btn log-filter-btn${logFilter === f ? ' active' : ''}${f !== 'all' ? ` log-filter-${f}` : ''}`}
                onClick={() => onSetFilter(f)}
              >{f}</button>
            ))}
          </div>
          <button
            className={`icon-btn log-ctrl-btn${logPaused ? ' active' : ''}`}
            title={logPaused ? 'Resume' : 'Pause'} aria-label={logPaused ? 'Resume' : 'Pause'}
            onClick={onTogglePause}
          ><Icon name={logPaused ? 'play' : 'pause'} /></button>
          <button
            className={`icon-btn log-ctrl-btn${logAutoScroll ? ' active' : ''}`}
            title={logAutoScroll ? 'Disable auto-scroll' : 'Enable auto-scroll'}
            aria-label={logAutoScroll ? 'Disable auto-scroll' : 'Enable auto-scroll'}
            onClick={onToggleAutoScroll}
          ><Icon name="follow" /></button>
          <button className="icon-btn log-ctrl-btn" title="Clear" aria-label="Clear" onClick={onClear}>
            <Icon name="trash" />
          </button>
          <button className="icon-btn log-close-btn" title="Close" aria-label="Close" onClick={onClose}>
            <Icon name="close" />
          </button>
        </div>
      </div>
      <div className="log-body">
        {logLines.length === 0 && (
          <div className="log-empty">Connecting to {agentName}…</div>
        )}
        {filtered.map((line, i) => {
          const spaceIdx = line.indexOf(' ')
          let ts = ''
          let msg = line
          if (spaceIdx > 0) {
            const candidate = line.slice(0, spaceIdx)
            const d = new Date(candidate)
            if (!isNaN(d.getTime())) {
              const hh = String(d.getHours()).padStart(2, '0')
              const mm = String(d.getMinutes()).padStart(2, '0')
              const ss = String(d.getSeconds()).padStart(2, '0')
              const ms = String(d.getMilliseconds()).padStart(3, '0')
              ts = `${hh}:${mm}:${ss}.${ms}`
              msg = line.slice(spaceIdx + 1)
            }
          }
          const low = msg.toLowerCase()
          const isError = low.includes('error') || low.includes('exception')
          const isWarn  = low.includes('warn')
          return (
            <div
              key={i}
              className={`log-line${isError ? ' log-line-error' : isWarn ? ' log-line-warn' : ''}`}
            >
              {ts && <span className="log-ts">{ts} </span>}
              {msg}
            </div>
          )
        })}
        <div ref={logBottomRef} />
      </div>
      {logPaused && (
        <div className="log-paused-banner">Paused — new lines are kept and shown when you resume</div>
      )}
    </div>
  )
}
