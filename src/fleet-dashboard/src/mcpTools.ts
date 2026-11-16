// Picking an agent's tools instead of typing them.
//
// The stored thing does not change: an agent's tools are a list of names, sent on save as they
// always were. This module reads what the orchestrator learned from the agent's MCP servers
// (`POST /api/agents/{name}/mcp-tools`: each server's tools, with descriptions) and lays the list
// out against it: which granted tool belongs to which server, which a server offers and is not
// granted, and which is granted and offered by nothing. A granted name is never dropped because a
// server did not list it; it stays, marked.
//
// Kept free of JSX so `node --test` can run it.

/** One tool as its server lists it: its own name, without the `mcp__server__` prefix. */
export interface DiscoveredTool {
  name: string
  description: string | null
}

/** What one MCP server answered, or why it was not asked. */
export interface DiscoveredServer {
  name: string
  /** `ok`, or why there is no list: `unreachable`, `timeout`, `refused`, `needs_agent_secret`, … */
  status: string
  reason: string | null
  tools: DiscoveredTool[]
  /** The server offers more tools than were sent. */
  truncated: boolean
}

export interface ToolDiscovery {
  /** Tools every agent has whether or not they are in its list. */
  alwaysOn: string[]
  servers: DiscoveredServer[]
}

function text(value: unknown): string | null {
  return typeof value === 'string' && value !== '' ? value : null
}

/** The orchestrator's answer, or null when it is not one. A tool or server it cannot read costs that one. */
export function readToolDiscovery(body: unknown): ToolDiscovery | null {
  if (!body || typeof body !== 'object') return null
  const fields = body as Record<string, unknown>
  if (!Array.isArray(fields.servers)) return null
  const servers: DiscoveredServer[] = []
  for (const raw of fields.servers) {
    if (!raw || typeof raw !== 'object') continue
    const server = raw as Record<string, unknown>
    const name = text(server.name)
    const status = text(server.status)
    if (!name || !status) continue
    const tools: DiscoveredTool[] = []
    for (const rawTool of Array.isArray(server.tools) ? server.tools : []) {
      if (!rawTool || typeof rawTool !== 'object') continue
      const tool = rawTool as Record<string, unknown>
      const toolName = text(tool.name)
      if (toolName) tools.push({ name: toolName, description: text(tool.description) })
    }
    servers.push({ name, status, reason: text(server.reason), tools, truncated: server.truncated === true })
  }
  const alwaysOn = Array.isArray(fields.alwaysOn) ? fields.alwaysOn.filter((t): t is string => typeof t === 'string' && t !== '') : []
  return { alwaysOn, servers }
}

// ── the list itself ─────────────────────────────────────────────────────────

/** The names in the comma-separated field, in order, each once. */
export function parseToolList(list: string): string[] {
  const names: string[] = []
  for (const part of list.split(',')) {
    const name = part.trim()
    if (name !== '' && !names.includes(name)) names.push(name)
  }
  return names
}

export function formatToolList(names: readonly string[]): string {
  return names.join(', ')
}

/** The list with these names granted or not. Granting adds at the end; the rest keep their order. */
export function setToolsGranted(list: string, ids: readonly string[], granted: boolean): string {
  const names = parseToolList(list)
  if (!granted) return formatToolList(names.filter(name => !ids.includes(name)))
  for (const id of ids) if (!names.includes(id)) names.push(id)
  return formatToolList(names)
}

// ── names ───────────────────────────────────────────────────────────────────

const MCP_PREFIX = 'mcp__'

/** A tool's name as an agent is granted it. */
export function mcpToolId(server: string, tool: string): string {
  return `${MCP_PREFIX}${server}__${tool}`
}

/**
 * Which of `servers` a granted name belongs to, and the tool's own name there. A name that grants
 * a whole server (`mcp__server`) has an empty tool. Null for a built-in tool and for a server not
 * in `servers`. The longest matching server wins, so `a` never claims a tool of `a__b`.
 */
export function splitMcpToolId(id: string, servers: readonly string[]): { server: string; tool: string } | null {
  if (!id.startsWith(MCP_PREFIX)) return null
  let found: { server: string; tool: string } | null = null
  for (const server of servers) {
    if (server === '' || (found && found.server.length >= server.length)) continue
    if (id === MCP_PREFIX + server) found = { server, tool: '' }
    else if (id.startsWith(`${MCP_PREFIX}${server}__`)) found = { server, tool: id.slice(MCP_PREFIX.length + server.length + 2) }
  }
  return found
}

// ── the view ────────────────────────────────────────────────────────────────

export interface ToolRow {
  /** The name as it is granted, and as it is stored. */
  id: string
  /** What the row shows: an MCP tool's own name, a built-in tool's whole name. */
  label: string
  description: string | null
  granted: boolean
  /** Whether its server lists it. False for a granted name nothing offers; always true for a built-in. */
  listed: boolean
}

export interface ServerTools {
  server: string
  /** What the server answered. Null when it was not asked: no saved endpoint by this name yet. */
  answer: DiscoveredServer | null
  rows: ToolRow[]
  grantedCount: number
}

export interface ToolView {
  alwaysOn: string[]
  /** Granted names that are not MCP tools, other than the always-on ones. */
  builtIn: ToolRow[]
  /** One entry per server, in the order given, then servers every agent has that were not named. */
  servers: ServerTools[]
  /** Granted MCP names whose server is not among the agent's endpoints. */
  other: ToolRow[]
  grantedCount: number
}

/**
 * The agent's tool list laid out against its servers.
 * @param endpointNames the names of the endpoints in the dialog, saved or not, in their order.
 * @param remembered names revoked since the dialog opened. A tool no server lists has a row only
 *   because it is granted; remembering it keeps the row, unticked, so a slip can be undone.
 */
export function buildToolView(
  list: string, endpointNames: readonly string[], discovery: ToolDiscovery | null, remembered: readonly string[] = [],
): ToolView {
  const granted = parseToolList(list)
  const alwaysOn = discovery?.alwaysOn ?? []
  const named = endpointNames.filter((name, i) => name !== '' && endpointNames.indexOf(name) === i)
  // A server every agent is given (fleet-memory) is listed although the agent has no row for it.
  const implicit = (discovery?.servers ?? []).map(s => s.name).filter(name => !named.includes(name))
  const serverNames = [...named, ...implicit]

  const byServer = new Map<string, ToolRow[]>(serverNames.map(name => [name, []]))
  const builtIn: ToolRow[] = []
  const other: ToolRow[] = []

  for (const server of serverNames) {
    const answer = discovery?.servers.find(s => s.name === server)
    for (const tool of answer?.tools ?? []) {
      const id = mcpToolId(server, tool.name)
      byServer.get(server)!.push({ id, label: tool.name, description: tool.description, granted: granted.includes(id), listed: true })
    }
  }

  for (const id of [...granted, ...remembered.filter(name => !granted.includes(name))]) {
    const isGranted = granted.includes(id)
    if (!id.startsWith(MCP_PREFIX)) {
      if (!alwaysOn.includes(id)) builtIn.push({ id, label: id, description: null, granted: isGranted, listed: true })
      continue
    }
    const place = splitMcpToolId(id, serverNames)
    if (!place) {
      other.push({ id, label: id, description: null, granted: isGranted, listed: false })
      continue
    }
    const rows = byServer.get(place.server)!
    if (rows.some(row => row.id === id)) continue
    rows.push(place.tool === ''
      ? { id, label: 'Every tool of this server', description: `Granted as ${id}.`, granted: isGranted, listed: true }
      : { id, label: place.tool, description: null, granted: isGranted, listed: false })
  }

  const servers = serverNames.map(server => {
    const rows = byServer.get(server)!
    return {
      server,
      answer: discovery?.servers.find(s => s.name === server) ?? null,
      rows,
      grantedCount: rows.filter(row => row.granted).length,
    }
  })
  return { alwaysOn, builtIn, servers, other, grantedCount: granted.length }
}

/** Whether a row matches what was typed into the filter. An empty filter matches everything. */
export function matchesFilter(row: ToolRow, query: string): boolean {
  const q = query.trim().toLowerCase()
  if (q === '') return true
  return row.id.toLowerCase().includes(q) || (row.description ?? '').toLowerCase().includes(q)
}

/** A description's first line, which is what a row shows; the whole of it is the row's tooltip. */
export function firstLine(description: string | null): string {
  return (description ?? '').split('\n').map(line => line.trim()).find(line => line !== '') ?? ''
}

/**
 * Why a server shows no list, in a few words for the head of its block. Null when it answered.
 * The orchestrator's own sentence, when it sent one, is shown beside it.
 */
export function answerHeadline(answer: DiscoveredServer | null, saved: boolean): string | null {
  if (!answer) return saved ? null : 'Save to list this server’s tools.'
  switch (answer.status) {
    case 'ok': return null
    case 'unreachable': return 'Could not reach this server.'
    case 'timeout': return 'This server did not answer in time.'
    case 'refused': return 'This server refused to list its tools.'
    case 'needs_agent_secret': return 'Its tools cannot be listed from here.'
    case 'unsupported_transport': return 'This transport cannot be asked for tools.'
    case 'misconfigured': return 'This endpoint cannot be asked as it is saved.'
    default: return 'Its tools could not be listed.'
  }
}
