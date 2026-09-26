// Context window of a local-model Claude agent's server, in tokens (#367). Mirrors
// Fleet.Shared.ContextWindow in the orchestrator. Empty means "not set" and is sent as 0, the
// value the API clears on. Out-of-range input is rejected, never clamped.

export const CONTEXT_WINDOW_MIN = 4096
export const CONTEXT_WINDOW_MAX = 1048576

export type ContextWindowParse =
  | { ok: true; value: number }
  | { ok: false; error: string }

export function parseContextWindow(raw: string): ContextWindowParse {
  if (raw.trim() === '') return { ok: true, value: 0 }
  const value = Number(raw)
  if (!Number.isInteger(value))
    return { ok: false, error: 'Context window must be a whole number of tokens' }
  if (value < CONTEXT_WINDOW_MIN || value > CONTEXT_WINDOW_MAX)
    return { ok: false, error: `Context window must be ${CONTEXT_WINDOW_MIN}–${CONTEXT_WINDOW_MAX} tokens, or empty` }
  return { ok: true, value }
}

/** Edit-form value from an API row: null or a missing field (older API) is an empty field. */
export function contextWindowEditValue(apiValue: number | null | undefined): string {
  return apiValue == null ? '' : String(apiValue)
}
