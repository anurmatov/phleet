import { test } from 'node:test'
import assert from 'node:assert/strict'
import {
  CLIENT_INSTANCE_KEY, DEFAULT_LIMITS, PONG_FRAME, PROTOCOL, THREAD_REF,
  cancelBody, closeAction, errorCode, eventsUrl, failureAction, isValidIdentifier, loadClientInstanceId,
  newIdentifier, openBody, parseFrame, pickAgent, readAcceptedSeq, readCatchUp, readChatAgents, readLimits, readOpen,
  reconnectDelayMs, sendProblem, shouldSendOnKey, sizeNote, streamUrl, submitBody, utf8Bytes, wsBaseFor,
} from './chatProtocol.ts'

// ── identifiers ─────────────────────────────────────────────────────────────

test('identifiers: letters, digits, _ and -, 1 to 128 characters, and nothing else', () => {
  assert.equal(isValidIdentifier('a'), true)
  assert.equal(isValidIdentifier('A-z_09'), true)
  assert.equal(isValidIdentifier('x'.repeat(128)), true)
  assert.equal(isValidIdentifier('x'.repeat(129)), false)
  assert.equal(isValidIdentifier(''), false)
  assert.equal(isValidIdentifier('has space'), false)
  assert.equal(isValidIdentifier('ünï'), false)
  assert.equal(isValidIdentifier('a:b'), false)
  assert.equal(isValidIdentifier(null), false)
  assert.equal(isValidIdentifier(42), false)
})

test('a new identifier is 32 hex characters from 16 random bytes, and valid on the wire', () => {
  const id = newIdentifier(bytes => bytes.map((_, i) => i * 17))
  assert.equal(id, '00112233445566778899aabbccddeeff')
  const random = newIdentifier()
  assert.match(random, /^[0-9a-f]{32}$/)
  assert.equal(isValidIdentifier(random), true)
  assert.notEqual(newIdentifier(), random)
})

function memoryStorage(initial: Record<string, string> = {}) {
  const values = new Map(Object.entries(initial))
  return {
    values,
    getItem: (key: string) => values.get(key) ?? null,
    setItem: (key: string, value: string) => { values.set(key, value) },
  }
}

test('the client instance id is made once and then kept', () => {
  const storage = memoryStorage()
  const first = loadClientInstanceId(storage, () => 'made-once')
  assert.equal(first, 'made-once')
  assert.equal(storage.values.get(CLIENT_INSTANCE_KEY), 'made-once')
  assert.equal(loadClientInstanceId(storage, () => 'made-twice'), 'made-once')
})

test('a stored client instance id that is not a valid identifier is replaced', () => {
  const storage = memoryStorage({ [CLIENT_INSTANCE_KEY]: 'not valid!' })
  assert.equal(loadClientInstanceId(storage, () => 'fresh'), 'fresh')
  assert.equal(storage.values.get(CLIENT_INSTANCE_KEY), 'fresh')
})

test('storage that is missing or refuses still gives an id, never an error', () => {
  assert.equal(loadClientInstanceId(null, () => 'page-only'), 'page-only')
  const refusing = {
    getItem: () => { throw new Error('blocked') },
    setItem: () => { throw new Error('blocked') },
  }
  assert.equal(loadClientInstanceId(refusing, () => 'page-only'), 'page-only')
})

test('message size is counted in UTF-8 bytes, as the chat service counts it', () => {
  assert.equal(utf8Bytes('abc'), 3)
  assert.equal(utf8Bytes('é'), 2)
  assert.equal(utf8Bytes('😀'), 4)
  assert.equal(utf8Bytes(''), 0)
})

// ── requests ────────────────────────────────────────────────────────────────

test('opening a thread names the agent, the shared "main" thread and this browser', () => {
  assert.deepEqual(openBody('cto-agent', 'browser-1'),
    { protocol: 'fleet.conversation.v1', externalRef: 'main', agent: 'cto-agent', clientInstanceId: 'browser-1' })
  assert.equal(THREAD_REF, 'main')
  assert.equal(PROTOCOL, 'fleet.conversation.v1')
})

test('a message is a create whose idempotency key is its submission id, with the text exactly as typed', () => {
  assert.deepEqual(submitBody('s1', '  hello\nworld  '),
    { protocol: PROTOCOL, type: 'create', submissionId: 's1', idempotencyKey: 's1', text: '  hello\nworld  ' })
})

test('stop cancels the current turn only', () => {
  assert.deepEqual(cancelBody(), { protocol: PROTOCOL, scope: 'current' })
})

test('urls encode the conversation id and carry no credential', () => {
  assert.equal(eventsUrl('c/1', 5, 200), '/v1/conversations/c%2F1/events?afterSeq=5&limit=200')
  const url = streamUrl('wss://fleet.example', 'C1', 'browser-1', 12)
  assert.equal(url, 'wss://fleet.example/v1/conversations/C1/stream?clientInstanceId=browser-1&afterSeq=12')
  assert.doesNotMatch(url, /token|session|auth/i)
})

test('the stream uses wss on an https page and ws on an http one, on the same host', () => {
  assert.equal(wsBaseFor({ protocol: 'https:', host: 'fleet.example' }), 'wss://fleet.example')
  assert.equal(wsBaseFor({ protocol: 'http:', host: '192.0.2.1:8080' }), 'ws://192.0.2.1:8080')
})

// ── answers ─────────────────────────────────────────────────────────────────

test('the agent list: available, names, and the display name falling back to the name', () => {
  assert.deepEqual(readChatAgents(200, {
    available: true,
    agents: [
      { agentName: 'cto', displayName: 'CTO' },
      { agentName: 'dev', displayName: '' },
      { displayName: 'no name' },
      'junk',
    ],
  }), {
    status: 'ok', available: true,
    agents: [{ agentName: 'cto', displayName: 'CTO' }, { agentName: 'dev', displayName: 'dev' }],
  })
})

test('chat not set up: available false, and an orchestrator without the route', () => {
  assert.deepEqual(readChatAgents(200, { available: false, agents: [] }), { status: 'ok', available: false, agents: [] })
  assert.deepEqual(readChatAgents(404, null), { status: 'ok', available: false, agents: [] })
})

test('an agent list that cannot be read is an error, not "no chat"', () => {
  assert.deepEqual(readChatAgents(502, null), { status: 'error', httpStatus: 502 })
  assert.deepEqual(readChatAgents(200, 'html'), { status: 'error', httpStatus: 200 })
})

test('limits: what the session says, each falling back on its own, catch-up pages capped at 1000', () => {
  assert.deepEqual(readLimits({ limits: { inboundTextBytes: 1000, catchUpLimitMax: 500 } }),
    { inboundTextBytes: 1000, catchUpLimitMax: 500 })
  assert.deepEqual(readLimits({ limits: { inboundTextBytes: 0, catchUpLimitMax: 5000 } }),
    { inboundTextBytes: DEFAULT_LIMITS.inboundTextBytes, catchUpLimitMax: 1000 })
  assert.deepEqual(readLimits(null), DEFAULT_LIMITS)
  assert.deepEqual(DEFAULT_LIMITS, { inboundTextBytes: 32768, catchUpLimitMax: 1000 })
})

test('open answer: the conversation id or nothing', () => {
  assert.equal(readOpen({ conversationId: 'C1', nextSeq: 4 }), 'C1')
  assert.equal(readOpen({ conversationId: '' }), null)
  assert.equal(readOpen({}), null)
})

test('catch-up page: events in order, the gap, hasMore; events that cannot be placed are dropped', () => {
  const page = readCatchUp({
    gap: { fromSeq: 1, toSeq: 9, retainedFloorSeq: 10 },
    events: [
      { eventId: 'e10', seq: 10, kind: 'submission.text', identity: { submissionId: 'S1' }, payload: { text: 'hi' } },
      { eventId: 'e11', seq: 11, kind: 'turn.started', identity: {} },
      { eventId: 'bad', seq: null, kind: 'conversation.replay_gap' },
      { seq: 12, kind: 'turn.final' },
    ],
    nextAfterSeq: 11,
    hasMore: true,
  })
  assert.deepEqual(page, {
    gap: { fromSeq: 1, toSeq: 9 },
    events: [
      { eventId: 'e10', seq: 10, kind: 'submission.text', identity: { submissionId: 'S1' }, payload: { text: 'hi' } },
      { eventId: 'e11', seq: 11, kind: 'turn.started', identity: { submissionId: '' }, payload: {} },
    ],
    hasMore: true,
  })
  assert.equal(readCatchUp({ events: [], hasMore: false })?.gap, null)
  assert.equal(readCatchUp({ hasMore: false }), null)
  assert.equal(readCatchUp('nope'), null)
})

test('accepted seq: a positive whole number or nothing', () => {
  assert.equal(readAcceptedSeq({ submissionId: 's', acceptedSeq: 7 }), 7)
  assert.equal(readAcceptedSeq({ acceptedSeq: 0 }), null)
  assert.equal(readAcceptedSeq({ acceptedSeq: '7' }), null)
  assert.equal(readAcceptedSeq(null), null)
})

test('stream frames: hello, ping, events, and anything else ignored', () => {
  assert.deepEqual(parseFrame('{"protocol":"fleet.conversation.v1","kind":"hello","conversationId":"C1","nextSeq":3}'), { type: 'hello' })
  assert.deepEqual(parseFrame('{"kind":"ping"}'), { type: 'ping' })
  assert.deepEqual(parseFrame(JSON.stringify({ eventId: 'e1', seq: 1, kind: 'turn.started', identity: { submissionId: 'S' }, payload: {} })),
    { type: 'event', event: { eventId: 'e1', seq: 1, kind: 'turn.started', identity: { submissionId: 'S' }, payload: {} } })
  assert.deepEqual(parseFrame('not json'), { type: 'other' })
  assert.deepEqual(parseFrame('{"kind":"turn.final"}'), { type: 'other' })
  assert.deepEqual(parseFrame(new ArrayBuffer(4)), { type: 'other' })
  assert.equal(PONG_FRAME, '{"kind":"pong"}')
})

test('error code: the code of an error body, else empty', () => {
  assert.equal(errorCode({ protocol: PROTOCOL, code: 'agent_unavailable', message: 'x' }), 'agent_unavailable')
  assert.equal(errorCode(null), '')
  assert.equal(errorCode({ code: 4 }), '')
})

// ── refusals ────────────────────────────────────────────────────────────────

test('what a refusal means while opening or reading the conversation', () => {
  const r = (httpStatus: number, code = '') => ({ httpStatus, code })
  // Nothing reachable, busy, or broken: try again.
  assert.equal(failureAction('session', r(0)), 'retry')
  assert.equal(failureAction('session', r(502)), 'retry')
  assert.equal(failureAction('open', r(503, 'internal')), 'retry')
  assert.equal(failureAction('catch-up', r(429, 'rate_limited')), 'retry')
  // Sign-in, wherever it happens.
  assert.equal(failureAction('session', r(401, 'unauthorized')), 'signed-out')
  assert.equal(failureAction('catch-up', r(401)), 'signed-out')
  // Chat off for the agent.
  assert.equal(failureAction('open', r(409, 'agent_unavailable')), 'agent-unavailable')
  // No conversation routes at all.
  assert.equal(failureAction('open', r(404)), 'not-set-up')
  assert.equal(failureAction('session', r(404)), 'not-set-up')
  // The conversation or the cursor, only while reading.
  assert.equal(failureAction('catch-up', r(404, 'conversation_not_found')), 'reopen')
  assert.equal(failureAction('catch-up', r(400, 'invalid_cursor')), 'reset-cursor')
  // Anything else asking again cannot change.
  assert.equal(failureAction('open', r(400, 'unsupported_protocol')), 'refused')
})

test('why a message did not go out, and whether a retry can help', () => {
  const p = (httpStatus: number, code = '') => sendProblem({ httpStatus, code })
  assert.deepEqual(p(0), {
    message: 'Not confirmed: the chat service did not answer. Retrying is safe — it cannot start a second turn.',
    canRetry: true,
  })
  assert.deepEqual(p(503, 'internal'), {
    message: 'Not confirmed: the chat service is not available just now. Retrying is safe — it cannot start a second turn.',
    canRetry: true,
  })
  assert.deepEqual(p(429, 'rate_limited'), { message: 'Not sent: too many requests just now. Retry in a moment.', canRetry: true })
  assert.equal(p(401).canRetry, true)
  assert.match(p(401).message, /signed out/)
  assert.deepEqual(p(409, 'agent_unavailable'), {
    message: 'Chat is off for this agent. Turn it on in the agent’s settings (Config → General → Chat). Then retry.',
    canRetry: true,
  })
  assert.equal(p(413, 'payload_too_large').canRetry, false)
  assert.equal(p(409, 'idempotency_conflict').canRetry, false)
  assert.equal(p(404, 'conversation_not_found').canRetry, true)
  assert.deepEqual(p(400, 'unsupported_kind'), { message: 'The chat service refused this message (unsupported_kind).', canRetry: false })
})

// ── the stream ──────────────────────────────────────────────────────────────

test('a closed stream: superseded and forbidden stay closed, a rate limit waits as told, anything else reconnects', () => {
  assert.deepEqual(closeAction(4409, 'superseded'), { type: 'superseded' })
  assert.deepEqual(closeAction(4403, ''), { type: 'forbidden' })
  assert.deepEqual(closeAction(4429, '7'), { type: 'wait', seconds: 7 })
  assert.deepEqual(closeAction(4429, 'soon'), { type: 'wait', seconds: 30 })
  assert.deepEqual(closeAction(4429, '0'), { type: 'wait', seconds: 1 })
  assert.deepEqual(closeAction(4429, '9999'), { type: 'wait', seconds: 300 })
  for (const code of [1000, 1001, 1006, 4401, 4408, 4413, 4500, 4503, 4999]) {
    assert.deepEqual(closeAction(code, ''), { type: 'reconnect' }, String(code))
  }
})

test('reconnect backoff doubles from about 1 s to a 30 s ceiling, never below half of it', () => {
  assert.equal(reconnectDelayMs(0, 0), 500)
  assert.equal(reconnectDelayMs(0, 1), 1000)
  assert.equal(reconnectDelayMs(1, 1), 2000)
  assert.equal(reconnectDelayMs(4, 1), 16000)
  assert.equal(reconnectDelayMs(5, 1), 30000)
  assert.equal(reconnectDelayMs(50, 1), 30000)
  assert.equal(reconnectDelayMs(50, 0), 15000)
  assert.equal(reconnectDelayMs(-3, 2), 1000)
})

// ── the view ────────────────────────────────────────────────────────────────

test('which agent opens: the linked one, else the open one, else the last used, else the only one', () => {
  const names = ['cto', 'dev']
  assert.equal(pickAgent(names, 'dev', 'cto', 'cto'), 'dev')
  assert.equal(pickAgent(names, '', 'cto', 'dev'), 'cto')
  assert.equal(pickAgent(names, '', '', 'dev'), 'dev')
  assert.equal(pickAgent(names, '', '', ''), '')
  assert.equal(pickAgent(['solo'], '', '', ''), 'solo')
})

test('an agent no longer in the list is never opened', () => {
  assert.equal(pickAgent(['cto', 'dev'], 'gone', 'also-gone', 'old'), '')
  assert.equal(pickAgent(['solo'], 'gone', '', 'old'), 'solo')
  assert.equal(pickAgent([], 'cto', 'cto', 'cto'), '')
})

test('size note: nothing until 80% of the limit, a count after, an error past it', () => {
  assert.equal(sizeNote(0, 32768), null)
  assert.equal(sizeNote(26214, 32768), null)
  assert.deepEqual(sizeNote(26215, 32768), { text: '26,215 of 32,768 bytes', tooLong: false })
  assert.deepEqual(sizeNote(32768, 32768), { text: '32,768 of 32,768 bytes', tooLong: false })
  assert.deepEqual(sizeNote(32769, 32768), { text: 'This message is too long to send (32,769 of 32,768 bytes).', tooLong: true })
})

test('Enter sends; Shift+Enter and the Enter that ends an IME composition do not', () => {
  assert.equal(shouldSendOnKey({ key: 'Enter', shiftKey: false, isComposing: false }), true)
  assert.equal(shouldSendOnKey({ key: 'Enter', shiftKey: true, isComposing: false }), false)
  assert.equal(shouldSendOnKey({ key: 'Enter', shiftKey: false, isComposing: true }), false)
  assert.equal(shouldSendOnKey({ key: 'a', shiftKey: false, isComposing: false }), false)
})
