import { test } from 'node:test'
import assert from 'node:assert/strict'
import { copyText } from './clipboard.ts'

test('uses the clipboard when there is one, the old way when there is none or it refuses, and says which went', async () => {
  const written: string[] = []
  const legacy: string[] = []
  const legacyCopy = (text: string) => { legacy.push(text); return true }

  assert.equal(await copyText('a', { clipboard: { writeText: async t => { written.push(t) } }, legacyCopy }), true)
  assert.deepEqual([written, legacy], [['a'], []])

  // Plain HTTP: no clipboard at all.
  assert.equal(await copyText('b', { clipboard: undefined, legacyCopy }), true)
  assert.deepEqual(legacy, ['b'])

  assert.equal(await copyText('c', { clipboard: { writeText: async () => { throw new Error('denied') } }, legacyCopy }), true)
  assert.deepEqual(legacy, ['b', 'c'])

  // Nothing went: never "Copied".
  assert.equal(await copyText('d', { clipboard: null, legacyCopy: () => false }), false)
  assert.equal(await copyText('e', { clipboard: null, legacyCopy: () => { throw new Error('no document') } }), false)
})
