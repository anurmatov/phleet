// One agent's chat thread, kept connected: open, catch up, attach the stream, reconnect.
//
// Everything outside the page's own logic is passed in (fetch, the socket, timers, randomness), so
// tests drive it with fakes and the component gives it the browser's.
//
// The order of one connection is catch-up first, then the stream attached at the cursor catch-up
// ended on. The stream replays the committed log after that cursor before it goes live, so nothing
// appended in between is missed, and what it replays is small: attaching with an old cursor would
// make it replay history into a buffer that overflows. Every reconnect is the same cycle from the
// cursor the page holds, which is how a dropped network catches up.

import {
  AGENT_UNAVAILABLE, BACKOFF_RESET_MS, DEFAULT_LIMITS, LIVENESS_TIMEOUT_MS, PONG_FRAME,
  cancelBody, closeAction, conversationPath, errorCode, eventsUrl, failureAction, newIdentifier, openBody,
  parseFrame, readAcceptedSeq, readCatchUp, readLimits, readOpen, reconnectDelayMs, sendProblem, streamUrl,
  submitBody, utf8Bytes,
} from './chatProtocol.ts'
import type { ChatLimits, FailureAction, Refusal, WireEvent } from './chatProtocol'
import { applyEvents, applyGap, emptyTranscript, isTurnRunning, type Transcript } from './chatTranscript.ts'

/** A stream as the connection uses it. The component adapts a browser WebSocket to this. */
export interface StreamHandle {
  send(data: string): void
  /** Close, and deliver no more frames or closes to the handlers. */
  close(): void
}

export interface StreamHandlers {
  message(data: unknown): void
  close(code: number, reason: string): void
}

export interface ChatDeps {
  /** `apiFetch`: same origin, the session cookie, and the write header. */
  fetch(url: string, init?: RequestInit): Promise<Response>
  openStream(url: string, handlers: StreamHandlers): StreamHandle
  /** Run `fn` after `ms`; the returned function cancels it. */
  schedule(fn: () => void, ms: number): () => void
  random(): number
  now(): number
  wsBase: string
  clientInstanceId: string
  newId?: () => string
}

export type StopReason =
  /** This conversation was opened in another tab of this browser. */
  | 'superseded'
  | 'agent-unavailable'
  | 'signed-out'
  /** The chat service has no conversation routes. */
  | 'not-set-up'
  /** The chat service will not let this person read the conversation (4403). */
  | 'forbidden'
  /** A refusal asking again cannot change. */
  | 'refused'

/** A message this page sent, until the transcript shows it. */
export interface PendingSend {
  submissionId: string
  text: string
  status: 'sending' | 'sent' | 'failed'
  /** The seq the message takes in the conversation, once the chat service has said. */
  acceptedSeq: number | null
  problem: string | null
  canRetry: boolean
}

export interface ChatState {
  phase: 'connecting' | 'live' | 'waiting' | 'stopped'
  /** The stream has said hello at least once: a later `connecting` is a reconnect. */
  connectedOnce: boolean
  stopReason: StopReason | null
  /** Why the page is waiting or stopped, in words. */
  problem: string | null
  conversationId: string | null
  transcript: Transcript
  /** Sends not yet in the transcript, oldest first. */
  pending: PendingSend[]
  limits: ChatLimits
  /** A Stop was asked for and the turn has not ended yet. */
  stopping: boolean
  /** A Stop that could not be sent, in words. */
  notice: string | null
}

/** A hard stop on paging, so an answer that always says `hasMore` cannot spin the page. */
const MAX_CATCH_UP_PAGES = 200

interface Answer { status: number; body: unknown }

export class ChatConnection {
  private state: ChatState
  private generation = 0
  private failures = 0
  private disposed = false
  private stream: StreamHandle | null = null
  private cancelTimer: (() => void) | null = null
  private cancelWatchdog: (() => void) | null = null
  private limitsRead = false
  private stopAskedAtSeq = 0
  private readonly newId: () => string
  // Plain fields rather than constructor parameter properties: `npm test` runs this file through
  // Node's type stripping, which does not support them.
  private readonly agent: string
  private readonly deps: ChatDeps
  private readonly onChange: (state: ChatState) => void

  constructor(agent: string, deps: ChatDeps, onChange: (state: ChatState) => void, retained?: ChatState) {
    this.agent = agent
    this.deps = deps
    this.onChange = onChange
    this.newId = deps.newId ?? (() => newIdentifier())
    this.state = retained ? { ...retained, phase: 'connecting', stopping: false } : {
      phase: 'connecting', connectedOnce: false, stopReason: null, problem: null, conversationId: null,
      transcript: emptyTranscript(), pending: [], limits: DEFAULT_LIMITS, stopping: false, notice: null,
    }
  }

  get current(): ChatState { return this.state }

  start(): void { void this.connect() }

  /** Connect again now: after a stop, or instead of waiting out a backoff. */
  reconnectNow(): void {
    if (this.disposed) return
    this.failures = 0
    void this.connect()
  }

  /** The browser came back online: skip the rest of a backoff. */
  nudge(): void {
    if (!this.disposed && this.state.phase === 'waiting') void this.connect()
  }

  dispose(): void {
    // Navigation cannot tell whether an in-flight POST committed. Retain its text and IDs as
    // uncertain, with an explicit same-ID retry, rather than discarding it or auto-resending.
    this.update({ pending: this.state.pending.map(p => p.status === 'sending'
      ? { ...p, status: 'failed', problem: 'The send outcome is unknown. Retry with the same message id.', canRetry: true }
      : p) })
    this.disposed = true
    this.generation++
    this.clearTimer()
    this.dropStream()
  }

  // ── connecting ────────────────────────────────────────────────────────────

  private async connect(): Promise<void> {
    const generation = ++this.generation
    this.clearTimer()
    this.dropStream()
    this.update({ phase: 'connecting', stopReason: null })

    const refusal = await this.prepare(generation)
    if (generation !== this.generation) return
    if (refusal) { this.onRefusal(refusal.action, refusal.problem); return }
    this.attach(generation)
  }

  /** The session limits, the conversation, and everything after the cursor. Null when all went well. */
  private async prepare(generation: number): Promise<{ action: FailureAction; problem: string } | null> {
    if (!this.limitsRead) {
      const answer = await this.request('/v1/session')
      if (generation !== this.generation) return null
      if (answer.status !== 200) return this.refused('session', answer)
      this.limitsRead = true
      this.update({ limits: readLimits(answer.body) })
    }

    if (!this.state.conversationId) {
      const answer = await this.request('/v1/conversations', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(openBody(this.agent, this.deps.clientInstanceId)),
      })
      if (generation !== this.generation) return null
      const conversationId = answer.status === 200 ? readOpen(answer.body) : null
      if (!conversationId) return this.refused('open', answer)
      this.update({ conversationId })
    }

    const conversationId = this.state.conversationId!
    let cursorReset = false
    for (let page = 0; page < MAX_CATCH_UP_PAGES; page++) {
      const answer = await this.request(eventsUrl(conversationId, this.state.transcript.lastSeq, this.state.limits.catchUpLimitMax))
      if (generation !== this.generation) return null

      const read = answer.status === 200 ? readCatchUp(answer.body) : null
      if (read) {
        let transcript = read.gap ? applyGap(this.state.transcript, read.gap) : this.state.transcript
        transcript = applyEvents(transcript, read.events)
        this.setTranscript(transcript)
        if (!read.hasMore) return null
        continue
      }

      const refusal = this.refused('catch-up', answer)
      // The page's cursor is not a position in this conversation (a restored database): read the
      // conversation again from its start, once.
      if (refusal.action === 'reset-cursor' && !cursorReset) {
        cursorReset = true
        this.resetTranscript()
        continue
      }
      return refusal
    }
    return null
  }

  private refused(step: 'session' | 'open' | 'catch-up', answer: Answer): { action: FailureAction; problem: string } {
    const refusal: Refusal = { httpStatus: answer.status, code: errorCode(answer.body) }
    // A 200 that could not be read is a broken answer, not a refusal.
    const action = answer.status === 200 ? 'retry' : failureAction(step, refusal)
    return { action, problem: describe(action, refusal) }
  }

  private onRefusal(action: FailureAction, problem: string): void {
    switch (action) {
      case 'signed-out':
      case 'agent-unavailable':
      case 'not-set-up':
      case 'refused':
        this.stop(action, problem)
        return
      case 'reopen':
        // Gone for this person: open the thread again from nothing. Sends it had accepted went
        // with it; a failed one stays, to be retried into the new thread.
        this.resetTranscript()
        this.update({ conversationId: null, pending: this.state.pending.filter(p => p.status !== 'sent') })
        this.retryLater(problem)
        return
      default:
        this.retryLater(problem)
    }
  }

  private attach(generation: number): void {
    const conversationId = this.state.conversationId!
    let connectedAt: number | null = null
    let handle: StreamHandle | null = null

    const mine = () => handle !== null && this.stream === handle && generation === this.generation

    handle = this.deps.openStream(
      streamUrl(this.deps.wsBase, conversationId, this.deps.clientInstanceId, this.state.transcript.lastSeq),
      {
        message: data => {
          if (!mine()) return
          this.armWatchdog()
          const frame = parseFrame(data)
          if (frame.type === 'hello') {
            connectedAt = this.deps.now()
            this.update({ phase: 'live', connectedOnce: true, problem: null })
          } else if (frame.type === 'ping') {
            try { handle!.send(PONG_FRAME) } catch { /* the close that follows decides */ }
          } else if (frame.type === 'event') {
            this.applyLive([frame.event])
          }
        },
        close: (code, reason) => {
          if (!mine()) return
          this.stream = null
          this.clearWatchdog()
          if (connectedAt !== null && this.deps.now() - connectedAt >= BACKOFF_RESET_MS) this.failures = 0

          const action = closeAction(code, reason)
          if (action.type === 'superseded') {
            this.stop('superseded', 'This chat is open in another tab of this browser.')
          } else if (action.type === 'forbidden') {
            this.stop('forbidden', 'The chat service does not let this account read this conversation.')
          } else if (action.type === 'wait') {
            this.retryAfter(action.seconds * 1000, 'The chat service asked this page to wait.')
          } else {
            this.retryLater('The connection to the chat service was lost.')
          }
        },
      },
    )
    this.stream = handle
    // Also bounds a stream that never opens: the server says hello as soon as it accepts.
    this.armWatchdog()
  }

  private retryLater(problem: string): void {
    this.retryAfter(reconnectDelayMs(this.failures++, this.deps.random()), problem)
  }

  private retryAfter(ms: number, problem: string): void {
    // Ends whatever cycle is in flight, so only the timer below connects again.
    this.generation++
    this.dropStream()
    this.clearTimer()
    this.update({ phase: 'waiting', problem })
    this.cancelTimer = this.deps.schedule(() => { this.cancelTimer = null; void this.connect() }, ms)
  }

  private stop(reason: StopReason, problem: string): void {
    this.generation++
    this.dropStream()
    this.clearTimer()
    this.update({ phase: 'stopped', stopReason: reason, problem })
  }

  private armWatchdog(): void {
    this.clearWatchdog()
    this.cancelWatchdog = this.deps.schedule(() => {
      this.cancelWatchdog = null
      this.retryLater('The connection to the chat service went quiet.')
    }, LIVENESS_TIMEOUT_MS)
  }

  private clearWatchdog(): void {
    this.cancelWatchdog?.()
    this.cancelWatchdog = null
  }

  private clearTimer(): void {
    this.cancelTimer?.()
    this.cancelTimer = null
  }

  private dropStream(): void {
    this.clearWatchdog()
    const stream = this.stream
    this.stream = null
    try { stream?.close() } catch { /* already gone */ }
  }

  // ── the transcript ────────────────────────────────────────────────────────

  private applyLive(events: WireEvent[]): void {
    this.setTranscript(applyEvents(this.state.transcript, events))
  }

  /** Start the transcript over, to be read again from the conversation's start. */
  private resetTranscript(): void {
    this.update({ transcript: emptyTranscript(), stopping: false })
  }

  /** A new transcript, with the sends it now shows dropped and a finished Stop cleared. */
  private setTranscript(transcript: Transcript): void {
    if (transcript === this.state.transcript) return
    const pending = this.state.pending.filter(p => p.acceptedSeq === null || p.acceptedSeq > transcript.lastSeq)
    const stopping = this.state.stopping && isTurnRunning(transcript) && transcript.lastAckSeq <= this.stopAskedAtSeq
    this.update({
      transcript,
      pending: pending.length === this.state.pending.length ? this.state.pending : pending,
      stopping,
    })
  }

  // ── sending ───────────────────────────────────────────────────────────────

  /** Whether the composer may send now. */
  canSend(): boolean {
    return !this.disposed && this.state.conversationId !== null && this.state.phase !== 'stopped'
  }

  /** Send a message. Returns why it was not sent, or null. */
  send(text: string): string | null {
    if (!this.canSend()) return 'Not connected yet.'
    if (text.trim() === '') return 'Type a message first.'
    if (utf8Bytes(text) > this.state.limits.inboundTextBytes) return 'This message is too long to send.'

    const pending: PendingSend = {
      submissionId: this.newId(), text, status: 'sending', acceptedSeq: null, problem: null, canRetry: false,
    }
    this.update({ pending: [...this.state.pending, pending] })
    void this.post(pending.submissionId)
    return null
  }

  /** Send a failed message again, with the same ids, so it cannot run twice. */
  retry(submissionId: string): void {
    const pending = this.state.pending.find(p => p.submissionId === submissionId)
    if (!pending || pending.status !== 'failed' || !pending.canRetry || !this.canSend()) return
    this.patchPending(submissionId, { status: 'sending', problem: null, canRetry: false })
    void this.post(submissionId)
  }

  /** Forget a failed message. */
  discard(submissionId: string): void {
    this.update({ pending: this.state.pending.filter(p => p.submissionId !== submissionId || p.status !== 'failed') })
  }

  private async post(submissionId: string): Promise<void> {
    const pending = this.state.pending.find(p => p.submissionId === submissionId)
    const conversationId = this.state.conversationId
    if (!pending || !conversationId) return

    const answer = await this.request(`${conversationPath(conversationId)}/submissions`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(submitBody(pending.submissionId, pending.text)),
    })
    if (this.disposed) return

    if (answer.status === 201) {
      const acceptedSeq = readAcceptedSeq(answer.body)
      if (acceptedSeq !== null && acceptedSeq <= this.state.transcript.lastSeq) {
        // The stream was quicker than the answer: the transcript already shows it.
        this.update({ pending: this.state.pending.filter(p => p.submissionId !== submissionId) })
      } else {
        this.patchPending(submissionId, { status: 'sent', acceptedSeq })
      }
      return
    }
    if (answer.status === 202) {
      // A retry of a message that was already durable. It is in the conversation, and the
      // transcript shows it from there; nothing here can say at which seq.
      this.update({ pending: this.state.pending.filter(p => p.submissionId !== submissionId) })
      return
    }

    const refusal: Refusal = { httpStatus: answer.status, code: errorCode(answer.body) }
    const problem = sendProblem(refusal)
    this.patchPending(submissionId, { status: 'failed', problem: problem.message, canRetry: problem.canRetry })
    if (refusal.code === 'conversation_not_found' && this.state.phase !== 'stopped') {
      this.onRefusal('reopen', 'The conversation was not found.')
    }
  }

  private patchPending(submissionId: string, patch: Partial<PendingSend>): void {
    this.update({ pending: this.state.pending.map(p => (p.submissionId === submissionId ? { ...p, ...patch } : p)) })
  }

  // ── stopping a turn ───────────────────────────────────────────────────────

  /** Stop the turn that is running in this conversation. */
  async stopTurn(): Promise<void> {
    const conversationId = this.state.conversationId
    if (!conversationId || this.state.stopping || this.disposed) return
    this.stopAskedAtSeq = this.state.transcript.lastSeq
    this.update({ stopping: true, notice: null })

    const answer = await this.request(`${conversationPath(conversationId)}:cancel`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(cancelBody()),
    })
    if (this.disposed) return
    if (answer.status === 200) return // the turn's end, or the acknowledgement, clears `stopping`

    const code = errorCode(answer.body)
    this.update({
      stopping: false,
      notice: code === 'agent_unavailable' ? AGENT_UNAVAILABLE
        : answer.status === 0 ? 'Stop was not sent: the chat service did not answer.'
        : `Stop was refused (${code || `HTTP ${answer.status}`}).`,
    })
  }

  dismissNotice(): void {
    if (this.state.notice !== null) this.update({ notice: null })
  }

  // ── plumbing ──────────────────────────────────────────────────────────────

  private async request(url: string, init?: RequestInit): Promise<Answer> {
    try {
      const response = await this.deps.fetch(url, init)
      let body: unknown = null
      try { body = await response.json() } catch { /* not JSON: the status decides */ }
      return { status: response.status, body }
    } catch {
      return { status: 0, body: null }
    }
  }

  private update(patch: Partial<ChatState>): void {
    if (this.disposed) return
    this.state = { ...this.state, ...patch }
    this.onChange(this.state)
  }
}

/** Why the page is waiting or stopped, for the line above the thread. */
function describe(action: FailureAction, refusal: Refusal): string {
  switch (action) {
    case 'signed-out': return 'The chat service did not accept this browser’s sign-in. Reload the page to sign in again.'
    case 'agent-unavailable': return AGENT_UNAVAILABLE
    case 'not-set-up': return 'Chat isn’t set up on this deployment: the chat service (Fleet.Comms) has no conversations.'
    case 'reopen': return 'The conversation was not found.'
    case 'refused': return `The chat service refused this page (${refusal.code || `HTTP ${refusal.httpStatus}`}).`
    default:
      return refusal.httpStatus === 0 ? 'The chat service did not answer.'
        : refusal.httpStatus === 200 ? 'The chat service sent an answer this page cannot read.'
        : `The chat service is not available (HTTP ${refusal.httpStatus}).`
  }
}
