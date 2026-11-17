import { test } from 'node:test'
import assert from 'node:assert/strict'
import { againstCurrent, changesOf, definitionVersions, previousVersion, samePair, withSide } from './versionHistory.ts'
import type { WorkflowDefinitionDetail } from './types'

test('a comparison opens on what the chosen version changed: it against the one before', () => {
  const numbers = [5, 4, 2, 1]
  assert.deepEqual(changesOf(numbers, 5), { older: 4, newer: 5 })
  // The one before is the highest number below it, whatever the order and whatever is missing.
  assert.deepEqual(changesOf(numbers, 4), { older: 2, newer: 4 })
  assert.deepEqual(changesOf([1, 4, 5, 2], 4), { older: 2, newer: 4 })
  assert.equal(previousVersion(numbers, 2), 1)
})

test('the oldest version is compared with nothing', () => {
  assert.deepEqual(changesOf([3, 2, 1], 1), { older: null, newer: 1 })
  assert.deepEqual(changesOf([7], 7), { older: null, newer: 7 })
  assert.equal(previousVersion([3, 2], 2), null)
})

test('against current puts the earlier of the two first, and is not offered for the current version', () => {
  assert.deepEqual(againstCurrent(2, 5), { older: 2, newer: 5 })
  // After a rollback the version in use can be an earlier one.
  assert.deepEqual(againstCurrent(5, 2), { older: 2, newer: 5 })
  assert.equal(againstCurrent(5, 5), null)
})

test('either side can be any version, and a pair chosen backwards is put in order', () => {
  const pair = { older: 2, newer: 4 }
  assert.deepEqual(withSide(pair, 'older', 1), { older: 1, newer: 4 })
  assert.deepEqual(withSide(pair, 'newer', 5), { older: 2, newer: 5 })
  // A later version chosen as the older side becomes the newer one, and the other way round.
  assert.deepEqual(withSide(pair, 'older', 6), { older: 4, newer: 6 })
  assert.deepEqual(withSide(pair, 'newer', 1), { older: 1, newer: 2 })
  // The same version on both sides is left as chosen: the view says there is nothing to compare.
  assert.deepEqual(withSide(pair, 'older', 4), { older: 4, newer: 4 })
  assert.deepEqual(withSide(pair, 'newer', 2), { older: 2, newer: 2 })
  // Only the older side can be nothing, and the newer side then moves freely.
  assert.deepEqual(withSide(pair, 'older', null), { older: null, newer: 4 })
  assert.deepEqual(withSide({ older: null, newer: 4 }, 'newer', 1), { older: null, newer: 1 })
})

test('two pairs are the same when both sides are, and never when there is no second pair', () => {
  assert.equal(samePair({ older: null, newer: 1 }, { older: null, newer: 1 }), true)
  assert.equal(samePair({ older: 1, newer: 2 }, { older: null, newer: 2 }), false)
  assert.equal(samePair({ older: 1, newer: 2 }, null), false)
})

test('a definition’s versions carry the date, author and reason of the write that made them', () => {
  // The orchestrator stamps the row of a replaced version with the write that replaced it.
  const detail: WorkflowDefinitionDetail = {
    name: 'Release', namespace: 'fleet', taskQueue: 'fleet', version: 3, isActive: true,
    createdAt: '2026-01-01T09:00:00Z', createdBy: 'owner', updatedAt: '2026-03-01T09:00:00Z',
    definition: '{"v":3}',
    versions: [
      { version: 2, definition: '{"v":2}', reason: 'wrap the review in a loop', createdAt: '2026-03-01T09:00:00Z', createdBy: 'agent1' },
      { version: 1, definition: '{"v":1}', reason: 'wait longer', createdAt: '2026-02-01T09:00:00Z', createdBy: 'owner' },
    ],
  }
  assert.deepEqual(definitionVersions(detail), [
    { number: 3, content: '{"v":3}', when: '2026-03-01T09:00:00Z', by: 'agent1', reason: 'wrap the review in a loop' },
    { number: 2, content: '{"v":2}', when: '2026-02-01T09:00:00Z', by: 'owner', reason: 'wait longer' },
    { number: 1, content: '{"v":1}', when: '2026-01-01T09:00:00Z', by: 'owner', reason: null },
  ])
})

test('a definition with no history is its one version, and a gap in the history leaves a version undated', () => {
  const base = { name: 'Release', namespace: 'fleet', taskQueue: 'fleet', isActive: true, updatedAt: '2026-03-01T09:00:00Z' }
  assert.deepEqual(definitionVersions({ ...base, version: 1, definition: '{}', versions: [] }),
    [{ number: 1, content: '{}', when: null, by: null, reason: null }])

  // Versions 1 and 3 are missing: nothing says when 2 and 4 were written.
  const gapped = definitionVersions({
    ...base, version: 5, definition: '{"v":5}',
    versions: [
      { version: 4, definition: '{"v":4}', reason: 'to five', createdAt: '2026-03-01T09:00:00Z' },
      { version: 2, definition: '{"v":2}', reason: 'to three', createdAt: '2026-02-01T09:00:00Z' },
    ],
  })
  assert.deepEqual(gapped.map(v => [v.number, v.when, v.reason]), [
    [5, '2026-03-01T09:00:00Z', 'to five'],
    [4, null, null],
    [2, null, null],
  ])
})
