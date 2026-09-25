// Size meter state for the instruction, project-context and memory editors (#346). Kept free of
// `import.meta.env` and JSX so `node --test` can run it directly.
//
// The measure is UTF-8 bytes, the same count the server makes (Encoding.UTF8.GetByteCount). JS
// `.length` counts UTF-16 units and undercounts non-Latin text, so it is never used here. No limit
// is hard-coded: the limit always comes from the server.

const encoder = new TextEncoder()

export function utf8Bytes(text: string): number {
  return encoder.encode(text).length
}

/** `12,345` — the same N0 rendering the server uses in its messages. */
export function formatBytes(n: number): string {
  return n.toLocaleString('en-US')
}

/**
 * The limit a meter measures against: a number when active, `null` when the server reports the
 * check disabled, `undefined` when the limit could not be read (fetch failed, 404, older server).
 */
export type MeterLimit = number | null | undefined

export type MeterStatus = 'under' | 'over' | 'off' | 'unavailable'

export interface MeterState {
  bytes: number
  status: MeterStatus
  /** e.g. `12,345 / 10,000 bytes`, `12,345 bytes — guidance off`, `12,345 bytes — limit unavailable`. */
  label: string
}

/**
 * @param offLabel what a disabled limit is called: `soft limit off` for prompts, `guidance off` for memories.
 */
export function meterState(text: string, limit: MeterLimit, offLabel: string): MeterState {
  const bytes = utf8Bytes(text)
  if (limit === undefined) return { bytes, status: 'unavailable', label: `${formatBytes(bytes)} bytes — limit unavailable` }
  if (limit === null) return { bytes, status: 'off', label: `${formatBytes(bytes)} bytes — ${offLabel}` }
  return {
    bytes,
    status: bytes > limit ? 'over' : 'under',
    label: `${formatBytes(bytes)} / ${formatBytes(limit)} bytes`,
  }
}
