import { useEffect, useId, useState } from 'react'
import type { ConfigEdits, McpEndpointEntry } from '../types'
import { parseEnvRefs } from '../localModel'
import { addHeaderRow, danglingMappings, envRefChoices, removeHeaderRow, updateHeaderRow } from '../mcpHeaders'
import {
  answerHeadline, buildToolView, firstLine, matchesFilter, mcpToolId, readToolDiscovery, setToolsGranted,
  type ServerTools, type ToolDiscovery, type ToolRow,
} from '../mcpTools'
import { apiFetch } from '../utils'
import FieldHint from './FieldHint'

/** Where asking the orchestrator for the servers' tools stands. */
type Asking = 'idle' | 'asking' | 'answered' | 'failed' | 'unavailable'

interface ToolsEditorProps {
  agentName: string
  /** Whether this panel is the one showing. Nothing is asked of any server until it is. */
  active: boolean
  /** Changes after each save: the saved endpoints are asked again. */
  refreshKey: number
  configEdits: ConfigEdits
  /** The names of the endpoints as saved: only those can have been asked. */
  savedEndpointNames: string[]
  /** Whether this agent's provider sends headers to an MCP endpoint (Claude and Codex). */
  headersSupported: boolean
  mcpHeaderError: string | null
  onEditsChange: (patch: Partial<ConfigEdits>) => void
}

/** One group's tools: a box to grant or revoke, the tool's name, and what the server says it does. */
function ToolTable({ rows, label, flagUnlisted = false, onToggle }: {
  rows: ToolRow[]
  label: string
  /** Whether a granted tool its server did not list is marked. Only where the server answered. */
  flagUnlisted?: boolean
  onToggle: (row: ToolRow) => void
}) {
  const id = useId()
  // Tools typed by name have nothing to say about themselves: a "Description" heading over an
  // empty column would only promise something.
  const described = rows.some(row => row.description !== null || (flagUnlisted && !row.listed))
  return (
    // The roles are spelled out because a phone lays the rows out as a grid, and a browser stops
    // treating a table as one once its rows are no longer displayed as table rows.
    <table className="tool-table" role="table" aria-label={label}>
      <thead role="rowgroup" hidden={!described}>
        <tr role="row">
          <th role="columnheader" className="tool-col-check" aria-label="Granted" />
          <th role="columnheader" className="tool-col-name">Tool</th>
          <th role="columnheader">Description</th>
        </tr>
      </thead>
      <tbody role="rowgroup">
        {rows.map((row, i) => (
          <tr key={row.id} role="row" className={row.granted ? 'tool-row tool-row--granted' : 'tool-row'}>
            <td role="cell" className="tool-col-check">
              <input type="checkbox" id={`${id}-${i}`} checked={row.granted} aria-label={row.id} onChange={() => onToggle(row)} />
            </td>
            <td role="cell" className="tool-name"><label htmlFor={`${id}-${i}`}>{row.label}</label></td>
            <td role="cell" className="tool-desc" title={row.description ?? undefined}>
              {firstLine(row.description)}
              {flagUnlisted && !row.listed && <span className="tool-note">not listed by its server</span>}
            </td>
          </tr>
        ))}
      </tbody>
    </table>
  )
}

/** A field and a button that grant a tool by its name, where there is no list to pick it from. */
function AddByName({ label, placeholder, onAdd }: { label: string; placeholder: string; onAdd: (name: string) => void }) {
  const [name, setName] = useState('')
  const add = () => {
    const typed = name.trim()
    // The list is comma-separated, so a name cannot hold one.
    if (typed === '' || typed.includes(',')) return
    onAdd(typed)
    setName('')
  }
  return (
    <div className="tool-add">
      <input
        className="config-input tool-add-input" value={name} placeholder={placeholder} aria-label={label}
        spellCheck={false} autoCapitalize="off" autoCorrect="off"
        onChange={e => setName(e.target.value)}
        onKeyDown={e => { if (e.key === 'Enter') { e.preventDefault(); add() } }}
      />
      <button type="button" className="config-add-btn tool-quiet-btn" onClick={add} disabled={name.trim() === ''}>Add</button>
    </div>
  )
}

/** The tools of one server, under its endpoint: its list when it answered, why not when it did not. */
function ServerToolList({ group, saved, asking, filter, onGrant }: {
  group: ServerTools
  saved: boolean
  asking: Asking
  filter: string
  onGrant: (ids: string[], granted: boolean) => void
}) {
  const shown = group.rows.filter(row => matchesFilter(row, filter))
  const offered = group.rows.filter(row => row.listed).length
  const waiting = saved && !group.answer && (asking === 'asking' || asking === 'idle')
  const headline = waiting ? 'Asking this server for its tools…' : answerHeadline(group.answer, saved)
  const listed = group.answer?.status === 'ok'
  return (
    <div className="tool-list">
      <div className="tool-list-head">
        <span className="tool-list-count">
          {listed ? `${group.grantedCount} of ${offered} granted` : `${group.grantedCount} granted`}
          {group.answer?.truncated && ' · the server offers more than are shown'}
        </span>
        {shown.length > 1 && (
          <span className="tool-list-actions">
            <button type="button" className="tool-link-btn" onClick={() => onGrant(shown.map(r => r.id), true)}>All</button>
            <button type="button" className="tool-link-btn" onClick={() => onGrant(shown.map(r => r.id), false)}>None</button>
          </span>
        )}
      </div>
      {headline && (
        <div className={`tool-list-notice${waiting ? '' : ' tool-list-notice--problem'}`} role="status">
          <strong>{headline}</strong>{group.answer?.reason && <> {group.answer.reason}</>}
        </div>
      )}
      {listed && group.rows.length === 0 && <div className="tool-list-notice">This server lists no tools.</div>}
      {shown.length > 0 && (
        <ToolTable rows={shown} label={`Tools of ${group.server}`} flagUnlisted={listed} onToggle={row => onGrant([row.id], !row.granted)} />
      )}
      {group.rows.length > 0 && shown.length === 0 && <div className="tool-list-notice">No tool here matches the filter.</div>}
      {!listed && !waiting && (
        <AddByName
          label={`Tool name to grant on ${group.server}`} placeholder="tool name"
          onAdd={name => onGrant([mcpToolId(group.server, name)], true)}
        />
      )}
    </div>
  )
}

/**
 * The Tools &amp; MCP panel: the agent's MCP servers, each with the tools it offers to tick, and the
 * tools that belong to no server. What is saved is unchanged: a list of tool names and the
 * endpoints. "Edit as text" shows that list as the comma-separated field it always was.
 */
export default function ToolsEditor({
  agentName, active, refreshKey, configEdits, savedEndpointNames, headersSupported, mcpHeaderError, onEditsChange,
}: ToolsEditorProps) {
  const [discovery, setDiscovery] = useState<ToolDiscovery | null>(null)
  const [asking, setAsking] = useState<Asking>('idle')
  const [reload, setReload] = useState(0)
  const [askedKey, setAskedKey] = useState<string | null>(null)
  const [filter, setFilter] = useState('')
  const [asText, setAsText] = useState(false)
  // Names unticked since the dialog opened: their rows stay, so a slip can be ticked back.
  const [revoked, setRevoked] = useState<string[]>([])

  const key = `${agentName}:${refreshKey}:${reload}`
  useEffect(() => {
    if (!active || askedKey === key) return
    let alive = true
    setAsking('asking')
    apiFetch(`/api/agents/${encodeURIComponent(agentName)}/mcp-tools`, { method: 'POST' })
      .then(async response => {
        if (!alive) return
        // An orchestrator from before this route has nothing at the address.
        if (response.status === 404 || response.status === 405) { setAsking('unavailable'); setAskedKey(key); return }
        const read = response.ok ? readToolDiscovery(await response.json().catch(() => null)) : null
        if (!alive) return
        if (read) setDiscovery(read)
        setAsking(read ? 'answered' : 'failed')
        setAskedKey(key)
      })
      .catch(() => { if (alive) { setAsking('failed'); setAskedKey(key) } })
    return () => { alive = false }
  }, [active, agentName, askedKey, key])

  const endpoints = configEdits.mcpEndpoints
  const envRefs = parseEnvRefs(configEdits.envRefs)
  const view = buildToolView(configEdits.tools, endpoints.map(e => e.mcpName.trim()), discovery, revoked)
  const grant = (ids: string[], granted: boolean) => {
    if (!granted) setRevoked(kept => [...kept, ...ids.filter(id => !kept.includes(id))])
    onEditsChange({ tools: setToolsGranted(configEdits.tools, ids, granted) })
  }
  const groupOf = (name: string) => view.servers.find(s => s.server === name.trim())
  const implicit = view.servers.filter(s => !endpoints.some(e => e.mcpName.trim() === s.server))
  const builtIn = view.builtIn.filter(row => matchesFilter(row, filter))
  const other = view.other.filter(row => matchesFilter(row, filter))

  return (
    <div className="tools-editor">
      <div className="config-section-label">Tools</div>
      <div className="tools-bar">
        <input
          type="search" className="config-input tools-filter" placeholder="Filter tools" aria-label="Filter tools"
          value={filter} onChange={e => setFilter(e.target.value)} disabled={asText} spellCheck={false}
        />
        <span className="tools-count">{view.grantedCount} granted</span>
        <button type="button" className="config-add-btn tool-quiet-btn" disabled={asking === 'asking'}
          title="Ask the saved servers for their tools again" onClick={() => setReload(n => n + 1)}>
          {asking === 'asking' ? 'Asking…' : 'Refresh'}
        </button>
        <button type="button" className="config-add-btn tool-quiet-btn" aria-pressed={asText} onClick={() => setAsText(v => !v)}>
          {asText ? 'Pick from lists' : 'Edit as text'}
        </button>
      </div>
      {asking === 'failed' && (
        <div className="tool-list-notice tool-list-notice--problem" role="status">
          <strong>The servers could not be asked for their tools.</strong> Granted tools are shown; grant another by its name, or press Refresh.
        </div>
      )}
      {asking === 'unavailable' && (
        <div className="tool-list-notice" role="status">
          This orchestrator does not list a server’s tools yet. Granted tools are shown; grant another by its name.
        </div>
      )}

      {asText ? (
        <div className="config-field">
          <FieldHint>Comma-separated tool names, exactly as they are saved (e.g. <code>WebFetch, mcp__fleet-memory__memory_search</code>).</FieldHint>
          <textarea
            aria-label="Tools" className="config-textarea config-textarea-mono" rows={8} spellCheck={false}
            autoCapitalize="off" autoCorrect="off" placeholder="comma-separated tool names"
            value={configEdits.tools} onChange={e => onEditsChange({ tools: e.target.value })}
          />
        </div>
      ) : (
        <div className="tool-group" role="region" aria-label="Built-in tools">
          <div className="tool-group-head">
            <span className="tool-group-title">Built-in</span>
            <span className="tool-group-sub">the provider’s own tools, such as <code>WebFetch</code> or <code>Bash(git *)</code></span>
          </div>
          {view.alwaysOn.length > 0 && (
            <div className="tool-always">
              <span className="tool-always-label">Always on</span>
              {view.alwaysOn.map(name => <code key={name} className="tool-chip">{name}</code>)}
            </div>
          )}
          {builtIn.length > 0 && (
            <ToolTable rows={builtIn} label="Built-in tools granted" onToggle={row => grant([row.id], !row.granted)} />
          )}
          <AddByName label="Built-in tool name to grant" placeholder="tool name" onAdd={name => grant([name], true)} />
        </div>
      )}

      <div className="config-section-label">MCP servers</div>
      <FieldHint>
        Each server lists its own tools; tick the ones this agent may call. A header carries the value of one of this
        agent&apos;s Env Refs, picked by name and never typed here (<code>http</code> endpoints, Claude and Codex).{' '}
        <strong>Changes take effect on reprovision.</strong>
      </FieldHint>

      {endpoints.map((ep, i) => {
        const setEndpoint = (next: McpEndpointEntry) => onEditsChange({ mcpEndpoints: endpoints.map((x, j) => j === i ? next : x) })
        const dangling = danglingMappings(ep, envRefs).filter(m => m.envRefName !== '')
        const endpointLabel = ep.mcpName || `endpoint ${i + 1}`
        const headers = ep.headerEnvRefs ?? []
        const canAddHeader = headersSupported && ep.transportType === 'http'
        const group = groupOf(ep.mcpName)
        // Two endpoints of one name share a list: it is shown under the first.
        const firstOfName = endpoints.findIndex(e => e.mcpName.trim() === ep.mcpName.trim()) === i
        return (
          <div key={i} className="tool-group mcp-server" role="region" aria-label={`MCP server ${endpointLabel}`}>
            <div className="mcp-server-head">
              <input
                className="config-input mcp-server-name" value={ep.mcpName} placeholder="name" aria-label="MCP endpoint name"
                spellCheck={false} autoCapitalize="off" autoCorrect="off"
                onChange={e => setEndpoint({ ...ep, mcpName: e.target.value })}
              />
              <input
                className="config-input config-input-url" value={ep.url} placeholder="https://…/mcp" aria-label={`URL of ${endpointLabel}`}
                spellCheck={false} autoCapitalize="off" autoCorrect="off"
                onChange={e => setEndpoint({ ...ep, url: e.target.value })}
              />
              <select
                className="config-input mcp-server-transport" value={ep.transportType} aria-label={`Transport of ${endpointLabel}`}
                title="http: streamable HTTP (preferred). sse: Server-Sent Events (legacy)."
                onChange={e => setEndpoint({ ...ep, transportType: e.target.value })}
              >
                <option value="http">http</option>
                <option value="sse">sse</option>
                {ep.transportType !== 'http' && ep.transportType !== 'sse' && <option value={ep.transportType}>{ep.transportType}</option>}
              </select>
              <button type="button" className="config-remove-btn" aria-label={`Remove ${endpointLabel}`}
                onClick={() => onEditsChange({ mcpEndpoints: endpoints.filter((_, j) => j !== i) })}>✕</button>
            </div>
            {(headers.length > 0 || canAddHeader) && (
              <div className="config-mcp-headers" role="group" aria-label={`Headers of ${endpointLabel}`}>
                {headers.map((h, k) => (
                  <div key={k} className="config-related-row config-mcp-header-row">
                    <input
                      className="config-input config-mcp-header-name" value={h.headerName} placeholder="X-Example-Key"
                      aria-label={`Header name ${k + 1} of ${endpointLabel}`} spellCheck={false} autoCapitalize="off" autoCorrect="off"
                      onChange={e => setEndpoint(updateHeaderRow(ep, k, { headerName: e.target.value }))}
                    />
                    <select
                      className="config-input config-mcp-header-ref" value={h.envRefName}
                      aria-label={`Env Ref of header ${k + 1} of ${endpointLabel}`}
                      onChange={e => setEndpoint(updateHeaderRow(ep, k, { envRefName: e.target.value }))}
                    >
                      <option value="">choose an Env Ref…</option>
                      {envRefChoices(envRefs, h.envRefName).map(c => <option key={c.value} value={c.value}>{c.label}</option>)}
                    </select>
                    <button type="button" className="config-remove-btn" aria-label={`Remove header ${k + 1} of ${endpointLabel}`}
                      onClick={() => setEndpoint(removeHeaderRow(ep, k))}>✕</button>
                  </div>
                ))}
                {dangling.length > 0 && (
                  <div className="config-feedback config-feedback-error">
                    {dangling.map(m => m.envRefName).join(', ')} {dangling.length === 1 ? 'is' : 'are'} not an Env Ref of this agent: pick another or remove the header. Reprovisioning refuses until then.
                  </div>
                )}
                <button type="button" className="tool-link-btn" onClick={() => setEndpoint(addHeaderRow(ep))}>Add header</button>
              </div>
            )}
            {!asText && group && firstOfName && (
              <ServerToolList
                group={group} saved={savedEndpointNames.includes(group.server)} asking={asking} filter={filter} onGrant={grant}
              />
            )}
          </div>
        )
      })}
      {!asText && implicit.map(group => (
        <div key={group.server} className="tool-group mcp-server" role="region" aria-label={`MCP server ${group.server}`}>
          <div className="tool-group-head">
            <span className="tool-group-title">{group.server}</span>
            <span className="tool-group-sub">given to every agent</span>
          </div>
          <ServerToolList group={group} saved asking={asking} filter={filter} onGrant={grant} />
        </div>
      ))}

      <button type="button" className="config-add-btn mcp-add-server" onClick={() => {
        onEditsChange({ mcpEndpoints: [...endpoints, { mcpName: '', url: '', transportType: 'http' }] })
      }}>+ add server</button>
      {mcpHeaderError && <div className="config-feedback config-feedback-error">{mcpHeaderError}</div>}

      {!asText && view.other.length > 0 && (discovery !== null || asking === 'failed' || asking === 'unavailable') && (
        <div className="tool-group" role="region" aria-label="Granted tools of no server">
          <div className="tool-group-head">
            <span className="tool-group-title">Other granted tools</span>
            <span className="tool-group-sub">their server is not one of this agent’s endpoints</span>
          </div>
          {other.length > 0
            ? <ToolTable rows={other} label="Granted tools of no server" onToggle={row => grant([row.id], !row.granted)} />
            : <div className="tool-list-notice">No tool here matches the filter.</div>}
        </div>
      )}
    </div>
  )
}
