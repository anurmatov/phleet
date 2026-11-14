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
export function submitBody(
  submissionId: string, text: string, attachments: readonly AttachmentRef[] = [], replyToEventId: string | null = null,
): {
  protocol: string; type: string; submissionId: string; idempotencyKey: string; text: string
  attachments?: { attachmentId: string; kind: string }[]
  replyToEventId?: string
} {
  // A message without files, answering nothing, keeps the body it always had: each key is left
  // out, not sent empty.
  return {
    protocol: PROTOCOL, type: 'create', submissionId, idempotencyKey: submissionId, text,
    ...(attachments.length === 0 ? {} : { attachments: attachments.map(a => ({ attachmentId: a.attachmentId, kind: a.kind })) }),
    ...(replyToEventId ? { replyToEventId } : {}),
  }
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
  /** When the event was written, in epoch milliseconds, or null when it does not say. */
  emittedAt: number | null
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
  const emittedAt = Date.parse(text(fields.emittedAt))
  return {
    eventId,
    seq,
    kind,
    emittedAt: Number.isFinite(emittedAt) ? emittedAt : null,
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

/** One of the person's conversations, as `GET /v1/conversations` lists it. */
export interface ThreadSummary {
  conversationId: string
  agent: string
  /** The seq the next event will take: one past the newest. */
  nextSeq: number
  /** When the thread last moved, in epoch milliseconds, or null. */
  lastActivityAt: number | null
  /** The start of the thread's newest message and who wrote it, where the chat service says. */
  lastMessage?: { fromUser: boolean; text: string }
}

/**
 * The thread each agent has under the reference this page uses, by agent name. Conversations under
 * another reference are another client's and are left out. Null when the answer cannot be read.
 */
export function readThreads(body: unknown): Record<string, ThreadSummary> | null {
  const list = record(body)?.conversations
  if (!Array.isArray(list)) return null
  const threads: Record<string, ThreadSummary> = {}
  for (const value of list) {
    const fields = record(value)
    const agent = text(fields?.agent)
    const conversationId = text(fields?.conversationId)
    const nextSeq = count(fields?.nextSeq)
    if (!agent || !conversationId || nextSeq === null || fields?.externalRef !== THREAD_REF) continue
    const at = Date.parse(text(fields?.lastActivityAt))
    const last = record(fields?.lastMessage)
    threads[agent] = {
      conversationId, agent, nextSeq, lastActivityAt: Number.isFinite(at) ? at : null,
      ...(last && typeof last.text === 'string' ? { lastMessage: { fromUser: last.from === 'user', text: last.text } } : {}),
    }
  }
  return threads
}

// ── attachments ─────────────────────────────────────────────────────────────

/** A file that is part of a message, as the transcript and a submission name it. */
export interface AttachmentRef {
  attachmentId: string
  kind: string
  contentType: string
  byteSize: number | null
  fileName: string
}

/** The most files one message may carry. */
export const MAX_ATTACHMENTS = 4

/** The image types the chat service takes as they are. Anything else is converted to JPEG first. */
export const IMAGE_TYPES: readonly string[] = ['image/jpeg', 'image/png', 'image/gif', 'image/webp']

/** The files a `submission.text` payload names. Entries without an id are dropped. */
export function readAttachments(payload: Record<string, unknown>): AttachmentRef[] {
  const out: AttachmentRef[] = []
  for (const value of Array.isArray(payload.attachments) ? payload.attachments : []) {
    const fields = record(value)
    const attachmentId = text(fields?.attachmentId)
    if (!isValidIdentifier(attachmentId)) continue
    out.push({
      attachmentId,
      kind: text(fields?.kind) || 'file',
      contentType: text(fields?.contentType),
      byteSize: count(fields?.byteSize),
      fileName: text(fields?.fileName),
    })
  }
  return out
}

/** Where a file's bytes are read from. Same origin: the session cookie is the credential. */
export function attachmentContentPath(attachmentId: string): string {
  return `/v1/attachments/${encodeURIComponent(attachmentId)}/content`
}

/** Whether a file is shown as a picture. Decided by what the server recorded, never by its name. */
export function isImageAttachment(attachment: Pick<AttachmentRef, 'kind' | 'contentType'>): boolean {
  return attachment.kind === 'image' || IMAGE_TYPES.includes(attachment.contentType)
}

export function reserveBody(file: { kind: 'image' | 'file'; contentType: string; byteSize: number; sha256: string; fileName: string }) {
  return {
    protocol: PROTOCOL, kind: file.kind, contentType: file.contentType, byteSize: file.byteSize,
    sha256: file.sha256, fileName: file.fileName,
  }
}

export interface Reservation {
  attachmentId: string
  uploadUrl: string
  uploadToken: string
}

/** What a reservation answered, or null. The upload URL must be a path on this origin. */
export function readReservation(body: unknown): Reservation | null {
  const fields = record(body)
  const attachmentId = text(fields?.attachmentId)
  const uploadUrl = text(fields?.uploadUrl)
  const uploadToken = text(fields?.uploadToken)
  if (!isValidIdentifier(attachmentId) || !uploadToken) return null
  // Never send the upload token anywhere the reservation names: only to this origin's own route.
  if (!uploadUrl.startsWith('/v1/attachments/') || uploadUrl.includes('..')) return null
  return { attachmentId, uploadUrl, uploadToken }
}

/** Why a file did not go up, in words, and whether trying again could help. */
export function uploadProblem(refusal: Refusal): { message: string; canRetry: boolean; unsupported: boolean } {
  const { httpStatus, code } = refusal
  if (code === 'unsupported_attachments' || (httpStatus === 404 && code !== 'attachment_not_found' && code !== 'conversation_not_found')) {
    return { message: 'This deployment has files in chat switched off.', canRetry: false, unsupported: true }
  }
  if (httpStatus === 0) return { message: 'A file did not go up: the chat service did not answer.', canRetry: true, unsupported: false }
  if (httpStatus === 429 || httpStatus >= 500) {
    return { message: 'A file did not go up: the chat service is not available just now.', canRetry: true, unsupported: false }
  }
  if (httpStatus === 401) return { message: 'This browser is signed out of chat. Reload the page to sign in.', canRetry: true, unsupported: false }
  if (httpStatus === 413 || code === 'payload_too_large') {
    return { message: 'A file is too large for the chat service.', canRetry: false, unsupported: false }
  }
  if (httpStatus === 415 || code === 'unsupported_media_type') {
    return { message: 'The chat service does not take this kind of file. An older one takes pictures only.', canRetry: false, unsupported: false }
  }
  if (code === 'attachment_limit') return { message: 'The chat service is holding too many files just now.', canRetry: false, unsupported: false }
  if (httpStatus === 422) return { message: 'The chat service could not read a file as the type it claims to be.', canRetry: false, unsupported: false }
  if (code === 'attachment_not_found') return { message: 'A file’s upload window ran out. Retry to send it again.', canRetry: true, unsupported: false }
  return { message: `The chat service refused a file (${code || `HTTP ${httpStatus}`}).`, canRetry: false, unsupported: false }
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
  if (code === 'reply_target_not_found') {
    return { message: 'Not sent: the message this replies to is no longer in the conversation. Discard it and send it again without the reply.', canRetry: false }
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

// The backoff is shared with the agent/workflow socket; it lives in reconnect.ts.
export { BACKOFF_RESET_MS, reconnectDelayMs } from './reconnect.ts'

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
