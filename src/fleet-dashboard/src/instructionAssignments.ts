import type { InstructionSummary } from './types'

// Role-instruction rows for the agent config modal. Kept free of `import.meta.env` and JSX so
// `node --test` can load it directly (see instructionAssignments.test.ts).

export interface InstructionAssignment {
  name: string
  loadOrder: number
}

export interface InstructionRow {
  name: string
  checked: boolean
  /** The assignment's load order; 1 for an unchecked row. */
  loadOrder: number
  /** UTF-8 bytes of the current version; null when unknown (an older orchestrator). */
  currentBytes: number | null
}

/**
 * One row per assignable instruction — active, and not `base`, which is auto-attached — in the
 * list's order. Derived from the latest instruction list on every render, so a new version shows
 * its size as soon as the list is refetched.
 */
export function instructionRows(all: InstructionSummary[], selected: InstructionAssignment[]): InstructionRow[] {
  return all
    .filter(i => i.isActive && i.name !== 'base')
    .map(i => {
      const assignment = selected.find(s => s.name === i.name)
      return {
        name: i.name,
        checked: assignment !== undefined,
        loadOrder: assignment?.loadOrder ?? 1,
        currentBytes: i.currentBytes ?? null,
      }
    })
}
