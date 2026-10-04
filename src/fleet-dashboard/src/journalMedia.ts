import { formatBytes } from './sizeMeter.ts'

// Media row of the Comms journal panel (#414). Reads the `media` block Comms already serves in
// `GET /journal/v1/status`; the block is absent when the install has no bucket. Kept free of JSX
// and `import.meta.env` so `node --test` can run it. Never throws for any JSON value: a render-time
// throw would unmount the whole Agents view.

export interface JournalMediaStatus {
  state: string
  objects: number
  bytesStored: number
  lastSweepAt?: string | null
  deletedSinceStart: { abandoned: number; retired: number; orphans: number }
  sweepFailures: number
  upload: { windowSeconds: number; samples: number; p50Ms?: number | null; p95Ms?: number | null }
}

export interface MediaStatItem {
  key: string
  text: string
  title?: string
  tone?: 'warn'
}

const UNITS = ['KiB', 'MiB', 'GiB', 'TiB']

/** `512 B`, `3.4 MiB`. IEC units, one decimal; the unit is picked after rounding, stopping at TiB. */
export function formatStorageBytes(n: number): string {
  if (n < 1024) return `${n} B`
  let value = n / 1024
  let unit = 0
  while (unit < UNITS.length - 1 && Number(value.toFixed(1)) >= 1024) {
    value /= 1024
    unit++
  }
  return `${value.toFixed(1)} ${UNITS[unit]}`
}

/** A finite, non-negative number. Anything else renders `n/a`, never a made-up `0`. */
function isCount(v: unknown): v is number {
  return typeof v === 'number' && Number.isFinite(v) && v >= 0
}

function isObject(v: unknown): v is Record<string, unknown> {
  return typeof v === 'object' && v !== null && !Array.isArray(v)
}

function field(v: unknown, key: string): unknown {
  return isObject(v) ? v[key] : undefined
}

/** The five media items in display order, or `[]` when `media` is absent, `null` or not an object. */
export function mediaStatItems(media: unknown): MediaStatItem[] {
  if (!isObject(media)) return []

  const state = media.state
  const stateItem: MediaStatItem = typeof state === 'string' && state.length > 0
    ? { key: 'state', text: `media ${state}` }
    : { key: 'state', text: 'media n/a' }
  if (state === 'degraded') stateItem.tone = 'warn'

  const bytes = media.bytesStored
  const bytesItem: MediaStatItem = isCount(bytes)
    ? { key: 'bytesStored', text: `stored ${formatStorageBytes(bytes)}`, title: `${formatBytes(bytes)} bytes` }
    : { key: 'bytesStored', text: 'stored n/a' }

  const orphans = field(media.deletedSinceStart, 'orphans')
  const sweepFailures = media.sweepFailures
  const samples = field(media.upload, 'samples')
  const p95 = field(media.upload, 'p95Ms')

  return [
    stateItem,
    bytesItem,
    { key: 'orphans', text: `orphans deleted ${isCount(orphans) ? orphans : 'n/a'}` },
    { key: 'sweepFailures', text: `sweep failures ${isCount(sweepFailures) ? sweepFailures : 'n/a'}` },
    { key: 'uploadP95', text: `upload p95 ${samples !== 0 && isCount(p95) ? `${Math.round(p95)} ms` : 'n/a'}` },
  ]
}
