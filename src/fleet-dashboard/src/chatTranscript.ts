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

import { readAttachments } from './chatProtocol.ts'
import type { AttachmentRef, Gap, WireEvent } from './chatProtocol'

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
  | {
      type: 'you'; key: string; seq: number; at: number | null; submissionId: string; text: string; attachments: AttachmentRef[]
      /** The `key` of the message this one answers, when it answers one. */
      replyTo?: string
    }
  /**
   * What the agent wrote. Shown as Markdown. `note` qualifies it; `text` may be empty. `interim`
   * marks a message written mid-turn, before a tool call: part of what it said, not its answer.
   */
  | { type: 'agent'; key: string; seq: number; at: number | null; text: string; note: string | null; interim?: true }
  /** A line that is neither: a notice, a failed or stopped turn, history that expired. */
  | { type: 'system'; key: string; at: number | null; tone: 'info' | 'warn' | 'error'; text: string }

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
  /** When the running turn started, in epoch milliseconds, when the event that started it said. */
  turnStartedAt: number | null
  /** The tools the running turn has started, oldest first, as far as this page saw them live. */
  trail: string[]
  /** The seq of the last cancel acknowledgement. */
  lastAckSeq: number
}

export function emptyTranscript(): Transcript {
  return { lastSeq: 0, entries: [], status: {}, host: null, attached: {}, attachedToUnknown: [], activity: null, turnStartedAt: null, trail: [], lastAckSeq: 0 }
}

/** How many tool names the trail keeps. A long turn shows its latest, not all of them. */
const MAX_TRAIL = 60

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
  return { ...transcript, entries: [...transcript.entries, { type: 'system', key, at: null, tone: 'warn', text: GAP_TEXT }] }
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
    trail: [...transcript.trail],
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
      t.entries.push({
        type: 'you', key: event.eventId, seq: event.seq, at: event.emittedAt, submissionId,
        text: text(payload.text), attachments: readAttachments(payload),
        ...(text(payload.replyToEventId) ? { replyTo: text(payload.replyToEventId) } : {}),
      })
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
      t.turnStartedAt = event.emittedAt
      t.trail = []
      return

    case 'turn.progress': {
      const tool = payload.activity === 'tool' ? text(payload.toolName) || null : null
      t.activity = { tool }
      if (tool) t.trail = [...t.trail.slice(-(MAX_TRAIL - 1)), tool]
      return
    }

    case 'turn.notice':
      if (text(payload.text)) t.entries.push({ type: 'system', key: event.eventId, at: event.emittedAt, tone: 'info', text: text(payload.text) })
      return

    case 'turn.message': {
      // What the model said before going on to a tool. The answer comes later, in turn.final.
      const said = text(payload.text)
      if (said.trim() !== '') {
        t.entries.push({
          type: 'agent', key: event.eventId, seq: event.seq, at: event.emittedAt, text: said,
          note: payload.truncated === true ? 'This message was shortened.' : null, interim: true,
        })
      }
      return
    }

    case 'turn.recovered_answer':
      t.entries.push({
        type: 'agent', key: event.eventId, seq: event.seq, at: event.emittedAt, text: text(payload.text),
        note: payload.truncated === true ? 'Recovered from an earlier turn, and shortened.' : 'Recovered from an earlier turn.',
      })
      return

    case 'turn.final':
      close(t, event, 'answered')
      t.entries.push({
        type: 'agent', key: event.eventId, seq: event.seq, at: event.emittedAt, text: text(payload.text), note: replyNote(payload),
      })
      return

    case 'turn.error':
      close(t, event, 'failed')
      t.entries.push({
        type: 'system', key: event.eventId, at: event.emittedAt, tone: 'error', text: text(payload.message) || 'The turn failed.',
      })
      return

    case 'turn.canceled':
      close(t, event, 'canceled')
      t.entries.push({
        type: 'system', key: event.eventId, at: event.emittedAt, tone: 'info',
        text: payload.reason === 'user' ? 'Stopped.' : payload.reason === 'operator' ? 'Stopped by the operator.' : 'Canceled.',
      })
      return

    case 'turn.outcome_unknown':
      close(t, event, 'unknown')
      t.entries.push({
        type: 'system', key: event.eventId, at: event.emittedAt, tone: 'warn',
        text: 'The outcome could not be confirmed: the agent may have run this in full, in part, or not at all.',
      })
      return

    case 'control.ack':
      t.lastAckSeq = event.seq
      // A cancel with nothing running is the whole story: no terminal follows it.
      if (payload.hadRunningTask === false) {
        t.entries.push({ type: 'system', key: event.eventId, at: event.emittedAt, tone: 'info', text: 'Nothing was running to stop.' })
      }
      return

    case 'protocol.rejected':
      t.entries.push({
        type: 'system', key: event.eventId, at: event.emittedAt, tone: 'error', text: text(payload.message) || 'The agent refused a message.',
      })
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
  t.turnStartedAt = null
  t.trail = []
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

// ── what the view derives ───────────────────────────────────────────────────

/** The trail with runs of one tool folded: `Read, Read, Bash` is `Read ×2`, `Bash`. */
export function foldTrail(trail: readonly string[]): { tool: string; count: number }[] {
  const out: { tool: string; count: number }[] = []
  for (const tool of trail) {
    const last = out[out.length - 1]
    if (last && last.tool === tool) last.count++
    else out.push({ tool, count: 1 })
  }
  return out
}

/** How long something has run, as `0:07`, `12:40` or `1:02:05`. */
export function formatElapsed(ms: number): string {
  const total = Math.max(0, Math.floor(ms / 1000))
  const hours = Math.floor(total / 3600)
  const minutes = Math.floor((total % 3600) / 60)
  const seconds = String(total % 60).padStart(2, '0')
  return hours > 0 ? `${hours}:${String(minutes).padStart(2, '0')}:${seconds}` : `${minutes}:${seconds}`
}

/** The local calendar day a moment falls on, as a number that only compares: days are equal or not. */
export function dayKey(at: number): number {
  const d = new Date(at)
  return d.getFullYear() * 10000 + (d.getMonth() + 1) * 100 + d.getDate()
}

/** What a day separator says: Today, Yesterday, or the date (with the year when it is another). */
export function dayLabel(at: number, now: number): string {
  const day = new Date(at)
  const today = new Date(now)
  if (dayKey(at) === dayKey(now)) return 'Today'
  const yesterday = new Date(today.getFullYear(), today.getMonth(), today.getDate() - 1)
  if (dayKey(at) === dayKey(yesterday.getTime())) return 'Yesterday'
  return day.toLocaleDateString(undefined, {
    weekday: 'short', day: 'numeric', month: 'short',
    ...(day.getFullYear() === today.getFullYear() ? {} : { year: 'numeric' }),
  })
}

/** The time of day beside a message. */
export function timeLabel(at: number): string {
  return new Date(at).toLocaleTimeString(undefined, { hour: 'numeric', minute: '2-digit' })
}

/** How long ago, in a few characters, for a list: `now`, `5m`, `3h`, `2d`, then a date. */
export function agoLabel(at: number, now: number): string {
  const seconds = Math.max(0, Math.floor((now - at) / 1000))
  if (seconds < 60) return 'now'
  if (seconds < 3600) return `${Math.floor(seconds / 60)}m`
  if (seconds < 86400) return `${Math.floor(seconds / 3600)}h`
  if (seconds < 7 * 86400) return `${Math.floor(seconds / 86400)}d`
  return new Date(at).toLocaleDateString(undefined, { day: 'numeric', month: 'short' })
}

/** A message in one line, for quoting it somewhere small: its start, with an ellipsis when cut. */
export function excerpt(text: string, max = 140): string {
  const line = text.replace(/\s+/g, ' ').trim()
  return line.length > max ? `${line.slice(0, max).trimEnd()}…` : line
}

/** What a reply points at, as a client shows it: who wrote it and how it starts. */
export interface Quoted {
  /** The `key` of the message. */
  key: string
  who: 'you' | 'agent'
  text: string
}

/**
 * The message an entry answers, looked up among those this page holds. Null when it answers
 * nothing; a message that has aged out of the conversation is found by nobody and reported with
 * empty text, so the reply still says it was one.
 */
export function quotedBy(entry: Entry, byKey: ReadonlyMap<string, Entry>): Quoted | null {
  if (entry.type !== 'you' || !entry.replyTo) return null
  const target = byKey.get(entry.replyTo)
  if (!target || target.type === 'system') return { key: entry.replyTo, who: 'agent', text: '' }
  const words = target.text.trim() !== '' ? target.text
    : target.type === 'you' && target.attachments.length > 0 ? (target.attachments.length === 1 ? 'A picture' : `${target.attachments.length} pictures`)
    : ''
  return { key: target.key, who: target.type, text: excerpt(words) }
}
