import type { AgentState } from './types'

// Reading `GET /api/agents`. Kept free of JSX and `import.meta.env` so `node --test` can run it.

/**
 * The agent names in `GET /api/agents`. Each row is the orchestrator's `AgentState`, named by
 * `agentName` (the workflow editor read `name`, which no row has, so its agent chips were always
 * empty). Anything that is not such a list yields no names.
 */
export function agentNamesFromList(body: unknown): string[] {
  if (!Array.isArray(body)) return []
  const names: string[] = []
  for (const row of body) {
    const name = row && typeof row === 'object' ? (row as Record<string, unknown>).agentName : null
    if (typeof name === 'string' && name !== '' && !names.includes(name)) names.push(name)
  }
  return names
}

/**
 * The agents to show after `GET /api/agents` answered with `list`, given the rows `held` so far.
 *
 * The socket only ever adds or updates a row (the orchestrator has no removal message), so an
 * agent deleted elsewhere stays listed until something replaces the list. This does: the result
 * has exactly the agents the server listed. For one in both, the row with the later `lastSeen`
 * is kept, so a socket message that arrived while the request was in flight is not rolled back.
 */
export function reconcileAgents(held: Record<string, AgentState>, list: AgentState[]): Record<string, AgentState> {
  const next: Record<string, AgentState> = {}
  for (const fetched of list) {
    const current = held[fetched.agentName]
    const heldIsNewer = current !== undefined && Date.parse(current.lastSeen) > Date.parse(fetched.lastSeen)
    next[fetched.agentName] = heldIsNewer ? current : fetched
  }
  return next
}
