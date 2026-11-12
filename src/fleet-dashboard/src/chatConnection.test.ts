import { test } from 'node:test'
import assert from 'node:assert/strict'
import { LIVENESS_TIMEOUT_MS, PONG_FRAME, PROTOCOL } from './chatProtocol.ts'
import { ChatConnection, type ChatState, type StreamHandlers } from './chatConnection.ts'

// ── fakes ───────────────────────────────────────────────────────────────────

interface Call { method: string; url: string; body: unknown }
type Reply = { status: number; body?: unknown } | 'network-error'
type Route = (call: Call) => Reply | Promise<Reply>

/** The chat service, as fetch sees it. Routes are tried newest first; a route returning undefined passes. */
class FakeServer {
  calls: Call[] = []
  private routes: Array<{ method: string; match: RegExp; reply: Route }> = []

  on(method: string, match: RegExp, reply: Route | Reply): this {
    this.routes.unshift({ method, match, reply: typeof reply === 'function' ? reply : () => reply })
    return this
  }

  fetch = async (url: string, init?: RequestInit): Promise<Response> => {
    const method = init?.method ?? 'GET'
    const call = { method, url, body: init?.body ? JSON.parse(String(init.body)) : null }
    this.calls.push(call)
    const route = this.routes.find(r => r.method === method && r.match.test(url))
    if (!route) throw new Error(`no route for ${method} ${url}`)
    const reply = await route.reply(call)
    if (reply === 'network-error') throw new TypeError('Failed to fetch')
    return new Response(reply.body === undefined ? null : JSON.stringify(reply.body), { status: reply.status })
  }

  callsTo(method: string, match: RegExp): Call[] {
    return this.calls.filter(c => c.method === method && match.test(c.url))
  }
}

class FakeStream {
  sent: string[] = []
  closed = false
  readonly url: string
  readonly handlers: StreamHandlers
  constructor(url: string, handlers: StreamHandlers) { this.url = url; this.handlers = handlers }
  deliver(frame: unknown) { this.handlers.message(typeof frame === 'string' ? frame : JSON.stringify(frame)) }
  hello() { this.deliver({ protocol: PROTOCOL, kind: 'hello', conversationId: 'C1', nextSeq: 1, retainedFloorSeq: 1 }) }
  serverClose(code: number, reason = '') { this.handlers.close(code, reason) }
}

/** Timers in virtual time. */
class Clock {
  now = 0
  private tasks: Array<{ at: number; fn: () => void; live: boolean; ms: number }> = []
  schedule = (fn: () => void, ms: number) => {
    const task = { at: this.now + ms, fn, live: true, ms }
    this.tasks.push(task)
    return () => { task.live = false }
  }
  /** Delays of the timers still waiting, other than the stream watchdog. */
  pending(): number[] { return this.tasks.filter(t => t.live && t.ms !== LIVENESS_TIMEOUT_MS).map(t => t.ms) }
  advance(ms: number) {
    const until = this.now + ms
    for (;;) {
      const due = this.tasks.filter(t => t.live && t.at <= until).sort((a, b) => a.at - b.at)[0]
      if (!due) break
      due.live = false
      this.now = due.at
      due.fn()
    }
    this.now = until
  }
}

/** Let every awaited fetch and JSON read settle. */
const settle = () => new Promise(resolve => setTimeout(resolve, 5))

function event(seq: number, kind: string, submissionId = '', payload: Record<string, unknown> = {}) {
  return {
    protocol: PROTOCOL, eventId: `e${seq}`, seq, emittedAt: '2026-11-13T00:00:00Z', kind,
    identity: { principalId: 'owner', role: 'owner', channelId: 'client', conversationId: 'C1', submissionId, attempt: 1 },
    payload,
  }
}

function catchUp(events: unknown[], hasMore = false) {
  return { status: 200, body: { protocol: PROTOCOL, events, nextAfterSeq: 0, hasMore } }
}

/** A chat service with one conversation, C1, holding `history`. */
function chatService(history: unknown[] = []) {
  return new FakeServer()
    .on('GET', /^\/v1\/session$/, { status: 200, body: { protocol: PROTOCOL, principalId: 'owner', limits: { inboundTextBytes: 64, catchUpLimitDefault: 200, catchUpLimitMax: 1000, identifierMaxLength: 128, outboundBufferEvents: 256 } } })
    .on('POST', /^\/v1\/conversations$/, { status: 200, body: { protocol: PROTOCOL, conversationId: 'C1', agent: 'cto', nextSeq: history.length + 1, retainedFloorSeq: 1 } })
    .on('GET', /^\/v1\/conversations\/C1\/events/, call => {
      const after = Number(new URL(call.url, 'http://x').searchParams.get('afterSeq'))
      return catchUp(history.filter(e => (e as { seq: number }).seq > after))
    })
}

function connect(server: FakeServer, options: { ids?: string[] } = {}) {
  const clock = new Clock()
  const streams: FakeStream[] = []
  const states: ChatState[] = []
  const ids = [...(options.ids ?? ['sub-1', 'sub-2', 'sub-3'])]
  const chat = new ChatConnection('cto', {
    fetch: server.fetch,
    openStream: (url, handlers) => {
      const stream = new FakeStream(url, handlers)
      streams.push(stream)
      return { send: data => { stream.sent.push(data) }, close: () => { stream.closed = true } }
    },
    schedule: clock.schedule,
    random: () => 1,
    now: () => clock.now,
    wsBase: 'wss://fleet.example',
    clientInstanceId: 'browser-1',
    newId: () => ids.shift() ?? 'sub-x',
  }, state => states.push(state))
  return { chat, clock, streams, states, last: () => chat.current, stream: () => streams[streams.length - 1] }
}

// ── connecting ──────────────────────────────────────────────────────────────

test('first connection: limits, the "main" thread with this agent, catch-up from 0, then the stream at the cursor', async () => {
  const server = chatService([
    event(1, 'submission.text', 'S1', { text: 'hello' }),
    event(2, 'turn.final', 'S1', { text: 'hi there', completion: 'completed' }),
  ])
  const { chat, stream, last } = connect(server)
  chat.start()
  await settle()

  assert.deepEqual(server.calls.map(c => `${c.method} ${c.url}`), [
    'GET /v1/session',
    'POST /v1/conversations',
    'GET /v1/conversations/C1/events?afterSeq=0&limit=1000',
  ])
  assert.deepEqual(server.calls[1].body, { protocol: PROTOCOL, externalRef: 'main', agent: 'cto', clientInstanceId: 'browser-1' })
  assert.equal(stream().url, 'wss://fleet.example/v1/conversations/C1/stream?clientInstanceId=browser-1&afterSeq=2')
  assert.equal(last().phase, 'connecting')
  assert.equal(last().connectedOnce, false)
  assert.equal(last().limits.inboundTextBytes, 64)
  assert.deepEqual(last().transcript.entries.map(e => e.type), ['you', 'agent'])

  stream().hello()
  assert.equal(last().phase, 'live')
  assert.equal(last().connectedOnce, true)

  stream().deliver(event(3, 'submission.text', 'S2', { text: 'again' }))
  assert.equal(last().transcript.lastSeq, 3)
  assert.equal(last().transcript.entries.length, 3)
  chat.dispose()
})

test('catch-up reads every page before the stream attaches', async () => {
  const server = chatService()
  server.on('GET', /^\/v1\/conversations\/C1\/events/, call => {
    const after = Number(new URL(call.url, 'http://x').searchParams.get('afterSeq'))
    return after === 0
      ? catchUp([event(1, 'submission.text', 'S1', { text: 'a' }), event(2, 'turn.started', 'S1')], true)
      : catchUp([event(3, 'turn.final', 'S1', { text: 'b', completion: 'completed' })], false)
  })
  const { chat, stream } = connect(server)
  chat.start()
  await settle()
  assert.deepEqual(server.callsTo('GET', /events/).map(c => c.url), [
    '/v1/conversations/C1/events?afterSeq=0&limit=1000',
    '/v1/conversations/C1/events?afterSeq=2&limit=1000',
  ])
  assert.match(stream().url, /afterSeq=3$/)
  chat.dispose()
})

test('a ping is answered with a pong, and that is the only thing the page ever sends on the stream', async () => {
  const { chat, stream } = connect(chatService())
  chat.start()
  await settle()
  stream().hello()
  stream().deliver({ kind: 'ping' })
  stream().deliver({ kind: 'ping' })
  assert.deepEqual(stream().sent, [PONG_FRAME, PONG_FRAME])
  chat.dispose()
})

test('a lost stream reconnects after a backoff and catches up from the cursor, without opening the thread again', async () => {
  const history = [event(1, 'submission.text', 'S1', { text: 'hello' })]
  const server = chatService(history)
  const { chat, clock, streams, stream, last } = connect(server)
  chat.start()
  await settle()
  stream().hello()

  // Something lands while the network is down.
  history.push(event(2, 'turn.final', 'S1', { text: 'answer', completion: 'completed' }))
  stream().serverClose(1006)
  assert.equal(last().phase, 'waiting')
  assert.deepEqual(clock.pending(), [1000])

  clock.advance(1000)
  assert.equal(last().phase, 'connecting')
  assert.equal(last().connectedOnce, true, 'a reconnect, not a first connection')
  await settle()
  assert.equal(server.callsTo('GET', /\/v1\/session/).length, 1)
  assert.equal(server.callsTo('POST', /^\/v1\/conversations$/).length, 1)
  assert.equal(server.callsTo('GET', /events/).at(-1)!.url, '/v1/conversations/C1/events?afterSeq=1&limit=1000')
  assert.equal(streams.length, 2)
  assert.match(stream().url, /afterSeq=2$/)
  assert.deepEqual(last().transcript.entries.map(e => e.type), ['you', 'agent'])
  chat.dispose()
})

test('the backoff grows while reconnects keep failing, and resets after a connection that stayed up a minute', async () => {
  const server = chatService()
  const { chat, clock, stream, last } = connect(server)
  chat.start()
  await settle()
  stream().hello()

  server.on('GET', /\/events/, 'network-error')
  stream().serverClose(1006)
  assert.deepEqual(clock.pending(), [1000])
  clock.advance(1000); await settle()
  assert.deepEqual(clock.pending(), [2000])
  clock.advance(2000); await settle()
  assert.deepEqual(clock.pending(), [4000])
  assert.equal(last().phase, 'waiting')
  assert.equal(last().problem, 'The chat service did not answer.')

  // The service comes back; the connection stays up for a minute, then drops.
  server.on('GET', /\/events/, catchUp([]))
  clock.advance(4000); await settle()
  stream().hello()
  clock.advance(30_000); stream().deliver({ kind: 'ping' })
  clock.advance(30_000); stream().deliver({ kind: 'ping' })
  stream().serverClose(1006)
  assert.deepEqual(clock.pending(), [1000])
  chat.dispose()
})

test('a stream that goes quiet is replaced: no frame for 45 s means it is dead', async () => {
  const { chat, clock, streams, stream, last } = connect(chatService())
  chat.start()
  await settle()
  stream().hello()
  clock.advance(LIVENESS_TIMEOUT_MS - 1)
  stream().deliver({ kind: 'ping' })
  clock.advance(LIVENESS_TIMEOUT_MS - 1)
  assert.equal(last().phase, 'live')

  clock.advance(1)
  assert.equal(streams[0].closed, true)
  assert.equal(last().phase, 'waiting')
  assert.equal(last().problem, 'The connection to the chat service went quiet.')
  clock.advance(1000); await settle()
  assert.equal(streams.length, 2)
  chat.dispose()
})

test('superseded by another tab: stays closed, no timer, until the person asks for it here', async () => {
  const server = chatService()
  const { chat, clock, streams, stream, last } = connect(server)
  chat.start()
  await settle()
  stream().hello()
  stream().serverClose(4409, 'superseded')
  assert.equal(last().phase, 'stopped')
  assert.equal(last().stopReason, 'superseded')
  assert.deepEqual(clock.pending(), [])

  chat.reconnectNow()
  await settle()
  assert.equal(streams.length, 2)
  assert.equal(last().phase, 'connecting')
  chat.dispose()
})

test('a rate-limited stream waits exactly as long as the chat service said', async () => {
  const { chat, clock, stream } = connect(chatService())
  chat.start()
  await settle()
  stream().serverClose(4429, '7')
  assert.deepEqual(clock.pending(), [7000])
  chat.dispose()
})

test('chat switched off for the agent: stopped with the sentence that says where to turn it on', async () => {
  const server = chatService()
    .on('POST', /^\/v1\/conversations$/, { status: 409, body: { protocol: PROTOCOL, code: 'agent_unavailable', message: 'This agent does not accept chat.' } })
  const { chat, clock, streams, last } = connect(server)
  chat.start()
  await settle()
  assert.equal(last().phase, 'stopped')
  assert.equal(last().stopReason, 'agent-unavailable')
  assert.equal(last().problem, 'Chat is off for this agent. Turn it on in the agent’s settings (Config → General → Chat).')
  assert.equal(streams.length, 0)
  assert.deepEqual(clock.pending(), [])
  assert.equal(chat.canSend(), false)
  chat.dispose()
})

test('signed out at the chat service: stopped, not retried in a loop', async () => {
  const server = chatService().on('GET', /^\/v1\/session$/, { status: 401, body: { protocol: PROTOCOL, code: 'unauthorized', message: 'x' } })
  const { chat, clock, last } = connect(server)
  chat.start()
  await settle()
  assert.equal(last().stopReason, 'signed-out')
  assert.deepEqual(clock.pending(), [])
  chat.dispose()
})

test('no conversation routes: chat is not set up, said once and not retried', async () => {
  const server = chatService().on('POST', /^\/v1\/conversations$/, { status: 404 })
  const { chat, clock, last } = connect(server)
  chat.start()
  await settle()
  assert.equal(last().stopReason, 'not-set-up')
  assert.match(last().problem!, /Chat isn’t set up/)
  assert.deepEqual(clock.pending(), [])
  chat.dispose()
})

test('a chat service that is not reachable yet is retried, and the reason is shown', async () => {
  const server = chatService().on('GET', /^\/v1\/session$/, { status: 502 })
  const { chat, clock, streams, last } = connect(server)
  chat.start()
  await settle()
  assert.equal(last().phase, 'waiting')
  assert.equal(last().problem, 'The chat service is not available (HTTP 502).')

  server.on('GET', /^\/v1\/session$/, { status: 200, body: { limits: {} } })
  clock.advance(1000); await settle()
  assert.equal(streams.length, 1)
  chat.dispose()
})

test('a cursor the conversation does not have: read it again from the start, once', async () => {
  const server = chatService()
  let refused = 0
  server.on('GET', /\/events/, call => {
    if (call.url.includes('afterSeq=5')) { refused++; return { status: 400, body: { code: 'invalid_cursor' } } }
    return catchUp([event(1, 'submission.text', 'S1', { text: 'restored' })])
  })
  const { chat, clock, stream, last } = connect(server)
  // Pretend this page had read further than the restored database holds.
  chat.start()
  await settle()
  stream().hello()
  for (const seq of [2, 3, 4, 5]) stream().deliver(event(seq, 'turn.progress', 'S1', { activity: 'typing' }))
  stream().serverClose(1006)
  clock.advance(1000); await settle()

  assert.equal(refused, 1)
  assert.deepEqual(server.callsTo('GET', /events/).map(c => c.url).slice(-2), [
    '/v1/conversations/C1/events?afterSeq=5&limit=1000',
    '/v1/conversations/C1/events?afterSeq=0&limit=1000',
  ])
  assert.equal(last().transcript.lastSeq, 1)
  assert.match(stream().url, /afterSeq=1$/)
  chat.dispose()
})

test('a conversation that is gone is opened again from nothing', async () => {
  const server = chatService([event(1, 'submission.text', 'S1', { text: 'old' })])
  const { chat, clock, stream, last } = connect(server)
  chat.start()
  await settle()
  stream().serverClose(1006)
  server.on('GET', /\/v1\/conversations\/C1\/events/, { status: 404, body: { code: 'conversation_not_found' } })
  server.on('POST', /^\/v1\/conversations$/, { status: 200, body: { conversationId: 'C2', nextSeq: 1, retainedFloorSeq: 1 } })
  server.on('GET', /\/v1\/conversations\/C2\/events/, catchUp([]))

  clock.advance(1000); await settle()
  assert.equal(last().phase, 'waiting')
  assert.equal(last().conversationId, null)
  assert.equal(last().transcript.entries.length, 0)

  clock.advance(clock.pending()[0]); await settle()
  assert.equal(last().conversationId, 'C2')
  assert.match(stream().url, /\/C2\/stream/)
  chat.dispose()
})

// ── sending ─────────────────────────────────────────────────────────────────

async function live(server = chatService()) {
  const harness = connect(server)
  harness.chat.start()
  await settle()
  harness.stream().hello()
  return harness
}

test('sending: a create with the same id as idempotency key, shown as sending, then sent, then replaced by the transcript', async () => {
  const server = chatService()
  let release: (reply: Reply) => void = () => {}
  server.on('POST', /\/submissions$/, () => new Promise<Reply>(resolve => { release = resolve }))
  const { chat, stream, last } = await live(server)

  assert.equal(chat.send('hello *there*'), null)
  assert.deepEqual(last().pending.map(p => [p.text, p.status]), [['hello *there*', 'sending']])
  await settle()
  const post = server.callsTo('POST', /\/submissions$/)[0]
  assert.equal(post.url, '/v1/conversations/C1/submissions')
  assert.deepEqual(post.body, { protocol: PROTOCOL, type: 'create', submissionId: 'sub-1', idempotencyKey: 'sub-1', text: 'hello *there*' })

  release({ status: 201, body: { protocol: PROTOCOL, submissionId: 'sub-1', acceptedSeq: 1 } })
  await settle()
  assert.deepEqual(last().pending.map(p => [p.status, p.acceptedSeq]), [['sent', 1]])

  stream().deliver(event(1, 'submission.text', 'INTERNAL-1', { text: 'hello *there*' }))
  assert.deepEqual(last().pending, [])
  assert.deepEqual(last().transcript.entries.map(e => e.type), ['you'])
  chat.dispose()
})

test('when the stream shows the message before the answer arrives, it is not shown twice', async () => {
  const server = chatService()
  let release: (reply: Reply) => void = () => {}
  server.on('POST', /\/submissions$/, () => new Promise<Reply>(resolve => { release = resolve }))
  const { chat, stream, last } = await live(server)
  chat.send('quick')
  await settle()
  stream().deliver(event(1, 'submission.text', 'INTERNAL-1', { text: 'quick' }))
  release({ status: 201, body: { submissionId: 'sub-1', acceptedSeq: 1 } })
  await settle()
  assert.deepEqual(last().pending, [])
  chat.dispose()
})

test('a send whose answer was lost can be retried, with the same ids, so it cannot run twice', async () => {
  const server = chatService().on('POST', /\/submissions$/, 'network-error')
  const { chat, last } = await live(server)
  chat.send('important')
  await settle()
  const failed = last().pending[0]
  assert.equal(failed.status, 'failed')
  assert.equal(failed.canRetry, true)
  assert.match(failed.problem!, /cannot start a second turn/)

  server.on('POST', /\/submissions$/, { status: 201, body: { submissionId: 'sub-1', acceptedSeq: 4 } })
  chat.retry('sub-1')
  assert.equal(last().pending[0].status, 'sending')
  await settle()
  const posts = server.callsTo('POST', /\/submissions$/)
  assert.equal(posts.length, 2)
  assert.deepEqual(posts[1].body, posts[0].body)
  assert.equal(last().pending[0].status, 'sent')
  chat.dispose()
})

test('a retry of a message that was already durable (202) leaves it to the transcript', async () => {
  const server = chatService().on('POST', /\/submissions$/, 'network-error')
  const { chat, last } = await live(server)
  chat.send('maybe sent')
  await settle()
  server.on('POST', /\/submissions$/, { status: 202, body: { submissionId: 'sub-1' } })
  chat.retry('sub-1')
  await settle()
  assert.deepEqual(last().pending, [])
  chat.dispose()
})

test('chat switched off while sending: the message says how to turn it on, and can be retried after', async () => {
  const server = chatService().on('POST', /\/submissions$/, { status: 409, body: { code: 'agent_unavailable' } })
  const { chat, last } = await live(server)
  chat.send('hi')
  await settle()
  assert.deepEqual(
    [last().pending[0].status, last().pending[0].problem, last().pending[0].canRetry],
    ['failed', 'Chat is off for this agent. Turn it on in the agent’s settings (Config → General → Chat). Then retry.', true])
  chat.dispose()
})

test('too long for the chat service: refused before any request, and a 413 is not offered a retry', async () => {
  const server = chatService().on('POST', /\/submissions$/, { status: 413, body: { code: 'payload_too_large' } })
  const { chat, last } = await live(server)
  // The fake session allows 64 bytes; 33 two-byte characters are 66.
  assert.equal(chat.send('é'.repeat(33)), 'This message is too long to send.')
  assert.equal(chat.send('   '), 'Type a message first.')
  assert.equal(server.callsTo('POST', /\/submissions$/).length, 0)
  assert.deepEqual(last().pending, [])

  chat.send('fits')
  await settle()
  assert.equal(last().pending[0].canRetry, false)
  chat.retry('sub-1')
  assert.equal(server.callsTo('POST', /\/submissions$/).length, 1)
  chat.discard('sub-1')
  assert.deepEqual(last().pending, [])
  chat.dispose()
})

test('nothing can be sent before the thread is open', async () => {
  const server = chatService().on('POST', /^\/v1\/conversations$/, () => new Promise<Reply>(() => {}))
  const { chat } = connect(server)
  chat.start()
  await settle()
  assert.equal(chat.canSend(), false)
  assert.equal(chat.send('early'), 'Not connected yet.')
  chat.dispose()
})

// ── stop ────────────────────────────────────────────────────────────────────

test('Stop cancels the current turn and shows stopping until the turn ends', async () => {
  const server = chatService().on('POST', /:cancel$/, { status: 200, body: { protocol: PROTOCOL, accepted: true } })
  const { chat, stream, last } = await live(server)
  stream().deliver(event(1, 'turn.started', 'S1'))

  await chat.stopTurn()
  const cancel = server.callsTo('POST', /:cancel$/)
  assert.equal(cancel[0].url, '/v1/conversations/C1:cancel')
  assert.deepEqual(cancel[0].body, { protocol: PROTOCOL, scope: 'current' })
  assert.equal(last().stopping, true)

  stream().deliver(event(2, 'control.ack', 'X', { target: 'cancel', accepted: true, hadRunningTask: true }))
  assert.equal(last().stopping, false)
  stream().deliver(event(3, 'turn.canceled', 'S1', { reason: 'user', mergedSubmissionIds: ['S1'] }))
  assert.equal(last().transcript.status.S1, 'canceled')
  chat.dispose()
})

test('Stop also clears when the turn ends before any acknowledgement', async () => {
  const server = chatService().on('POST', /:cancel$/, { status: 200, body: {} })
  const { chat, stream, last } = await live(server)
  stream().deliver(event(1, 'turn.started', 'S1'))
  await chat.stopTurn()
  stream().deliver(event(2, 'turn.final', 'S1', { text: 'done anyway', completion: 'completed' }))
  assert.equal(last().stopping, false)
  chat.dispose()
})

test('a Stop still waiting when the transcript is read again from the start does not outlive it', async () => {
  const server = chatService().on('POST', /:cancel$/, { status: 200, body: {} })
  const { chat, clock, stream, last } = await live(server)
  stream().deliver(event(1, 'turn.started', 'S1'))
  await chat.stopTurn()
  assert.equal(last().stopping, true)

  server.on('GET', /\/events/, call => (call.url.includes('afterSeq=1&')
    ? { status: 400, body: { code: 'invalid_cursor' } }
    : catchUp([])))
  stream().serverClose(1006)
  clock.advance(1000); await settle()
  assert.equal(last().transcript.lastSeq, 0)
  assert.equal(last().stopping, false)
  chat.dispose()
})

test('a conversation that is gone takes its accepted sends with it; a failed one stays to be retried', async () => {
  const server = chatService()
  let calls = 0
  server.on('POST', /\/submissions$/, () => (++calls === 1 ? { status: 201, body: { acceptedSeq: 7 } } : 'network-error'))
  const { chat, clock, stream, last } = await live(server)
  chat.send('accepted, not yet shown')
  chat.send('never confirmed')
  await settle()
  assert.deepEqual(last().pending.map(p => p.status), ['sent', 'failed'])

  server.on('GET', /\/v1\/conversations\/C1\/events/, { status: 404, body: { code: 'conversation_not_found' } })
  stream().serverClose(1006)
  clock.advance(1000); await settle()
  assert.equal(last().conversationId, null)
  assert.deepEqual(last().pending.map(p => p.text), ['never confirmed'])
  chat.dispose()
})

test('a Stop that is refused says why and can be pressed again', async () => {
  const server = chatService().on('POST', /:cancel$/, { status: 409, body: { code: 'agent_unavailable' } })
  const { chat, stream, last } = await live(server)
  stream().deliver(event(1, 'turn.started', 'S1'))
  await chat.stopTurn()
  assert.equal(last().stopping, false)
  assert.equal(last().notice, 'Chat is off for this agent. Turn it on in the agent’s settings (Config → General → Chat).')
  chat.dismissNotice()
  assert.equal(last().notice, null)
  chat.dispose()
})

// ── lifetime ────────────────────────────────────────────────────────────────

test('after dispose nothing changes: the stream is closed, timers are dead, late answers are ignored', async () => {
  const server = chatService()
  let release: (reply: Reply) => void = () => {}
  server.on('POST', /\/submissions$/, () => new Promise<Reply>(resolve => { release = resolve }))
  const { chat, clock, streams, stream, states } = await live(server)
  chat.send('bye')
  await settle()
  chat.dispose()
  assert.equal(stream().closed, true)

  const seen = states.length
  release({ status: 201, body: { acceptedSeq: 1 } })
  await settle()
  stream().deliver(event(1, 'submission.text', 'S', { text: 'late' }))
  stream().serverClose(1006)
  clock.advance(60_000)
  await settle()
  assert.equal(states.length, seen)
  assert.equal(streams.length, 1)
})

test('frames and closes from a stream that was replaced are ignored', async () => {
  const { chat, clock, streams, last } = await live()
  const first = streams[0]
  first.serverClose(1006)
  clock.advance(1000); await settle()
  assert.equal(streams.length, 2)

  // The old socket's late frames and close must not touch the new connection.
  first.deliver(event(9, 'submission.text', 'S9', { text: 'ghost' }))
  first.serverClose(4409)
  assert.equal(last().transcript.entries.length, 0)
  assert.equal(last().phase, 'connecting')
  chat.dispose()
})

test('navigation retains an unconfirmed send and retries only with its original id', async () => {
  let release!: (value: Reply) => void
  const server = chatService().on('POST', /\/submissions$/, () => new Promise<Reply>(resolve => { release = resolve }))
  const first = connect(server, { ids: ['original-id'] })
  first.chat.start()
  await settle()
  first.stream().hello()
  assert.equal(first.chat.send('do not lose this'), null)
  await settle()
  first.chat.dispose()
  const retained = first.chat.current
  release('network-error')
  await settle()
  const resumed = new ChatConnection('cto', {
    fetch: server.fetch,
    openStream: () => ({ send() {}, close() {} }),
    schedule: first.clock.schedule, random: () => 1, now: () => first.clock.now,
    wsBase: 'wss://example.invalid', clientInstanceId: 'browser-1', newId: () => 'WRONG-new-id',
  }, () => {}, retained)
  resumed.start()
  await settle()
  assert.equal(resumed.current.pending[0].text, 'do not lose this')
  assert.equal(resumed.current.pending[0].submissionId, 'original-id')
  assert.equal(resumed.current.pending[0].status, 'failed')
  assert.equal(server.callsTo('POST', /\/submissions$/).length, 1, 'returning never resends automatically')
  server.on('POST', /\/submissions$/, { status: 202 })
  resumed.retry('original-id')
  await settle()
  const calls = server.callsTo('POST', /\/submissions$/)
  assert.deepEqual(calls[1].body, calls[0].body)
  resumed.dispose()
})
