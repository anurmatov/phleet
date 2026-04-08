import type { AnyStep, StepType } from './editorTypes'
import { genId } from './treeUtils'

export function makeStep(type: StepType): AnyStep {
  const base: AnyStep = { type, _id: genId() }
  switch (type) {
    case 'sequence':
      return { ...base, steps: [] }
    case 'parallel':
      return { ...base, steps: [] }
    case 'loop':
      return { ...base, maxIterations: 5, steps: [] }
    case 'branch':
      return { ...base, on: '', cases: {} }
    case 'delegate':
      return { ...base, target: '', instruction: '', timeoutMinutes: 30, retryOnIncomplete: true, maxIncompleteRetries: 3 }
    case 'delegate_with_escalation':
      return { ...base, target: '', instruction: '', timeoutMinutes: 30, retryOnIncomplete: true, maxIncompleteRetries: 3 }
    case 'wait_for_signal':
      return { ...base, signalName: '', maxReminders: 3 }
    case 'child_workflow':
      return { ...base, workflowType: '' }
    case 'fire_and_forget':
      return { ...base, workflowType: '' }
    case 'cross_namespace_start':
      return { ...base, workflowType: '', namespace: '', taskQueue: '' }
    case 'set_attribute':
      return { ...base, attributes: [] }
    case 'http_request':
      return { ...base, url: '', method: 'GET', timeoutSeconds: 30 }
    default:
      return base
  }
}
