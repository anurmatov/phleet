// The chat protocol as this page speaks it (fleet.conversation.v1, served by Fleet.Comms under /v1/).
//
// Plain functions only: request bodies, URLs, reading answers, and the decisions about what to do
// after a refusal or a closed stream. The connection that uses them is chatConnection.ts.
//
// The page holds no credential. Requests go through the dashboard's own origin with the session
// cookie the browser sends by itself; a write adds X-Fleet-Request (apiFetch does that). Nothing
// here ever puts a credential in a URL.

/** The wire version every request body carries. */
export const PROTOCOL = 'fleet.conversation.v1'

/**
 * One thread per agent, shared with the phone app: the same person opening "main" with the same
 * agent from either gets the same conversation.
 */
export const THREAD_REF = 'main'

/** Where this browser's client instance id is kept. */
export const CLIENT_INSTANCE_KEY = 'fleet.chat.clientInstanceId'

export interface ChatLimits {
  /** The largest message the chat service accepts, in UTF-8 bytes. */
  inboundTextBytes: number
  /** The largest catch-up page the chat service accepts. */
  catchUpLimitMax: number
}

/** What the chat service publishes today; used only until `GET /v1/session` has answered. */
export const DEFAULT_LIMITS: ChatLimits = { inboundTextBytes: 32 * 1024, catchUpLimitMax: 1000 }

/** The stream's frame the page answers a server ping with. The only frame a client may send. */
export const PONG_FRAME = '{"kind":"pong"}'

// ── identifiers ─────────────────────────────────────────────────────────────

/**
 * Whether a value is an identifier this page may keep and send: `[A-Za-z0-9_-]`, 1–128 characters.
 * Narrower than the server's rule (printable ASCII without spaces), so anything it passes the
 * server accepts too.
 */
export function isValidIdentifier(value: unknown): value is string {
  return typeof value === 'string' && /^[A-Za-z0-9_-]{1,128}$/.test(value)
}

/**
 * A fresh random identifier: 32 hex characters.
 *
 * `crypto.getRandomValues` rather than `crypto.randomUUID`, because the second exists only in a
 * secure context and a dashboard on a LAN address is often plain HTTP.
 */
export function newIdentifier(fill: (bytes: Uint8Array) => Uint8Array = bytes => crypto.getRandomValues(bytes)): string {
  return Array.from(fill(new Uint8Array(16)), b => b.toString(16).padStart(2, '0')).join('')
}

/**
 * This browser's client instance id: generated once and kept. Storage that is missing, blocked or
 * holding something unusable gives a fresh id for this page instead of an error.
 */
export function loadClientInstanceId(
  storage: Pick<Storage, 'getItem' | 'setItem'> | null,
  make: () => string = newIdentifier,
): string {
  try {
    const kept = storage?.getItem(CLIENT_INSTANCE_KEY)
    if (isValidIdentifier(kept)) return kept
  } catch { /* storage refused: fall through to a fresh id */ }
  const fresh = make()
  try { storage?.setItem(CLIENT_INSTANCE_KEY, fresh) } catch { /* kept for this page only */ }
  return fresh
}

/** Message size as the chat service measures it: UTF-8 bytes, not characters. */
export function utf8Bytes(text: string): number {
  return new TextEncoder().encode(text).length
}

// ── requests ────────────────────────────────────────────────────────────────

export function openBody(agent: string, clientInstanceId: string) {
  return { protocol: PROTOCOL, externalRef: THREAD_REF, agent, clientInstanceId }
}

/**
 * A new message. Always `create`: the agent decides itself whether a message joins a running turn,
 * exactly as for Telegram, so `steer` changes nothing.
 *
 * The idempotency key is the submission id, and a retry sends the same body, so a message whose
 * first answer was lost cannot start a second turn.
 */
export function submitBody(submissionId: string, text: string) {
  return { protocol: PROTOCOL, type: 'create', submissionId, idempotencyKey: submissionId, text }
}

/** Stop: cancel the turn that is running in this conversation. */
export function cancelBody() {
  return { protocol: PROTOCOL, scope: 'current' }
}

export function conversationPath(conversationId: string): string {
  return `/v1/conversations/${encodeURIComponent(conversationId)}`
}

export function eventsUrl(conversationId: string, afterSeq: number, limit: number): string {
  return `${conversationPath(conversationId)}/events?afterSeq=${afterSeq}&limit=${limit}`
}

/** `ws:` or `wss:` on this page's own host, so nginx carries the upgrade to the chat service. */
export function wsBaseFor(location: { protocol: string; host: string }): string {
  return `${location.protocol === 'https:' ? 'wss:' : 'ws:'}//${location.host}`
}

/** The stream. The cookie goes with the upgrade on its own; the URL carries no credential. */
export function streamUrl(wsBase: string, conversationId: string, clientInstanceId: string, afterSeq: number): string {
  return `${wsBase}${conversationPath(conversationId)}/stream`
    + `?clientInstanceId=${encodeURIComponent(clientInstanceId)}&afterSeq=${afterSeq}`
}

// ── answers ─────────────────────────────────────────────────────────────────

export interface ChatAgent {
  agentName: string
  displayName: string
}

export type ChatAgentsAnswer =
  | { status: 'ok'; available: boolean; agents: ChatAgent[] }
  | { status: 'error'; httpStatus: number }

function record(value: unknown): Record<string, unknown> | null {
  return value && typeof value === 'object' && !Array.isArray(value) ? value as Record<string, unknown> : null
}

function text(value: unknown): string {
  return typeof value === 'string' ? value : ''
}

/** A whole, non-negative number, as every seq and limit on this wire is. */
function count(value: unknown): number | null {
  return typeof value === 'number' && Number.isSafeInteger(value) && value >= 0 ? value : null
}

/** What `GET /api/chat/agents` said. An orchestrator without the route has no chat. */
export function readChatAgents(httpStatus: number, body: unknown): ChatAgentsAnswer {
  if (httpStatus === 404) return { status: 'ok', available: false, agents: [] }
  if (httpStatus !== 200) return { status: 'error', httpStatus }
  const fields = record(body)
  if (!fields) return { status: 'error', httpStatus }

  const agents: ChatAgent[] = []
  for (const entry of Array.isArray(fields.agents) ? fields.agents : []) {
    const agent = record(entry)
    const agentName = text(agent?.agentName)
    if (!agentName) continue
    agents.push({ agentName, displayName: text(agent?.displayName) || agentName })
  }
  return { status: 'ok', available: fields.available === true, agents }
}

/** The limits `GET /v1/session` reported, each falling back to the published value. */
export function readLimits(body: unknown): ChatLimits {
  const limits = record(record(body)?.limits)
  const inbound = count(limits?.inboundTextBytes)
  const catchUp = count(limits?.catchUpLimitMax)
  return {
    inboundTextBytes: inbound && inbound > 0 ? inbound : DEFAULT_LIMITS.inboundTextBytes,
    catchUpLimitMax: catchUp && catchUp > 0 ? Math.min(catchUp, DEFAULT_LIMITS.catchUpLimitMax) : DEFAULT_LIMITS.catchUpLimitMax,
  }
}

/** The conversation id `POST /v1/conversations` answered with, or null. */
export function readOpen(body: unknown): string | null {
  const id = record(body)?.conversationId
  return typeof id === 'string' && id !== '' ? id : null
}

/** One event as the wire carries it. Unknown kinds and fields are kept and ignored, never fatal. */
export interface WireEvent {
  eventId: string
  seq: number
  kind: string
  identity: { submissionId: string }
  payload: Record<string, unknown>
}

export interface Gap {
  fromSeq: number
  toSeq: number
}

export interface CatchUpPage {
  gap: Gap | null
  events: WireEvent[]
  hasMore: boolean
}

/** One event, or null when it is not a sequenced event this page can place. */
export function readEvent(value: unknown): WireEvent | null {
  const fields = record(value)
  if (!fields) return null
  const seq = count(fields.seq)
  const eventId = text(fields.eventId)
  const kind = text(fields.kind)
  if (seq === null || seq < 1 || !eventId || !kind) return null
  return {
    eventId,
    seq,
    kind,
    identity: { submissionId: text(record(fields.identity)?.submissionId) },
    payload: record(fields.payload) ?? {},
  }
}

/** A catch-up page, or null when the answer cannot be read. */
export function readCatchUp(body: unknown): CatchUpPage | null {
  const fields = record(body)
  if (!fields || !Array.isArray(fields.events)) return null
  const events: WireEvent[] = []
  for (const value of fields.events) {
    const event = readEvent(value)
    if (event) events.push(event)
  }
  const gapFields = record(fields.gap)
  const fromSeq = count(gapFields?.fromSeq)
  const toSeq = count(gapFields?.toSeq)
  return {
    gap: fromSeq !== null && toSeq !== null ? { fromSeq, toSeq } : null,
    events,
    hasMore: fields.hasMore === true,
  }
}

/** The `acceptedSeq` of a `201` submission answer: the seq the message itself takes. */
export function readAcceptedSeq(body: unknown): number | null {
  const seq = count(record(body)?.acceptedSeq)
  return seq !== null && seq > 0 ? seq : null
}

export type Frame =
  | { type: 'hello' }
  | { type: 'ping' }
  | { type: 'event'; event: WireEvent }
  | { type: 'other' }

/** One stream frame. Anything this page does not know is `other`, and is ignored. */
export function parseFrame(data: unknown): Frame {
  if (typeof data !== 'string') return { type: 'other' }
  let value: unknown
  try { value = JSON.parse(data) } catch { return { type: 'other' } }
  const kind = record(value)?.kind
  if (kind === 'hello') return { type: 'hello' }
  if (kind === 'ping') return { type: 'ping' }
  const event = readEvent(value)
  return event ? { type: 'event', event } : { type: 'other' }
}

// ── refusals ────────────────────────────────────────────────────────────────

/** A request that did not succeed: its HTTP status (0 when nothing answered) and error code. */
export interface Refusal {
  httpStatus: number
  code: string
}

/** The error code of a chat service error body (`{ protocol, code, message }`), or ''. */
export function errorCode(body: unknown): string {
  return text(record(body)?.code)
}

export type FailureAction =
  /** The chat service did not accept this browser's sign-in. */
  | 'signed-out'
  /** Chat is off for this agent. */
  | 'agent-unavailable'
  /** No conversation routes: the chat service runs without conversations, or is too old. */
  | 'not-set-up'
  /** The conversation is gone for this person: open it again from nothing. */
  | 'reopen'
  /** The cursor is not a position in the conversation: read it again from the start. */
  | 'reset-cursor'
  /** Something asking again cannot change. */
  | 'refused'
  /** Not reachable, busy or broken for now: try again after a pause. */
  | 'retry'

/** What to do when opening or reading the conversation was refused. */
export function failureAction(step: 'session' | 'open' | 'catch-up', refusal: Refusal): FailureAction {
  const { httpStatus, code } = refusal
  if (httpStatus === 0 || httpStatus === 429 || httpStatus >= 500) return 'retry'
  if (httpStatus === 401) return 'signed-out'
  if (code === 'agent_unavailable') return 'agent-unavailable'
  if (step === 'catch-up' && code === 'conversation_not_found') return 'reopen'
  if (step === 'catch-up' && code === 'invalid_cursor') return 'reset-cursor'
  if (httpStatus === 404) return 'not-set-up'
  return 'refused'
}

/** Why a message did not go out, in words, and whether sending it again could help. */
export function sendProblem(refusal: Refusal): { message: string; canRetry: boolean } {
  const { httpStatus, code } = refusal
  const safe = 'Retrying is safe — it cannot start a second turn.'
  if (httpStatus === 0) return { message: `Not confirmed: the chat service did not answer. ${safe}`, canRetry: true }
  if (httpStatus === 429) return { message: 'Not sent: too many requests just now. Retry in a moment.', canRetry: true }
  if (httpStatus >= 500) {
    return { message: `Not confirmed: the chat service is not available just now. ${safe}`, canRetry: true }
  }
  if (httpStatus === 401) return { message: 'This browser is signed out of chat. Reload the page to sign in.', canRetry: true }
  if (code === 'agent_unavailable') {
    return { message: `${AGENT_UNAVAILABLE} Then retry.`, canRetry: true }
  }
  if (httpStatus === 413 || code === 'payload_too_large') {
    return { message: 'This message is too long for the chat service.', canRetry: false }
  }
  if (code === 'conversation_not_found') {
    return { message: 'The conversation was not found. Reconnecting; then retry.', canRetry: true }
  }
  if (code === 'idempotency_conflict') {
    return { message: 'Not sent: this browser reused a message id. Nothing was sent twice.', canRetry: false }
  }
  return { message: `The chat service refused this message (${code || `HTTP ${httpStatus}`}).`, canRetry: false }
}

/** The one sentence for an agent whose chat is switched off. */
export const AGENT_UNAVAILABLE = 'Chat is off for this agent. Turn it on in the agent’s settings (Config → General → Chat).'

// ── the stream ──────────────────────────────────────────────────────────────

export type CloseAction =
  /** Read what was missed and attach again, after a backoff. */
  | { type: 'reconnect' }
  /** Wait exactly this long first: the chat service asked. */
  | { type: 'wait'; seconds: number }
  /** This conversation was opened again by this browser elsewhere (another tab). Stay closed. */
  | { type: 'superseded' }
  /** The chat service will not let this person read the conversation. Stay closed. */
  | { type: 'forbidden' }

/**
 * What a closed stream means. A browser never sees the HTTP status of a refused upgrade (that
 * arrives as 1006), so a refusal is learned from the catch-up that every reconnect starts with.
 */
export function closeAction(code: number, reason: string): CloseAction {
  switch (code) {
    case 4409: return { type: 'superseded' }
    case 4403: return { type: 'forbidden' }
    case 4429: {
      const seconds = /^\d{1,4}$/.test(reason.trim()) ? Number(reason.trim()) : 30
      return { type: 'wait', seconds: Math.min(Math.max(seconds, 1), 300) }
    }
    default: return { type: 'reconnect' }
  }
}

/** The pause before reconnect attempt `failures` (0-based): doubling from 1 s to 30 s, jittered. */
export function reconnectDelayMs(failures: number, random: number): number {
  const ceiling = Math.min(30_000, 1_000 * 2 ** Math.min(Math.max(failures, 0), 5))
  const fraction = Math.min(Math.max(random, 0), 1)
  return Math.round(ceiling / 2 + (ceiling / 2) * fraction)
}

/** A connection that stayed up this long resets the backoff; one that drops sooner does not. */
export const BACKOFF_RESET_MS = 60_000

/**
 * With no frame at all for this long the stream is taken as dead and replaced. The server pings
 * every 20 s, so a live stream is never this quiet; a lost network often never fires `close`.
 */
export const LIVENESS_TIMEOUT_MS = 45_000

// ── the view ────────────────────────────────────────────────────────────────

/**
 * Which agent the Chat view opens: the one the link names, else the one already open, else the
 * one last used in this browser, else the only one there is. Never an agent the list lacks.
 */
export function pickAgent(names: readonly string[], linked: string, current: string, remembered: string): string {
  return [linked, current, remembered].find(name => name !== '' && names.includes(name))
    ?? (names.length === 1 ? names[0] : '')
}

/** Enter sends; Shift+Enter is a new line; Enter that finishes an IME composition does neither. */
export function shouldSendOnKey(key: { key: string; shiftKey: boolean; isComposing: boolean }): boolean {
  return key.key === 'Enter' && !key.shiftKey && !key.isComposing
}

/** The size note under the composer: from 80% of the limit, and an error past it. Null below. */
export function sizeNote(bytes: number, limit: number): { text: string; tooLong: boolean } | null {
  if (bytes > limit) {
    return { text: `This message is too long to send (${bytes.toLocaleString('en-US')} of ${limit.toLocaleString('en-US')} bytes).`, tooLong: true }
  }
  if (bytes > limit * 0.8) return { text: `${bytes.toLocaleString('en-US')} of ${limit.toLocaleString('en-US')} bytes`, tooLong: false }
  return null
}
