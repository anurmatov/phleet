// Run with `npm test` (node --test, Node ≥ 22.18 strips the types). Type-checked by `tsc -b`.
import { test } from 'node:test'
import assert from 'node:assert/strict'
import {
  WARMUP_TIMEOUT_DEFAULT, WARMUP_TIMEOUT_MAX, WARMUP_TIMEOUT_MIN,
  parseWarmupTimeoutSeconds, warmupTimeoutEditValue,
} from './warmupTimeout.ts'

test('bounds: 10..600 inclusive, default 60 — cloud agents keep the old startup pace', () => {
  assert.equal(WARMUP_TIMEOUT_MIN, 10)
  assert.equal(WARMUP_TIMEOUT_MAX, 600)
  assert.equal(WARMUP_TIMEOUT_DEFAULT, 60)
})

test('parse accepts the boundaries and a local-model 180', () => {
  for (const raw of ['10', '60', '180', '600']) {
    assert.deepEqual(parseWarmupTimeoutSeconds(raw), { ok: true, value: Number(raw) }, raw)
  }
})

test('parse rejects 9 and 601 without clamping them', () => {
  for (const raw of ['9', '601']) {
    const parsed = parseWarmupTimeoutSeconds(raw)
    assert.equal(parsed.ok, false, raw)
    if (!parsed.ok) assert.match(parsed.error, /10–600/)
  }
})

test('parse rejects non-integers and empty input as whole seconds are required', () => {
  for (const raw of ['', '  ', 'abc', '12.5']) {
    assert.equal(parseWarmupTimeoutSeconds(raw).ok, false, raw)
  }
})

test('edit mapping: a missing API field (older API) falls back to 60, present values round-trip', () => {
  assert.equal(warmupTimeoutEditValue(undefined), '60')
  assert.equal(warmupTimeoutEditValue(null), '60')
  assert.equal(warmupTimeoutEditValue(180), '180')
  assert.deepEqual(parseWarmupTimeoutSeconds(warmupTimeoutEditValue(600)), { ok: true, value: 600 })
})
