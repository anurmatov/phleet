import type { WorkflowSummary } from './types'
import { REQUEST_HEADER, isWrite } from './session'
import { requestJson, requestWrite } from './apiClient'

export { ApiError, ensureOk, errorMessage, firstFailure } from './apiClient'

export const TEMPORAL_UI  = import.meta.env.VITE_TEMPORAL_UI_URL ?? ''

/** Sent on the window when the orchestrator answers a request with 401: this browser may have been signed out. */
export const SIGNED_OUT_EVENT = 'fleet:unauthorized'

/**
 * Every request this page makes to the orchestrator and the chat service.
 *
 * The page holds no token. The session cookie is sent by the browser on its own (same origin), and
 * a write adds the one header the orchestrator requires of a cookie-authenticated write.
 */
export async function apiFetch(url: string, init?: RequestInit): Promise<Response> {
  const headers = new Headers(init?.headers)
  if (isWrite(init?.method)) headers.set(REQUEST_HEADER, '1')

  const response = await fetch(url, { ...init, headers, credentials: 'same-origin' })
  // Announced, not acted on here: whether this browser is really signed out is the session
  // route's answer to give, and one route's 401 is not always that.
  if (response.status === 401) window.dispatchEvent(new Event(SIGNED_OUT_EVENT))
  return response
}

/**
 * A read through {@link apiFetch} that fails loudly: the parsed JSON body of a 2xx answer, or a
 * thrown `ApiError`. Use it wherever the body goes into state.
 */
export function apiGet<T>(url: string, init?: RequestInit): Promise<T> {
  return requestJson<T>(apiFetch, url, init)
}

/**
 * A write through {@link apiFetch} that fails loudly. `json`, when given, is the JSON body.
 * Resolves with the answer's parsed body (null when it has none) or throws an `ApiError`, so
 * "Saved" can only follow a write the server accepted.
 */
export function apiSend<T = unknown>(url: string, method: 'POST' | 'PUT' | 'PATCH' | 'DELETE', json?: unknown): Promise<T> {
  return requestWrite<T>(apiFetch, url, method, json)
}

export function statusDot(effective: string) {
  if (effective === 'active' || effective === 'busy' || effective === 'idle' || effective === 'healthy')
    return <span className="dot dot-green" title={effective} />
  if (effective === 'stale')
    return <span className="dot dot-yellow" title="stale" />
  if (effective === 'dead')
    return <span className="dot dot-red" title="dead" />
  return <span className="dot dot-grey" title={effective} />
}

export function heartbeatAge(lastSeen: string): string {
  const secs = Math.floor((Date.now() - new Date(lastSeen).getTime()) / 1000)
  if (secs < 60) return `${secs}s ago`
  const mins = Math.floor(secs / 60)
  if (mins < 60) return `${mins}m ago`
  return `${Math.floor(mins / 60)}h ago`
}

export function formatDuration(secs: number): string {
  if (secs < 60) return `${Math.round(secs)}s`
  const mins = Math.floor(secs / 60)
  const rem = Math.round(secs % 60)
  return rem > 0 ? `${mins}m ${rem}s` : `${mins}m`
}

export function formatTime(iso: string): string {
  return new Date(iso).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' })
}

export function formatUptime(startedAt: string | null | undefined): string | null {
  if (!startedAt) return null
  const secs = Math.floor((Date.now() - new Date(startedAt).getTime()) / 1000)
  if (secs < 0) return null
  if (secs < 60) return `${secs}s`
  const mins = Math.floor(secs / 60)
  if (mins < 60) return `${mins}m`
  const hours = Math.floor(mins / 60)
  const remMins = mins % 60
  if (hours < 24) return remMins > 0 ? `${hours}h ${remMins}m` : `${hours}h`
  const days = Math.floor(hours / 24)
  const remHours = hours % 24
  return remHours > 0 ? `${days}d ${remHours}h` : `${days}d`
}

export function decodeUnicode(s: string): string {
  return s.replace(/\\u([0-9a-fA-F]{4})/g, (_, hex) => String.fromCharCode(parseInt(hex, 16)))
}

export function temporalUiUrl(wf: WorkflowSummary): string | null {
  if (!TEMPORAL_UI) return null
  return `${TEMPORAL_UI}/namespaces/${wf.namespace}/workflows/${encodeURIComponent(wf.workflowId)}/${wf.runId}/history`
}

export function relativeTime(iso: string): string {
  const secs = Math.floor((Date.now() - new Date(iso).getTime()) / 1000)
  if (secs < 60) return `${secs}s ago`
  const mins = Math.floor(secs / 60)
  if (mins < 60) return `${mins}m ago`
  const hours = Math.floor(mins / 60)
  if (hours < 24) return `${hours}h ago`
  return `${Math.floor(hours / 24)}d ago`
}

export type InlineSegment = { text: string; changed: boolean }
export type DiffLine = { type: 'same' | 'add' | 'remove'; text: string; segments?: InlineSegment[] }

export function computeInlineDiff(
  oldLine: string,
  newLine: string,
): { oldSegments: InlineSegment[]; newSegments: InlineSegment[] } {
  const m = oldLine.length, n = newLine.length
  const dp: number[][] = Array.from({ length: m + 1 }, () => new Array(n + 1).fill(0))
  for (let i = 1; i <= m; i++)
    for (let j = 1; j <= n; j++)
      dp[i][j] = oldLine[i-1] === newLine[j-1] ? dp[i-1][j-1] + 1 : Math.max(dp[i-1][j], dp[i][j-1])

  // Backtrack to classify each character as same or changed
  const oldMark: boolean[] = new Array(m).fill(true)   // true = changed
  const newMark: boolean[] = new Array(n).fill(true)
  let i = m, j = n
  while (i > 0 || j > 0) {
    if (i > 0 && j > 0 && oldLine[i-1] === newLine[j-1]) {
      oldMark[i-1] = false; newMark[j-1] = false; i--; j--
    } else if (j > 0 && (i === 0 || dp[i][j-1] >= dp[i-1][j])) { j-- }
    else { i-- }
  }

  function toSegments(str: string, marks: boolean[]): InlineSegment[] {
    const segs: InlineSegment[] = []
    let k = 0
    while (k < str.length) {
      const changed = marks[k]
      let text = str[k++]
      while (k < str.length && marks[k] === changed) text += str[k++]
      segs.push({ text, changed })
    }
    return segs
  }

  return {
    oldSegments: toSegments(oldLine, oldMark),
    newSegments: toSegments(newLine, newMark),
  }
}

function enrichWithInlineDiff(lines: DiffLine[]): DiffLine[] {
  const result = [...lines]
  let i = 0
  while (i < result.length) {
    if (result[i].type === 'remove' && i + 1 < result.length && result[i + 1].type === 'add') {
      const { oldSegments, newSegments } = computeInlineDiff(result[i].text, result[i + 1].text)
      result[i] = { ...result[i], segments: oldSegments }
      result[i + 1] = { ...result[i + 1], segments: newSegments }
      i += 2
    } else {
      i++
    }
  }
  return result
}

export function computeDiff(
  oldText: string,
  newText: string,
): DiffLine[] {
  const oldLines = oldText.split('\n')
  const newLines = newText.split('\n')
  const m = oldLines.length, n = newLines.length
  const dp: number[][] = Array.from({ length: m + 1 }, () => new Array(n + 1).fill(0))
  for (let i = 1; i <= m; i++)
    for (let j = 1; j <= n; j++)
      dp[i][j] = oldLines[i-1] === newLines[j-1] ? dp[i-1][j-1] + 1 : Math.max(dp[i-1][j], dp[i][j-1])
  const result: DiffLine[] = []
  let i = m, j = n
  while (i > 0 || j > 0) {
    if (i > 0 && j > 0 && oldLines[i-1] === newLines[j-1]) { result.unshift({ type: 'same', text: oldLines[i-1] }); i--; j-- }
    else if (j > 0 && (i === 0 || dp[i][j-1] >= dp[i-1][j])) { result.unshift({ type: 'add', text: newLines[j-1] }); j-- }
    else { result.unshift({ type: 'remove', text: oldLines[i-1] }); i-- }
  }
  return enrichWithInlineDiff(result)
}
