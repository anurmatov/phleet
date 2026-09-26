// Run with `npm test` (node --test, Node ≥ 22.18 strips the types). Type-checked by `tsc -b`.
import { test } from 'node:test'
import assert from 'node:assert/strict'
import {
  CONTEXT_WINDOW_MAX, CONTEXT_WINDOW_MIN, contextWindowEditValue, parseContextWindow,
} from './contextWindow.ts'

test('bounds: 4096..1048576 inclusive, the same as the orchestrator', () => {
  assert.equal(CONTEXT_WINDOW_MIN, 4096)
  assert.equal(CONTEXT_WINDOW_MAX, 1048576)
})

test('parse accepts the boundaries and a typical server --ctx', () => {
  for (const raw of ['4096', '65536', '131072', '1048576']) {
    assert.deepEqual(parseContextWindow(raw), { ok: true, value: Number(raw) }, raw)
  }
})

test('empty input means not set and is sent as 0, the value the API clears on', () => {
  assert.deepEqual(parseContextWindow(''), { ok: true, value: 0 })
  assert.deepEqual(parseContextWindow('   '), { ok: true, value: 0 })
})

test('parse rejects out-of-range values without clamping them, naming the range', () => {
  for (const raw of ['4095', '1048577', '0', '-1']) {
    const parsed = parseContextWindow(raw)
    assert.equal(parsed.ok, false, raw)
    if (!parsed.ok) assert.match(parsed.error, /4096–1048576/)
  }
})

test('parse rejects non-integers', () => {
  for (const raw of ['abc', '65536.5']) assert.equal(parseContextWindow(raw).ok, false, raw)
})

test('edit mapping: null or missing is an empty field; values round-trip', () => {
  assert.equal(contextWindowEditValue(undefined), '')
  assert.equal(contextWindowEditValue(null), '')
  assert.equal(contextWindowEditValue(131072), '131072')
  assert.deepEqual(parseContextWindow(contextWindowEditValue(65536)), { ok: true, value: 65536 })
  assert.deepEqual(parseContextWindow(contextWindowEditValue(null)), { ok: true, value: 0 })
})
