// Run with `npm test` (node --test, Node ≥ 22.18 strips the types). Type-checked by `tsc -b`.
import { test } from 'node:test'
import assert from 'node:assert/strict'
import {
  NO_FILTERS, filterOptions, groupHits, hasFilters, matchDetail, matchKind, matchLabel, narrowList,
  searchNotice, searchUrl, singleIdHit, snippetRuns, withChosen,
} from './memorySearch.ts'
import type { MemorySearchHit, SnippetRun } from './memorySearch.ts'

function hit(id: string, match?: string, over: Partial<MemorySearchHit> = {}): MemorySearchHit {
  return {
    id, title: `Memory ${id}`, project: 'atlas', type: 'reference', tags: [], snippet: 'text',
    updated_at: '2026-01-05T10:00:00Z', ...(match === undefined ? {} : { match }), ...over,
  }
}

const ids = (hits: { id: string }[]) => hits.map(h => h.id)
const drawn = (runs: SnippetRun[]) => runs.map(r => r.marked ? `[${r.text}]` : r.text).join('')

// ── the two groups ───────────────────────────────────────────────────────────────────────────────

test('hits found by id, title, tag or keyword come first and hits found by meaning apart, each in the order given', () => {
  const groups = groupHits([
    hit('a', 'id'), hit('b', 'title'), hit('c', 'meaning'), hit('d', 'tag'), hit('e', 'keyword'), hit('f', 'meaning'),
  ])
  assert.equal(groups.grouped, true)
  assert.deepEqual(ids(groups.matches), ['a', 'b', 'd', 'e'])
  assert.deepEqual(ids(groups.related), ['c', 'f'])
  assert.deepEqual(groups.ungrouped, [])
  assert.equal(searchNotice(groups), null)
})

test('an answer with only meaning hits is said to be related only; an empty answer is said to be empty', () => {
  const relatedOnly = groupHits([hit('a', 'meaning'), hit('b', 'meaning')])
  assert.deepEqual(ids(relatedOnly.matches), [])
  assert.deepEqual(ids(relatedOnly.related), ['a', 'b'])
  assert.equal(searchNotice(relatedOnly), 'related-only')

  assert.equal(searchNotice(groupHits([])), 'none')
  assert.equal(searchNotice(groupHits([hit('a', 'keyword')])), null)
})

test('an answer that does not say how its hits matched is one list in its own order, with no claim', () => {
  const old = groupHits([hit('a'), hit('b'), hit('c')])
  assert.equal(old.grouped, false)
  assert.deepEqual(ids(old.ungrouped), ['a', 'b', 'c'])
  assert.deepEqual([old.matches, old.related], [[], []])
  assert.equal(searchNotice(old), null)

  // One hit of a kind this page does not know: nothing is put under a heading it did not earn.
  const mixed = groupHits([hit('a', 'keyword'), hit('b', 'phonetic'), hit('c', 'meaning')])
  assert.equal(mixed.grouped, false)
  assert.deepEqual(ids(mixed.ungrouped), ['a', 'b', 'c'])
})

test('a kind is one of the five the service names, exactly, and each has its word', () => {
  assert.deepEqual(
    (['id', 'title', 'tag', 'keyword', 'meaning'] as const).map(k => matchLabel(matchKind({ match: k })!)),
    ['ID', 'Title', 'Tag', 'Keyword', 'Meaning'])
  for (const other of [undefined, null, '', 'ID', 'toString', 'constructor', 7, ['id']]) {
    assert.equal(matchKind({ match: other }), null, String(other))
  }
})

test('Enter opens the one id hit, and nothing when there is none or more than one', () => {
  assert.equal(singleIdHit([hit('76a05a22-1', 'id'), hit('b', 'keyword'), hit('c', 'meaning')]), '76a05a22-1')
  assert.equal(singleIdHit([hit('76a05a22-1', 'id'), hit('76a05a22-2', 'id')]), null)
  assert.equal(singleIdHit([hit('a', 'title')]), null)
  assert.equal(singleIdHit([hit('a')]), null)
  assert.equal(singleIdHit([]), null)
})

test('an id hit shows the start of its id and a tag hit the tag that was typed', () => {
  assert.equal(matchDetail(hit('76a05a22-0000-4000-8000-000000000001', 'id'), '76a05a'), '76a05a22')
  assert.equal(matchDetail(hit('a', 'tag', { tags: ['ops', 'Deploy'] }), ' deploy '), '#Deploy')
  assert.equal(matchDetail(hit('a', 'tag', { tags: ['ops'] }), 'deploy'), null)
  assert.equal(matchDetail(hit('a', 'keyword', { tags: ['deploy'] }), 'deploy'), null)
  assert.equal(matchDetail(hit('a', undefined, { tags: ['deploy'] }), 'deploy'), null)
})

// ── the request ──────────────────────────────────────────────────────────────────────────────────

test('a search sends the trimmed query and only the filters that are set, each escaped', () => {
  assert.equal(searchUrl('  76a05a22 ', NO_FILTERS), '/api/memory/search?q=76a05a22')
  assert.equal(
    searchUrl('поиск a&b=c', { project: 'atlas/north', type: 'how to', tag: 'c#&more' }),
    '/api/memory/search?q=%D0%BF%D0%BE%D0%B8%D1%81%D0%BA%20a%26b%3Dc&project=atlas%2Fnorth&type=how%20to&tag=c%23%26more')
  assert.equal(searchUrl('x', { project: '', type: 'decision', tag: '' }), '/api/memory/search?q=x&type=decision')
  assert.equal(searchUrl('x', { project: '', type: '', tag: 'ops' }), '/api/memory/search?q=x&tag=ops')
  assert.equal(searchUrl('x', { project: 'atlas', type: '', tag: '' }), '/api/memory/search?q=x&project=atlas')
})

test('filters are set when any one of them is', () => {
  assert.equal(hasFilters(NO_FILTERS), false)
  assert.equal(hasFilters({ ...NO_FILTERS, tag: 'ops' }), true)
  assert.equal(hasFilters({ ...NO_FILTERS, project: 'atlas' }), true)
  assert.equal(hasFilters({ ...NO_FILTERS, type: 'decision' }), true)
})

// ── the filters' options ─────────────────────────────────────────────────────────────────────────

const LIST = [
  { id: '1', project: 'borealis', type: 'reference', tags: ['ops', 'deploy'] },
  { id: '2', project: 'atlas', type: 'decision', tags: ['deploy'] },
  { id: '3', project: 'atlas', type: 'reference', tags: [] },
  { id: '4', project: '', type: '', tags: ['заметки', 'ops'] },
]

test('the filters offer every project, type and tag of the loaded list once, in order, and never an empty one', () => {
  assert.deepEqual(filterOptions(LIST), {
    projects: ['atlas', 'borealis'],
    types: ['decision', 'reference'],
    tags: ['deploy', 'ops', 'заметки'],
  })
  assert.deepEqual(filterOptions([]), { projects: [], types: [], tags: [] })
  // A list item from a service that sent no tags at all.
  assert.deepEqual(filterOptions([{ project: 'atlas', type: 'note', tags: undefined as unknown as string[] }]).tags, [])
})

test('a chosen value stays in its list when no memory has it any more', () => {
  assert.deepEqual(withChosen(['atlas', 'borealis'], ''), ['atlas', 'borealis'])
  assert.deepEqual(withChosen(['atlas', 'borealis'], 'atlas'), ['atlas', 'borealis'])
  assert.deepEqual(withChosen(['atlas', 'cirrus'], 'borealis'), ['atlas', 'borealis', 'cirrus'])
})

test('the filters narrow the loaded list by exact project, type and tag; none set leaves it whole', () => {
  assert.deepEqual(ids(narrowList(LIST, NO_FILTERS)), ['1', '2', '3', '4'])
  assert.deepEqual(ids(narrowList(LIST, { ...NO_FILTERS, project: 'atlas' })), ['2', '3'])
  assert.deepEqual(ids(narrowList(LIST, { ...NO_FILTERS, type: 'reference' })), ['1', '3'])
  assert.deepEqual(ids(narrowList(LIST, { ...NO_FILTERS, tag: 'ops' })), ['1', '4'])
  assert.deepEqual(ids(narrowList(LIST, { project: 'atlas', type: 'decision', tag: 'deploy' })), ['2'])
  assert.deepEqual(ids(narrowList(LIST, { ...NO_FILTERS, tag: 'dep' })), [])
})

// ── marked snippets ──────────────────────────────────────────────────────────────────────────────

test('a snippet is cut at its highlights, and the runs joined are the snippet', () => {
  const snippet = 'the deploy reaches the host after the deploy check'
  const runs = snippetRuns(snippet, [[4, 6], [38, 6]])
  assert.equal(drawn(runs), 'the [deploy] reaches the host after the [deploy] check')
  assert.equal(runs.map(r => r.text).join(''), snippet)
})

test('a snippet with no highlights, or none that can be used, is one unmarked run', () => {
  for (const highlights of [undefined, null, [], 'nope', 7, { 0: [0, 3] }, [[0]], [[0, 3, 1]], [['0', '3']], [[0.5, 2]],
    [[0, 0]], [[3, -2]], [[40, 5]], [[-9, 4]], [[Number.NaN, 2]], [[0, Number.POSITIVE_INFINITY]], [null, undefined, 'x']]) {
    assert.deepEqual(snippetRuns('plain text', highlights), [{ text: 'plain text', marked: false }], JSON.stringify(highlights))
  }
  assert.deepEqual(snippetRuns('', [[0, 3]]), [])
})

test('a range is cut to the snippet, and ranges that overlap, touch or come out of order are joined', () => {
  assert.equal(drawn(snippetRuns('abcdef', [[4, 50]])), 'abcd[ef]')
  assert.equal(drawn(snippetRuns('abcdef', [[-2, 4]])), '[ab]cdef')
  assert.equal(drawn(snippetRuns('abcdef', [[0, 6]])), '[abcdef]')
  assert.equal(drawn(snippetRuns('abcdefgh', [[5, 2], [1, 2], [2, 2]])), 'a[bcd]e[fg]h')
  assert.equal(drawn(snippetRuns('abcdefgh', [[1, 2], [3, 2]])), 'a[bcde]fgh')
  assert.equal(drawn(snippetRuns('abcdefgh', [[1, 5], [2, 1]])), 'a[bcdef]gh')
  // One bad pair among good ones is dropped; the rest still mark.
  assert.equal(drawn(snippetRuns('abcdefgh', [[1, 2], ['x', 1], [6, 1]])), 'a[bc]def[g]h')
})

test('offsets are UTF-16 units: non-Latin text is marked where the service says, and a character is never cut in half', () => {
  assert.equal(drawn(snippetRuns('Как выкатить релиз на сервер', [[4, 8]])), 'Как [выкатить] релиз на сервер')
  assert.equal(drawn(snippetRuns('部署到主机的步骤', [[0, 2]])), '[部署]到主机的步骤')

  // "🚀" is two units. A range that starts or ends inside it takes the whole character.
  const snippet = 'go 🚀 now'
  assert.equal(snippet.length, 9)
  assert.equal(drawn(snippetRuns(snippet, [[3, 2]])), 'go [🚀] now')
  assert.equal(drawn(snippetRuns(snippet, [[4, 3]])), 'go [🚀 n]ow')
  assert.equal(drawn(snippetRuns(snippet, [[0, 4]])), '[go 🚀] now')
  const loneHalf = /[\uD800-\uDBFF](?![\uDC00-\uDFFF])|(?<![\uD800-\uDBFF])[\uDC00-\uDFFF]/
  for (const highlights of [[[4, 1]], [[3, 1]], [[0, 4]], [[4, 5]]]) {
    for (const run of snippetRuns(snippet, highlights)) assert.equal(loneHalf.test(run.text), false, JSON.stringify(highlights))
  }
})
