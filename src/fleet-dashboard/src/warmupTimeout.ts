// Per-agent warmup timeout (#357): seconds the startup ping may run before it gives up.
// Mirrors Fleet.Shared.WarmupTimeout in the orchestrator. The dashboard validates and
// never clamps — an out-of-range value must surface to the operator, not be silently fixed.

export const WARMUP_TIMEOUT_MIN = 10
export const WARMUP_TIMEOUT_MAX = 600
export const WARMUP_TIMEOUT_DEFAULT = 60

export type WarmupTimeoutParse =
  | { ok: true; value: number }
  | { ok: false; error: string }

export function parseWarmupTimeoutSeconds(raw: string): WarmupTimeoutParse {
  const value = Number(raw)
  if (raw.trim() === '' || !Number.isInteger(value))
    return { ok: false, error: 'Warmup timeout must be a whole number of seconds' }
  if (value < WARMUP_TIMEOUT_MIN || value > WARMUP_TIMEOUT_MAX)
    return { ok: false, error: `Warmup timeout must be ${WARMUP_TIMEOUT_MIN}–${WARMUP_TIMEOUT_MAX} seconds` }
  return { ok: true, value }
}

/** Edit-form value from an API row; a missing field (older API) falls back to the default 60. */
export function warmupTimeoutEditValue(apiValue: number | null | undefined): string {
  return String(apiValue ?? WARMUP_TIMEOUT_DEFAULT)
}
