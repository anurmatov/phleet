// The Usage page's logic: the request it sends, the response it accepts and what each group means.
// Kept free of JSX and `import.meta.env` so `node --test` can run it.
//
// Values arrive as exact strings (or null) and stay strings: a label shows the receiver's own digits,
// and only the chart's line converts them to numbers. Null is "not reported", never 0.

export const USAGE_METRICS = [
  { id: 'output_tokens', label: 'Output tokens' },
  { id: 'input_tokens', label: 'Input tokens' },
  { id: 'cache_read_tokens', label: 'Cache read tokens' },
  { id: 'cache_creation_tokens', label: 'Cache creation tokens' },
  { id: 'cost_usd', label: 'Reported cost (USD)' },
] as const

export type UsageMetric = (typeof USAGE_METRICS)[number]['id']

export const USAGE_PRESETS = [
  { id: '24h', label: 'Last 24 hours', hours: 24 },
  { id: '7d', label: 'Last 7 days', hours: 168 },
] as const

export type UsagePreset = (typeof USAGE_PRESETS)[number]['id']

export interface UsageFilters {
  preset: UsagePreset
  metric: UsageMetric
  agent: string
  provider: string
  model: string
}

export const DEFAULT_USAGE_FILTERS: UsageFilters = {
  preset: '24h', metric: 'output_tokens', agent: '', provider: '', model: '',
}

export interface UsageBucket {
  start: string
  reported_value: string | null
  eligible_turns: number
  measured_turns: number
  reset_excluded: number
}

export interface UsageGroup extends Omit<UsageBucket, 'start'> {
  agent: string
  provider: string
  model: string
  buckets: UsageBucket[]
}

export interface UsageSummary {
  from: string
  to: string
  metric: string
  bucket_seconds: number
  matched_groups: number
  groups_shown: number
  truncated: boolean
  groups: UsageGroup[]
}

/** Why there is no current answer. */
export type UsageFailure = 'disabled' | 'not_configured' | 'unavailable'

const HOUR_MS = 3_600_000

export function metricLabel(metric: string): string {
  return USAGE_METRICS.find(m => m.id === metric)?.label ?? metric
}

/** The bounds for a preset ending now, as UTC with milliseconds. */
export function usageRange(preset: UsagePreset, now: Date): { from: string; to: string } {
  const hours = USAGE_PRESETS.find(p => p.id === preset)?.hours ?? 24
  const to = new Date(Math.floor(now.getTime()))
  const from = new Date(to.getTime() - hours * HOUR_MS)
  return { from: from.toISOString(), to: to.toISOString() }
}

/** The query string for `/api/usage`. Exact filters are sent trimmed, and only when set. */
export function usageQuery(filters: UsageFilters, now: Date): string {
  const { from, to } = usageRange(filters.preset, now)
  const params = new URLSearchParams({ from, to, metric: filters.metric })
  for (const key of ['agent', 'provider', 'model'] as const) {
    const value = filters[key].trim()
    if (value) params.set(key, value)
  }
  return params.toString()
}

const isCount = (v: unknown): v is number => typeof v === 'number' && Number.isInteger(v) && v >= 0
const isValue = (v: unknown): v is string | null => v === null || (typeof v === 'string' && /^\d+(\.\d+)?$/.test(v))

function isBucketShape(b: unknown): boolean {
  if (typeof b !== 'object' || b === null) return false
  const r = b as Record<string, unknown>
  return isValue(r.reported_value) && isCount(r.eligible_turns) && isCount(r.measured_turns) && isCount(r.reset_excluded)
}

/**
 * The response if it has the contract's shape, else null. A malformed answer is shown as
 * unavailable, never patched up with zeros.
 */
export function parseUsageSummary(body: unknown): UsageSummary | null {
  if (typeof body !== 'object' || body === null) return null
  const s = body as Record<string, unknown>
  if (typeof s.from !== 'string' || typeof s.to !== 'string' || typeof s.metric !== 'string') return null
  if (!isCount(s.matched_groups) || !isCount(s.groups_shown) || typeof s.truncated !== 'boolean') return null
  if (!isCount(s.bucket_seconds) || !Array.isArray(s.groups)) return null
  for (const g of s.groups as unknown[]) {
    if (!isBucketShape(g)) return null
    const r = g as Record<string, unknown>
    if (typeof r.agent !== 'string' || typeof r.provider !== 'string' || typeof r.model !== 'string') return null
    if (!Array.isArray(r.buckets)) return null
    for (const b of r.buckets as unknown[]) {
      if (!isBucketShape(b) || typeof (b as Record<string, unknown>).start !== 'string') return null
    }
  }
  return body as UsageSummary
}

/** A non-2xx answer as a failure: the orchestrator names the two integration states; anything else is unavailable. */
export function usageFailure(status: number, body: unknown): UsageFailure {
  const error = typeof body === 'object' && body !== null ? (body as Record<string, unknown>).error : undefined
  if (status === 503 && error === 'usage_disabled') return 'disabled'
  if (status === 503 && error === 'usage_not_configured') return 'not_configured'
  return 'unavailable'
}

export type CoverageState = 'measured' | 'partial' | 'not_reported' | 'reset_only'

/** How much of a group or hour the value covers. */
export function coverageState(c: Omit<UsageBucket, 'start'>): CoverageState {
  if (c.eligible_turns === 0 && c.reset_excluded > 0) return 'reset_only'
  if (c.measured_turns === 0) return 'not_reported'
  return c.measured_turns < c.eligible_turns ? 'partial' : 'measured'
}

/** "2 of 3 turns reported", plus the reset turns kept out of the subtotal. */
export function coverageText(c: Omit<UsageBucket, 'start'>): string {
  const turns = (n: number) => `${n} turn${n === 1 ? '' : 's'}`
  const parts = c.eligible_turns > 0 ? [`${c.measured_turns} of ${turns(c.eligible_turns)} reported`] : []
  if (c.reset_excluded > 0) parts.push(`${turns(c.reset_excluded)} reset, excluded`)
  return parts.length > 0 ? parts.join(' · ') : 'no turns'
}

/** The exact value with digit grouping, or "not reported". The digits themselves never change. */
export function formatValue(value: string | null, metric: string): string {
  if (value === null) return 'not reported'
  const [whole, fraction] = value.split('.')
  const grouped = whole.replace(/\B(?=(\d{3})+(?!\d))/g, ',')
  const text = fraction === undefined ? grouped : `${grouped}.${fraction}`
  return metric === 'cost_usd' ? `$${text}` : text
}

/** The label of a group's value: a subtotal unless every eligible turn reported. */
export function valueLabel(c: Omit<UsageBucket, 'start'>, metric: string): string {
  const state = coverageState(c)
  if (state === 'reset_only') return 'reset turns only'
  if (state === 'not_reported') return 'not reported'
  const text = formatValue(c.reported_value, metric)
  return state === 'partial' ? `${text} (subtotal)` : text
}

export interface ChartPoint {
  index: number
  start: string
  value: number | null
  raw: string | null
  bucket: UsageBucket | null
}

export interface ChartSeries {
  slots: number
  points: ChartPoint[]
  /** Runs of consecutive known points; an unknown or empty hour breaks the line. */
  segments: ChartPoint[][]
  max: number
  maxRaw: string | null
}

/** One point per UTC hour of the range, known or not. Unknown and empty hours are gaps, never zero. */
export function chartSeries(group: UsageGroup, from: string, to: string): ChartSeries {
  const first = Math.floor(Date.parse(from) / HOUR_MS) * HOUR_MS
  const end = Date.parse(to)
  const byStart = new Map(group.buckets.map(b => [Date.parse(b.start), b]))
  const points: ChartPoint[] = []
  for (let t = first, i = 0; t < end && i < 169; t += HOUR_MS, i++) {
    const bucket = byStart.get(t) ?? null
    const raw = bucket?.reported_value ?? null
    points.push({ index: i, start: new Date(t).toISOString(), value: raw === null ? null : Number(raw), raw, bucket })
  }

  const segments: ChartPoint[][] = []
  let run: ChartPoint[] = []
  for (const p of points) {
    if (p.value === null) {
      if (run.length) segments.push(run)
      run = []
    } else run.push(p)
  }
  if (run.length) segments.push(run)

  let max = 0
  let maxRaw: string | null = null
  for (const p of points) {
    if (p.value !== null && p.value >= max) { max = p.value; maxRaw = p.raw }
  }
  return { slots: points.length, points, segments, max, maxRaw }
}

/** The identity of a group, for selection that survives a refresh. */
export const groupKey = (g: Pick<UsageGroup, 'agent' | 'provider' | 'model'>) => `${g.agent}\u0000${g.provider}\u0000${g.model}`
