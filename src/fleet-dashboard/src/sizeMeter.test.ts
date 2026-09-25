// Run with `npm test` (node --test, Node ≥ 22.18 strips the types). Type-checked by `tsc -b`.
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { formatBytes, meterState, utf8Bytes } from './sizeMeter.ts'

test('utf8Bytes counts bytes, not UTF-16 units — same table as the server evaluators', () => {
  const table: Array<[string, number]> = [['', 0], ['abc', 3], ['д', 2], ['中', 3], ['😀', 4], ['aд中😀', 10]]
  for (const [text, bytes] of table) assert.equal(utf8Bytes(text), bytes, JSON.stringify(text))
  // 10 UTF-16 units, 11 bytes: .length would undercount.
  assert.equal('aaaaaaaaaд'.length, 10)
  assert.equal(utf8Bytes('aaaaaaaaaд'), 11)
})

test('formatBytes renders N0 like the server', () => {
  assert.equal(formatBytes(0), '0')
  assert.equal(formatBytes(10000), '10,000')
  assert.equal(formatBytes(1234567), '1,234,567')
})

test('under: at the limit is still under', () => {
  assert.deepEqual(meterState('aaaaaaaa' + 'д', 10, 'soft limit off'), { bytes: 10, status: 'under', label: '10 / 10 bytes' })
})

test('over: strictly greater than the limit', () => {
  assert.deepEqual(meterState('д'.repeat(600), 1000, 'soft limit off'), { bytes: 1200, status: 'over', label: '1,200 / 1,000 bytes' })
})

test('unavailable: no limit could be read', () => {
  assert.deepEqual(meterState('abc', undefined, 'guidance off'), { bytes: 3, status: 'unavailable', label: '3 bytes — limit unavailable' })
})

test('off: the server reports the check disabled', () => {
  assert.deepEqual(meterState('a'.repeat(20000), null, 'guidance off'), { bytes: 20000, status: 'off', label: '20,000 bytes — guidance off' })
})
