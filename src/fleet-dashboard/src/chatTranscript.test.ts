import { test } from 'node:test'
import assert from 'node:assert/strict'
import type { WireEvent } from './chatProtocol'
import {
  GAP_TEXT, applyEvents, applyGap, emptyTranscript, isTurnRunning, replyNote, statusLabel, type Transcript,
} from './chatTranscript.ts'

/** An event as catch-up or the stream delivers it. */
function ev(seq: number, kind: string, submissionId = '', payload: Record<string, unknown> = {}): WireEvent {
  return { eventId: `e${seq}`, seq, kind, identity: { submissionId }, payload }
}

function fold(...events: WireEvent[]): Transcript {
  return applyEvents(emptyTranscript(), events)
}

const shapes = (t: Transcript) => t.entries.map(e => (e.type === 'system' ? `system:${e.tone}:${e.text}` : `${e.type}:${e.text}`))

test('one exchange: the message, a running turn with a tool, then the answer', () => {
  let t = fold(
    ev(1, 'submission.text', 'S1', { text: 'What is **up**?' }),
    ev(2, 'turn.started', 'S1'),
    ev(3, 'submission.accepted', 'S1', { disposition: 'ran' }),
    ev(4, 'turn.progress', 'S1', { activity: 'tool', toolName: 'Bash' }),
  )
  assert.equal(isTurnRunning(t), true)
  assert.deepEqual(t.activity, { tool: 'Bash' })
  assert.equal(t.status.S1, 'working')

  t = applyEvents(t, [ev(5, 'turn.final', 'S1', {
    text: 'All **good**.', completion: 'completed', isPartial: false, truncated: false, mergedSubmissionIds: ['S1'],
  })])
  assert.deepEqual(shapes(t), ['you:What is **up**?', 'agent:All **good**.'])
  assert.equal(t.status.S1, 'answered')
  assert.equal(isTurnRunning(t), false)
  assert.equal(t.activity, null)
  assert.equal(t.lastSeq, 5)
})

test('typing progress shows working without a tool name', () => {
  const t = fold(ev(1, 'turn.started', 'S1'), ev(2, 'turn.progress', 'S1', { activity: 'typing' }))
  assert.deepEqual(t.activity, { tool: null })
})

test('a new turn does not inherit the tool an earlier one was using', () => {
  // Progress with no terminal seen for it (history began mid-turn), then a new turn starts.
  const t = fold(ev(1, 'turn.progress', 'S0', { activity: 'tool', toolName: 'Bash' }), ev(2, 'turn.started', 'S1'))
  assert.equal(t.activity, null)
})

test('events at or below the cursor are repeats and change nothing; the input is never modified', () => {
  const before = fold(ev(1, 'submission.text', 'S1', { text: 'hi' }), ev(2, 'turn.started', 'S1'))
  const snapshot = JSON.stringify(before)
  assert.equal(applyEvents(before, [ev(1, 'submission.text', 'S1', { text: 'hi' }), ev(2, 'turn.started', 'S1')]), before)

  const after = applyEvents(before, [ev(2, 'turn.started', 'S1'), ev(3, 'turn.final', 'S1', { text: 'yo', completion: 'completed' })])
  assert.notEqual(after, before)
  assert.equal(JSON.stringify(before), snapshot)
  assert.deepEqual(shapes(after), ['you:hi', 'agent:yo'])
})

test('a message folded into the running turn is closed by that turn’s answer, which names only the host', () => {
  const t = fold(
    ev(1, 'submission.text', 'A', { text: 'first' }),
    ev(2, 'turn.started', 'A'),
    ev(3, 'submission.text', 'B', { text: 'and also' }),
    ev(4, 'submission.accepted', 'B', { disposition: 'injected' }),
    ev(5, 'turn.final', 'A', { text: 'both answered', completion: 'completed', mergedSubmissionIds: ['A'] }),
  )
  assert.equal(t.status.A, 'answered')
  assert.equal(t.status.B, 'answered')
  assert.equal(isTurnRunning(t), false)
  // In seq order: both messages, then the one answer.
  assert.deepEqual(shapes(t), ['you:first', 'you:and also', 'agent:both answered'])
})

test('turn.error carries no merged list, and still closes what was folded into the turn', () => {
  const t = fold(
    ev(1, 'turn.started', 'A'),
    ev(2, 'submission.accepted', 'B', { disposition: 'injected' }),
    ev(3, 'turn.error', 'A', { code: 'executor_error', message: 'The turn failed.' }),
  )
  assert.equal(t.status.A, 'failed')
  assert.equal(t.status.B, 'failed')
  assert.equal(isTurnRunning(t), false)
  assert.deepEqual(shapes(t), ['system:error:The turn failed.'])
})

test('mergedSubmissionIds close every known submission; an id never seen is not invented', () => {
  const t = fold(
    ev(1, 'submission.text', 'A', { text: 'one' }),
    ev(2, 'submission.accepted', 'A', { disposition: 'queued' }),
    ev(3, 'submission.text', 'B', { text: 'two' }),
    ev(4, 'turn.started', 'B'),
    ev(5, 'turn.final', 'B', { text: 'ok', completion: 'completed', mergedSubmissionIds: ['A', 'B', 'ghost'] }),
  )
  assert.equal(t.status.A, 'answered')
  assert.equal(t.status.B, 'answered')
  assert.equal('ghost' in t.status, false)
  assert.equal(isTurnRunning(t), false)
})

test('a queued message waits, then runs, then is answered', () => {
  let t = fold(ev(1, 'submission.text', 'A', { text: 'later please' }), ev(2, 'submission.accepted', 'A', { disposition: 'queued', queuePosition: 1 }))
  assert.equal(t.status.A, 'waiting')
  assert.equal(statusLabel(t.status.A), 'Waiting — the agent is busy with something else')
  assert.equal(isTurnRunning(t), false)
  t = applyEvents(t, [ev(3, 'turn.started', 'A')])
  assert.equal(t.status.A, 'working')
  assert.equal(statusLabel(t.status.A), null)
  t = applyEvents(t, [ev(4, 'turn.final', 'A', { text: 'done', completion: 'completed' })])
  assert.equal(t.status.A, 'answered')
})

test('a message that was not run says so, and a late acceptance does not reopen it', () => {
  const full = fold(ev(1, 'submission.accepted', 'A', { disposition: 'queue_full' }))
  assert.equal(statusLabel(full.status.A), 'Not run — the agent’s queue was full')
  const dropped = fold(ev(1, 'submission.accepted', 'A', { disposition: 'dropped' }), ev(2, 'turn.started', 'A'))
  assert.equal(dropped.status.A, 'dropped')
  assert.equal(statusLabel(dropped.status.A), 'Not run — the agent dropped it')
  assert.equal(isTurnRunning(dropped), false)

  const answered = fold(
    ev(1, 'turn.started', 'A'),
    ev(2, 'turn.final', 'A', { text: 'x', completion: 'completed' }),
    ev(3, 'submission.accepted', 'A', { disposition: 'ran' }),
  )
  assert.equal(answered.status.A, 'answered')
  assert.equal(isTurnRunning(answered), false)
})

test('the first terminal wins; a second one does not relabel the submission', () => {
  const t = fold(
    ev(1, 'turn.started', 'A'),
    ev(2, 'turn.canceled', 'A', { reason: 'user', mergedSubmissionIds: ['A'] }),
    ev(3, 'turn.outcome_unknown', 'A', { reason: 'attempt_abandoned' }),
  )
  assert.equal(t.status.A, 'canceled')
})

test('a terminal naming no submission closes the running one', () => {
  const t = fold(ev(1, 'turn.started', 'A'), ev(2, 'turn.outcome_unknown', '', { reason: 'turn_reaped' }))
  assert.equal(t.status.A, 'unknown')
  assert.equal(isTurnRunning(t), false)
  assert.equal(t.host, null)
})

test('history that begins mid-turn: a folded message whose host was never seen is closed by the next terminal', () => {
  const t = fold(
    ev(40, 'submission.accepted', 'B', { disposition: 'injected' }),
    ev(41, 'turn.final', 'A', { text: 'answer', completion: 'completed', mergedSubmissionIds: ['A'] }),
  )
  assert.equal(t.status.B, 'answered')
  assert.equal(t.status.A, 'answered')
  assert.equal(isTurnRunning(t), false)
  assert.deepEqual(shapes(t), ['agent:answer'])
})

test('answers that need a word: shortened, partial, unfinished, empty', () => {
  assert.equal(replyNote({ text: 'x', completion: 'completed', truncated: true }), 'This answer was shortened.')
  assert.equal(replyNote({ text: 'x', completion: 'completed', isPartial: true }), 'This answer is partial.')
  assert.equal(replyNote({ text: 'x', completion: 'incomplete' }), 'This turn did not finish normally.')
  assert.equal(replyNote({ text: '', completion: 'idle' }), 'The agent had nothing to add.')
  assert.equal(replyNote({ text: '  ', completion: 'completed' }), 'No answer was produced.')
  assert.equal(replyNote({ text: 'fine', completion: 'completed', isPartial: false, truncated: false }), null)

  const t = fold(ev(1, 'turn.started', 'A'), ev(2, 'turn.final', 'A', { text: '', completion: 'idle' }))
  assert.deepEqual(t.entries, [{ type: 'agent', key: 'e2', seq: 2, text: '', note: 'The agent had nothing to add.' }])
})

test('stopped, canceled by the operator, unconfirmed, refused, and a notice each get their line', () => {
  const t = fold(
    ev(1, 'turn.started', 'A'),
    ev(2, 'turn.canceled', 'A', { reason: 'user', mergedSubmissionIds: ['A'] }),
    ev(3, 'turn.started', 'B'),
    ev(4, 'turn.canceled', 'B', { reason: 'operator', mergedSubmissionIds: ['B'] }),
    ev(5, 'turn.started', 'C'),
    ev(6, 'turn.notice', 'C', { text: 'An image sent with this message is unavailable and was not included.' }),
    ev(7, 'turn.outcome_unknown', 'C', { reason: 'attempt_abandoned' }),
    ev(8, 'protocol.rejected', '', { code: 'payload_too_large', message: 'The submission exceeds the maximum accepted size.' }),
  )
  assert.deepEqual(shapes(t), [
    'system:info:Stopped.',
    'system:info:Stopped by the operator.',
    'system:info:An image sent with this message is unavailable and was not included.',
    'system:warn:The outcome could not be confirmed: the agent may have run this in full, in part, or not at all.',
    'system:error:The submission exceeds the maximum accepted size.',
  ])
  assert.equal(t.status.C, 'unknown')
})

test('a cancel acknowledgement is recorded; with nothing running it says so and no terminal is invented', () => {
  const t = fold(ev(1, 'submission.text', 'A', { text: 'hi' }), ev(2, 'control.ack', 'X', { target: 'cancel', accepted: true, hadRunningTask: false }))
  assert.equal(t.lastAckSeq, 2)
  assert.deepEqual(shapes(t), ['you:hi', 'system:info:Nothing was running to stop.'])
  assert.equal(t.status.A, 'sent')

  const running = fold(ev(1, 'turn.started', 'A'), ev(2, 'control.ack', 'X', { target: 'cancel', accepted: true, hadRunningTask: true }))
  assert.equal(running.lastAckSeq, 2)
  assert.equal(running.entries.length, 0)
  assert.equal(isTurnRunning(running), true)
})

test('a recovered answer is shown as the agent’s, labelled', () => {
  const t = fold(ev(1, 'turn.recovered_answer', '', { text: 'from before', truncated: false }))
  assert.deepEqual(t.entries, [{ type: 'agent', key: 'e1', seq: 1, text: 'from before', note: 'Recovered from an earlier turn.' }])
})

test('expired history is shown once, before what follows it', () => {
  let t = applyGap(emptyTranscript(), { fromSeq: 1, toSeq: 9 })
  t = applyGap(t, { fromSeq: 1, toSeq: 9 })
  t = applyEvents(t, [ev(10, 'submission.text', 'A', { text: 'still here' })])
  assert.deepEqual(shapes(t), [`system:warn:${GAP_TEXT}`, 'you:still here'])
})

test('kinds this page does not render move the cursor and nothing else', () => {
  const t = fold(ev(1, 'something.new', 'A', { x: 1 }))
  assert.equal(t.lastSeq, 1)
  assert.equal(t.entries.length, 0)
  assert.deepEqual(t.status, {})
})
