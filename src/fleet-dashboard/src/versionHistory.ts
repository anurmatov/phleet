// Which two versions a comparison shows. The comparison itself is versionDiff.ts; this is the
// choice of its sides, the same on every page that keeps versions.
import type { WorkflowDefinitionDetail } from './types'

/** One version as the comparison knows it. */
export interface ComparedVersion {
  number: number
  content: string
  /** When it was written, who wrote it and why, where the page knows. */
  when?: string | null
  by?: string | null
  reason?: string | null
}

/**
 * The two sides. `older` is null for "nothing", which shows every line of `newer` as added.
 * `older` is never above `newer`: a comparison reads from the earlier version to the later one.
 */
export interface VersionPair { older: number | null; newer: number }

/** The version before `version` in the history: the highest number below it, or null for the first. */
export function previousVersion(numbers: readonly number[], version: number): number | null {
  let previous: number | null = null
  for (const n of numbers) if (n < version && (previous === null || n > previous)) previous = n
  return previous
}

/** What `version` changed: it against the one before it, and the first version against nothing. */
export function changesOf(numbers: readonly number[], version: number): VersionPair {
  return { older: previousVersion(numbers, version), newer: version }
}

/** `version` against the one in use, earlier first. Null when it is the one in use. */
export function againstCurrent(version: number, current: number): VersionPair | null {
  if (version === current) return null
  return { older: Math.min(version, current), newer: Math.max(version, current) }
}

/**
 * The pair with one side chosen freely. Choosing a later version as the older side, or an earlier
 * one as the newer side, swaps the two rather than showing the comparison backwards. Null is
 * "nothing", and only the older side can be that.
 */
export function withSide(pair: VersionPair, side: 'older' | 'newer', version: number | null): VersionPair {
  if (version === null) return { older: null, newer: pair.newer }
  if (side === 'older') return version > pair.newer ? { older: pair.newer, newer: version } : { older: version, newer: pair.newer }
  return pair.older !== null && version < pair.older ? { older: version, newer: pair.older } : { older: pair.older, newer: version }
}

export function samePair(a: VersionPair, b: VersionPair | null): boolean {
  return b !== null && a.older === b.older && a.newer === b.newer
}

/**
 * A workflow definition's versions, newest first: the one in use, then its history.
 *
 * The orchestrator keeps a history row per replaced version: the row numbered N holds the step
 * tree version N had, and is stamped with the time, the author and the reason of the write that
 * replaced it. So those three describe version N + 1, and are given to it here; version 1's are
 * the definition's own creation.
 */
export function definitionVersions(detail: WorkflowDefinitionDetail): ComparedVersion[] {
  const history = detail.versions.filter(v => v.version !== detail.version)
  const replacedBy = new Map(history.map(v => [v.version + 1, v]))
  const describe = (number: number, content: string): ComparedVersion => {
    const write = replacedBy.get(number)
    if (write) return { number, content, when: write.createdAt, by: write.createdBy ?? null, reason: write.reason ?? null }
    if (number === 1) return { number, content, when: detail.createdAt ?? null, by: detail.createdBy ?? null, reason: null }
    // Its write is not in the history that was sent: nothing is known of it.
    return { number, content, when: null, by: null, reason: null }
  }
  return [describe(detail.version, detail.definition), ...history.map(v => describe(v.version, v.definition))]
}
