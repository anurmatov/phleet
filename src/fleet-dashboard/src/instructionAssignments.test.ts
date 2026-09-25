// Run with `npm test` (node --test, Node ≥ 22.18 strips the types). Type-checked by `tsc -b`.
import { test } from 'node:test'
import assert from 'node:assert/strict'
import type { InstructionSummary } from './types'
import { instructionRows } from './instructionAssignments.ts'
import { sizeCell } from './sizeMeter.ts'

function instr(name: string, currentBytes?: number | null, isActive = true): InstructionSummary {
  return { name, currentVersion: 1, isActive, totalVersions: 1, agents: [], currentBytes }
}

test('rows: active instructions except base, with their load order and size', () => {
  const rows = instructionRows(
    [instr('base', 900), instr('dev', 1200), instr('old', 50, false), instr('ops', 300)],
    [{ name: 'ops', loadOrder: 2 }])
  assert.deepEqual(rows, [
    { name: 'dev', checked: false, loadOrder: 1, currentBytes: 1200 },
    { name: 'ops', checked: true, loadOrder: 2, currentBytes: 300 },
  ])
})

test('size refresh: a refetched list after a new instruction version changes the Size cell', () => {
  const selected = [{ name: 'dev', loadOrder: 1 }]
  const before = instructionRows([instr('dev', 9_000)], selected)[0]
  assert.deepEqual(sizeCell(before.currentBytes, 10_000), { text: '9,000 B', over: false, title: '9,000 / 10,000 bytes' })

  // The dashboard refetches /api/instructions after every write; the rows are derived from it.
  const after = instructionRows([instr('dev', 12_345)], selected)[0]
  assert.equal(sizeCell(after.currentBytes, 10_000).text, '12,345 B')
  assert.equal(sizeCell(after.currentBytes, 10_000).over, true)
})

test('size: an older orchestrator without currentBytes shows a dash', () => {
  const row = instructionRows([instr('dev')], [])[0]
  assert.equal(row.currentBytes, null)
  assert.deepEqual(sizeCell(row.currentBytes, 10_000), { text: '—', over: false })
})
