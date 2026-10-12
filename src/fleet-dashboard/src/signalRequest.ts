import type { WorkflowSummary } from './types'

type RenderedGate = Pick<WorkflowSummary, 'workflowType' | 'runId' | 'gateRef'>

// The modal's rendered snapshot is the expectation; never refresh it while sending.
export function buildSignalRequest(wf: RenderedGate, signalName: string, payload: string, comment?: string) {
  return {
    signalName,
    workflowType: wf.workflowType,
    payload: comment ? JSON.stringify({ ...JSON.parse(payload), Comment: comment }) : payload,
    runId: wf.runId,
    expectedGateRef: wf.gateRef,
  }
}

export function signalErrorMessage(status: number, body: { error?: string } | null): string {
  return status === 409 ? 'This gate changed. Refresh before deciding.' : body?.error ?? `Error ${status}`
}
