// Folding conversation events into what the chat panel shows.
//
// A transcript is a value: `applyEvents` returns a new one and never changes the one it was given,
// so React can compare by reference and a test can feed it events and look at the result.
//
// Two rules carry the whole design, both from the protocol and both shared with the phone app:
//
// - Events are applied in seq order and at most once. The seq of the last one applied is the
//   cursor a reconnect catches up from, so an event at or below it is a repeat and is dropped.
// - A submission's state depends on which kinds have arrived for it, never on their order, and a
//   terminal closes every submission the turn answered — the one it names, the ones in
//   `mergedSubmissionIds`, and the ones folded into it (`injected`). A message folded into a
//   running turn never receives a terminal naming it; closing it any other way leaves it working
//   forever.

import type { Gap, WireEvent } from './chatProtocol'

export type SubmissionStatus =
  /** The message is durable; the agent has not said what it did with it yet. */
  | 'sent'
  /** Queued behind other work on the agent. */
  | 'waiting'
  /** Owns the running turn. */
  | 'working'
  /** Folded into the turn that was already running, and answered with it. */
  | 'attached'
  /** Accepted and never run: the agent's queue was full. */
  | 'queue-full'
  /** Accepted and never run: dropped. */
  | 'dropped'
  | 'answered'
  | 'failed'
  | 'canceled'
  /** The agent may have run it in full, in part, or not at all. */
  | 'unknown'

const TERMINAL: ReadonlySet<SubmissionStatus> = new Set(['queue-full', 'dropped', 'answered', 'failed', 'canceled', 'unknown'])

export type Entry =
  /** What the person sent. Shown as plain text, never as Markdown. */
  | { type: 'you'; key: string; seq: number; submissionId: string; text: string }
  /** What the agent answered. Shown as Markdown. `note` qualifies it; `text` may be empty. */
  | { type: 'agent'; key: string; seq: number; text: string; note: string | null }
  /** A line that is neither: a notice, a failed or stopped turn, history that expired. */
  | { type: 'system'; key: string; tone: 'info' | 'warn' | 'error'; text: string }

export interface Transcript {
  /** The seq of the last event applied: the cursor. 0 before any. */
  lastSeq: number
  entries: Entry[]
  /** By submission id, for every submission an event has named. */
  status: Record<string, SubmissionStatus>
  /** The submission whose turn is running, when known. */
  host: string | null
  /** Submissions folded into a running turn, by the turn's submission id. */
  attached: Record<string, string[]>
  /** Submissions folded into a turn this page never saw start (it began before the history it has). */
  attachedToUnknown: string[]
  /** What the running turn is doing: `tool` names a tool it started, null when it is just working. */
  activity: { tool: string | null } | null
  /** The seq of the last cancel acknowledgement. */
  lastAckSeq: number
}

export function emptyTranscript(): Transcript {
  return { lastSeq: 0, entries: [], status: {}, host: null, attached: {}, attachedToUnknown: [], activity: null, lastAckSeq: 0 }
}

export const GAP_TEXT = 'Earlier messages are no longer available.'

/** Whether a turn of this conversation is running: the Stop button and the working indicator. */
export function isTurnRunning(transcript: Transcript): boolean {
  return Object.values(transcript.status).some(s => s === 'working' || s === 'attached')
}

/** The few words shown under a message of the person's, or null when its state needs none. */
export function statusLabel(status: SubmissionStatus | undefined): string | null {
  switch (status) {
    case 'waiting': return 'Waiting — the agent is busy with something else'
    case 'queue-full': return 'Not run — the agent’s queue was full'
    case 'dropped': return 'Not run — the agent dropped it'
    default: return null
  }
}

/** History expired beneath the cursor. Shown once, never dropped silently. */
export function applyGap(transcript: Transcript, gap: Gap): Transcript {
  const key = `gap:${gap.fromSeq}`
  if (transcript.entries.some(entry => entry.key === key)) return transcript
  return { ...transcript, entries: [...transcript.entries, { type: 'system', key, tone: 'warn', text: GAP_TEXT }] }
}

/** Apply events in order. Returns the same transcript when none of them was new. */
export function applyEvents(transcript: Transcript, events: readonly WireEvent[]): Transcript {
  if (!events.some(event => event.seq > transcript.lastSeq)) return transcript

  const next: Transcript = {
    ...transcript,
    entries: [...transcript.entries],
    status: { ...transcript.status },
    attached: { ...transcript.attached },
    attachedToUnknown: [...transcript.attachedToUnknown],
  }
  for (const event of events) {
    if (event.seq <= next.lastSeq) continue
    next.lastSeq = event.seq
    apply(next, event)
  }
  return next
}

function text(value: unknown): string {
  return typeof value === 'string' ? value : ''
}

/** Applies one event to a transcript this module owns (a copy made by applyEvents). */
function apply(t: Transcript, event: WireEvent): void {
  const submissionId = event.identity.submissionId
  const payload = event.payload

  switch (event.kind) {
    case 'submission.text':
      if (submissionId) known(t, submissionId)
      t.entries.push({ type: 'you', key: event.eventId, seq: event.seq, submissionId, text: text(payload.text) })
      return

    case 'submission.accepted':
      if (!submissionId || isClosed(t, submissionId)) return
      known(t, submissionId)
      switch (payload.disposition) {
        case 'ran': t.status[submissionId] = 'working'; t.host = submissionId; return
        case 'injected':
          t.status[submissionId] = 'attached'
          if (t.host) t.attached[t.host] = [...(t.attached[t.host] ?? []), submissionId]
          else t.attachedToUnknown.push(submissionId)
          return
        case 'queued': t.status[submissionId] = 'waiting'; return
        case 'queue_full': t.status[submissionId] = 'queue-full'; return
        case 'dropped': t.status[submissionId] = 'dropped'; return
        // A disposition this page does not know changes nothing; the turn's terminal still closes it.
        default: return
      }

    case 'turn.started':
      if (!submissionId || isClosed(t, submissionId)) return
      known(t, submissionId)
      t.status[submissionId] = 'working'
      t.host = submissionId
      // A new turn has done nothing yet; a tool named by an earlier one is not this one's.
      t.activity = null
      return

    case 'turn.progress':
      t.activity = { tool: payload.activity === 'tool' ? text(payload.toolName) || null : null }
      return

    case 'turn.notice':
      if (text(payload.text)) t.entries.push({ type: 'system', key: event.eventId, tone: 'info', text: text(payload.text) })
      return

    case 'turn.recovered_answer':
      t.entries.push({
        type: 'agent', key: event.eventId, seq: event.seq, text: text(payload.text),
        note: payload.truncated === true ? 'Recovered from an earlier turn, and shortened.' : 'Recovered from an earlier turn.',
      })
      return

    case 'turn.final':
      close(t, event, 'answered')
      t.entries.push({ type: 'agent', key: event.eventId, seq: event.seq, text: text(payload.text), note: replyNote(payload) })
      return

    case 'turn.error':
      close(t, event, 'failed')
      t.entries.push({ type: 'system', key: event.eventId, tone: 'error', text: text(payload.message) || 'The turn failed.' })
      return

    case 'turn.canceled':
      close(t, event, 'canceled')
      t.entries.push({
        type: 'system', key: event.eventId, tone: 'info',
        text: payload.reason === 'user' ? 'Stopped.' : payload.reason === 'operator' ? 'Stopped by the operator.' : 'Canceled.',
      })
      return

    case 'turn.outcome_unknown':
      close(t, event, 'unknown')
      t.entries.push({
        type: 'system', key: event.eventId, tone: 'warn',
        text: 'The outcome could not be confirmed: the agent may have run this in full, in part, or not at all.',
      })
      return

    case 'control.ack':
      t.lastAckSeq = event.seq
      // A cancel with nothing running is the whole story: no terminal follows it.
      if (payload.hadRunningTask === false) {
        t.entries.push({ type: 'system', key: event.eventId, tone: 'info', text: 'Nothing was running to stop.' })
      }
      return

    case 'protocol.rejected':
      t.entries.push({ type: 'system', key: event.eventId, tone: 'error', text: text(payload.message) || 'The agent refused a message.' })
      return

    // Kinds this page does not render advance the cursor and nothing else.
    default:
      return
  }
}

function known(t: Transcript, submissionId: string): void {
  if (!(submissionId in t.status)) t.status[submissionId] = 'sent'
}

function isClosed(t: Transcript, submissionId: string): boolean {
  const status = t.status[submissionId]
  return status !== undefined && TERMINAL.has(status)
}

/**
 * A terminal: close the submission it names (or the running one, when it names none), every known
 * id in `mergedSubmissionIds`, and everything folded into that turn. The first terminal for a
 * submission wins; a later one does not reopen or relabel it. Merged ids this page has never seen
 * are ignored rather than invented.
 */
function close(t: Transcript, event: WireEvent, outcome: SubmissionStatus): void {
  const host = event.identity.submissionId || t.host
  const closing = new Set<string>()
  if (host) { known(t, host); closing.add(host) }

  const merged = Array.isArray(event.payload.mergedSubmissionIds) ? event.payload.mergedSubmissionIds : []
  for (const id of merged) if (typeof id === 'string' && id in t.status) closing.add(id)
  for (const id of host ? t.attached[host] ?? [] : []) closing.add(id)
  for (const id of t.attachedToUnknown) closing.add(id)

  for (const id of closing) if (!isClosed(t, id)) t.status[id] = outcome

  if (host) t.attached[host] = []
  t.attachedToUnknown = []
  if (t.host && closing.has(t.host)) t.host = null
  t.activity = null
}

/** What qualifies an answer, if anything: shortened, partial, unfinished, or empty. */
export function replyNote(payload: Record<string, unknown>): string | null {
  const empty = text(payload.text).trim() === ''
  if (payload.truncated === true) return 'This answer was shortened.'
  if (payload.isPartial === true) return 'This answer is partial.'
  if (payload.completion === 'incomplete' || payload.completion === 'failed') return 'This turn did not finish normally.'
  if (empty) return payload.completion === 'idle' ? 'The agent had nothing to add.' : 'No answer was produced.'
  return null
}
