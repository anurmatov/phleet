/**
 * Memory page — split-pane tree explorer for the fleet-memory corpus.
 *
 * V1 corpus bound: 5000 memories total. Beyond 5000, revisit lazy-loading.
 *
 * Layout: left pane = search box and filters, then the collapsible tree (project → type → memory)
 *         or, with a query, the search results; read-stats footer.
 *         right pane = MemoryContentView for the selected memory.
 *
 * Search: results come in two groups, the ones that matched the words (id, title, tag, keyword)
 *         and the ones only related by meaning, and each says how it matched. The filters narrow
 *         the search (sent as parameters) and, with no query, the tree. memorySearch.ts holds the
 *         rules; an older fleet-memory that does not say how a hit matched gets one plain list.
 *
 * M2: project groups collapsed by default; state persisted to localStorage.
 *     When selectedId changes, auto-expands the target project/type and scrolls into view (S5).
 */
import { useState, useEffect, useCallback, useMemo, useRef } from 'react'
import type { MemoryListItem, MemorySearchResult, MemoryStatsResponse } from '../types'
import { apiGet } from '../utils'
import { useMemoryIdCache } from '../context/MemoryIdCacheContext'
import MemoryContentView from './MemoryContentView'
import Icon from './Icon'
import {
  NO_FILTERS, filterOptions, groupHits, hasFilters, matchDetail, matchKind, matchLabel, narrowList,
  searchNotice, searchUrl, singleIdHit, snippetRuns, withChosen,
} from '../memorySearch'
import type { MemoryFilters } from '../memorySearch'

const UNASSIGNED = '(unassigned)'
// Key format matches spec: memory-tree-collapsed-{project} → "true" (expanded) | "false" (collapsed)
const LS_PREFIX = 'memory-tree-collapsed-'

function groupTree(items: MemoryListItem[]): Map<string, Map<string, MemoryListItem[]>> {
  const tree = new Map<string, Map<string, MemoryListItem[]>>()
  for (const item of items) {
    const project = item.project || UNASSIGNED
    const type = item.type || UNASSIGNED
    if (!tree.has(project)) tree.set(project, new Map())
    const byType = tree.get(project)!
    if (!byType.has(type)) byType.set(type, [])
    byType.get(type)!.push(item)
  }
  for (const byType of tree.values()) {
    for (const list of byType.values()) {
      list.sort((a, b) => (b.updated_at ?? '').localeCompare(a.updated_at ?? ''))
    }
  }
  return tree
}

function shortDate(iso: string): string {
  if (!iso) return ''
  try { return new Date(iso).toLocaleDateString() } catch { return '' }
}

function lsGetExpanded(project: string): boolean {
  try { return localStorage.getItem(LS_PREFIX + project) === 'true' } catch { return false }
}

function lsSetExpanded(project: string, expanded: boolean) {
  try { localStorage.setItem(LS_PREFIX + project, String(expanded)) } catch { /* noop */ }
}

const FILTERS: { key: keyof MemoryFilters; label: string; any: string; options: 'projects' | 'types' | 'tags' }[] = [
  { key: 'project', label: 'Project', any: 'All projects', options: 'projects' },
  { key: 'type', label: 'Type', any: 'All types', options: 'types' },
  { key: 'tag', label: 'Tag', any: 'All tags', options: 'tags' },
]

/** One search hit: its title, how it matched, where it lives, and its snippet with the matched words marked. */
function SearchHit({ hit, query, selected, onOpen }: {
  hit: MemorySearchResult; query: string; selected: boolean; onOpen: (id: string) => void
}) {
  const kind = matchKind(hit)
  const detail = matchDetail(hit, query)
  const where = [hit.project, hit.type].filter(Boolean).join(' · ')
  return (
    <button
      className={`memory-tree-item memory-result-item${selected ? ' memory-selected' : ''}`}
      onClick={() => onOpen(hit.id)}
    >
      <div className="memory-result-head">
        <div className="memory-result-title">{hit.title}</div>
        {kind && (
          <span
            className={`pill memory-result-match memory-result-match--${kind}`}
            title={kind === 'meaning' && typeof hit.score === 'number' ? `Similarity ${hit.score.toFixed(2)}` : undefined}
          >
            {matchLabel(kind)}
          </span>
        )}
      </div>
      <div className="memory-result-meta">
        {where}
        {detail && <>{where && ' · '}<span className="memory-result-detail">{detail}</span></>}
      </div>
      <div className="memory-result-snippet">
        {snippetRuns(hit.snippet ?? '', hit.highlights).map((run, i) =>
          run.marked ? <mark key={i}>{run.text}</mark> : <span key={i}>{run.text}</span>)}
      </div>
    </button>
  )
}

export default function MemoryView() {
  const [items, setItems] = useState<MemoryListItem[]>([])
  const [loading, setLoading] = useState(true)
  const [loadError, setLoadError] = useState('')
  const [selectedId, setSelectedId] = useState<string | null>(null)
  const [filter, setFilter] = useState('')
  const [filters, setFilters] = useState<MemoryFilters>(NO_FILTERS)
  const [searchResults, setSearchResults] = useState<MemorySearchResult[] | null>(null)
  // The request `searchResults` answers. Enter acts on an answer only when it is for what is typed now.
  const [answeredUrl, setAnsweredUrl] = useState('')
  const [searchLoading, setSearchLoading] = useState(false)
  const [searchError, setSearchError] = useState('')
  // M2: expandedProjects — present = expanded, absent = collapsed (default collapsed)
  const [expandedProjects, setExpandedProjects] = useState<Set<string>>(new Set())
  // collapsedTypes — present = collapsed, absent = expanded (default expanded within open project)
  const [collapsedTypes, setCollapsedTypes] = useState<Set<string>>(new Set())
  const [stats, setStats] = useState<MemoryStatsResponse | null>(null)
  const [statsLoading, setStatsLoading] = useState(false)
  const [statsError, setStatsError] = useState('')
  const [statsSince, setStatsSince] = useState('')
  const [statsCollapsed, setStatsCollapsed] = useState(false)
  const [deleteToast, setDeleteToast] = useState('')
  const filterDebounce = useRef<ReturnType<typeof setTimeout> | null>(null)
  const toastTimer = useRef<ReturnType<typeof setTimeout> | null>(null)
  // The request most recently sent: an answer to an earlier one is dropped, never drawn.
  const latestSearch = useRef('')
  // Set by Enter pressed before the answer is in: that request's one id hit opens when it arrives.
  const openOnAnswer = useRef('')
  const { refresh: refreshIdCache } = useMemoryIdCache()

  const loadList = useCallback(async () => {
    setLoading(true)
    setLoadError('')
    try {
      const data = await apiGet<MemoryListItem[]>('/api/memory')
      setItems(data)
      // M2: restore per-project collapse state from localStorage; default is collapsed
      setExpandedProjects(() => {
        const restored = new Set<string>()
        for (const item of data) {
          const p = item.project || UNASSIGNED
          if (lsGetExpanded(p)) restored.add(p)
        }
        return restored
      })
    } catch (e) {
      setLoadError(e instanceof Error ? e.message : String(e))
    } finally {
      setLoading(false)
    }
  }, [])

  const loadStats = useCallback(async () => {
    setStatsError('')
    setStatsLoading(true)
    try {
      const data = await apiGet<MemoryStatsResponse>('/api/memory/stats/reads')
      setStats(data)
      setStatsSince(data.since)
    } catch (e) {
      setStatsError(e instanceof Error ? e.message : String(e))
    } finally {
      setStatsLoading(false)
    }
  }, [])

  useEffect(() => {
    loadList()
    loadStats()
    const interval = setInterval(loadStats, 30_000)
    return () => clearInterval(interval)
  }, [loadList, loadStats])

  // Extracted so the retry button can call it directly (setFilter(f=>f) is a no-op in React)
  const runSearch = useCallback(async (url: string) => {
    latestSearch.current = url
    setSearchLoading(true)
    setSearchError('')
    try {
      const data = await apiGet<MemorySearchResult[]>(url)
      if (latestSearch.current !== url) return
      const hits = Array.isArray(data) ? data : []
      setSearchResults(hits)
      setAnsweredUrl(url)
      if (openOnAnswer.current === url) {
        const id = singleIdHit(hits)
        if (id) setSelectedId(id)
      }
    } catch (e) {
      if (latestSearch.current !== url) return
      setSearchError(e instanceof Error ? e.message : String(e))
      setSearchResults(null)
      setAnsweredUrl('')
    } finally {
      if (latestSearch.current === url) {
        openOnAnswer.current = ''
        setSearchLoading(false)
      }
    }
  }, [])

  const query = filter.trim()
  const currentUrl = query ? searchUrl(query, filters) : ''

  // Debounced search: the query and the filters that are set.
  useEffect(() => {
    if (filterDebounce.current) clearTimeout(filterDebounce.current)
    // Invalidate the old answer now, not when the next debounced request starts.
    latestSearch.current = ''
    openOnAnswer.current = ''
    if (!currentUrl) {
      setSearchResults(null)
      setAnsweredUrl('')
      setSearchLoading(false)
      setSearchError('')
      return
    }
    filterDebounce.current = setTimeout(() => runSearch(currentUrl), 250)
    return () => { if (filterDebounce.current) clearTimeout(filterDebounce.current) }
  }, [currentUrl, runSearch])

  // M2 + S5: auto-expand project/type containing selected memory, then scroll it into view.
  // Double rAF ensures the element is in the DOM after state update + paint before scrolling.
  useEffect(() => {
    if (!selectedId || !items.length) return
    const mem = items.find(i => i.id === selectedId)
    if (!mem) return
    const project = mem.project || UNASSIGNED
    const typeKey = `${project}:${mem.type || UNASSIGNED}`
    setExpandedProjects(prev => {
      if (prev.has(project)) return prev
      lsSetExpanded(project, true)
      return new Set([...prev, project])
    })
    setCollapsedTypes(prev => {
      if (!prev.has(typeKey)) return prev
      const next = new Set(prev)
      next.delete(typeKey)
      return next
    })
    // Double rAF: first frame commits state, second frame element is in DOM
    let raf1 = 0, raf2 = 0
    raf1 = requestAnimationFrame(() => {
      raf2 = requestAnimationFrame(() => {
        const el = document.querySelector<HTMLElement>(`[data-memid="${selectedId}"]`)
        el?.scrollIntoView({ behavior: 'smooth', block: 'nearest' })
      })
    })
    return () => { cancelAnimationFrame(raf1); cancelAnimationFrame(raf2) }
  }, [selectedId, items])

  function toggleProject(project: string) {
    setExpandedProjects(prev => {
      const next = new Set(prev)
      const nowExpanded = !next.has(project)
      nowExpanded ? next.add(project) : next.delete(project)
      lsSetExpanded(project, nowExpanded)
      return next
    })
  }

  function toggleType(key: string) {
    setCollapsedTypes(prev => {
      const next = new Set(prev)
      next.has(key) ? next.delete(key) : next.add(key)
      return next
    })
  }

  function clearFilter() {
    setFilter('')
    setSearchResults(null)
  }

  // Enter on a query that is a memory's id opens that memory: the answer's one id hit. Pressed
  // before the answer is in, it asks at once and opens the hit when it arrives.
  function handleSearchKeyDown(e: React.KeyboardEvent<HTMLInputElement>) {
    if (e.key !== 'Enter' || !currentUrl) return
    e.preventDefault()
    if (answeredUrl === currentUrl && searchResults) {
      const id = singleIdHit(searchResults)
      if (id) setSelectedId(id)
      return
    }
    if (filterDebounce.current) clearTimeout(filterDebounce.current)
    openOnAnswer.current = currentUrl
    runSearch(currentUrl)
  }

  // S4: keyboard navigation within the tree — Arrow keys move focus, Escape clears filter
  function handleTreeKeyDown(e: React.KeyboardEvent<HTMLButtonElement>, memId: string) {
    if (e.key === 'Escape') {
      clearFilter()
      return
    }
    if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
      e.preventDefault()
      const all = [...document.querySelectorAll<HTMLButtonElement>('[data-memid]')]
      const idx = all.findIndex(el => el.dataset.memid === memId)
      const next = e.key === 'ArrowDown' ? all[idx + 1] : all[idx - 1]
      next?.focus()
    }
  }

  function handleDeleted(id: string) {
    setItems(prev => prev.filter(i => i.id !== id))
    setSelectedId(null)
    refreshIdCache()
    loadStats()
    if (toastTimer.current) clearTimeout(toastTimer.current)
    setDeleteToast('Memory deleted.')
    toastTimer.current = setTimeout(() => setDeleteToast(''), 3000)
  }

  function handleSaved() {
    loadList()
  }

  const options = useMemo(() => filterOptions(items), [items])
  const filtered = hasFilters(filters)
  // With no query the filters narrow the tree, so a filter always shows what it would search.
  const tree = groupTree(narrowList(items, filters))
  const groups = groupHits(searchResults ?? [])
  // Said only of the answer to what is typed now, never of the one still on screen from before.
  const notice = searchResults !== null && answeredUrl === currentUrl ? searchNotice(groups) : null
  const hit = (r: MemorySearchResult) =>
    <SearchHit key={r.id} hit={r} query={query} selected={selectedId === r.id} onOpen={setSelectedId} />
  const sortedProjects = [...tree.keys()].sort((a, b) =>
    a === UNASSIGNED ? 1 : b === UNASSIGNED ? -1 : a.localeCompare(b))

  return (
    <div className={`memory-page${selectedId ? ' has-selection' : ''}`}>
      {/* Left pane */}
      <div className="memory-left">
        {/* M4: Filter input with clear button, muted style */}
        <div className="memory-search-bar">
          <div className="memory-search-wrap">
            <input
              className="memory-search-input"
              type="search"
              aria-label="Search memories"
              placeholder="Search memories or paste an id…"
              value={filter}
              onChange={e => setFilter(e.target.value)}
              onKeyDown={handleSearchKeyDown}
            />
            {filter && (
              <button className="memory-search-clear" onClick={clearFilter} title="Clear search">×</button>
            )}
          </div>
          <div className="memory-filters" role="group" aria-label="Filters">
            {FILTERS.map(f => (
              <select
                key={f.key}
                className={`memory-filter${filters[f.key] ? ' memory-filter--set' : ''}`}
                aria-label={f.label}
                value={filters[f.key]}
                onChange={e => setFilters(prev => ({ ...prev, [f.key]: e.target.value }))}
              >
                <option value="">{f.any}</option>
                {withChosen(options[f.options], filters[f.key]).map(o => <option key={o} value={o}>{o}</option>)}
              </select>
            ))}
            {filtered && (
              <button className="btn btn-quiet btn-s memory-filters-clear" onClick={() => setFilters(NO_FILTERS)}>
                Clear filters
              </button>
            )}
          </div>
        </div>

        {/* Search results (when filter active) */}
        {query ? (
          <div className="memory-search-results">
            {searchLoading && <div className="memory-tree-placeholder">Searching…</div>}
            {searchError && (
              <div className="memory-tree-error">
                Search failed: {searchError}
                <button className="memory-retry-btn" onClick={() => runSearch(currentUrl)}>Retry</button>
              </div>
            )}
            {!searchLoading && notice === 'none' && (
              <div className="memory-tree-placeholder" role="status">
                Nothing found for “{query}”{filtered ? ' with these filters' : ''}.
                <button className="memory-retry-btn" onClick={clearFilter}>Clear</button>
              </div>
            )}
            {notice === 'related-only' && (
              <div className="memory-results-notice" role="status">
                Nothing matched the words “{query}”{filtered ? ' with these filters' : ''}.
                The memories below are related by meaning only.
              </div>
            )}
            {groups.matches.length > 0 && (
              <section className="memory-results-group" aria-labelledby="memory-results-matches">
                <h3 className="memory-results-heading" id="memory-results-matches">
                  Matches<span className="memory-tree-count">{groups.matches.length}</span>
                </h3>
                {groups.matches.map(hit)}
              </section>
            )}
            {groups.related.length > 0 && (
              <section className="memory-results-group" aria-labelledby="memory-results-related">
                <h3 className="memory-results-heading" id="memory-results-related">
                  Related by meaning<span className="memory-tree-count">{groups.related.length}</span>
                </h3>
                {groups.related.map(hit)}
              </section>
            )}
            {groups.ungrouped.map(hit)}
          </div>
        ) : (
          /* Tree */
          <div className="memory-tree">
            {loading && <div className="memory-tree-placeholder">Loading…</div>}
            {loadError && (
              <div className="memory-tree-error">
                Failed to load: {loadError}
                <button className="memory-retry-btn" onClick={loadList}>Retry</button>
              </div>
            )}
            {!loading && !loadError && items.length === 0 && (
              <div className="memory-tree-placeholder">No memories yet.</div>
            )}
            {!loading && !loadError && items.length > 0 && sortedProjects.length === 0 && (
              <div className="memory-tree-placeholder" role="status">No memories match these filters.</div>
            )}
            {sortedProjects.map(project => {
              const byType = tree.get(project)!
              const projExpanded = expandedProjects.has(project)
              const sortedTypes = [...byType.keys()].sort((a, b) =>
                a === UNASSIGNED ? 1 : b === UNASSIGNED ? -1 : a.localeCompare(b))
              return (
                <div key={project} className="memory-tree-project">
                  <button className="memory-tree-project-btn" onClick={() => toggleProject(project)}>
                    <span className={`memory-tree-arrow${projExpanded ? ' open' : ''}`}><Icon name="chevronRight" size={14} /></span>
                    <span className="memory-tree-project-name">{project}</span>
                    <span className="memory-tree-count">
                      {[...byType.values()].reduce((s, a) => s + a.length, 0)}
                    </span>
                  </button>
                  {projExpanded && sortedTypes.length === 0 && (
                    <div className="memory-tree-placeholder memory-tree-placeholder--indent">No memories.</div>
                  )}
                  {projExpanded && sortedTypes.map(type => {
                    const typeKey = `${project}:${type}`
                    const typeExpanded = !collapsedTypes.has(typeKey) // expanded by default
                    const mems = byType.get(type)!
                    return (
                      <div key={type} className="memory-tree-type">
                        <button className="memory-tree-type-btn" onClick={() => toggleType(typeKey)}>
                          <span className={`memory-tree-arrow${typeExpanded ? ' open' : ''}`}><Icon name="chevronRight" size={14} /></span>
                          <span className="memory-tree-type-name">{type}</span>
                          <span className="memory-tree-count">{mems.length}</span>
                        </button>
                        {typeExpanded && mems.map(mem => (
                          <button
                            key={mem.id}
                            data-memid={mem.id}
                            className={`memory-tree-mem${selectedId === mem.id ? ' memory-selected' : ''}`}
                            onClick={() => setSelectedId(mem.id)}
                            onKeyDown={e => handleTreeKeyDown(e, mem.id)}
                            title={mem.title}
                          >
                            <span className="memory-tree-mem-title">{mem.title}</span>
                            <span className="memory-tree-mem-date">{shortDate(mem.updated_at)}</span>
                          </button>
                        ))}
                      </div>
                    )
                  })}
                </div>
              )
            })}
          </div>
        )}

        {/* M3: Collapsible read-stats footer pinned at bottom of left pane */}
        <div className={`memory-stats-panel${statsCollapsed ? ' memory-stats-collapsed' : ''}`}>
          <div className="memory-stats-header">
            <button className="memory-stats-toggle-btn" onClick={() => setStatsCollapsed(c => !c)}>
              <span className={`memory-stats-chevron${statsCollapsed ? '' : ' open'}`}><Icon name="chevronRight" size={14} /></span>
              <span className="memory-stats-title">
                Read stats{statsSince ? ` · ${new Date(statsSince).toLocaleDateString()}` : ''}
              </span>
            </button>
            <button className="memory-retry-btn" onClick={loadStats} title="Refresh stats">↺</button>
          </div>
          {!statsCollapsed && (
            <div className="memory-stats-body">
              {statsLoading && !stats && (
                <div className="memory-tree-placeholder">Loading stats…</div>
              )}
              {statsError && <div className="memory-stats-stale">Error: {statsError}</div>}
              {!statsLoading && stats && stats.entries.length === 0 && (
                <div className="memory-tree-placeholder">No reads recorded yet.</div>
              )}
              {stats && stats.entries.slice(0, 10).map(e => {
                const mem = items.find(i => i.id === e.memoryId)
                const top3 = Object.entries(e.byAgent)
                  .sort((a, b) => b[1].count - a[1].count)
                  .slice(0, 3)
                return (
                  <button
                    key={e.memoryId}
                    className={`memory-stats-row${selectedId === e.memoryId ? ' memory-selected' : ''}`}
                    onClick={() => setSelectedId(e.memoryId)}
                  >
                    <div className="memory-stats-row-title">{mem?.title ?? e.memoryId.substring(0, 8)}</div>
                    <div className="memory-stats-row-meta">
                      {e.total} read{e.total !== 1 ? 's' : ''}
                      {top3.length > 0 && (
                        <> · {top3.map(([a, s]) => `${a}(${s.count})`).join(', ')}</>
                      )}
                    </div>
                  </button>
                )
              })}
            </div>
          )}
        </div>
      </div>

      {/* Right pane */}
      <div className="memory-right">
        {/* A phone shows the list or the memory, never both: this is the way back to the list. */}
        {selectedId && (
          <button className="btn btn-quiet btn-s memory-back only-phone" onClick={() => setSelectedId(null)}>
            <Icon name="chevronLeft" size={16} />All memories
          </button>
        )}
        {deleteToast && <div className="memory-delete-toast">{deleteToast}</div>}
        {selectedId
          ? <MemoryContentView
              key={selectedId}
              id={selectedId}
              onDeleted={handleDeleted}
              onSaved={handleSaved}
            />
          : <div className="memory-cv-placeholder">
              Select a memory to view it.
            </div>
        }
      </div>
    </div>
  )
}
