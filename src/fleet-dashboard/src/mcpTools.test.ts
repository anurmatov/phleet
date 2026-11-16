import { test } from 'node:test'
import assert from 'node:assert/strict'
import {
  answerHeadline, buildToolView, firstLine, formatToolList, matchesFilter, mcpToolId, parseToolList,
  readToolDiscovery, setToolsGranted, splitMcpToolId, type ToolDiscovery,
} from './mcpTools.ts'

const discovery: ToolDiscovery = {
  alwaysOn: ['Read', 'Write', 'Edit', 'Bash', 'Glob', 'Grep'],
  servers: [
    {
      name: 'example', status: 'ok', reason: null, truncated: false,
      tools: [
        { name: 'alpha_list', description: 'Lists things.' },
        { name: 'zeta_lookup', description: 'Looks one thing up.\nSecond line.' },
      ],
    },
    { name: 'fleet-memory', status: 'ok', reason: null, truncated: false, tools: [{ name: 'memory_search', description: null }] },
    { name: 'guarded', status: 'needs_agent_secret', reason: 'Header X-Example-Key carries the bound secret EXAMPLE_KEY.', tools: [], truncated: false },
  ],
}

test('the orchestrator’s answer is read, and what it cannot read costs only that entry', () => {
  const read = readToolDiscovery({
    agent: 'alpha',
    alwaysOn: ['Read', 7, ''],
    servers: [
      { name: 'example', status: 'ok', reason: null, tools: [{ name: 'a', description: 'A.' }, { name: '' }, 'junk', { name: 'b' }], truncated: true },
      { name: 'guarded', status: 'refused', reason: 'The server refused the request (HTTP 401).', tools: [] },
      { status: 'ok', tools: [] },
      null,
    ],
  })
  assert.deepEqual(read, {
    alwaysOn: ['Read'],
    servers: [
      { name: 'example', status: 'ok', reason: null, truncated: true, tools: [{ name: 'a', description: 'A.' }, { name: 'b', description: null }] },
      { name: 'guarded', status: 'refused', reason: 'The server refused the request (HTTP 401).', truncated: false, tools: [] },
    ],
  })
})

test('an answer that is not one reads as none, never as an empty list of servers', () => {
  for (const body of [null, undefined, 'text', [], {}, { servers: 'none' }, { error: 'Agent not found' }])
    assert.equal(readToolDiscovery(body), null)
})

test('the list is the names in order, each once, and survives a round trip', () => {
  assert.deepEqual(parseToolList(' Read ,, mcp__a__x,Read,\n mcp__a__y '), ['Read', 'mcp__a__x', 'mcp__a__y'])
  assert.deepEqual(parseToolList(''), [])
  assert.equal(formatToolList(parseToolList('Read,mcp__a__x')), 'Read, mcp__a__x')
})

test('granting adds at the end and keeps the rest in order; revoking removes only what was named', () => {
  assert.equal(setToolsGranted('Read, mcp__a__x', ['mcp__a__y'], true), 'Read, mcp__a__x, mcp__a__y')
  assert.equal(setToolsGranted('Read, mcp__a__x', ['mcp__a__x'], true), 'Read, mcp__a__x', 'a name already granted is not added twice')
  assert.equal(setToolsGranted('Read, mcp__a__x, mcp__a__y', ['mcp__a__x'], false), 'Read, mcp__a__y')
  assert.equal(setToolsGranted('Read, mcp__a__x, mcp__b__x', ['mcp__a__x', 'mcp__a__y'], false), 'Read, mcp__b__x')
  assert.equal(setToolsGranted('', ['mcp__a__x', 'mcp__a__y'], true), 'mcp__a__x, mcp__a__y')
})

test('a granted name belongs to the longest server that matches it', () => {
  assert.equal(mcpToolId('fleet-memory', 'memory_search'), 'mcp__fleet-memory__memory_search')
  assert.deepEqual(splitMcpToolId('mcp__fleet-memory__memory_search', ['fleet-memory']), { server: 'fleet-memory', tool: 'memory_search' })
  assert.deepEqual(splitMcpToolId('mcp__a__b__tool', ['a', 'a__b']), { server: 'a__b', tool: 'tool' })
  assert.deepEqual(splitMcpToolId('mcp__a__b__tool', ['a__b', 'a']), { server: 'a__b', tool: 'tool' })
  assert.deepEqual(splitMcpToolId('mcp__a', ['a']), { server: 'a', tool: '' }, 'a grant of the whole server')
  assert.equal(splitMcpToolId('mcp__ab__tool', ['a']), null, 'a server name is matched whole')
  assert.equal(splitMcpToolId('mcp__other__tool', ['a']), null)
  assert.equal(splitMcpToolId('Read', ['a']), null)
  assert.equal(splitMcpToolId('mcp____tool', ['']), null, 'an endpoint with no name yet claims nothing')
})

test('each server shows what it offers, with what is granted checked', () => {
  const view = buildToolView('Read, WebFetch, mcp__example__zeta_lookup', ['example'], discovery)
  assert.deepEqual(view.alwaysOn, ['Read', 'Write', 'Edit', 'Bash', 'Glob', 'Grep'])
  // Read is always on, so it is not a row; WebFetch is.
  assert.deepEqual(view.builtIn.map(r => r.id), ['WebFetch'])
  const example = view.servers.find(s => s.server === 'example')!
  assert.deepEqual(example.rows, [
    { id: 'mcp__example__alpha_list', label: 'alpha_list', description: 'Lists things.', granted: false, listed: true },
    { id: 'mcp__example__zeta_lookup', label: 'zeta_lookup', description: 'Looks one thing up.\nSecond line.', granted: true, listed: true },
  ])
  assert.equal(example.grantedCount, 1)
  assert.equal(view.grantedCount, 3)
})

test('a server every agent has is shown although the dialog has no endpoint for it', () => {
  const view = buildToolView('', ['example'], discovery)
  assert.deepEqual(view.servers.map(s => s.server), ['example', 'fleet-memory', 'guarded'])
  assert.equal(view.servers[1].rows[0].id, 'mcp__fleet-memory__memory_search')
})

test('a granted tool its server does not list stays, marked as not listed', () => {
  const view = buildToolView('mcp__example__gone, mcp__guarded__orders_get', ['example', 'guarded'], discovery)
  const example = view.servers.find(s => s.server === 'example')!
  assert.deepEqual(example.rows.at(-1), { id: 'mcp__example__gone', label: 'gone', description: null, granted: true, listed: false })
  // A server that could not be asked still shows what the agent is granted there.
  const guarded = view.servers.find(s => s.server === 'guarded')!
  assert.deepEqual(guarded.rows, [{ id: 'mcp__guarded__orders_get', label: 'orders_get', description: null, granted: true, listed: false }])
  assert.equal(guarded.answer?.status, 'needs_agent_secret')
})

test('a granted tool of no endpoint is kept apart, and never dropped', () => {
  const view = buildToolView('mcp__removed__thing, mcp__example__alpha_list', ['example'], discovery)
  assert.deepEqual(view.other, [{ id: 'mcp__removed__thing', label: 'mcp__removed__thing', description: null, granted: true, listed: false }])
  const everyRow = [...view.builtIn, ...view.other, ...view.servers.flatMap(s => s.rows)].filter(r => r.granted).map(r => r.id)
  assert.deepEqual(everyRow.sort(), ['mcp__example__alpha_list', 'mcp__removed__thing'])
})

test('a tool revoked in this dialog keeps its row, unticked, so it can be granted again', () => {
  const remembered = ['WebFetch', 'mcp__example__gone', 'mcp__removed__thing', 'mcp__example__alpha_list']
  const view = buildToolView('Read', ['example'], discovery, remembered)
  assert.deepEqual(view.builtIn, [{ id: 'WebFetch', label: 'WebFetch', description: null, granted: false, listed: true }])
  assert.deepEqual(view.other, [{ id: 'mcp__removed__thing', label: 'mcp__removed__thing', description: null, granted: false, listed: false }])
  const example = view.servers[0]
  assert.deepEqual(example.rows.map(r => [r.id, r.granted]), [
    ['mcp__example__alpha_list', false], ['mcp__example__zeta_lookup', false], ['mcp__example__gone', false],
  ], 'a listed tool is not given a second row')
  assert.equal(view.grantedCount, 1)
  // Granted again, it is one row, ticked.
  const again = buildToolView('Read, WebFetch', ['example'], discovery, remembered)
  assert.deepEqual(again.builtIn.map(r => [r.id, r.granted]), [['WebFetch', true]])
})

test('a grant of a whole server is one row of that server', () => {
  const view = buildToolView('mcp__example', ['example'], discovery)
  const row = view.servers[0].rows.at(-1)!
  assert.equal(row.id, 'mcp__example')
  assert.equal(row.label, 'Every tool of this server')
  assert.equal(row.granted, true)
})

test('before the servers answer, and for an endpoint not saved yet, the granted tools still show', () => {
  const view = buildToolView('WebFetch, mcp__new__thing', ['new', '', 'new'], null)
  assert.deepEqual(view.alwaysOn, [])
  assert.deepEqual(view.servers.map(s => s.server), ['new'], 'an unnamed endpoint and a repeated name are not servers')
  assert.equal(view.servers[0].answer, null)
  assert.deepEqual(view.servers[0].rows.map(r => r.id), ['mcp__new__thing'])
  assert.deepEqual(view.builtIn.map(r => r.id), ['WebFetch'])
})

test('the filter looks in the name as granted and in the description', () => {
  const row = { id: 'mcp__example__zeta_lookup', label: 'zeta_lookup', description: 'Looks one thing up.', granted: false, listed: true }
  assert.equal(matchesFilter(row, ''), true)
  assert.equal(matchesFilter(row, '  ZETA '), true)
  assert.equal(matchesFilter(row, 'example'), true, 'the server is part of the name')
  assert.equal(matchesFilter(row, 'one thing'), true)
  assert.equal(matchesFilter(row, 'orders'), false)
  assert.equal(matchesFilter({ ...row, description: null }, 'looks'), false)
})

test('a row shows a description’s first line', () => {
  assert.equal(firstLine('Looks one thing up.\nSecond line.'), 'Looks one thing up.')
  assert.equal(firstLine('\n\n  Indented first.  \nMore.'), 'Indented first.')
  assert.equal(firstLine(null), '')
})

test('a server without a list says why in a few words', () => {
  const answer = (status: string) => ({ name: 's', status, reason: null, tools: [], truncated: false })
  assert.equal(answerHeadline(answer('ok'), true), null)
  assert.equal(answerHeadline(null, false), 'Save to list this server’s tools.')
  assert.equal(answerHeadline(null, true), null, 'still asking, or the orchestrator could not be asked: said once, above')
  assert.match(answerHeadline(answer('needs_agent_secret'), true)!, /cannot be listed from here/)
  assert.match(answerHeadline(answer('unreachable'), true)!, /Could not reach/)
  assert.match(answerHeadline(answer('something_new'), true)!, /could not be listed/)
})
