// Run with `npm test` (node --test, Node ≥ 22.18 strips the types). Type-checked by `tsc -b`.
import { test } from 'node:test'
import assert from 'node:assert/strict'
import type { ProjectCardState, ProjectContextSummary } from './types'
import {
  assignProject, cardSelectable, projectPayload, projectRows, setProjectMode, unassignProject,
  type ProjectAssignments,
} from './projectAssignments.ts'

function ctx(name: string, cardVersion: number | null = null, cardStale = false): ProjectContextSummary {
  return { name, currentVersion: 1, isActive: true, totalVersions: 1, agents: [], cardVersion, cardStale }
}

// alpha has a card, beta has none, gamma has a stale one.
const contexts = [ctx('gamma', 1, true), ctx('beta'), ctx('alpha', 2)]

const mixed: ProjectAssignments = { projects: ['gamma', 'Alpha'], projectModes: { gamma: 'card', Alpha: 'full' } }

test('load: a mixed assignment shows the right checkboxes and modes, sorted by name', () => {
  const rows = projectRows(contexts, mixed)
  assert.deepEqual(
    rows.map(r => [r.name, r.assigned, r.mode, r.cardVersion]),
    [['Alpha', true, 'full', 2], ['beta', false, 'full', null], ['gamma', true, 'card', 1]])
})

test('load: an assignment with no project context still shows, so it can be unassigned', () => {
  const rows = projectRows(contexts, { projects: ['delta'], projectModes: { delta: 'card' } })
  const delta = rows.find(r => r.name === 'delta')!
  assert.equal(delta.assigned, true)
  assert.equal(delta.hasContext, false)
  assert.equal(delta.mode, 'card')
  assert.equal(rows.length, 4)
})

test('load: no mode entry means full', () => {
  const rows = projectRows(contexts, { projects: ['alpha'], projectModes: {} })
  assert.equal(rows.find(r => r.name === 'alpha')!.mode, 'full')
})

test('select: a project with a card is assigned in full mode and card becomes available', () => {
  const next = assignProject({ projects: ['gamma'], projectModes: { gamma: 'card' } }, 'alpha')
  assert.deepEqual(next, { projects: ['gamma', 'alpha'], projectModes: { gamma: 'card', alpha: 'full' } })
  const alpha = projectRows(contexts, next).find(r => r.name === 'alpha')!
  assert.equal(alpha.mode, 'full')
  assert.equal(cardSelectable(alpha), true)
})

test('select: a project without a card is assigned in full mode and card stays unavailable', () => {
  const next = assignProject({ projects: [], projectModes: {} }, 'beta')
  const beta = projectRows(contexts, next).find(r => r.name === 'beta')!
  assert.equal(beta.assigned, true)
  assert.equal(beta.mode, 'full')
  assert.equal(beta.cardVersion, null)
  assert.equal(cardSelectable(beta), false)
})

test('select: an unassigned row never offers card', () => {
  const alpha = projectRows(contexts, { projects: [], projectModes: {} }).find(r => r.name === 'alpha')!
  assert.equal(cardSelectable(alpha), false)
})

test('select: an already-assigned project is left alone, whatever the casing', () => {
  assert.equal(assignProject(mixed, 'ALPHA'), mixed)
})

test('deselect: removes both the assignment and its mode, case-insensitively', () => {
  const next = unassignProject(mixed, 'GAMMA')
  assert.deepEqual(next, { projects: ['Alpha'], projectModes: { Alpha: 'full' } })
  assert.deepEqual(projectPayload(next), { projects: ['Alpha'], projectModes: { Alpha: 'full' } })
})

test('deselect then select again starts from full', () => {
  const next = assignProject(unassignProject(mixed, 'gamma'), 'gamma')
  assert.equal(projectRows(contexts, next).find(r => r.name === 'gamma')!.mode, 'full')
})

test('mode change: replaces the entry under any casing instead of adding a second key', () => {
  const next = setProjectMode(mixed, 'alpha', 'card')
  assert.deepEqual(next.projectModes, { gamma: 'card', alpha: 'card' })
  assert.deepEqual(next.projects, mixed.projects)
  assert.deepEqual(projectPayload(next).projectModes, { gamma: 'card', Alpha: 'card' })
})

test('missing card: a stored card mode stays selectable so it still shows as card', () => {
  const gone = [ctx('alpha')]
  const alpha = projectRows(gone, { projects: ['alpha'], projectModes: { alpha: 'card' } })[0]
  assert.equal(alpha.cardVersion, null)
  assert.equal(alpha.mode, 'card')
  assert.equal(cardSelectable(alpha), true)
})

test('missing card: card detail adds missing keeps and the stale base version', () => {
  const card: ProjectCardState = {
    currentVersion: 1, basedOnFullVersion: 3, stale: true, missingKeeps: ['deploy'], invalidKeeps: [], versions: [],
  }
  const gamma = projectRows(contexts, mixed, { gamma: card }).find(r => r.name === 'gamma')!
  assert.equal(gamma.stale, true)
  assert.equal(gamma.staleBasedOn, 3)
  assert.deepEqual(gamma.missingKeeps, ['deploy'])
  // Without the detail, the list row's stale flag still shows.
  const listOnly = projectRows(contexts, mixed).find(r => r.name === 'gamma')!
  assert.equal(listOnly.stale, true)
  assert.equal(listOnly.staleBasedOn, null)
})

test('payload: one mode per assignment in stored order; stray and duplicate entries do not travel', () => {
  const payload = projectPayload({
    projects: [' gamma ', 'Alpha', 'alpha', ''],
    projectModes: { GAMMA: 'card', removed: 'card' },
  })
  assert.deepEqual(payload, { projects: ['gamma', 'Alpha'], projectModes: { gamma: 'card', Alpha: 'full' } })
})

test('payload: an unchanged load round-trips exactly', () => {
  assert.deepEqual(projectPayload(mixed), mixed)
})
