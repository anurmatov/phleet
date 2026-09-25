// Run with `npm test` (node --test). The instruction and project-context lists build their row
// Size cell from the same expression — sizeCell(currentBytes, sizeLimit) fed into <SizeCellView> —
// so the three states must agree across both pages. These tests pin that contract without a DOM.
import { test } from 'node:test'
import assert from 'node:assert/strict'
import type { InstructionSummary, ProjectContextSummary } from './types'
import { sizeCell } from './sizeMeter.ts'

function instr(name: string, currentBytes?: number | null): InstructionSummary {
  return { name, currentVersion: 1, isActive: true, totalVersions: 1, agents: [], currentBytes }
}

function ctx(name: string, currentBytes?: number | null): ProjectContextSummary {
  return { name, currentVersion: 1, isActive: true, totalVersions: 1, agents: [], currentBytes }
}

// The instruction and project-context soft limits are the same value the server reports; the
// rendering contract does not depend on which kind, so one limit covers both lists.
const LIMIT = 10_000
const instrCell = (i: InstructionSummary) => sizeCell(i.currentBytes, LIMIT)
const ctxCell = (c: ProjectContextSummary) => sizeCell(c.currentBytes, LIMIT)

test('a known under-limit size renders the same on both lists', () => {
  const cell = { text: '4,586 B', over: false, title: '4,586 / 10,000 bytes' }
  assert.deepEqual(instrCell(instr('fleet', 4_586)), cell)
  assert.deepEqual(ctxCell(ctx('fleet', 4_586)), cell)
  assert.deepEqual(instrCell(instr('fleet', 4_586)), ctxCell(ctx('fleet', 4_586)))
})

test('an over-limit size shows the warning style and tooltip on both lists', () => {
  // 40,586 B is the example from issue #359: over the 10,000 soft limit but saving stays allowed.
  const cell = {
    text: '40,586 B',
    over: true,
    title: '40,586 / 10,000 bytes — over the soft limit, saving is still allowed',
  }
  assert.deepEqual(instrCell(instr('fleet', 40_586)), cell)
  assert.deepEqual(ctxCell(ctx('fleet', 40_586)), cell)
  assert.deepEqual(instrCell(instr('fleet', 40_586)), ctxCell(ctx('fleet', 40_586)))
})

test('a missing size (older orchestrator) renders a dash on both lists, not an estimate', () => {
  // currentBytes is absent/undefined when the server omits it; null is the same signal.
  assert.deepEqual(instrCell(instr('fleet')), { text: '—', over: false })
  assert.deepEqual(ctxCell(ctx('fleet')), { text: '—', over: false })
  assert.deepEqual(instrCell(instr('fleet')), ctxCell(ctx('fleet')))
  assert.deepEqual(sizeCell(null, LIMIT), { text: '—', over: false })
  assert.deepEqual(sizeCell(undefined, LIMIT), { text: '—', over: false })
})
