import type { ProjectContextSummary } from './types'

// Project assignment editing for the agent config modal. Kept free of `import.meta.env` and JSX
// so `node --test` can load it directly (see projectAssignments.test.ts).

/** Project names compare case-insensitively, the way the orchestrator compares them. */
function sameName(a: string, b: string): boolean {
  return a.toLowerCase() === b.toLowerCase()
}

export interface ProjectAssignments {
  projects: string[]
}

/** Adds `project` after the existing assignments. Already assigned → unchanged. */
export function assignProject(current: ProjectAssignments, project: string): ProjectAssignments {
  if (current.projects.some(p => sameName(p, project))) return current
  return { projects: [...current.projects, project] }
}

export function unassignProject(current: ProjectAssignments, project: string): ProjectAssignments {
  return { projects: current.projects.filter(p => !sameName(p, project)) }
}

/**
 * The `projects` list for `PUT /api/agents/{name}/config`: names trimmed and deduplicated
 * case-insensitively in their existing order.
 */
export function projectPayload(current: ProjectAssignments): ProjectAssignments {
  return {
    projects: current.projects
      .map(p => p.trim())
      .filter(Boolean)
      .filter((p, i, all) => all.findIndex(q => sameName(q, p)) === i),
  }
}

export interface ProjectRow {
  /** The stored assignment name when assigned, else the project context's name. */
  name: string
  assigned: boolean
  /** False for an assignment that matches no project context. */
  hasContext: boolean
  /** UTF-8 bytes of the canonical context; null when unknown or when there is no context. */
  currentBytes: number | null
}

/** One row per project context plus one per assignment that matches none, sorted by name. */
export function projectRows(contexts: ProjectContextSummary[], current: ProjectAssignments): ProjectRow[] {
  const rows: ProjectRow[] = contexts.map(ctx => {
    const assignedAs = current.projects.find(p => sameName(p, ctx.name))
    return {
      name: assignedAs ?? ctx.name,
      assigned: assignedAs !== undefined,
      hasContext: true,
      currentBytes: ctx.currentBytes ?? null,
    }
  })
  for (const p of projectPayload(current).projects) {
    if (contexts.some(ctx => sameName(ctx.name, p))) continue
    rows.push({ name: p, assigned: true, hasContext: false, currentBytes: null })
  }
  const key = (r: ProjectRow) => r.name.toLowerCase()
  return rows.sort((a, b) => key(a) < key(b) ? -1 : key(a) > key(b) ? 1 : 0)
}
