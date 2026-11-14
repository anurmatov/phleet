import { useEffect, useRef, useState } from 'react'

export type LogFilter = 'all' | 'error' | 'warn' | 'info'

/** The log viewer: which agent it follows, the lines received, and how they are shown. */
export interface LogViewerState {
  /** The agent whose logs are open, or null. */
  agent: string | null
  /** Opens the agent's logs, or closes the viewer when it is already that agent's. */
  toggle: (agentName: string) => void
  close: () => void
  lines: string[]
  clear: () => void
  filter: LogFilter
  setFilter: (filter: LogFilter) => void
  applyFilter: (lines: string[]) => string[]
  paused: boolean
  togglePause: () => void
  autoScroll: boolean
  toggleAutoScroll: () => void
}

export function useLogViewer(): LogViewerState {
  const [agent, setAgent] = useState<string | null>(null)
  const [lines, setLines] = useState<string[]>([])
  const [autoScroll, setAutoScroll] = useState(true)
  const [paused, setPaused] = useState(false)
  const [filter, setFilter] = useState<LogFilter>('all')
  const socket = useRef<WebSocket | null>(null)
  // Read by the socket's handler, which outlives the render that created it.
  const pausedRef = useRef(false)

  useEffect(() => {
    if (socket.current) {
      socket.current.close()
      socket.current = null
    }
    if (!agent) return
    setLines([])
    setPaused(false)
    pausedRef.current = false
    const proto = location.protocol === 'https:' ? 'wss' : 'ws'
    const ws = new WebSocket(`${proto}://${location.host}/ws/logs/${encodeURIComponent(agent)}`)
    socket.current = ws
    ws.onmessage = (event) => {
      if (pausedRef.current) return
      const line = event.data as string
      setLines(prev => {
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
      setLines(prev => [...prev, '── log stream ended; close and reopen the viewer to follow again ──'])
    }
    return () => { closedHere = true; ws.close(); socket.current = null }
  }, [agent])

  function applyFilter(all: string[]): string[] {
    if (filter === 'all') return all
    return all.filter(l => {
      const low = l.toLowerCase()
      if (filter === 'error') return low.includes('error') || low.includes(' err ') || low.includes('[err') || low.includes('exception')
      if (filter === 'warn') return low.includes('warn')
      if (filter === 'info') return low.includes('info')
      return true
    })
  }

  return {
    agent,
    toggle: agentName => setAgent(current => current === agentName ? null : agentName),
    close: () => setAgent(null),
    lines, clear: () => setLines([]),
    filter, setFilter, applyFilter,
    paused,
    togglePause: () => {
      const next = !paused
      setPaused(next)
      pausedRef.current = next
    },
    autoScroll, toggleAutoScroll: () => setAutoScroll(a => !a),
  }
}
