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
