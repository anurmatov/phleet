import { useRef, useEffect } from 'react'

interface LogViewerProps {
  agentName: string
  logLines: string[]
  logFilter: 'all' | 'error' | 'warn' | 'info'
  logPaused: boolean
  logAutoScroll: boolean
  onSetFilter: (f: 'all' | 'error' | 'warn' | 'info') => void
  onTogglePause: () => void
  onToggleAutoScroll: () => void
  onClear: () => void
  onClose: () => void
  applyLogFilter: (lines: string[]) => string[]
}

export default function LogViewer({
  agentName,
  logLines,
  logFilter,
  logPaused,
  logAutoScroll,
  onSetFilter,
  onTogglePause,
  onToggleAutoScroll,
  onClear,
  onClose,
  applyLogFilter,
}: LogViewerProps) {
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
          <div className="log-filter-group">
            {(['all', 'error', 'warn', 'info'] as const).map(f => (
              <button
                key={f}
                className={`log-filter-btn${logFilter === f ? ' active' : ''}${f !== 'all' ? ` log-filter-${f}` : ''}`}
                onClick={() => onSetFilter(f)}
              >{f}</button>
            ))}
          </div>
          <button
            className={`log-ctrl-btn${logPaused ? ' active' : ''}`}
            title={logPaused ? 'Resume' : 'Pause'}
            onClick={onTogglePause}
          >{logPaused ? '▶' : '⏸'}</button>
          <button
            className={`log-ctrl-btn${logAutoScroll ? ' active' : ''}`}
            title={logAutoScroll ? 'Disable auto-scroll' : 'Enable auto-scroll'}
            onClick={onToggleAutoScroll}
          >↓</button>
          <button
            className="log-ctrl-btn"
            title="Clear"
            onClick={onClear}
          >✕</button>
          <button
            className="log-close-btn"
            title="Close"
            onClick={onClose}
          >✕ close</button>
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
        <div className="log-paused-banner">⏸ paused — new lines buffered</div>
      )}
    </div>
  )
}
