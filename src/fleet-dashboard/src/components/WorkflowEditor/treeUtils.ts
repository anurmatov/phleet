import type { AnyStep, StepPath, BranchCaseValue } from './editorTypes'

let _idCounter = 0
export function genId(): string {
  return `s${++_idCounter}_${Math.random().toString(36).slice(2, 7)}`
}

/** Assign _id recursively to all steps that lack one */
export function ensureIds(step: AnyStep): AnyStep {
  const s: AnyStep = { ...step, _id: step._id ?? genId() }
  if (s.steps) s.steps = s.steps.map(ensureIds)
  if (s.step) s.step = ensureIds(s.step)
  if (s.notifyStep) s.notifyStep = ensureIds(s.notifyStep)
  if (s.cases) {
    const newCases: Record<string, BranchCaseValue> = {}
    for (const [k, v] of Object.entries(s.cases)) {
      newCases[k] = typeof v === 'string' ? v : ensureIds(v)
    }
    s.cases = newCases
  }
  if (s.default && typeof s.default !== 'string') {
    s.default = ensureIds(s.default as AnyStep)
  }
  return s
}

/** Strip editor-only fields before serializing to JSON */
export function stripIds(step: AnyStep): AnyStep {
  // eslint-disable-next-line @typescript-eslint/no-unused-vars
  const { _id, ...rest } = step
  const s = { ...rest } as AnyStep
  if (s.steps) s.steps = s.steps.map(stripIds)
  if (s.step) s.step = stripIds(s.step)
  if (s.notifyStep) s.notifyStep = stripIds(s.notifyStep)
  if (s.cases) {
    const newCases: Record<string, BranchCaseValue> = {}
    for (const [k, v] of Object.entries(s.cases)) {
      newCases[k] = typeof v === 'string' ? v : stripIds(v)
    }
    s.cases = newCases
  }
  if (s.default && typeof s.default !== 'string') {
    s.default = stripIds(s.default as AnyStep)
  }
  if (s.type === 'set_attribute' && Array.isArray(s.attributes)) {
    const dict: Record<string, string> = {}
    for (const attr of s.attributes as Array<{ name: string; value: string }>) {
      if (attr.name) dict[attr.name] = attr.value
    }
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    ;(s as any).attributes = dict
  }
  return s
}

export function getStepAtPath(root: AnyStep, path: StepPath): AnyStep | null {
  if (path.length === 0) return root
  const [seg, ...rest] = path
  if (typeof seg === 'string') {
    // Branch case key — navigate into cases[key]
    const caseVal = root.cases?.[seg]
    if (!caseVal || typeof caseVal === 'string') return null
    return getStepAtPath(caseVal as AnyStep, rest)
  }
  const children = root.steps
  if (!children || seg >= children.length) return null
  return getStepAtPath(children[seg], rest)
}

export function updateStepAtPath(root: AnyStep, path: StepPath, updater: (s: AnyStep) => AnyStep): AnyStep {
  if (path.length === 0) return updater(root)
  const [seg, ...rest] = path
  if (typeof seg === 'string') {
    const caseVal = root.cases?.[seg]
    if (!caseVal || typeof caseVal === 'string') return root
    const updated = updateStepAtPath(caseVal as AnyStep, rest, updater)
    return { ...root, cases: { ...(root.cases ?? {}), [seg]: updated } }
  }
  if (!root.steps) return root
  const newSteps = [...root.steps]
  newSteps[seg] = updateStepAtPath(newSteps[seg], rest, updater)
  return { ...root, steps: newSteps }
}

export function insertStepAtPath(root: AnyStep, parentPath: StepPath, index: number, step: AnyStep): AnyStep {
  return updateStepAtPath(root, parentPath, parent => {
    const newSteps = [...(parent.steps ?? [])]
    newSteps.splice(index, 0, step)
    return { ...parent, steps: newSteps }
  })
}

export function removeStepAtPath(root: AnyStep, path: StepPath): AnyStep {
  const parentPath = path.slice(0, -1)
  const seg = path[path.length - 1]
  if (typeof seg === 'string') {
    // Removing a branch case sub-step removes the whole case entry
    return updateStepAtPath(root, parentPath, parent => {
      const newCases = { ...(parent.cases ?? {}) }
      delete newCases[seg]
      return { ...parent, cases: newCases }
    })
  }
  return updateStepAtPath(root, parentPath, parent => {
    const newSteps = [...(parent.steps ?? [])]
    newSteps.splice(seg, 1)
    return { ...parent, steps: newSteps }
  })
}

export function moveStep(root: AnyStep, fromPath: StepPath, toParentPath: StepPath, toIndex: number): AnyStep {
  const step = getStepAtPath(root, fromPath)
  if (!step) return root
  // Adjust toIndex if moving within same parent (numeric) and to later position
  let adjustedIndex = toIndex
  const fromParentPath = fromPath.slice(0, -1)
  const fromSeg = fromPath[fromPath.length - 1]
  const sameParent = JSON.stringify(fromParentPath) === JSON.stringify(toParentPath)
  if (sameParent && typeof fromSeg === 'number' && fromSeg < toIndex) adjustedIndex--

  let newRoot = removeStepAtPath(root, fromPath)
  newRoot = insertStepAtPath(newRoot, toParentPath, adjustedIndex, step)
  return newRoot
}

/** Build a flat map: stepId → { path, step, parentPath } */
export type NodeInfo = { path: StepPath; step: AnyStep; parentPath: StepPath; index: number }
export function buildNodeMap(root: AnyStep, path: StepPath = [], parentPath: StepPath = [], index = 0): Map<string, NodeInfo> {
  const map = new Map<string, NodeInfo>()
  if (root._id) map.set(root._id, { path, step: root, parentPath, index })
  if (root.steps) {
    root.steps.forEach((s, i) => {
      const childMap = buildNodeMap(s, [...path, i], path, i)
      childMap.forEach((v, k) => map.set(k, v))
    })
  }
  if (root.cases) {
    Object.entries(root.cases).forEach(([caseKey, v], i) => {
      if (typeof v !== 'string' && v._id) {
        const childMap = buildNodeMap(v, [...path, caseKey], path, i)
        childMap.forEach((cv, ck) => map.set(ck, cv))
      }
    })
  }
  return map
}

/** Normalize a step parsed from JSON for the editor:
 *  - set_attribute.attributes: dict {name: value} → Array<{name, value}>
 *  - branch.default: ensure it's handled recursively
 */
function normalizeSetAttributeAttrs(step: AnyStep): AnyStep {
  const s = { ...step }
  if (s.type === 'set_attribute' && s.attributes && !Array.isArray(s.attributes)) {
    const dict = s.attributes as unknown as Record<string, string>
    s.attributes = Object.entries(dict).map(([name, value]) => ({ name, value }))
  }
  if (s.steps) s.steps = s.steps.map(normalizeSetAttributeAttrs)
  if (s.step) s.step = normalizeSetAttributeAttrs(s.step)
  if (s.notifyStep) s.notifyStep = normalizeSetAttributeAttrs(s.notifyStep)
  if (s.cases) {
    const newCases: Record<string, BranchCaseValue> = {}
    for (const [k, v] of Object.entries(s.cases)) {
      newCases[k] = typeof v === 'string' ? v : normalizeSetAttributeAttrs(v as AnyStep)
    }
    s.cases = newCases
  }
  if (s.default && typeof s.default !== 'string') {
    s.default = normalizeSetAttributeAttrs(s.default as AnyStep)
  }
  return s
}

/** Deserialize from raw JSON string → AnyStep with IDs */
export function deserializeStep(json: string): AnyStep {
  const parsed = JSON.parse(json) as AnyStep
  return ensureIds(normalizeSetAttributeAttrs(parsed))
}

/** Serialize AnyStep → JSON string (strips _id fields) */
export function serializeStep(step: AnyStep, indent = 2): string {
  return JSON.stringify(stripIds(step), null, indent)
}

/** Update a branch case */
export function updateBranchCase(root: AnyStep, path: StepPath, caseKey: string, value: BranchCaseValue): AnyStep {
  return updateStepAtPath(root, path, step => ({
    ...step,
    cases: { ...(step.cases ?? {}), [caseKey]: value },
  }))
}

export function removeBranchCase(root: AnyStep, path: StepPath, caseKey: string): AnyStep {
  return updateStepAtPath(root, path, step => {
    const newCases = { ...(step.cases ?? {}) }
    delete newCases[caseKey]
    return { ...step, cases: newCases }
  })
}

/** Rename a branch case key */
export function renameBranchCase(root: AnyStep, path: StepPath, oldKey: string, newKey: string): AnyStep {
  return updateStepAtPath(root, path, step => {
    const newCases: Record<string, BranchCaseValue> = {}
    for (const [k, v] of Object.entries(step.cases ?? {})) {
      newCases[k === oldKey ? newKey : k] = v
    }
    return { ...step, cases: newCases }
  })
}

/** Check if toPath is inside fromPath (prevent dropping into own descendants) */
export function isDescendant(fromPath: StepPath, toPath: StepPath): boolean {
  if (toPath.length <= fromPath.length) return false
  return fromPath.every((v, i) => v === toPath[i])
}
