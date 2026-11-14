import {
  useCallback, useEffect, useLayoutEffect, useRef, useState,
  type ClipboardEvent, type DragEvent, type FormEvent, type KeyboardEvent,
} from 'react'
import { apiFetch } from '../utils'
import {
  MAX_ATTACHMENTS, loadClientInstanceId, newIdentifier, pickAgent, readChatAgents, shouldSendOnKey, sizeNote, utf8Bytes,
  wsBaseFor, type ChatAgent,
} from '../chatProtocol'
import { ChatConnection, type ChatDeps, type ChatState, type OutgoingFile } from '../chatConnection'
import { agoLabel, foldTrail, formatElapsed, isTurnRunning, type Quoted, type Transcript } from '../chatTranscript'
import { agentStanding, isUnread } from '../chatActivity'
import { browserJpegEncoder, prepareFile } from '../chatFiles'
import { chatActivity, useChatActivity } from '../hooks/useChatActivity'
import { useTick } from '../hooks/useTick'
import type { AgentState } from '../types'
import ChatTranscript from './ChatTranscript'
import Icon from './Icon'

const LAST_AGENT_KEY = 'fleet.chat.lastAgent'
const DRAFT_KEY = 'fleet.chat.draft.'

/** Files picked and not sent yet, per agent, kept while the page is open. */
const draftFiles = new Map<string, OutgoingFile[]>()
/** The message being answered and not sent yet, per agent, kept while the page is open. */
const draftReplies = new Map<string, Quoted>()
/** Per-agent state survives tab/page navigation, but never a page reload or sign-out reload. */
const threads = new Map<string, ChatState>()

function storage(): Storage | null {
  try { return window.localStorage } catch { return null }
}

/** Unsent words per agent, kept in this browser so a reload or a closed tab loses nothing. */
function readDraft(agentName: string): string {
  try { return storage()?.getItem(DRAFT_KEY + agentName) ?? '' } catch { return '' }
}

function writeDraft(agentName: string, text: string): void {
  try {
    if (text === '') storage()?.removeItem(DRAFT_KEY + agentName)
    else storage()?.setItem(DRAFT_KEY + agentName, text)
  } catch { /* kept for this page only */ }
}

/** A finger, not a keyboard: Enter is a new line there, and Send is the button. */
function isTouch(): boolean {
  try { return window.matchMedia('(pointer: coarse)').matches } catch { return false }
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
 *
 * The list says how each agent is doing and which threads have moved on. On a phone the list and
 * the thread take turns on the screen.
 */
export default function ChatView({ initialAgent, fleetAgents, onOpenAgent }: {
  initialAgent: string
  /** The fleet as the Agents page shows it, by name: for how each agent is doing. */
  fleetAgents: Record<string, AgentState>
  /** Opens an agent on the Agents page. */
  onOpenAgent: (agentName: string) => void
}) {
  const [list, setList] = useState<AgentList>({ status: 'loading' })
  const [selected, setSelected] = useState<string>('')
  const activity = useChatActivity()
  // The "3m" beside a thread moves with the clock, not with anything this page receives.
  useTick(30_000)

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
  // Where each thread stands, for the marks in the list.
  useEffect(() => { void chatActivity().refresh() }, [])

  function choose(agentName: string) {
    setSelected(agentName)
    if (agentName) {
      try { storage()?.setItem(LAST_AGENT_KEY, agentName) } catch { /* a convenience only */ }
    }
    window.history.replaceState(null, '', agentName ? `#chat/${encodeURIComponent(agentName)}` : '#chat')
  }

  if (list.status === 'loading') return <div className="chat-view-message">Loading…</div>
  if (list.status === 'error') {
    return (
      <div className="chat-view-message">
        <p role="alert">{list.message}</p>
        <button className="btn" onClick={() => void load()}>Try again</button>
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
  const now = Date.now()

  return (
    <div className={`chat-view${agent ? ' chat-view--thread' : ''}`}>
      <nav className="chat-agents" aria-label="Agents to chat with">
        <div className="chat-agents-title">Chat</div>
        {list.agents.map(a => {
          const standing = agentStanding(fleetAgents[a.agentName])
          const thread = activity.threads[a.agentName]
          const unread = a.agentName !== selected && isUnread(thread, activity.seen[a.agentName])
          const caption = standing?.text ?? (a.displayName !== a.agentName ? a.agentName : '')
          return (
            <button key={a.agentName}
              className={`chat-agent${a.agentName === selected ? ' active' : ''}${unread ? ' chat-agent--unread' : ''}`}
              onClick={() => choose(a.agentName)} aria-current={a.agentName === selected ? 'true' : undefined}>
              <span className={`dot dot-${standing?.dot ?? 'grey'}`} aria-hidden="true" />
              <span className="chat-agent-text">
                <span className="chat-agent-name">{a.displayName}</span>
                {caption && <span className="chat-agent-caption">{caption}</span>}
              </span>
              <span className="chat-agent-side">
                {thread?.lastActivityAt != null && <span className="chat-agent-ago">{agoLabel(thread.lastActivityAt, now)}</span>}
                {unread && <span className="chat-agent-new" title="New activity"><span className="chat-sr-only">New activity</span></span>}
              </span>
            </button>
          )
        })}
      </nav>
      {agent
        ? <ChatThread key={agent.agentName} agent={agent} fleetAgent={fleetAgents[agent.agentName]}
            onBack={() => choose('')} onOpenAgent={onOpenAgent} />
        : <div className="chat-thread chat-thread--none chat-view-message">Pick an agent to chat with.</div>}
    </div>
  )
}

function ChatThread({ agent, fleetAgent, onBack, onOpenAgent }: {
  agent: ChatAgent
  fleetAgent: AgentState | undefined
  onBack: () => void
  onOpenAgent: (agentName: string) => void
}) {
  const connection = useRef<ChatConnection | null>(null)
  const [state, setState] = useState<ChatState | null>(null)
  const [draft, setDraftState] = useState(() => readDraft(agent.agentName))
  const [files, setFilesState] = useState<OutgoingFile[]>(() => draftFiles.get(agent.agentName) ?? [])
  const [replyTo, setReplyToState] = useState<Quoted | null>(() => draftReplies.get(agent.agentName) ?? null)
  const [preparing, setPreparing] = useState(0)
  const [composerError, setComposerError] = useState('')
  const [dragging, setDragging] = useState(false)
  /** The reader scrolled up, and whether something arrived below since. */
  const [away, setAway] = useState(false)
  const [fresh, setFresh] = useState(false)
  const scroller = useRef<HTMLDivElement>(null)
  const pinned = useRef(true)
  const shown = useRef(0)
  const input = useRef<HTMLTextAreaElement>(null)
  const picker = useRef<HTMLInputElement>(null)
  const previews = useRef(new Set<string>())

  useEffect(() => {
    const chat = new ChatConnection(agent.agentName, browserDeps(), next => {
      threads.set(agent.agentName, next)
      setState(next)
    }, threads.get(agent.agentName))
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
    writeDraft(agent.agentName, value)
    setComposerError('')
  }

  const setFiles = (next: OutgoingFile[]) => {
    setFilesState(next)
    if (next.length === 0) draftFiles.delete(agent.agentName)
    else draftFiles.set(agent.agentName, next)
  }

  const setReplyTo = useCallback((next: Quoted | null) => {
    setReplyToState(next)
    if (next) draftReplies.set(agent.agentName, next)
    else draftReplies.delete(agent.agentName)
  }, [agent.agentName])
  // Start a reply: remember what it answers and put the cursor where the answer is typed.
  const startReply = useCallback((quoted: Quoted) => {
    setReplyTo(quoted)
    input.current?.focus()
  }, [setReplyTo])

  // Where this thread stands, for the list and the mark on the Chat tab. It counts as shown only
  // while the page is in front: an answer that lands in a hidden tab is still news.
  const conversationId = state?.conversationId ?? null
  const lastSeq = state?.transcript.lastSeq ?? 0
  useEffect(() => {
    if (!conversationId || lastSeq === 0) return
    const entries = connection.current?.current.transcript.entries ?? []
    let at: number | null = null
    for (let i = entries.length - 1; i >= 0 && at === null; i--) at = entries[i].at
    chatActivity().noteThread(agent.agentName, conversationId, lastSeq, at)
    if (document.visibilityState === 'visible') chatActivity().markSeen(agent.agentName, lastSeq)
  }, [agent.agentName, conversationId, lastSeq])
  useEffect(() => {
    const onVisible = () => {
      const seq = connection.current?.current.transcript.lastSeq ?? 0
      if (document.visibilityState === 'visible' && seq > 0) chatActivity().markSeen(agent.agentName, seq)
    }
    document.addEventListener('visibilitychange', onVisible)
    return () => document.removeEventListener('visibilitychange', onVisible)
  }, [agent.agentName])

  // Follow new messages while the reader is at the bottom; leave them alone when they scrolled up,
  // and say that something arrived.
  const rows = (state?.transcript.entries.length ?? 0) + (state?.pending.length ?? 0)
  useLayoutEffect(() => {
    const el = scroller.current
    if (el && pinned.current) el.scrollTop = el.scrollHeight
    else if (rows > shown.current) setFresh(true)
    shown.current = rows
  }, [state?.transcript, state?.pending, rows])

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
  }, [draft, ready])

  // A picture's preview is held by the browser until it is let go: let go of each one that is no
  // longer in the composer or in a message on its way.
  useEffect(() => {
    const live = new Set<string>()
    for (const f of files) if (f.previewUrl) live.add(f.previewUrl)
    for (const p of state?.pending ?? []) for (const f of p.files) if (f.previewUrl) live.add(f.previewUrl)
    for (const url of previews.current) if (!live.has(url)) URL.revokeObjectURL(url)
    previews.current = live
  }, [files, state?.pending])

  if (!state) return <div className="chat-thread chat-view-message">Connecting…</div>

  const chat = connection.current
  const running = isTurnRunning(state.transcript)
  const canSend = state.conversationId !== null && state.phase !== 'stopped'
  const note = sizeNote(utf8Bytes(draft), state.limits.inboundTextBytes)
  const tooLong = note?.tooLong === true
  const empty = state.transcript.entries.length === 0 && state.pending.length === 0
  const standing = agentStanding(fleetAgent)
  const nothingToSend = draft.trim() === '' && files.length === 0
  const canAttach = canSend && !state.filesOff && files.length + preparing < MAX_ATTACHMENTS

  function toLatest() {
    const el = scroller.current
    if (el) el.scrollTop = el.scrollHeight
    pinned.current = true
    setAway(false)
    setFresh(false)
  }

  function submit(event?: FormEvent | KeyboardEvent<HTMLTextAreaElement>) {
    event?.preventDefault()
    if (!chat || tooLong || nothingToSend || preparing > 0) return
    const problem = chat.send(draft, files, replyTo)
    if (problem) { setComposerError(problem); return }
    setDraft('')
    setFiles([])
    setReplyTo(null)
    toLatest()
  }

  function onKeyDown(event: KeyboardEvent<HTMLTextAreaElement>) {
    // keyCode 229: Safari reports the Enter that ends an IME composition with isComposing false.
    const isComposing = event.nativeEvent.isComposing || event.keyCode === 229
    if (event.key === 'Escape' && replyTo) { event.preventDefault(); setReplyTo(null); return }
    if (!isTouch() && shouldSendOnKey({ key: event.key, shiftKey: event.shiftKey, isComposing })) submit(event)
  }

  /** Take picked, pasted or dropped files into the message being written. */
  async function addFiles(picked: File[]) {
    if (picked.length === 0) return
    if (state?.filesOff) { setComposerError('This deployment has files in chat switched off.'); return }
    const room = MAX_ATTACHMENTS - files.length - preparing
    if (room <= 0) { setComposerError(`A message carries at most ${MAX_ATTACHMENTS} pictures.`); return }
    const taken = picked.slice(0, room)
    let problem = picked.length > room ? `A message carries at most ${MAX_ATTACHMENTS} pictures; the rest were left out.` : ''
    setPreparing(n => n + taken.length)
    const added: OutgoingFile[] = []
    for (const file of taken) {
      const prepared = await prepareFile(file, browserJpegEncoder)
      if (prepared.ok) {
        added.push({
          id: newIdentifier(), fileName: prepared.fileName, contentType: prepared.contentType, bytes: prepared.bytes,
          previewUrl: URL.createObjectURL(new Blob([prepared.bytes as BlobPart], { type: prepared.contentType })),
          attachmentId: null,
        })
      } else {
        problem = prepared.problem
      }
      setPreparing(n => n - 1)
    }
    setFiles([...(draftFiles.get(agent.agentName) ?? []), ...added])
    setComposerError(problem)
  }

  function onPaste(event: ClipboardEvent<HTMLTextAreaElement>) {
    const pasted = Array.from(event.clipboardData.files)
    if (pasted.length === 0) return
    event.preventDefault()
    void addFiles(pasted)
  }

  function onDragOver(event: DragEvent) {
    if (!Array.from(event.dataTransfer.types).includes('Files')) return
    event.preventDefault()
    setDragging(true)
  }

  function onDrop(event: DragEvent) {
    if (!Array.from(event.dataTransfer.types).includes('Files')) return
    event.preventDefault()
    setDragging(false)
    void addFiles(Array.from(event.dataTransfer.files))
  }

  return (
    <section className={`chat-thread${dragging ? ' chat-thread--drop' : ''}`} aria-label={`Chat with ${agent.displayName}`}
      onDragOver={onDragOver} onDrop={onDrop}
      onDragLeave={event => { if (event.currentTarget === event.target) setDragging(false) }}>
      <header className="chat-thread-header">
        <button type="button" className="icon-btn chat-back" onClick={onBack} aria-label="All chats" title="All chats">
          <Icon name="chevronLeft" />
        </button>
        <div className="chat-thread-who">
          <span className="chat-thread-title">{agent.displayName}</span>
          {standing && (
            <span className="chat-thread-standing">
              <span className={`dot dot-${standing.dot}`} aria-hidden="true" />{standing.text}
            </span>
          )}
        </div>
        <ConnectionLine state={state} onReconnect={() => chat?.reconnectNow()} />
        {fleetAgent && (
          <button type="button" className="icon-btn" onClick={() => onOpenAgent(agent.agentName)}
            aria-label={`${agent.displayName} on the Agents page`} title="Open on the Agents page">
            <Icon name="agents" />
          </button>
        )}
      </header>

      <div className="chat-scroll-frame">
        <div className="chat-scroll" ref={scroller} role="log" aria-live="polite"
          onScroll={e => {
            const el = e.currentTarget
            const atBottom = el.scrollHeight - el.scrollTop - el.clientHeight < 40
            pinned.current = atBottom
            setAway(!atBottom)
            if (atBottom) setFresh(false)
          }}>
          {empty && state.connectedOnce && (
            <div className="chat-empty">
              <Icon name="chat" size={28} />
              <p>No messages yet.</p>
              <p className="chat-empty-hint">
                {standing?.down
                  ? `${agent.displayName} is offline. A message waits until it is started.`
                  : `This is the same thread the phone app opens with ${agent.displayName}.`}
              </p>
            </div>
          )}
          <ChatTranscript entries={state.transcript.entries} status={state.transcript.status} pending={state.pending}
            onRetry={id => chat?.retry(id)} onDiscard={id => chat?.discard(id)} onReply={canSend ? startReply : undefined} agentLabel={agent.displayName} />
          {running && <Working name={agent.displayName} transcript={state.transcript} />}
        </div>
        {away && (
          <button type="button" className={`chat-latest${fresh ? ' chat-latest--fresh' : ''}`} onClick={toLatest}>
            <Icon name="chevronDown" size={16} />{fresh ? 'New messages' : 'Latest'}
          </button>
        )}
        {dragging && <div className="chat-drop" aria-hidden="true">Drop pictures to attach them</div>}
      </div>

      {state.notice && (
        <div className="chat-notice" role="alert">
          {state.notice}
          <button type="button" className="chat-link-btn" onClick={() => chat?.dismissNotice()}>Dismiss</button>
        </div>
      )}

      <form className="chat-composer" onSubmit={submit}>
        {replyTo && (
          <div className="chat-replying">
            <Icon name="reply" size={16} />
            <span className="chat-replying-text">
              <span className="chat-replying-who">Replying to {replyTo.who === 'you' ? 'your message' : agent.displayName}</span>
              <span className="chat-replying-quote">{replyTo.text}</span>
            </span>
            <button type="button" className="icon-btn icon-btn-s" onClick={() => setReplyTo(null)} aria-label="Cancel reply" title="Cancel reply">
              <Icon name="close" size={16} />
            </button>
          </div>
        )}
        {(files.length > 0 || preparing > 0) && (
          <div className="chat-draft-files">
            {files.map(f => (
              <span key={f.id} className="chat-draft-file">
                {f.previewUrl && <img src={f.previewUrl} alt={f.fileName} />}
                <button type="button" className="chat-draft-remove" aria-label={`Remove ${f.fileName}`} title="Remove"
                  onClick={() => setFiles(files.filter(x => x.id !== f.id))}>
                  <Icon name="close" size={12} />
                </button>
              </span>
            ))}
            {preparing > 0 && <span className="chat-draft-file chat-draft-file--preparing" role="status">Preparing…</span>}
          </div>
        )}
        <div className="chat-composer-row">
          {!state.filesOff && (
            <>
              <button type="button" className="icon-btn chat-attach" disabled={!canAttach}
                onClick={() => picker.current?.click()} aria-label="Attach pictures" title="Attach pictures">
                <Icon name="attach" />
              </button>
              <input ref={picker} type="file" accept="image/*" multiple hidden id="chat-file-picker"
                onChange={event => {
                  const picked = Array.from(event.target.files ?? [])
                  event.target.value = ''
                  void addFiles(picked)
                }} />
            </>
          )}
          <textarea ref={input} id="chat-input" className="chat-input" rows={1} value={draft}
            placeholder={canSend ? `Message ${agent.displayName}` : ''} aria-label={`Message ${agent.displayName}`}
            disabled={!canSend} onChange={e => setDraft(e.target.value)} onKeyDown={onKeyDown} onPaste={onPaste} />
          {running && (
            <button type="button" className="btn chat-stop" onClick={() => void chat?.stopTurn()} disabled={state.stopping}>
              <Icon name="stop" size={16} />{state.stopping ? 'Stopping…' : 'Stop'}
            </button>
          )}
          <button type="submit" className="btn btn-primary chat-send" aria-label="Send" title="Send"
            disabled={!canSend || tooLong || nothingToSend || preparing > 0}>
            <Icon name="send" />
          </button>
        </div>
      </form>
      {(composerError || note) && (
        <div className={`chat-composer-note${tooLong || composerError ? ' chat-composer-note--error' : ''}`} role={composerError ? 'alert' : undefined}>
          {composerError || note?.text}
        </div>
      )}
      <div className="chat-composer-hint">
        Enter to send, Shift+Enter for a new line.{!state.filesOff && ' Paste or drop pictures to attach them.'}
      </div>
    </section>
  )
}

/**
 * What the agent is doing while a turn runs: how long it has been at it, and the tools it has
 * used so far. The answer itself arrives whole, at the end; this is what there is to watch until
 * then. The tools are those this page saw start while it was connected.
 */
function Working({ name, transcript }: { name: string; transcript: Transcript }) {
  useTick(1000)
  const trail = foldTrail(transcript.trail)
  const recent = trail.slice(-6)
  const startedAt = transcript.turnStartedAt
  return (
    <div className="chat-working" role="status">
      <div className="chat-working-line">
        <span className="chat-working-dots" aria-hidden="true"><i /><i /><i /></span>
        <span>
          {transcript.activity?.tool ? `${name} is using ${transcript.activity.tool}…` : `${name} is working…`}
        </span>
        {startedAt !== null && (
          // The clock is for eyes: read aloud every second it would drown everything else.
          <span className="chat-working-time" aria-hidden="true">{formatElapsed(Date.now() - startedAt)}</span>
        )}
      </div>
      {trail.length > 1 && (
        <div className="chat-trail" aria-label="Tools used so far">
          {trail.length > recent.length && <span className="chat-trail-more">…</span>}
          {recent.map((step, index) => (
            <span key={`${trail.length - recent.length + index}`} className="chat-trail-step">
              {step.tool}{step.count > 1 && <span className="chat-trail-count"> ×{step.count}</span>}
            </span>
          ))}
        </div>
      )}
    </div>
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
