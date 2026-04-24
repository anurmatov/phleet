/**
 * Memory page — split-pane tree explorer for the fleet-memory corpus.
 *
 * V1 corpus bound: 5000 memories total. Beyond 5000, revisit lazy-loading.
 *
 * Layout: left pane = collapsible tree (project → type → memory) + search box + read-stats table.
 *         right pane = MemoryContentView for the selected memory.
 */
import { useState, useEffect, useCallback, useRef } from 'react'
import type { MemoryListItem, MemorySearchResult, MemoryStatsResponse } from '../types'
import { apiFetch } from '../utils'
import { useMemoryIdCache } from '../context/MemoryIdCacheContext'
import MemoryContentView from './MemoryContentView'

// Tree grouping helpers
const UNASSIGNED = '(unassigned)'

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
  // Sort memories within each bucket by updated_at desc
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

export default function MemoryView() {
  const [items, setItems] = useState<MemoryListItem[]>([])
  const [loading, setLoading] = useState(true)
  const [loadError, setLoadError] = useState('')
  const [selectedId, setSelectedId] = useState<string | null>(null)
  const [query, setQuery] = useState('')
  const [searchResults, setSearchResults] = useState<MemorySearchResult[] | null>(null)
  const [searchLoading, setSearchLoading] = useState(false)
  const [searchError, setSearchError] = useState('')
  const [expandedProjects, setExpandedProjects] = useState<Set<string>>(new Set())
  const [expandedTypes, setExpandedTypes] = useState<Set<string>>(new Set())
  const [stats, setStats] = useState<MemoryStatsResponse | null>(null)
  const [statsError, setStatsError] = useState('')
  const [statsSince, setStatsSince] = useState('')
  const [deleteToast, setDeleteToast] = useState('')
  const searchDebounce = useRef<ReturnType<typeof setTimeout> | null>(null)
  const toastTimer = useRef<ReturnType<typeof setTimeout> | null>(null)
  const { refresh: refreshIdCache } = useMemoryIdCache()

  const loadList = useCallback(async () => {
    setLoading(true)
    setLoadError('')
    try {
      const resp = await apiFetch('/api/memory')
      if (!resp.ok) throw new Error(`HTTP ${resp.status}`)
      const data: MemoryListItem[] = await resp.json()
      setItems(data)
      // Auto-expand all projects on first load
      const projects = new Set(data.map(d => d.project || UNASSIGNED))
      setExpandedProjects(projects)
    } catch (e) {
      setLoadError(e instanceof Error ? e.message : String(e))
    } finally {
      setLoading(false)
    }
  }, [])

  const loadStats = useCallback(async () => {
    setStatsError('')
    try {
      const resp = await apiFetch('/api/memory/stats/reads')
      if (!resp.ok) throw new Error(`HTTP ${resp.status}`)
      const data: MemoryStatsResponse = await resp.json()
      setStats(data)
      setStatsSince(data.since)
    } catch (e) {
      setStatsError(e instanceof Error ? e.message : String(e))
    }
  }, [])

  useEffect(() => {
    loadList()
    loadStats()
    const interval = setInterval(loadStats, 30_000)
    return () => clearInterval(interval)
  }, [loadList, loadStats])

  // Debounced search
  useEffect(() => {
    if (searchDebounce.current) clearTimeout(searchDebounce.current)
    if (!query.trim()) { setSearchResults(null); return }
    searchDebounce.current = setTimeout(async () => {
      setSearchLoading(true)
      setSearchError('')
      try {
        const resp = await apiFetch(`/api/memory/search?q=${encodeURIComponent(query)}`)
        if (!resp.ok) throw new Error(`HTTP ${resp.status}`)
        const data: MemorySearchResult[] = await resp.json()
        setSearchResults(data)
      } catch (e) {
        setSearchError(e instanceof Error ? e.message : String(e))
        setSearchResults(null)
      } finally {
        setSearchLoading(false)
      }
    }, 250)
  }, [query])

  function toggleProject(project: string) {
    setExpandedProjects(prev => {
      const next = new Set(prev)
      next.has(project) ? next.delete(project) : next.add(project)
      return next
    })
  }

  function toggleType(key: string) {
    setExpandedTypes(prev => {
      const next = new Set(prev)
      next.has(key) ? next.delete(key) : next.add(key)
      return next
    })
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

  const tree = groupTree(items)
  const sortedProjects = [...tree.keys()].sort((a, b) =>
    a === UNASSIGNED ? 1 : b === UNASSIGNED ? -1 : a.localeCompare(b))

  return (
    <div className="memory-page">
      {/* Left pane */}
      <div className="memory-left">
        {/* Search */}
        <div className="memory-search-bar">
          <input
            className="memory-search-input"
            placeholder="Search memories…"
            value={query}
            onChange={e => setQuery(e.target.value)}
          />
        </div>

        {/* Search results */}
        {query.trim() && (
          <div className="memory-search-results">
            {searchLoading && <div className="memory-tree-placeholder">Searching…</div>}
            {searchError && (
              <div className="memory-tree-error">
                Search failed: {searchError}
                <button className="memory-retry-btn" onClick={() => setQuery(q => q)}>Retry</button>
              </div>
            )}
            {!searchLoading && searchResults !== null && searchResults.length === 0 && (
              <div className="memory-tree-placeholder">No results for "{query}"</div>
            )}
            {searchResults?.map(r => (
              <button
                key={r.id}
                className={`memory-tree-item memory-result-item${selectedId === r.id ? ' memory-selected' : ''}`}
                onClick={() => setSelectedId(r.id)}
              >
                <div className="memory-result-title">{r.title}</div>
                <div className="memory-result-meta">{r.project} · {r.type}</div>
                <div className="memory-result-snippet">{r.snippet}</div>
              </button>
            ))}
          </div>
        )}

        {/* Tree */}
        {!query.trim() && (
          <div className="memory-tree">
            {loading && <div className="memory-tree-placeholder">Loading…</div>}
            {loadError && (
              <div className="memory-tree-error">
                Failed to load memories: {loadError}
                <button className="memory-retry-btn" onClick={loadList}>Retry</button>
              </div>
            )}
            {!loading && !loadError && items.length === 0 && (
              <div className="memory-tree-placeholder">No memories yet.</div>
            )}
            {sortedProjects.map(project => {
              const byType = tree.get(project)!
              const projExpanded = expandedProjects.has(project)
              const sortedTypes = [...byType.keys()].sort((a, b) =>
                a === UNASSIGNED ? 1 : b === UNASSIGNED ? -1 : a.localeCompare(b))
              return (
                <div key={project} className="memory-tree-project">
                  <button className="memory-tree-project-btn" onClick={() => toggleProject(project)}>
                    <span className="memory-tree-arrow">{projExpanded ? '▾' : '▸'}</span>
                    <span className="memory-tree-project-name">{project}</span>
                    <span className="memory-tree-count">
                      {[...byType.values()].reduce((s, a) => s + a.length, 0)}
                    </span>
                  </button>
                  {projExpanded && sortedTypes.map(type => {
                    const typeKey = `${project}:${type}`
                    const typeExpanded = !expandedTypes.has(typeKey) // expanded by default
                    const mems = byType.get(type)!
                    return (
                      <div key={type} className="memory-tree-type">
                        <button className="memory-tree-type-btn" onClick={() => toggleType(typeKey)}>
                          <span className="memory-tree-arrow">{typeExpanded ? '▾' : '▸'}</span>
                          <span className="memory-tree-type-name">{type}</span>
                          <span className="memory-tree-count">{mems.length}</span>
                        </button>
                        {typeExpanded && mems.map(mem => (
                          <button
                            key={mem.id}
                            className={`memory-tree-mem${selectedId === mem.id ? ' memory-selected' : ''}`}
                            onClick={() => setSelectedId(mem.id)}
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

        {/* Read stats panel */}
        <div className="memory-stats-panel">
          <div className="memory-stats-header">
            <span className="memory-stats-title">
              Most read{statsSince ? ` since ${new Date(statsSince).toLocaleDateString()}` : ''}
            </span>
            <button className="memory-retry-btn" onClick={loadStats}>↺</button>
          </div>
          {statsError && <div className="memory-stats-stale">Retrying… {statsError}</div>}
          {stats && stats.entries.length === 0 && (
            <div className="memory-tree-placeholder">No reads since container start.</div>
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
      </div>

      {/* Right pane */}
      <div className="memory-right">
        {deleteToast && <div className="memory-delete-toast">{deleteToast}</div>}
        {selectedId
          ? <MemoryContentView
              key={selectedId}
              id={selectedId}
              onDeleted={handleDeleted}
              onSaved={handleSaved}
            />
          : <div className="memory-cv-placeholder">
              Select a memory from the tree, or search above.
            </div>
        }
      </div>
    </div>
  )
}
