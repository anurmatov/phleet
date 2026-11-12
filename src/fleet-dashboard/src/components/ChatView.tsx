import { useCallback, useEffect, useLayoutEffect, useRef, useState, type FormEvent, type KeyboardEvent } from 'react'
import { apiFetch } from '../utils'
import {
  loadClientInstanceId, pickAgent, readChatAgents, shouldSendOnKey, sizeNote, utf8Bytes, wsBaseFor, type ChatAgent,
} from '../chatProtocol'
import { ChatConnection, type ChatDeps, type ChatState } from '../chatConnection'
import { isTurnRunning } from '../chatTranscript'
import ChatTranscript from './ChatTranscript'

const LAST_AGENT_KEY = 'fleet.chat.lastAgent'

/** Unsent text per agent, kept while the page is open so switching agents loses nothing. */
const drafts = new Map<string, string>()

function storage(): Storage | null {
  try { return window.localStorage } catch { return null }
}

/** The browser's side of a connection: same-origin fetch with the session cookie, and a WebSocket. */
function browserDeps(): ChatDeps {
  return {
    fetch: apiFetch,
    openStream(url, handlers) {
      const socket = new WebSocket(url)
      socket.onmessage = event => handlers.message(event.data)
      socket.onclose = event => handlers.close(event.code, event.reason)
      return {
        send: data => socket.send(data),
        close: () => {
          socket.onmessage = null
          socket.onclose = null
          socket.close(1000)
        },
      }
    },
    schedule(fn, ms) {
      const timer = window.setTimeout(fn, ms)
      return () => window.clearTimeout(timer)
    },
    random: Math.random,
    now: Date.now,
    wsBase: wsBaseFor(window.location),
    clientInstanceId: loadClientInstanceId(storage()),
  }
}

type AgentList =
  | { status: 'loading' }
  | { status: 'error'; message: string }
  | { status: 'ok'; available: boolean; agents: ChatAgent[] }

/**
 * Chat: pick an agent and talk to it. One thread per agent ("main"), the same one the phone app
 * opens, so a conversation continues across both.
 */
export default function ChatView({ initialAgent }: { initialAgent: string }) {
  const [list, setList] = useState<AgentList>({ status: 'loading' })
  const [selected, setSelected] = useState<string>('')

  const load = useCallback(async () => {
    setList({ status: 'loading' })
    try {
      const response = await apiFetch('/api/chat/agents')
      let body: unknown = null
      try { body = await response.json() } catch { /* the status decides */ }
      const answer = readChatAgents(response.status, body)
      if (answer.status === 'error') {
        setList({ status: 'error', message: `The agent list could not be read (HTTP ${answer.httpStatus}).` })
        return
      }
      setList({ status: 'ok', available: answer.available, agents: answer.agents })
      let remembered = ''
      try { remembered = storage()?.getItem(LAST_AGENT_KEY) ?? '' } catch { /* none */ }
      const names = answer.agents.map(a => a.agentName)
      setSelected(current => pickAgent(names, initialAgent, current, remembered))
    } catch {
      setList({ status: 'error', message: 'The orchestrator did not answer.' })
    }
  }, [initialAgent])

  useEffect(() => { void load() }, [load])

  function choose(agentName: string) {
    setSelected(agentName)
    try { storage()?.setItem(LAST_AGENT_KEY, agentName) } catch { /* a convenience only */ }
    window.history.replaceState(null, '', agentName ? `#chat/${encodeURIComponent(agentName)}` : '#chat')
  }

  if (list.status === 'loading') return <div className="chat-view-message">Loading…</div>
  if (list.status === 'error') {
    return (
      <div className="chat-view-message">
        <p role="alert">{list.message}</p>
        <button className="cred-action-btn" onClick={() => void load()}>Try again</button>
      </div>
    )
  }
  if (!list.available) {
    return (
      <div className="chat-view-message">
        Chat isn’t set up on this deployment. It needs the chat service (Fleet.Comms) with conversations turned on.
      </div>
    )
  }
  if (list.agents.length === 0) {
    return (
      <div className="chat-view-message">
        No agent has chat turned on. Turn it on in an agent’s settings (Config → General → Chat).
      </div>
    )
  }

  const agent = list.agents.find(a => a.agentName === selected) ?? null

  return (
    <div className="chat-view">
      <nav className="chat-agents" aria-label="Agents to chat with">
        {list.agents.map(a => (
          <button key={a.agentName} className={`chat-agent${a.agentName === selected ? ' active' : ''}`}
            onClick={() => choose(a.agentName)} aria-current={a.agentName === selected ? 'true' : undefined}>
            <span className="chat-agent-name">{a.displayName}</span>
            {a.displayName !== a.agentName && <span className="chat-agent-id">{a.agentName}</span>}
          </button>
        ))}
      </nav>
      <select className="config-input chat-agent-select" value={selected} aria-label="Agent"
        onChange={e => choose(e.target.value)}>
        <option value="">Choose an agent…</option>
        {list.agents.map(a => <option key={a.agentName} value={a.agentName}>{a.displayName}</option>)}
      </select>
      {agent
        ? <ChatThread key={agent.agentName} agent={agent} />
        : <div className="chat-thread chat-view-message">Pick an agent to chat with.</div>}
    </div>
  )
}

function ChatThread({ agent }: { agent: ChatAgent }) {
  const connection = useRef<ChatConnection | null>(null)
  const [state, setState] = useState<ChatState | null>(null)
  const [draft, setDraftState] = useState(() => drafts.get(agent.agentName) ?? '')
  const [composerError, setComposerError] = useState('')
  const scroller = useRef<HTMLDivElement>(null)
  const pinned = useRef(true)
  const input = useRef<HTMLTextAreaElement>(null)

  useEffect(() => {
    const chat = new ChatConnection(agent.agentName, browserDeps(), setState)
    connection.current = chat
    setState(chat.current)
    chat.start()
    const online = () => chat.nudge()
    window.addEventListener('online', online)
    return () => {
      window.removeEventListener('online', online)
      chat.dispose()
      if (connection.current === chat) connection.current = null
    }
  }, [agent.agentName])

  const setDraft = (value: string) => {
    setDraftState(value)
    drafts.set(agent.agentName, value)
    setComposerError('')
  }

  // Follow new messages while the reader is at the bottom; leave them alone when they scrolled up.
  useLayoutEffect(() => {
    const el = scroller.current
    if (el && pinned.current) el.scrollTop = el.scrollHeight
  }, [state?.transcript, state?.pending])

  // The same when the window is resized or a phone turned: narrower text is taller.
  const ready = state !== null
  useEffect(() => {
    const el = scroller.current
    if (!el || typeof ResizeObserver === 'undefined') return
    const observer = new ResizeObserver(() => { if (pinned.current) el.scrollTop = el.scrollHeight })
    observer.observe(el)
    return () => observer.disconnect()
  }, [ready])

  // The text box grows with what is typed, up to the cap CSS sets.
  useLayoutEffect(() => {
    const el = input.current
    if (!el) return
    el.style.height = 'auto'
    // scrollHeight leaves out the border, which border-box sizing counts.
    el.style.height = `${el.scrollHeight + el.offsetHeight - el.clientHeight}px`
  }, [draft])

  if (!state) return <div className="chat-thread chat-view-message">Connecting…</div>

  const chat = connection.current
  const running = isTurnRunning(state.transcript)
  const canSend = state.conversationId !== null && state.phase !== 'stopped'
  const note = sizeNote(utf8Bytes(draft), state.limits.inboundTextBytes)
  const tooLong = note?.tooLong === true
  const empty = state.transcript.entries.length === 0 && state.pending.length === 0

  function submit(event?: FormEvent | KeyboardEvent<HTMLTextAreaElement>) {
    event?.preventDefault()
    if (!chat || tooLong || draft.trim() === '') return
    const problem = chat.send(draft)
    if (problem) { setComposerError(problem); return }
    pinned.current = true
    setDraft('')
  }

  function onKeyDown(event: KeyboardEvent<HTMLTextAreaElement>) {
    // keyCode 229: Safari reports the Enter that ends an IME composition with isComposing false.
    const isComposing = event.nativeEvent.isComposing || event.keyCode === 229
    if (shouldSendOnKey({ key: event.key, shiftKey: event.shiftKey, isComposing })) submit(event)
  }

  return (
    <section className="chat-thread" aria-label={`Chat with ${agent.displayName}`}>
      <header className="chat-thread-header">
        <span className="chat-thread-title">{agent.displayName}</span>
        <ConnectionLine state={state} onReconnect={() => chat?.reconnectNow()} />
      </header>

      <div className="chat-scroll" ref={scroller} role="log" aria-live="polite"
        onScroll={e => {
          const el = e.currentTarget
          pinned.current = el.scrollHeight - el.scrollTop - el.clientHeight < 40
        }}>
        {empty && state.connectedOnce && <div className="chat-empty">No messages yet.</div>}
        <ChatTranscript entries={state.transcript.entries} status={state.transcript.status} pending={state.pending}
          onRetry={id => chat?.retry(id)} onDiscard={id => chat?.discard(id)} />
        {running && (
          <div className="chat-working" role="status">
            {state.transcript.activity?.tool
              ? `${agent.displayName} is using ${state.transcript.activity.tool}…`
              : `${agent.displayName} is working…`}
          </div>
        )}
      </div>

      {state.notice && (
        <div className="chat-notice" role="alert">
          {state.notice}
          <button type="button" className="chat-link-btn" onClick={() => chat?.dismissNotice()}>Dismiss</button>
        </div>
      )}

      <form className="chat-composer" onSubmit={submit}>
        <textarea ref={input} className="config-input chat-input" rows={1} value={draft}
          placeholder={canSend ? `Message ${agent.displayName}` : ''} aria-label={`Message ${agent.displayName}`}
          disabled={!canSend} onChange={e => setDraft(e.target.value)} onKeyDown={onKeyDown} />
        <div className="chat-composer-actions">
          {running && (
            <button type="button" className="cred-action-btn" onClick={() => void chat?.stopTurn()} disabled={state.stopping}>
              {state.stopping ? 'Stopping…' : 'Stop'}
            </button>
          )}
          <button type="submit" className="config-save-btn" disabled={!canSend || tooLong || draft.trim() === ''}>Send</button>
        </div>
      </form>
      {(composerError || note) && (
        <div className={`chat-composer-note${tooLong || composerError ? ' chat-composer-note--error' : ''}`}>
          {composerError || note?.text}
        </div>
      )}
      <div className="chat-composer-hint">Enter to send, Shift+Enter for a new line.</div>
    </section>
  )
}

/** Where the connection stands, and what the person can do about it. */
export function ConnectionLine({ state, onReconnect }: { state: ChatState; onReconnect: () => void }) {
  if (state.phase === 'live') return <span className="chat-status chat-status--live">Connected</span>
  if (state.phase === 'connecting') {
    return <span className="chat-status">{state.connectedOnce ? 'Reconnecting…' : 'Connecting…'}</span>
  }
  if (state.phase === 'waiting') {
    return (
      <span className="chat-status chat-status--warn" role="status">
        {state.problem} Reconnecting shortly.
        <button type="button" className="chat-link-btn" onClick={onReconnect}>Reconnect now</button>
      </span>
    )
  }
  const action = state.stopReason === 'superseded' ? 'Use it here'
    : state.stopReason === 'not-set-up' || state.stopReason === 'forbidden' ? null
    : 'Try again'
  return (
    <span className="chat-status chat-status--error" role="alert">
      {state.problem}
      {action && <button type="button" className="chat-link-btn" onClick={onReconnect}>{action}</button>}
    </span>
  )
}
