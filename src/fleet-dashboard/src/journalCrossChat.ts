// Match .NET String.Trim whitespace, including NEL but excluding BOM.
function trimGrant(name: string): string {
  return name.replace(/^[\u0009-\u000d\u0020\u0085\u00a0\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000]+|[\u0009-\u000d\u0020\u0085\u00a0\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000]+$/g, '')
}
/** Must match JournalGrants.IsSendGrant; covered by the shared parity fixture. */
export function hasSendGrant(tools: ReadonlyArray<{ name: string; enabled: boolean }>): boolean {
  return tools.some(t => t.enabled && /^[\x00-\x7f]*$/.test(trimGrant(t.name))
    && trimGrant(t.name).toLowerCase() === 'mcp__fleet-journal-files__send_attachment')
}
export function crossChatDisabledReason(journal: boolean, tools: ReadonlyArray<{ name: string; enabled: boolean }>): string | null {
  if (!journal) return 'Needs journal capture.'
  return hasSendGrant(tools) ? null : 'Needs the send_attachment grant.'
}

/** REST preserves an existing row's enabled flag; a new edited entry defaults on. */
export function editedTools(text: string, stored: ReadonlyArray<{ toolName: string; isEnabled: boolean }>): Array<{ name: string; enabled: boolean }> {
  return text.split(',').map(name => ({ name, enabled: stored.find(t =>
    t.toolName.toLowerCase() === name.trim().toLowerCase())?.isEnabled ?? true }))
}
