import type { AnyStep, ValidationError } from './editorTypes'

export function validateTree(root: AnyStep): ValidationError[] {
  const errors: ValidationError[] = []
  validateStep(root, '', errors)
  return errors
}

function validateStep(step: AnyStep, pathStr: string, errors: ValidationError[]) {
  const p = pathStr || 'root'

  switch (step.type) {
    case 'delegate':
    case 'delegate_with_escalation':
      if (!step.target?.trim())
        errors.push({ pathStr: p, message: '"target" is required', blocking: true })
      if (!step.instruction?.trim())
        errors.push({ pathStr: p, message: '"instruction" is required', blocking: true })
      break
    case 'wait_for_signal':
      if (!step.signalName?.trim())
        errors.push({ pathStr: p, message: '"signalName" is required', blocking: true })
      break
    case 'child_workflow':
    case 'fire_and_forget':
      if (!step.workflowType?.trim())
        errors.push({ pathStr: p, message: '"workflowType" is required', blocking: true })
      break
    case 'cross_namespace_start':
      if (!step.workflowType?.trim())
        errors.push({ pathStr: p, message: '"workflowType" is required', blocking: true })
      if (!step.namespace?.trim())
        errors.push({ pathStr: p, message: '"namespace" is required', blocking: true })
      if (!step.taskQueue?.trim())
        errors.push({ pathStr: p, message: '"taskQueue" is required', blocking: true })
      break
    case 'http_request':
      if (!step.url?.trim())
        errors.push({ pathStr: p, message: '"url" is required', blocking: true })
      break
    case 'branch':
      if (!step.on?.trim())
        errors.push({ pathStr: p, message: '"on" expression is required', blocking: true })
      if (!step.cases || Object.keys(step.cases).length === 0)
        errors.push({ pathStr: p, message: 'at least one case is required', blocking: true })
      Object.entries(step.cases ?? {}).forEach(([key, val]) =>
        validateStep(val as AnyStep, `${p}.cases.${key}`, errors))
      if (step.default) validateStep(step.default as AnyStep, `${p}.default`, errors)
      break
    case 'break':
    case 'continue':
    case 'noop':
      // leaf steps — no children required
      break
    case 'set_variable':
      if (!step.vars || Object.keys(step.vars).length === 0)
        errors.push({ pathStr: p, message: '"vars" must be non-empty', blocking: true })
      break
    case 'loop':
    case 'sequence':
      if (!step.steps || step.steps.length === 0)
        errors.push({ pathStr: p, message: '"steps" must be non-empty', blocking: true })
      step.steps?.forEach((s, i) => validateStep(s, `${p}.steps[${i}]`, errors))
      break
    case 'parallel':
      if (step.forEach !== undefined && step.forEach !== '') {
        if (!step.itemVar?.trim())
          errors.push({ pathStr: p, message: 'forEach mode requires "itemVar"', blocking: true })
        if (!step.step)
          errors.push({ pathStr: p, message: 'forEach mode requires "step"', blocking: true })
      } else {
        step.steps?.forEach((s, i) => validateStep(s, `${p}.steps[${i}]`, errors))
      }
      break
  }

  // Template syntax warning
  const json = JSON.stringify(step)
  const openCount = (json.match(/\{\{/g) || []).length
  const closeCount = (json.match(/\}\}/g) || []).length
  if (openCount > closeCount)
    errors.push({ pathStr: p, message: 'unclosed {{ expression', blocking: false })
}

export function stepSummary(step: AnyStep): string {
  switch (step.type) {
    case 'delegate':
    case 'delegate_with_escalation':
      return step.target ? `→ ${step.target}` : ''
    case 'wait_for_signal':
      return step.signalName ? `signal: ${step.signalName}` : ''
    case 'child_workflow':
    case 'fire_and_forget':
    case 'cross_namespace_start':
      return step.workflowType ?? ''
    case 'branch':
      return step.on ? `on: ${step.on.slice(0, 30)}` : ''
    case 'loop':
      return step.maxIterations ? `max: ${step.maxIterations}` : ''
    case 'http_request':
      return step.url ? step.url.slice(0, 40) : ''
    case 'set_variable':
      return step.vars ? Object.keys(step.vars).join(', ') : ''
    case 'set_attribute':
      return step.attributes?.map(a => a.name).join(', ') ?? ''
    default:
      return ''
  }
}

export function isBranchCaseTerminal(v: AnyStep): boolean {
  return v.type === 'break' || v.type === 'continue' || v.type === 'noop'
}
