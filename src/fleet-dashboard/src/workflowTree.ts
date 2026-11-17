// Which run belongs under which.
//
// The list of running workflows is flat, and several of its rows are one piece of work: a driver,
// the runs it started, and the reviews those runs started in turn. Two things link them. A run
// started as a child workflow names its Temporal parent. A run a driver started through the start
// tool is not Temporal's child, and names the driver as the run that waits for it. This lays the
// list out as the tree those links make, without changing which runs are in it or their order
// among siblings.
//
// Kept free of JSX so `node --test` can run it.

/** What of a run the tree reads. */
export interface LinkedRun {
  workflowId: string
  runId?: string | null
  namespace: string
  parentWorkflowId?: string | null
  parentRunId?: string | null
  waiterWorkflowId?: string | null
}

/** How a run is tied to the one it sits under. */
export type RunLink = 'child' | 'started'

export interface PlacedRun<T> {
  run: T
  /** 0 for a run that stands at the top of the list. */
  depth: number
  /** How it is tied to the row it is nested under. Null at the top. */
  link: RunLink | null
  /**
   * The run it belongs to, when that run is not in this list (it is shown elsewhere, or is over).
   * Said on the row in words, since there is no row to nest it under.
   */
  elsewhere: { workflowId: string; link: RunLink; run: T | null } | null
}

/** Deeper than this, rows stop moving right: a phone has no room, and the order still says it. */
export const MAX_DEPTH = 4

function sameRun(candidate: LinkedRun, run: LinkedRun): boolean {
  return candidate.namespace === run.namespace && candidate.workflowId === run.workflowId
    && (candidate.runId ?? '') === (run.runId ?? '')
}

/** The run `run` belongs to among `among`, and how. The Temporal parent wins over the waiter. */
function ownerOf<T extends LinkedRun>(run: T, among: readonly T[]): { owner: T; link: RunLink } | null {
  if (run.parentWorkflowId) {
    const parent = among.find(c => !sameRun(c, run) && c.namespace === run.namespace && c.workflowId === run.parentWorkflowId
      // A parent's id is reused across its runs; the run id says which one, when both sides know it.
      && (!run.parentRunId || !c.runId || c.runId === run.parentRunId))
    if (parent) return { owner: parent, link: 'child' }
  }
  if (run.waiterWorkflowId) {
    const candidates = among.filter(c => !sameRun(c, run) && c.workflowId === run.waiterWorkflowId)
    const waiter = candidates.find(c => c.namespace === run.namespace) ?? candidates[0]
    if (waiter) return { owner: waiter, link: 'started' }
  }
  return null
}

/**
 * `runs` in tree order: each run that belongs to another in the list directly after it, one level
 * in. Every run is placed exactly once, and runs at the same level keep the order they came in.
 * @param everything every run the page knows, to name an owner that is not in `runs`.
 */
export function arrangeRuns<T extends LinkedRun>(runs: readonly T[], everything: readonly T[] = runs): PlacedRun<T>[] {
  const owners = new Map<T, { owner: T; link: RunLink }>()
  const children = new Map<T, T[]>()
  for (const run of runs) {
    const tie = ownerOf(run, runs)
    if (!tie) continue
    owners.set(run, tie)
    children.set(tie.owner, [...(children.get(tie.owner) ?? []), run])
  }

  const placed: PlacedRun<T>[] = []
  const seen = new Set<T>()
  const place = (run: T, depth: number) => {
    if (seen.has(run)) return
    seen.add(run)
    const tie = depth > 0 ? owners.get(run) : undefined
    placed.push({ run, depth: Math.min(depth, MAX_DEPTH), link: tie?.link ?? null, elsewhere: tie ? null : elsewhereOf(run, runs, everything) })
    for (const child of children.get(run) ?? []) place(child, depth + 1)
  }
  for (const run of runs) if (!owners.has(run)) place(run, 0)
  // Runs that only point at each other have no top: each still gets its row.
  for (const run of runs) place(run, 0)
  return placed
}

function elsewhereOf<T extends LinkedRun>(run: T, runs: readonly T[], everything: readonly T[]): PlacedRun<T>['elsewhere'] {
  if (ownerOf(run, runs)) return null
  const known = ownerOf(run, everything)
  if (known) return { workflowId: known.owner.workflowId, link: known.link, run: known.owner }
  if (run.parentWorkflowId) return { workflowId: run.parentWorkflowId, link: 'child', run: null }
  if (run.waiterWorkflowId) return { workflowId: run.waiterWorkflowId, link: 'started', run: null }
  return null
}

/** The few words that tie a row to its owner: "child of …" or "started by …". */
export function linkWords(link: RunLink): string {
  return link === 'child' ? 'child of' : 'started by'
}
