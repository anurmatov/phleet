import { test } from 'node:test'
import assert from 'node:assert/strict'
import { arrangeRuns, linkWords, MAX_DEPTH, type LinkedRun } from './workflowTree.ts'

const run = (workflowId: string, over: Partial<LinkedRun> = {}): LinkedRun =>
  ({ workflowId, runId: `run-${workflowId}`, namespace: 'fleet', ...over })

const shape = (placed: ReturnType<typeof arrangeRuns<LinkedRun>>) =>
  placed.map(p => `${'  '.repeat(p.depth)}${p.run.workflowId}${p.link ? ` (${p.link})` : ''}`)

test('a driver, the run it started and that run’s review read as one tree', () => {
  const review = run('review-1', { parentWorkflowId: 'implementation-2', parentRunId: 'run-implementation-2' })
  const driver = run('driver-7')
  const pr = run('implementation-2', { waiterWorkflowId: 'driver-7' })
  const other = run('unrelated-3')
  const blocked = run('blocked-4')

  assert.deepEqual(shape(arrangeRuns([review, driver, pr, other, blocked])), [
    'driver-7',
    '  implementation-2 (started)',
    '    review-1 (child)',
    'unrelated-3',
    'blocked-4',
  ])
})

test('runs with no link keep exactly the order they came in', () => {
  const runs = [run('c'), run('a'), run('b')]
  const placed = arrangeRuns(runs)
  assert.deepEqual(placed.map(p => p.run), runs)
  assert.ok(placed.every(p => p.depth === 0 && p.link === null && p.elsewhere === null))
})

test('siblings keep their order, and every run is placed exactly once', () => {
  const runs = [
    run('child-b', { parentWorkflowId: 'parent' }), run('other'), run('parent'),
    run('child-a', { parentWorkflowId: 'parent' }), run('grandchild', { parentWorkflowId: 'child-b' }),
  ]
  assert.deepEqual(shape(arrangeRuns(runs)), [
    'other', 'parent', '  child-b (child)', '    grandchild (child)', '  child-a (child)',
  ])
  assert.equal(arrangeRuns(runs).length, runs.length)
})

test('the run id tells one run of a reused workflow id from another', () => {
  const old = run('parent', { runId: 'run-old' })
  const current = run('parent', { runId: 'run-new' })
  const child = run('child', { parentWorkflowId: 'parent', parentRunId: 'run-new' })
  assert.deepEqual(shape(arrangeRuns([old, current, child])), ['parent', 'parent', '  child (child)'])
  assert.equal(arrangeRuns([old, current, child])[2].run, child)
  // Without a run id on the link, the first run of that id is the one.
  const loose = run('loose', { parentWorkflowId: 'parent' })
  assert.deepEqual(shape(arrangeRuns([old, current, loose])), ['parent', '  loose (child)', 'parent'])
})

test('a parent is looked for in the run’s own namespace; a waiter anywhere, its own namespace first', () => {
  const otherParent = run('parent', { namespace: 'ops' })
  const child = run('child', { parentWorkflowId: 'parent' })
  assert.deepEqual(shape(arrangeRuns([otherParent, child])), ['parent', 'child'])

  const opsDriver = run('driver', { namespace: 'ops' })
  const fleetDriver = run('driver', { namespace: 'fleet', runId: 'run-fleet-driver' })
  const started = run('started', { waiterWorkflowId: 'driver' })
  assert.deepEqual(shape(arrangeRuns([opsDriver, started])), ['driver', '  started (started)'])
  assert.equal(arrangeRuns([opsDriver, fleetDriver, started])[1].run, fleetDriver)
  assert.deepEqual(shape(arrangeRuns([opsDriver, fleetDriver, started])), ['driver', 'driver', '  started (started)'])
})

test('the Temporal parent wins over the waiter, and the waiter is used when the parent is not listed', () => {
  const both = run('both', { parentWorkflowId: 'parent', waiterWorkflowId: 'driver' })
  assert.deepEqual(shape(arrangeRuns([run('driver'), run('parent'), both])), ['driver', 'parent', '  both (child)'])
  assert.deepEqual(shape(arrangeRuns([run('driver'), both])), ['driver', '  both (started)'])
})

test('a run whose owner is not in the list stays at the top and says where it belongs', () => {
  const driver = run('driver-1')
  const started = run('started', { waiterWorkflowId: 'driver-1' })
  const orphan = run('orphan', { parentWorkflowId: 'gone-parent' })

  // The driver is shown in another section of the page: named, with the run itself.
  const [first, second] = arrangeRuns([started, orphan], [driver, started, orphan])
  assert.equal(first.depth, 0)
  assert.deepEqual(first.elsewhere, { workflowId: 'driver-1', link: 'started', run: driver })
  // The parent is over: named by its id alone.
  assert.deepEqual(second.elsewhere, { workflowId: 'gone-parent', link: 'child', run: null })
})

test('a run nested under its owner does not also say it belongs elsewhere', () => {
  const placed = arrangeRuns([run('parent'), run('child', { parentWorkflowId: 'parent' })])
  assert.equal(placed[1].elsewhere, null)
})

test('runs that only point at each other, or at themselves, each get one row', () => {
  const a = run('a', { waiterWorkflowId: 'b' })
  const b = run('b', { waiterWorkflowId: 'a' })
  const self = run('self', { waiterWorkflowId: 'self', parentWorkflowId: 'self', parentRunId: 'run-self' })
  const placed = arrangeRuns([a, b, self])
  assert.equal(placed.length, 3)
  assert.deepEqual(new Set(placed.map(p => p.run)), new Set([a, b, self]))
})

test('a long chain stops moving right, and stays in order', () => {
  const chain = Array.from({ length: 8 }, (_, i) => run(`r${i}`, i === 0 ? {} : { parentWorkflowId: `r${i - 1}` }))
  const placed = arrangeRuns(chain)
  assert.deepEqual(placed.map(p => p.run.workflowId), chain.map(r => r.workflowId))
  assert.deepEqual(placed.map(p => p.depth), [0, 1, 2, 3, MAX_DEPTH, MAX_DEPTH, MAX_DEPTH, MAX_DEPTH])
})

test('an orchestrator from before the links gives a flat list', () => {
  const runs = [{ workflowId: 'a', namespace: 'fleet' }, { workflowId: 'b', namespace: 'fleet' }]
  assert.deepEqual(arrangeRuns(runs).map(p => [p.run.workflowId, p.depth]), [['a', 0], ['b', 0]])
})

test('the words for a link', () => {
  assert.equal(linkWords('child'), 'child of')
  assert.equal(linkWords('started'), 'started by')
})


test('a reused parent id with a different run id cannot capture the child', () => {
  const old = run('parent', { runId: 'old-run' })
  const child = run('child', { parentWorkflowId: 'parent', parentRunId: 'new-run' })
  const placed = arrangeRuns([old, child])
  assert.deepEqual(placed.map(p => p.depth), [0, 0])
  assert.deepEqual(placed[1].elsewhere, { workflowId: 'parent', link: 'child', run: null })
})

test('explicit null links from a newer server are flat just like absent links', () => {
  const runs = [run('a', { parentWorkflowId: null, parentRunId: null, waiterWorkflowId: null }), run('b')]
  assert.deepEqual(arrangeRuns(runs).map(p => [p.run.workflowId, p.depth]), [['a', 0], ['b', 0]])
})
