import type { McpEndpointEntry, McpHeaderEnvRef } from './types'

// The MCP endpoint header editor's rules (#593): rows of a header name and an Env Ref name, never a
// value. Kept free of JSX so `node --test` can run it. The server checks every rule again.

/** One choice in a row's Env Ref selector. `dangling`: the row names a reference the agent no longer holds. */
export interface EnvRefChoice {
  value: string
  label: string
  dangling: boolean
}

/**
 * The selector's choices: the agent's own Env Refs, plus the row's current reference when the agent
 * does not hold it, shown by name so a dangling mapping is corrected or removed, never dropped.
 */
export function envRefChoices(envRefs: string[], current: string): EnvRefChoice[] {
  const choices = envRefs.map(r => ({ value: r, label: r, dangling: false }))
  if (current !== '' && !envRefs.includes(current))
    choices.push({ value: current, label: `${current} (not an Env Ref of this agent)`, dangling: true })
  return choices
}

/** The mappings of `endpoint` whose reference the agent does not hold. */
export function danglingMappings(endpoint: McpEndpointEntry, envRefs: string[]): McpHeaderEnvRef[] {
  return (endpoint.headerEnvRefs ?? []).filter(m => !envRefs.includes(m.envRefName))
}

/** `endpoint` with one more, empty row. */
export function addHeaderRow(endpoint: McpEndpointEntry): McpEndpointEntry {
  return { ...endpoint, headerEnvRefs: [...(endpoint.headerEnvRefs ?? []), { headerName: '', envRefName: '' }] }
}

/** `endpoint` with row `index` changed. */
export function updateHeaderRow(endpoint: McpEndpointEntry, index: number, patch: Partial<McpHeaderEnvRef>): McpEndpointEntry {
  return { ...endpoint, headerEnvRefs: (endpoint.headerEnvRefs ?? []).map((m, i) => i === index ? { ...m, ...patch } : m) }
}

/** `endpoint` without row `index`. Removing the last row leaves `[]`, which the API reads as "clear". */
export function removeHeaderRow(endpoint: McpEndpointEntry, index: number): McpEndpointEntry {
  return { ...endpoint, headerEnvRefs: (endpoint.headerEnvRefs ?? []).filter((_, i) => i !== index) }
}

/** A save refused for a mapping: its message names one of these fixed codes. */
export function isMcpHeaderFault(message: string): boolean {
  return message.includes('mcp_header')
}
