import { test } from 'node:test'
import assert from 'node:assert/strict'
import { ChatActivityStore, SEEN_KEY, agentStanding, isUnread, readSeen, threadPreview, unreadAgents } from './chatActivity.ts'
import { PROTOCOL } from './chatProtocol.ts'

class MemoryStorage {
  values = new Map<string, string>()
  getItem = (key: string) => this.values.get(key) ?? null
  setItem = (key: string, value: string) => { this.values.set(key, value) }
}

type Reply = { status: number; body?: unknown }

function service(agents: Reply, conversations: Reply) {
  const calls: string[] = []
  const answers: Record<string, Reply> = { '/api/chat/agents': agents, '/v1/conversations': conversations }
  return {
    calls,
    answers,
    fetch: async (url: string) => {
      calls.push(url)
      const reply = answers[url]
      return new Response(reply.body === undefined ? null : JSON.stringify(reply.body), { status: reply.status })
    },
  }
}

const twoAgents = { status: 200, body: { available: true, agents: [{ agentName: 'acto', displayName: 'Acto' }, { agentName: 'adev', displayName: 'Adev' }] } }
const thread = (agent: string, nextSeq: number, conversationId = `c-${agent}`) =>
  ({ conversationId, agent, externalRef: 'main', nextSeq, lastActivityAt: '2026-11-14T10:00:00+00:00' })
const list = (...threads: unknown[]) => ({ status: 200, body: { protocol: PROTOCOL, conversations: threads } })

test('a browser that never kept positions takes every thread as read where it stands', async () => {
  const storage = new MemoryStorage()
  const s = service(twoAgents, list(thread('acto', 41), thread('adev', 7)))
  const store = new ChatActivityStore({ fetch: s.fetch, storage })
  await store.refresh()

  assert.deepEqual(unreadAgents(store.snapshot()), [])
  assert.deepEqual(JSON.parse(storage.values.get(SEEN_KEY)!), { acto: 40, adev: 6 })

  // Something arrives for one of them.
  s.answers['/v1/conversations'] = list(thread('acto', 44), thread('adev', 7))
  await store.refresh()
  assert.deepEqual(unreadAgents(store.snapshot()), ['acto'])

  store.markSeen('acto', 43)
  assert.deepEqual(unreadAgents(store.snapshot()), [])
  assert.equal(readSeen(storage)?.acto, 43)
})

test('with positions kept, a thread first seen later is news; an agent with chat off is not counted', async () => {
  const storage = new MemoryStorage()
  storage.setItem(SEEN_KEY, JSON.stringify({ acto: 40 }))
  const s = service(
    { status: 200, body: { available: true, agents: [{ agentName: 'acto' }, { agentName: 'adev' }] } },
    list(thread('acto', 41), thread('adev', 3), thread('retired', 90)),
  )
  const store = new ChatActivityStore({ fetch: s.fetch, storage })
  await store.refresh()
  assert.deepEqual(unreadAgents(store.snapshot()), ['adev'])
})

test('a position never moves back, and another tab’s further position is taken', () => {
  const storage = new MemoryStorage()
  storage.setItem(SEEN_KEY, JSON.stringify({ acto: 40 }))
  const store = new ChatActivityStore({ fetch: async () => new Response(null, { status: 500 }), storage })
  store.markSeen('acto', 12)
  assert.equal(store.snapshot().seen.acto, 40)

  // Another tab read further in one thread and this tab in another: both are kept.
  storage.setItem(SEEN_KEY, JSON.stringify({ acto: 55, adev: 9 }))
  store.markSeen('writer', 3)
  assert.deepEqual(readSeen(storage), { acto: 55, adev: 9, writer: 3 })
  store.reloadSeen()
  assert.deepEqual(store.snapshot().seen, { acto: 55, adev: 9, writer: 3 })
})

test('the thread on screen can be ahead of the list, and the list does not pull it back', async () => {
  const storage = new MemoryStorage()
  storage.setItem(SEEN_KEY, JSON.stringify({ acto: 40 }))
  const s = service(twoAgents, list(thread('acto', 41)))
  const store = new ChatActivityStore({ fetch: s.fetch, storage })
  store.noteThread('acto', 'c-acto', 50, 1234)
  await store.refresh()
  assert.equal(store.snapshot().threads.acto.nextSeq, 51)
  assert.deepEqual(unreadAgents(store.snapshot()), ['acto'])

  // A conversation opened again from nothing has a new id: its position is what the list says.
  s.answers['/v1/conversations'] = list(thread('acto', 2, 'c-new'))
  await store.refresh()
  assert.equal(store.snapshot().threads.acto.nextSeq, 2)
})

test('a deployment without chat is known as such; an orchestrator that fails says nothing', async () => {
  const none = new ChatActivityStore({ fetch: service({ status: 404 }, list()).fetch, storage: null })
  await none.refresh()
  assert.equal(none.snapshot().available, false)

  const s = service({ status: 503 }, list())
  const failing = new ChatActivityStore({ fetch: s.fetch, storage: null })
  await failing.refresh()
  assert.equal(failing.snapshot().available, null)
  assert.deepEqual(s.calls, ['/api/chat/agents'])

  // A chat service that cannot list keeps the agents and marks nothing.
  const partial = new ChatActivityStore({ fetch: service(twoAgents, { status: 503 }).fetch, storage: null })
  await partial.refresh()
  assert.equal(partial.snapshot().agents.length, 2)
  assert.deepEqual(unreadAgents(partial.snapshot()), [])
})

test('storage that is blocked or holds something else never breaks the page', () => {
  const blocked = { getItem: () => { throw new Error('denied') }, setItem: () => { throw new Error('denied') } }
  assert.equal(readSeen(blocked), null)
  const store = new ChatActivityStore({ fetch: async () => new Response(null, { status: 500 }), storage: blocked })
  store.markSeen('acto', 4)
  assert.equal(store.snapshot().seen.acto, 4)

  const odd = new MemoryStorage()
  odd.setItem(SEEN_KEY, '{"acto":"x","adev":-1,"ok":3}')
  assert.deepEqual(readSeen(odd), { ok: 3 })
  odd.setItem(SEEN_KEY, 'not json')
  assert.deepEqual(readSeen(odd), {})
})

test('unread is "moved on past what was shown", and nothing else', () => {
  const t = { conversationId: 'c', agent: 'a', nextSeq: 10, lastActivityAt: null }
  assert.equal(isUnread(t, 9), false)
  assert.equal(isUnread(t, 8), true)
  assert.equal(isUnread(t, undefined), true)
  assert.equal(isUnread(undefined, 0), false)
  assert.equal(isUnread({ ...t, nextSeq: 1 }, undefined), false)
})

test('how an agent is doing: offline first, then its task, then stale, then idle', () => {
  assert.equal(agentStanding(undefined), null)
  assert.deepEqual(agentStanding({ effectiveStatus: 'dead', currentTask: 'left over' }), { dot: 'red', text: 'Offline', down: true })
  assert.equal(agentStanding({ effectiveStatus: 'stale', currentTask: 'Reviewing #12\nsecond line' })?.text, 'Working: Reviewing #12')
  assert.equal(agentStanding({ effectiveStatus: 'stale', currentTask: null })?.dot, 'yellow')
  assert.equal(agentStanding({ effectiveStatus: 'idle', currentTask: '  ' })?.text, 'Idle')
  assert.equal(agentStanding({ effectiveStatus: 'busy', currentTask: 'x'.repeat(200) })?.text.length, 'Working: '.length + 91)
})

test('a thread’s line in the list: its newest message, marked when it is the person’s own', async () => {
  const s = service(twoAgents, list(
    { ...thread('acto', 5), lastMessage: { from: 'agent', text: 'Three issues\n are   open.' } },
    { ...thread('adev', 3), lastMessage: { from: 'user', text: '' } },
  ))
  const store = new ChatActivityStore({ fetch: s.fetch, storage: null })
  await store.refresh()
  const threads = store.snapshot().threads

  assert.equal(threadPreview(threads.acto), 'Three issues are open.')
  assert.equal(threadPreview(threads.adev), 'You: Sent a file')
  assert.equal(threadPreview(undefined), null)
  assert.equal(threadPreview({ conversationId: 'c', agent: 'a', nextSeq: 1, lastActivityAt: null }), null)
  assert.equal(threadPreview({ conversationId: 'c', agent: 'a', nextSeq: 1, lastActivityAt: null, lastMessage: { fromUser: false, text: 'x'.repeat(400) } })!.length, 161)

  // The thread on screen moves on: its line follows what was just said, and a move with nothing
  // said (progress) keeps the line it had.
  store.noteThread('acto', 'c-acto', 9, null, { fromUser: true, text: 'thanks' })
  assert.equal(threadPreview(store.snapshot().threads.acto), 'You: thanks')
  store.noteThread('acto', 'c-acto', 12, null)
  assert.equal(threadPreview(store.snapshot().threads.acto), 'You: thanks')
})
