import type { CSSProperties } from 'react'
import type { WorkflowSummary } from '../types'
import { PHASE_LABELS } from '../constants'
import { linkWords, type PlacedRun } from '../workflowTree'

// The small parts every list of workflows shows the same way: the phase a run is in, and the
// issue and pull requests it is about.

export function PhaseChip({ phase }: { phase: string | null | undefined }) {
  if (!phase) return null
  const info = PHASE_LABELS[phase]
  const label = info?.label ?? phase
  const color = info?.color ?? 'grey'
  return <span className={`pill phase-chip phase-${color}`}>{label}</span>
}

export function WorkflowRefs({ wf }: { wf: WorkflowSummary }) {
  // A link inside a row that is itself pressable must not also open the row.
  const stop = (e: React.MouseEvent) => e.stopPropagation()
  return (
    <>
      {wf.issueNumber && wf.repo && (
        <a href={`https://github.com/${wf.repo}/issues/${wf.issueNumber}`} target="_blank" rel="noreferrer" className="wf-link" onClick={stop} title={`GitHub issue #${wf.issueNumber}`}>#{wf.issueNumber}</a>
      )}
      {wf.prNumber && wf.repo && (
        <a href={`https://github.com/${wf.repo}/pull/${wf.prNumber}`} target="_blank" rel="noreferrer" className="wf-link" onClick={stop} title={`GitHub PR #${wf.prNumber}`}>PR #{wf.prNumber}</a>
      )}
      {wf.docPrs && wf.docPrs.split(/\s+/).map(ref => {
        const m = ref.match(/^([\w-]+\/[\w-]+)#(\d+)$/)
        if (!m) return null
        return (
          <a key={ref} href={`https://github.com/${m[1]}/pull/${m[2]}`} target="_blank" rel="noreferrer" className="wf-link" onClick={stop} title={`Doc PR ${ref}`}>doc #{m[2]}</a>
        )
      })}
    </>
  )
}

/** A run id is long and mostly noise: the front of it is enough to recognise, the title has it all. */
export function shortWorkflowId(id: string) {
  return id.length > 40 ? id.slice(0, 38) + '…' : id
}

// ── a run among the runs it belongs with ─────────────────────────────────────
// Every list of running workflows is laid out by `arrangeRuns`, and shows the result the same
// way: these three go on a row together.

/** What a nested row adds to its `wf-row`: the class, and how far in it sits. */
export function nestedRow(depth: number): { className: string; 'data-depth': number; style?: CSSProperties } {
  return {
    className: depth > 0 ? ' wf-row--nested' : '',
    'data-depth': depth,
    style: depth > 0 ? { '--wf-depth': depth } as CSSProperties : undefined,
  }
}

/** The elbow that ties a nested run to the one above it. The words are for a screen reader. */
export function RunElbow({ placed }: { placed: Pick<PlacedRun<WorkflowSummary>, 'depth' | 'link'> }) {
  if (placed.depth === 0 || !placed.link) return null
  return <span className="wf-nest" role="img" aria-label={`${linkWords(placed.link)} the run above`} />
}

/** Whose a run is, in words, when its owner is not in this list. Goes first in the row's meta line. */
export function RunOwner({ elsewhere }: { elsewhere: PlacedRun<WorkflowSummary>['elsewhere'] }) {
  if (!elsewhere) return null
  return (
    <span className="wf-owner" title={elsewhere.workflowId}>
      {linkWords(elsewhere.link)} {elsewhere.run?.workflowType ?? shortWorkflowId(elsewhere.workflowId)}
    </span>
  )
}
