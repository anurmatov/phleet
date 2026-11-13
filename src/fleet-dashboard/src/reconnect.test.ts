// Run with `npm test` (node --test, Node ≥ 22.18 strips the types). Type-checked by `tsc -b`.
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { BACKOFF_RESET_MS, KeyedTimers, afterClose, reconnectDelayMs } from './reconnect.ts'
import * as chatProtocol from './chatProtocol.ts'
import { reconcileAgents } from './agentList.ts'
import type { AgentState } from './types'

test('the chat stream and the agent socket share one backoff', () => {
  assert.equal(chatProtocol.reconnectDelayMs, reconnectDelayMs)
  assert.equal(chatProtocol.BACKOFF_RESET_MS, BACKOFF_RESET_MS)
})

test('a socket that keeps failing waits longer each time, up to 30 s, never a fixed 3 s', () => {
  let failures = 0
  const waits: number[] = []
  for (let attempt = 0; attempt < 8; attempt++) {
    const next = afterClose(failures, null, 1_000_000, 1)
    waits.push(next.delayMs)
    failures = next.failures
  }
  assert.deepEqual(waits, [1000, 2000, 4000, 8000, 16000, 30000, 30000, 30000])
  // Jitter: the same attempt lands anywhere in the upper half of its ceiling.
  assert.equal(afterClose(3, null, 0, 0).delayMs, 4000)
  assert.equal(afterClose(3, null, 0, 0.5).delayMs, 6000)
})

test('a connection that held resets the backoff; one that dropped at once does not', () => {
  const opened = 5_000_000
  assert.deepEqual(afterClose(4, opened, opened + BACKOFF_RESET_MS, 1), { delayMs: 1000, failures: 1 })
  assert.deepEqual(afterClose(4, opened, opened + BACKOFF_RESET_MS - 1, 1), { delayMs: 16000, failures: 5 })
  // Accepted and dropped straight away (a proxy that upgrades, then loses its upstream) keeps climbing.
  assert.deepEqual(afterClose(2, opened, opened + 50, 1), { delayMs: 4000, failures: 3 })
})

/** Timers a test can advance by hand. */
function manualClock() {
  let now = 0, next = 1
  const queue = new Map<number, { at: number; fn: () => void }>()
  return {
    start: (fn: () => void, ms: number) => { queue.set(next, { at: now + ms, fn }); return next++ as unknown as ReturnType<typeof setTimeout> },
    stop: (handle: ReturnType<typeof setTimeout>) => { queue.delete(handle as unknown as number) },
    advance(ms: number) {
      now += ms
      for (const [id, timer] of [...queue]) if (timer.at <= now) { queue.delete(id); timer.fn() }
    },
    size: () => queue.size,
  }
}

test('an earlier click\'s timer cannot expire a confirmation armed after it', () => {
  const clock = manualClock()
  const timers = new KeyedTimers(clock.start, clock.stop)
  let state = 'idle'

  // Click Stop: "confirm?" for five seconds.
  state = 'confirming'
  timers.after('stop:a1', 5000, () => { if (state === 'confirming') state = 'idle' })
  clock.advance(3000)
  // Three seconds in, the operator backs out and clicks again: a fresh five seconds.
  state = 'idle'
  state = 'confirming'
  timers.after('stop:a1', 5000, () => { if (state === 'confirming') state = 'idle' })
  // At five seconds from the FIRST click the old timer used to fire and clear this one.
  clock.advance(2000)
  assert.equal(state, 'confirming')
  clock.advance(2999)
  assert.equal(state, 'confirming')
  clock.advance(1)
  assert.equal(state, 'idle')
  assert.equal(clock.size(), 0)
})

test('a result message\'s reset does not wipe the confirmation that follows it', () => {
  const clock = manualClock()
  const timers = new KeyedTimers(clock.start, clock.stop)
  let state = 'success'
  timers.after('stop:a1', 4000, () => { state = 'idle' })   // "Stopped", cleared after 4 s
  clock.advance(3000)
  state = 'confirming'                                      // clicked again
  timers.after('stop:a1', 5000, () => { if (state === 'confirming') state = 'idle' })
  clock.advance(1000)                                       // the reset's time: it no longer exists
  assert.equal(state, 'confirming')
})

test('keys are independent, cancel drops one, cancelAll drops the rest', () => {
  const clock = manualClock()
  const timers = new KeyedTimers(clock.start, clock.stop)
  const fired: string[] = []
  timers.after('stop:a1', 100, () => fired.push('a1'))
  timers.after('stop:a2', 100, () => fired.push('a2'))
  timers.after('delete:a3', 100, () => fired.push('a3'))
  assert.ok(timers.has('stop:a1'))
  timers.cancel('stop:a1')
  timers.cancel('never-set')
  assert.equal(timers.has('stop:a1'), false)
  clock.advance(100)
  assert.deepEqual(fired, ['a2', 'a3'])
  timers.after('stop:a1', 100, () => fired.push('late'))
  timers.cancelAll()
  clock.advance(1000)
  assert.deepEqual(fired, ['a2', 'a3'])
  assert.equal(clock.size(), 0)
})

test('a callback that was already queued when its key was replaced does not run', () => {
  // A `stop` that cannot unqueue a callback (it is already on its way) must still not run it.
  const callbacks: (() => void)[] = []
  const timers = new KeyedTimers((fn) => { callbacks.push(fn); return callbacks.length as unknown as ReturnType<typeof setTimeout> }, () => {})
  const fired: string[] = []
  timers.after('k', 10, () => fired.push('old'))
  timers.after('k', 10, () => fired.push('new'))
  callbacks[0]()
  assert.deepEqual(fired, [])
  callbacks[1]()
  assert.deepEqual(fired, ['new'])
  assert.equal(timers.has('k'), false)
})

const agent = (agentName: string, lastSeen: string, currentTask: string | null = null): AgentState => ({
  agentName, model: null, role: null, currentTask, currentTaskId: null, reportedStatus: 'idle',
  effectiveStatus: 'idle', lastSeen, version: null, queuedCount: 0, queuedMessages: null,
  backgroundTasks: null, containerName: null, containerStartedAt: null, hostPort: null, journal: null,
})

test('after a reconnect the list is exactly what the server lists, so a deleted agent goes', () => {
  const held = { dev: agent('dev', '2026-11-13T10:00:00Z'), gone: agent('gone', '2026-11-13T09:00:00Z') }
  const next = reconcileAgents(held, [agent('dev', '2026-11-13T10:00:05Z'), agent('new', '2026-11-13T10:00:01Z')])
  assert.deepEqual(Object.keys(next).sort(), ['dev', 'new'])
  assert.equal(next.dev.lastSeen, '2026-11-13T10:00:05Z')
})

test('a socket message newer than the fetched row is not rolled back by it', () => {
  const held = { dev: agent('dev', '2026-11-13T10:00:09Z', 'working') }
  const next = reconcileAgents(held, [agent('dev', '2026-11-13T10:00:05Z', null)])
  assert.equal(next.dev.currentTask, 'working')
  // Unreadable times: the fetched row wins, it is what the server said last by request.
  const undated = reconcileAgents({ dev: agent('dev', '', 'old') }, [agent('dev', '', 'fetched')])
  assert.equal(undated.dev.currentTask, 'fetched')
})
