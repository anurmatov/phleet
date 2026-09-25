// Run with `npm test` (node --test, Node ≥ 22.18 strips the types). Type-checked by `tsc -b`.
import { test } from 'node:test'
import assert from 'node:assert/strict'
import type { ProjectContextSummary } from './types'
import {
  assignProject, projectPayload, projectRows, unassignProject,
  type ProjectAssignments,
} from './projectAssignments.ts'
import { sizeCell } from './sizeMeter.ts'

function ctx(name: string, currentBytes?: number | null): ProjectContextSummary {
  return { name, currentVersion: 1, isActive: true, totalVersions: 1, agents: [], currentBytes }
}

const contexts = [ctx('gamma', 12_000), ctx('beta', 400), ctx('alpha', 2_048)]

const assigned: ProjectAssignments = { projects: ['gamma', 'Alpha'] }

test('load: checkboxes, names and sizes, sorted by name', () => {
  assert.deepEqual(
    projectRows(contexts, assigned).map(r => [r.name, r.assigned, r.currentBytes]),
    [['Alpha', true, 2_048], ['beta', false, 400], ['gamma', true, 12_000]])
})

test('load: an assignment with no project context still shows, so it can be unassigned', () => {
  const rows = projectRows(contexts, { projects: ['delta'] })
  const delta = rows.find(r => r.name === 'delta')!
  assert.equal(delta.assigned, true)
  assert.equal(delta.hasContext, false)
  assert.equal(delta.currentBytes, null)
  assert.equal(rows.length, 4)
})

test('toggle on: assigns after the existing projects and checks the row', () => {
  const next = assignProject(assigned, 'beta')
  assert.deepEqual(next, { projects: ['gamma', 'Alpha', 'beta'] })
  assert.equal(projectRows(contexts, next).find(r => r.name === 'beta')!.assigned, true)
})

test('toggle on: an already-assigned project is left alone, whatever the casing', () => {
  assert.equal(assignProject(assigned, 'ALPHA'), assigned)
})

test('toggle off: removes the assignment case-insensitively and unchecks the row', () => {
  const next = unassignProject(assigned, 'GAMMA')
  assert.deepEqual(next, { projects: ['Alpha'] })
  assert.equal(projectRows(contexts, next).find(r => r.name === 'gamma')!.assigned, false)
})

test('toggle off then on again restores the assignment', () => {
  assert.deepEqual(assignProject(unassignProject(assigned, 'gamma'), 'gamma'), { projects: ['Alpha', 'gamma'] })
})

test('payload: names only, trimmed and deduplicated in stored order, nothing else travels', () => {
  const payload = projectPayload({ projects: [' gamma ', 'Alpha', 'alpha', ''] })
  assert.deepEqual(payload, { projects: ['gamma', 'Alpha'] })
  assert.deepEqual(Object.keys(payload), ['projects'])
})

test('size refresh: a refetched context list after a save changes the Size cell', () => {
  const before = projectRows(contexts, assigned).find(r => r.name === 'gamma')!
  assert.deepEqual(sizeCell(before.currentBytes, 10_000),
    { text: '12,000 B', over: true, title: '12,000 / 10,000 bytes — over the soft limit, saving is still allowed' })

  // The dashboard refetches /api/project-contexts after every context write; rows derive from it.
  const after = projectRows([ctx('gamma', 3_000), ctx('beta', 400), ctx('alpha', 2_048)], assigned)
    .find(r => r.name === 'gamma')!
  assert.deepEqual(sizeCell(after.currentBytes, 10_000), { text: '3,000 B', over: false, title: '3,000 / 10,000 bytes' })
})

test('size: limit disabled or unavailable shows bytes only, never a warning', () => {
  assert.deepEqual(sizeCell(12_000, null), { text: '12,000 B', over: false })
  assert.deepEqual(sizeCell(12_000, undefined), { text: '12,000 B', over: false })
})
