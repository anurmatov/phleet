import type { WorkflowSummary, WorkflowEvent } from './types'

type Gate = Pick<WorkflowSummary, 'namespace' | 'workflowId' | 'runId' | 'workflowType' | 'status' | 'phase' | 'gateRef'>
export interface HumanInputRequest {
  Question: string; Context: string; AnswerMode: 'choice' | 'text' | 'approve-reject'
  Choices?: { Id: string; Label: string }[]
  Origin: { Kind: 'agent'; RequesterAgent: string } | { Kind: 'parent'; Namespace: string; WorkflowId: string; RunId: string }
}
export function humanInputIdentity(wf: Gate): string { return JSON.stringify([wf.namespace, wf.workflowId, wf.runId, wf.gateRef, wf.status, wf.phase, wf.workflowType]) }
export function humanInputHistoryUrl(wf: Gate): string {
  if (!wf.runId) throw new Error('Exact run is required.')
  return `/api/workflows/${encodeURIComponent(wf.namespace)}/history/${encodeURIComponent(wf.workflowId)}?runId=${encodeURIComponent(wf.runId)}`
}
const exact = (v: object, keys: string[]) => Object.keys(v).sort().join(',') === keys.sort().join(',')
const nonblank = (v: unknown): v is string => typeof v === 'string' && !!v.trim()
export function humanInputRequestFromHistory(wf: Gate, events: Pick<WorkflowEvent, 'eventType' | 'inputSummary'>[]): HumanInputRequest {
  if (wf.workflowType !== 'UweHumanInputWorkflow' || wf.status !== 'Running' || wf.phase !== 'request-input' ||
      !wf.runId || wf.gateRef !== wf.runId) throw new Error('This exact request is no longer actionable.')
  const starts = events.filter(e => e.eventType === 'WorkflowStarted')
  if (starts.length !== 1 || !starts[0].inputSummary) throw new Error('Request history is unavailable.')
  const r = JSON.parse(starts[0].inputSummary) as HumanInputRequest
  if (!r || Array.isArray(r) || !exact(r, ['Question', 'Context', 'AnswerMode', 'Origin', ...(r.AnswerMode === 'choice' ? ['Choices'] : [])]) ||
      !nonblank(r.Question) || !nonblank(r.Context) || !r.Origin || !['choice', 'text', 'approve-reject'].includes(r.AnswerMode)) throw new Error('Malformed request.')
  if (r.AnswerMode === 'choice' && (!Array.isArray(r.Choices) || r.Choices.length < 2 || r.Choices.length > 8 ||
    r.Choices.some(c => !c || !exact(c, ['Id', 'Label']) || !/^[a-z][a-z0-9_-]{0,31}$/.test(c.Id) || !nonblank(c.Label)) ||
    new Set(r.Choices.map(c => c.Id)).size !== r.Choices.length)) throw new Error('Malformed choices.')
  const o = r.Origin
  if (o.Kind === 'agent' ? !exact(o, ['Kind', 'RequesterAgent']) || !nonblank(o.RequesterAgent) :
      o.Kind !== 'parent' || !exact(o, ['Kind', 'Namespace', 'WorkflowId', 'RunId']) || ![o.Namespace, o.WorkflowId, o.RunId].every(nonblank)) throw new Error('Malformed origin.')
  return r
}
export function humanInputChoices(r: HumanInputRequest) {
  return r.AnswerMode === 'approve-reject' ? [{ Id: 'approved', Label: 'Approve' }, { Id: 'rejected', Label: 'Reject' }] : r.Choices ?? []
}
export function validHumanAnswer(r: HumanInputRequest, value: string): boolean {
  return r.AnswerMode === 'text' ? !!value.trim() && value.length <= 2000 : humanInputChoices(r).some(c => c.Id === value)
}
