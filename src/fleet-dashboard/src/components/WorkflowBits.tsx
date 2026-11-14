import type { WorkflowSummary } from '../types'
import { PHASE_LABELS } from '../constants'

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
