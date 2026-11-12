import type { WorkflowSummary, WorkflowEvent } from './types'

type Gate = Pick<WorkflowSummary, 'namespace' | 'workflowId' | 'runId' | 'workflowType' | 'status' | 'phase' | 'gateRef'>
export interface ApprovalRequest { Action: string; Scope: string; RequesterAgent: string }

export function approvalIdentity(wf: Gate): string {
  return JSON.stringify([wf.namespace, wf.workflowId, wf.runId, wf.gateRef])
}
export function approvalHistoryUrl(wf: Gate): string {
  if (!wf.runId) throw new Error('Exact run is required.')
  return `/api/workflows/${encodeURIComponent(wf.namespace)}/history/${encodeURIComponent(wf.workflowId)}?runId=${encodeURIComponent(wf.runId)}`
}
export function approvalRequestFromHistory(wf: Gate, events: Pick<WorkflowEvent, 'eventType' | 'inputSummary'>[]): ApprovalRequest {
  if (wf.workflowType !== 'UweHumanApprovalRequestWorkflow' || wf.status !== 'Running' ||
      wf.phase !== 'request-approval' || !wf.runId || wf.gateRef !== wf.runId)
    throw new Error('This exact request is no longer actionable.')
  const starts = events.filter(e => e.eventType === 'WorkflowStarted')
  if (starts.length !== 1 || !starts[0].inputSummary) throw new Error('Request history is unavailable.')
  const input: unknown = JSON.parse(starts[0].inputSummary)
  if (!input || typeof input !== 'object' || Array.isArray(input) ||
      Object.keys(input).sort().join(',') !== 'Action,RequesterAgent,Scope')
    throw new Error('Request history is malformed.')
  const request = input as ApprovalRequest
  if (![request.Action, request.Scope, request.RequesterAgent].every(v => typeof v === 'string' && v.trim().length > 0))
    throw new Error('Request history is incomplete.')
  return request
}
