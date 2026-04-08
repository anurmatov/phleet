// Types for the Visual Workflow Editor

export type StepType =
  | 'sequence' | 'parallel' | 'loop' | 'branch'
  | 'break' | 'continue' | 'noop'
  | 'delegate' | 'delegate_with_escalation'
  | 'wait_for_signal'
  | 'child_workflow' | 'fire_and_forget' | 'cross_namespace_start'
  | 'set_variable' | 'set_attribute' | 'http_request'

export const STEP_TYPES: StepType[] = [
  'sequence', 'parallel', 'loop', 'branch',
  'break', 'continue', 'noop',
  'delegate', 'delegate_with_escalation',
  'wait_for_signal',
  'child_workflow', 'fire_and_forget', 'cross_namespace_start',
  'set_variable', 'set_attribute', 'http_request',
]

export const STEP_COLORS: Record<string, string> = {
  sequence: '#a78bfa',
  parallel: '#a78bfa',
  loop: '#a78bfa',
  branch: '#a78bfa',
  break: '#f87171',
  continue: '#f87171',
  noop: '#94a3b8',
  delegate: '#4f6ef7',
  delegate_with_escalation: '#4f6ef7',
  wait_for_signal: '#f59e0b',
  child_workflow: '#4ade80',
  fire_and_forget: '#4ade80',
  cross_namespace_start: '#4ade80',
  set_variable: '#fb923c',
  set_attribute: '#94a3b8',
  http_request: '#94a3b8',
}

export const STEP_CATEGORIES = [
  { label: 'Control Flow', types: ['sequence', 'parallel', 'loop', 'branch', 'break', 'continue', 'noop'] as StepType[] },
  { label: 'Agent', types: ['delegate', 'delegate_with_escalation'] as StepType[] },
  { label: 'Signal', types: ['wait_for_signal'] as StepType[] },
  { label: 'Workflow', types: ['child_workflow', 'fire_and_forget', 'cross_namespace_start'] as StepType[] },
  { label: 'Utility', types: ['set_variable', 'set_attribute', 'http_request'] as StepType[] },
]

export const CONTAINER_TYPES = new Set<StepType>(['sequence', 'parallel', 'loop', 'branch'])

// Branch case is always a step definition; use {"type":"break"} or {"type":"continue"} for loop control
export type BranchCaseValue = AnyStep

export interface AnyStep {
  type: StepType
  _id?: string  // editor-only, stripped on serialize
  name?: string
  outputVar?: string
  ignoreFailure?: boolean
  // sequence, loop, parallel (static)
  steps?: AnyStep[]
  // loop
  maxIterations?: number
  // parallel forEach
  forEach?: string
  itemVar?: string
  step?: AnyStep
  // branch
  on?: string
  cases?: Record<string, BranchCaseValue>
  default?: BranchCaseValue
  // delegate / delegate_with_escalation
  target?: string
  instruction?: string
  timeoutMinutes?: number
  retryOnIncomplete?: boolean
  maxIncompleteRetries?: number
  // wait_for_signal
  signalName?: string
  phase?: string
  reminderIntervalMinutes?: number
  maxReminders?: number
  autoCompleteOnTimeout?: boolean
  notifyStep?: AnyStep
  // child_workflow / fire_and_forget / cross_namespace_start
  workflowType?: string
  taskQueue?: string
  namespace?: string
  workflowId?: string
  args?: Record<string, unknown>  // key -> typed value (string, number, boolean, or array)
  // set_variable
  vars?: Record<string, string>
  // set_attribute
  attributes?: Array<{ name: string; value: string }>
  // http_request
  url?: string
  method?: string
  headers?: Array<{ name: string; value: string }>
  body?: string
  timeoutSeconds?: number
  expectedStatusCodes?: number[]
}

/** Path segments are numeric (index in .steps[]) or string (branch case key) */
export type StepPath = (number | string)[]
export type ViewMode = 'visual' | 'json' | 'split'

export interface ValidationError {
  pathStr: string
  message: string
  blocking: boolean
}
