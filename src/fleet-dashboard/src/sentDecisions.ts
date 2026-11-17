import type { WorkflowSummary } from './types'

// A decision this page has sent to a waiting run. The run keeps its phase until the workflow takes
// the signal up, which can be minutes when it is in the middle of something else; until then the
// run is not waiting for the person any more, and its buttons stay away.
//
// It is kept for one visit of one phase of one run: a new run, another phase, or a run that has
// stopped, ends it. Two visits of the same phase with nothing seen between them cannot be told
// apart from here, which is why the note that replaces the buttons offers them back.

export interface SentDecision {
  runId: string
  phase: string | null
  /** The label of the button that was sent, e.g. "Approve". */
  label: string
}

/** Keyed by namespace and workflow id. */
export type SentDecisions = Readonly<Record<string, SentDecision>>

function keyOf(wf: WorkflowSummary): string { return `${wf.namespace}/${wf.workflowId}` }

export function withDecision(sent: SentDecisions, wf: WorkflowSummary, label: string): SentDecisions {
  return { ...sent, [keyOf(wf)]: { runId: wf.runId, phase: wf.phase, label } }
}

export function withoutDecision(sent: SentDecisions, wf: WorkflowSummary): SentDecisions {
  if (!(keyOf(wf) in sent)) return sent
  const next = { ...sent }
  delete next[keyOf(wf)]
  return next
}

/** The label of the decision sent for this run's current phase, or null when none was. */
export function sentDecision(sent: SentDecisions, wf: WorkflowSummary): string | null {
  const entry = sent[keyOf(wf)]
  return entry && entry.runId === wf.runId && entry.phase === wf.phase ? entry.label : null
}

/** Drops the decisions whose run left that phase, was replaced, stopped, or left the list. */
export function pruneDecisions(sent: SentDecisions, current: WorkflowSummary[]): SentDecisions {
  const running = new Map(current.filter(wf => wf.status === 'Running').map(wf => [keyOf(wf), wf]))
  const kept = Object.entries(sent).filter(([key, entry]) => {
    const wf = running.get(key)
    return wf !== undefined && wf.runId === entry.runId && wf.phase === entry.phase
  })
  return kept.length === Object.keys(sent).length ? sent : Object.fromEntries(kept)
}
