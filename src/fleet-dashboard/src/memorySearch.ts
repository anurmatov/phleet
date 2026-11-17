// What the Memory page does with a search answer, apart from drawing it: which hits matched the
// words and which are only related, where a snippet is marked, what a search request carries and
// what the filters offer.
//
// Kept free of JSX and of the page's own imports so `node --test` can run it.

/** How the memory service says a hit was found. */
export type MemoryMatch = 'id' | 'title' | 'tag' | 'keyword' | 'meaning'

/** One hit of `GET /api/memory/search`. The last three members are absent on an older memory service. */
export interface MemorySearchHit {
  id: string
  title: string
  project: string
  type: string
  tags: string[]
  snippet: string
  updated_at: string
  match?: string
  /** `[start, length]` pairs into `snippet`, in UTF-16 units. */
  highlights?: unknown
  /** The similarity of a `meaning` hit; null for every other kind. */
  score?: number | null
}

const LABELS: Record<MemoryMatch, string> = {
  id: 'ID', title: 'Title', tag: 'Tag', keyword: 'Keyword', meaning: 'Meaning',
}

/** The kind a hit names, or null when it names none this page knows. */
export function matchKind(hit: { match?: unknown }): MemoryMatch | null {
  const value = hit.match
  return typeof value === 'string' && Object.prototype.hasOwnProperty.call(LABELS, value) ? value as MemoryMatch : null
}

/** The word shown on a hit for how it matched. */
export function matchLabel(kind: MemoryMatch): string {
  return LABELS[kind]
}

// ── What a search request carries ────────────────────────────────────────────────────────────────

/** The page's three filters. An empty string is "any". */
export interface MemoryFilters {
  project: string
  type: string
  tag: string
}

export const NO_FILTERS: MemoryFilters = { project: '', type: '', tag: '' }

export function hasFilters(filters: MemoryFilters): boolean {
  return filters.project !== '' || filters.type !== '' || filters.tag !== ''
}

/** The search request for a query and the filters that are set. A filter left on "any" is not sent. */
export function searchUrl(query: string, filters: MemoryFilters): string {
  const parts = [`q=${encodeURIComponent(query.trim())}`]
  for (const name of ['project', 'type', 'tag'] as const) {
    if (filters[name] !== '') parts.push(`${name}=${encodeURIComponent(filters[name])}`)
  }
  return `/api/memory/search?${parts.join('&')}`
}

// ── What the filters offer ───────────────────────────────────────────────────────────────────────

interface Listed { project: string; type: string; tags: string[] }

export interface FilterOptions {
  projects: string[]
  types: string[]
  tags: string[]
}

function distinct(values: Iterable<string | null | undefined>): string[] {
  const seen = new Set<string>()
  for (const value of values) if (typeof value === 'string' && value !== '') seen.add(value)
  return [...seen].sort((a, b) => a.localeCompare(b))
}

/** Every project, type and tag the loaded list holds, each once, in alphabetical order. */
export function filterOptions(items: readonly Listed[]): FilterOptions {
  return {
    projects: distinct(items.map(i => i.project)),
    types: distinct(items.map(i => i.type)),
    tags: distinct(items.flatMap(i => i.tags ?? [])),
  }
}

/** The options of one filter, with the chosen value kept in the list when the memories that had it are gone. */
export function withChosen(options: readonly string[], chosen: string): string[] {
  return chosen === '' || options.includes(chosen) ? [...options] : distinct([...options, chosen])
}

/** The loaded list as the filters leave it, for browsing with no query. The same exact match the search applies. */
export function narrowList<T extends Listed>(items: readonly T[], filters: MemoryFilters): T[] {
  if (!hasFilters(filters)) return [...items]
  return items.filter(i =>
    (filters.project === '' || i.project === filters.project)
    && (filters.type === '' || i.type === filters.type)
    && (filters.tag === '' || (i.tags ?? []).includes(filters.tag)))
}

// ── The two groups ───────────────────────────────────────────────────────────────────────────────

export interface GroupedHits<T> {
  /**
   * False when the answer does not say how its hits matched (an older memory service): there is
   * then one list, in the order it came, and nothing is claimed about any hit.
   */
  grouped: boolean
  /** Found by id, title, tag or keyword, in the order the service put them. */
  matches: T[]
  /** Found by meaning alone. */
  related: T[]
  /** The whole answer when `grouped` is false. */
  ungrouped: T[]
}

/**
 * Splits an answer into what matched the words and what is only related by meaning. The order
 * inside each group is the service's. An answer in which any hit names no known kind is not
 * split at all: a hit is never put under a heading it did not earn.
 */
export function groupHits<T extends { match?: unknown }>(hits: readonly T[]): GroupedHits<T> {
  if (!hits.every(h => matchKind(h) !== null)) return { grouped: false, matches: [], related: [], ungrouped: [...hits] }
  return {
    grouped: true,
    matches: hits.filter(h => matchKind(h) !== 'meaning'),
    related: hits.filter(h => matchKind(h) === 'meaning'),
    ungrouped: [],
  }
}

/**
 * What the page says above the hits: `none` when there is nothing to show, `related-only` when
 * nothing matched the words and what follows is related by meaning alone, null otherwise.
 */
export function searchNotice(groups: GroupedHits<unknown>): 'none' | 'related-only' | null {
  if (!groups.grouped) return groups.ungrouped.length === 0 ? 'none' : null
  if (groups.matches.length > 0) return null
  return groups.related.length === 0 ? 'none' : 'related-only'
}

/** The id to open when Enter is pressed: the answer's one `id` hit. Null when there is none or several. */
export function singleIdHit(hits: readonly { id: string; match?: unknown }[]): string | null {
  const byId = hits.filter(h => matchKind(h) === 'id')
  return byId.length === 1 ? byId[0].id : null
}

/**
 * The part of a hit that the query matched, when the hit itself shows it: the start of the id for
 * an `id` hit, the tag for a `tag` hit. Null for the kinds whose match is in the title or snippet.
 */
export function matchDetail(hit: { id: string; tags?: string[]; match?: unknown }, query: string): string | null {
  const kind = matchKind(hit)
  if (kind === 'id') return hit.id.slice(0, 8)
  if (kind === 'tag') {
    const wanted = query.trim().toLowerCase()
    const tag = (hit.tags ?? []).find(t => t.toLowerCase() === wanted)
    return tag === undefined ? null : `#${tag}`
  }
  return null
}

// ── Marked snippets ──────────────────────────────────────────────────────────────────────────────

/** A run of a snippet, marked or not. The runs of one snippet, joined, are the snippet. */
export interface SnippetRun {
  text: string
  marked: boolean
}

const isHighSurrogate = (code: number) => code >= 0xd800 && code <= 0xdbff
const isLowSurrogate = (code: number) => code >= 0xdc00 && code <= 0xdfff

/** True when `index` falls between the two halves of one character. */
function splitsPair(text: string, index: number): boolean {
  return index > 0 && index < text.length
    && isHighSurrogate(text.charCodeAt(index - 1)) && isLowSurrogate(text.charCodeAt(index))
}

/**
 * Cuts a snippet into marked and unmarked runs from `[start, length]` pairs. Anything that is not
 * such a pair is ignored; a range is cut to the snippet, widened to whole characters, and joined
 * with a range it overlaps or touches. With nothing usable the snippet is one unmarked run.
 */
export function snippetRuns(snippet: string, highlights: unknown): SnippetRun[] {
  if (snippet === '') return []
  const ranges: [number, number][] = []
  if (Array.isArray(highlights)) {
    for (const pair of highlights) {
      if (!Array.isArray(pair) || pair.length !== 2) continue
      const [start, length] = pair
      if (!Number.isInteger(start) || !Number.isInteger(length) || length <= 0) continue
      let from = Math.max(0, start)
      let to = Math.min(snippet.length, start + length)
      if (from >= to) continue
      if (splitsPair(snippet, from)) from -= 1
      if (splitsPair(snippet, to)) to += 1
      ranges.push([from, to])
    }
  }
  ranges.sort((a, b) => a[0] - b[0] || a[1] - b[1])

  const runs: SnippetRun[] = []
  let at = 0
  let open: [number, number] | null = null
  const close = () => {
    if (!open) return
    if (open[0] > at) runs.push({ text: snippet.slice(at, open[0]), marked: false })
    runs.push({ text: snippet.slice(open[0], open[1]), marked: true })
    at = open[1]
    open = null
  }
  for (const range of ranges) {
    if (open && range[0] <= open[1]) open[1] = Math.max(open[1], range[1])
    else { close(); open = [range[0], range[1]] }
  }
  close()
  if (at < snippet.length) runs.push({ text: snippet.slice(at), marked: false })
  return runs
}
