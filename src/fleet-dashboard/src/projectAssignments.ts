import type { ProjectCardState, ProjectContextMode, ProjectContextSummary } from './types'

// Project assignment editing for the agent config modal. Kept free of `import.meta.env` and JSX
// so `node --test` can load it directly (see projectAssignments.test.ts).

/** Project names compare case-insensitively, the way the orchestrator compares them. */
function sameName(a: string, b: string): boolean {
  return a.toLowerCase() === b.toLowerCase()
}

function withoutKey(modes: Record<string, ProjectContextMode>, project: string): Record<string, ProjectContextMode> {
  return Object.fromEntries(Object.entries(modes).filter(([k]) => !sameName(k, project)))
}

/**
 * A project's context mode from a name-keyed map. Matched case-insensitively, the way the
 * orchestrator compares project names; a project that is not in the map is `full`.
 */
export function projectModeFor(
  modes: Record<string, ProjectContextMode> | undefined,
  project: string,
): ProjectContextMode {
  if (!modes) return 'full'
  const key = Object.keys(modes).find(k => sameName(k, project))
  return key !== undefined && modes[key] === 'card' ? 'card' : 'full'
}

export interface ProjectAssignments {
  projects: string[]
  projectModes: Record<string, ProjectContextMode>
}

/** Adds `project` in `full` mode, after the existing assignments. Already assigned → unchanged. */
export function assignProject(current: ProjectAssignments, project: string): ProjectAssignments {
  if (current.projects.some(p => sameName(p, project))) return current
  return {
    projects: [...current.projects, project],
    projectModes: { ...withoutKey(current.projectModes, project), [project]: 'full' },
  }
}

/** Removes `project` from the assignments and drops its mode, so a re-check starts at `full`. */
export function unassignProject(current: ProjectAssignments, project: string): ProjectAssignments {
  return {
    projects: current.projects.filter(p => !sameName(p, project)),
    projectModes: withoutKey(current.projectModes, project),
  }
}

export function setProjectMode(current: ProjectAssignments, project: string, mode: ProjectContextMode): ProjectAssignments {
  return { ...current, projectModes: { ...withoutKey(current.projectModes, project), [project]: mode } }
}

/**
 * The `projects` / `projectModes` pair for `PUT /api/agents/{name}/config`: names trimmed and
 * deduplicated case-insensitively in their existing order, and exactly one mode per assignment,
 * keyed as it is sent in `projects` — the server rejects a mode key that is not an assignment.
 */
export function projectPayload(current: ProjectAssignments): ProjectAssignments {
  const projects = current.projects
    .map(p => p.trim())
    .filter(Boolean)
    .filter((p, i, all) => all.findIndex(q => sameName(q, p)) === i)
  return {
    projects,
    projectModes: Object.fromEntries(projects.map(p => [p, projectModeFor(current.projectModes, p)])),
  }
}

export interface ProjectRow {
  /** The stored assignment name when assigned, else the project context's name. */
  name: string
  assigned: boolean
  /** `full` for an unassigned row. */
  mode: ProjectContextMode
  /** False for an assignment that matches no project context. */
  hasContext: boolean
  /** Current card version, or null when the project has no card. */
  cardVersion: number | null
  stale: boolean
  /** The full-context version a stale card was written for, when the card detail is loaded. */
  staleBasedOn: number | null
  /** Keep markers the card lacks; provisioning renders full until it has them. */
  missingKeeps: string[]
}

/**
 * One row per project context plus one per assignment that matches none, sorted by name.
 * `cardStates` holds the card detail by lower-cased project name, where it has been loaded.
 */
export function projectRows(
  contexts: ProjectContextSummary[],
  current: ProjectAssignments,
  cardStates: Record<string, ProjectCardState | null> = {},
): ProjectRow[] {
  const rows: ProjectRow[] = contexts.map(ctx => {
    const assignedAs = current.projects.find(p => sameName(p, ctx.name))
    const card = ctx.cardVersion != null ? cardStates[ctx.name.toLowerCase()] : undefined
    const stale = card?.stale ?? ctx.cardStale ?? false
    return {
      name: assignedAs ?? ctx.name,
      assigned: assignedAs !== undefined,
      mode: assignedAs !== undefined ? projectModeFor(current.projectModes, assignedAs) : 'full',
      hasContext: true,
      cardVersion: ctx.cardVersion ?? null,
      stale,
      staleBasedOn: stale && card ? card.basedOnFullVersion : null,
      missingKeeps: card?.missingKeeps ?? [],
    }
  })
  for (const p of projectPayload(current).projects) {
    if (contexts.some(ctx => sameName(ctx.name, p))) continue
    rows.push({
      name: p, assigned: true, mode: projectModeFor(current.projectModes, p), hasContext: false,
      cardVersion: null, stale: false, staleBasedOn: null, missingKeeps: [],
    })
  }
  const key = (r: ProjectRow) => r.name.toLowerCase()
  return rows.sort((a, b) => key(a) < key(b) ? -1 : key(a) > key(b) ? 1 : 0)
}

/**
 * Whether `card` can be picked for the row. A stored `card` stays pickable so it still shows as
 * card while the project list catches up; the server rejects it on save if the card is gone.
 */
export function cardSelectable(row: ProjectRow): boolean {
  return row.assigned && (row.cardVersion != null || row.mode === 'card')
}
