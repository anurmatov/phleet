// Types for the Visual Workflow Editor

export type StepType =
  | 'sequence' | 'parallel' | 'loop' | 'branch'
  | 'break' | 'continue' | 'noop'
  | 'delegate' | 'delegate_with_escalation'
  | 'wait_for_signal'
  | 'child_workflow' | 'fire_and_forget' | 'cross_namespace_start'
  | 'set_variable' | 'set_attribute' | 'http_request'
  | 'sleep'

export const STEP_TYPES: StepType[] = [
  'sequence', 'parallel', 'loop', 'branch',
  'break', 'continue', 'noop',
  'delegate', 'delegate_with_escalation',
  'wait_for_signal',
  'child_workflow', 'fire_and_forget', 'cross_namespace_start',
  'set_variable', 'set_attribute', 'http_request',
  'sleep',
]

export const STEP_COLORS: Record<string, string> = {
  sequence: 'var(--purple)',
  parallel: 'var(--purple)',
  loop: 'var(--purple)',
  branch: 'var(--purple)',
  break: 'var(--danger)',
  continue: 'var(--danger)',
  noop: 'var(--text-3)',
  delegate: 'var(--info)',
  delegate_with_escalation: 'var(--info)',
  wait_for_signal: 'var(--warn)',
  child_workflow: 'var(--accent)',
  fire_and_forget: 'var(--accent)',
  cross_namespace_start: 'var(--accent)',
  set_variable: 'var(--warn)',
  set_attribute: 'var(--text-3)',
  http_request: 'var(--text-3)',
  sleep: 'var(--text-3)',
}

export const STEP_CATEGORIES = [
  { label: 'Control Flow', types: ['sequence', 'parallel', 'loop', 'branch', 'break', 'continue', 'noop'] as StepType[] },
  { label: 'Agent', types: ['delegate', 'delegate_with_escalation'] as StepType[] },
  { label: 'Signal', types: ['wait_for_signal'] as StepType[] },
  { label: 'Workflow', types: ['child_workflow', 'fire_and_forget', 'cross_namespace_start'] as StepType[] },
  { label: 'Utility', types: ['set_variable', 'set_attribute', 'http_request', 'sleep'] as StepType[] },
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
  // sleep
  seconds?: number
}

/** Path segments are numeric (index in .steps[]) or string (branch case key) */
export type StepPath = (number | string)[]
export type ViewMode = 'visual' | 'json' | 'split'

export interface ValidationError {
  pathStr: string
  message: string
  blocking: boolean
}
