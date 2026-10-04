// Run with `npm test` (node --test, Node ≥ 22.18 strips the types). Type-checked by `tsc -b`.
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { formatStorageBytes, mediaStatItems, type MediaStatItem } from './journalMedia.ts'

const texts = (items: MediaStatItem[]) => items.map(item => item.text)

const full = {
  state: 'enabled',
  objects: 12,
  bytesStored: 3565158,
  lastSweepAt: '2026-01-01T00:00:00Z',
  deletedSinceStart: { abandoned: 1, retired: 2, orphans: 3 },
  sweepFailures: 0,
  upload: { windowSeconds: 3600, samples: 40, p50Ms: 120.2, p95Ms: 839.6 },
}

function assertNoBadText(items: MediaStatItem[]) {
  for (const text of texts(items)) assert.doesNotMatch(text, /NaN|undefined|null/, text)
}

test('formatStorageBytes: IEC units, one decimal, unit picked after rounding, stops at TiB', () => {
  const table: Array<[number, string]> = [
    [0, '0 B'],
    [1023, '1023 B'],
    [1024, '1.0 KiB'],
    [1536, '1.5 KiB'],
    [1048575, '1.0 MiB'],
    [3565158, '3.4 MiB'],
    [1073741824, '1.0 GiB'],
    [1099511627776, '1.0 TiB'],
    [5629499534213120, '5120.0 TiB'],
  ]
  for (const [n, text] of table) assert.equal(formatStorageBytes(n), text, String(n))
})

test('absent, null or non-object media gives no row', () => {
  for (const media of [undefined, null, 'x', 42, true, []]) assert.deepEqual(mediaStatItems(media), [], JSON.stringify(media))
})

test('full fixture: five items in order, bytes title, no tone', () => {
  const items = mediaStatItems(full)
  assert.deepEqual(texts(items), ['media enabled', 'stored 3.4 MiB', 'orphans deleted 3', 'sweep failures 0', 'upload p95 840 ms'])
  assert.equal(items[1].title, '3,565,158 bytes')
  for (const item of items) assert.equal('tone' in item, false, item.key)
  assertNoBadText(items)
})

test('degraded carries the warn tone; any other state is shown as-is without one', () => {
  const degraded = mediaStatItems({ ...full, state: 'degraded' })
  assert.equal(degraded[0].text, 'media degraded')
  assert.equal(degraded[0].tone, 'warn')
  for (const item of degraded.slice(1)) assert.equal('tone' in item, false, item.key)

  const paused = mediaStatItems({ ...full, state: 'paused' })
  assert.equal(paused[0].text, 'media paused')
  assert.equal('tone' in paused[0], false)
})

test('zero is a real value; no upload samples is n/a', () => {
  const items = mediaStatItems({
    ...full,
    bytesStored: 0,
    deletedSinceStart: { abandoned: 0, retired: 0, orphans: 0 },
    sweepFailures: 0,
    upload: { samples: 0, p50Ms: null, p95Ms: null },
  })
  assert.deepEqual(texts(items).slice(1), ['stored 0 B', 'orphans deleted 0', 'sweep failures 0', 'upload p95 n/a'])
  assert.equal(items[1].title, '0 bytes')
  assertNoBadText(items)
})

test('malformed fields render n/a, never a made-up zero', () => {
  const empty = mediaStatItems({})
  assert.deepEqual(texts(empty), ['media n/a', 'stored n/a', 'orphans deleted n/a', 'sweep failures n/a', 'upload p95 n/a'])
  assert.equal('title' in empty[1], false)
  assertNoBadText(empty)

  const wrong = mediaStatItems({ bytesStored: '12', sweepFailures: -1, deletedSinceStart: null, upload: null })
  assert.deepEqual(texts(wrong).slice(1), ['stored n/a', 'orphans deleted n/a', 'sweep failures n/a', 'upload p95 n/a'])
  assertNoBadText(wrong)

  assert.equal(mediaStatItems({ upload: { samples: 0, p95Ms: 12 } })[4].text, 'upload p95 n/a')
  assert.equal(mediaStatItems({ upload: { p95Ms: 12.4 } })[4].text, 'upload p95 12 ms')

  for (const state of ['', 7]) {
    const items = mediaStatItems({ state })
    assert.equal(items[0].text, 'media n/a', JSON.stringify(state))
    assertNoBadText(items)
  }

  for (const n of [NaN, Infinity, -0.5]) {
    const items = mediaStatItems({ bytesStored: n, sweepFailures: n, deletedSinceStart: { orphans: n }, upload: { p95Ms: n } })
    assert.deepEqual(texts(items).slice(1), ['stored n/a', 'orphans deleted n/a', 'sweep failures n/a', 'upload p95 n/a'], String(n))
  }
})

test('never throws for nested wrong types', () => {
  const values = [undefined, null, 'x', 42, true, [], {}, [1, 2]]
  for (const v of values) {
    assert.doesNotThrow(() => mediaStatItems({ state: v, bytesStored: v, deletedSinceStart: v, sweepFailures: v, upload: v }))
    assert.doesNotThrow(() => mediaStatItems({ deletedSinceStart: { orphans: v }, upload: { samples: v, p95Ms: v } }))
  }
})
