// Run with `npm test` (node --test, Node ≥ 22.18 strips the types). Type-checked by `tsc -b`.
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { addHeaderRow, danglingMappings, envRefChoices, isMcpHeaderFault, removeHeaderRow, updateHeaderRow } from './mcpHeaders.ts'
import { configSaveBody } from './agentConfigSave.ts'
import type { ConfigEdits, McpEndpointEntry } from './types'

const endpoint: McpEndpointEntry = { mcpName: 'example', url: 'https://mcp.example.test/mcp', transportType: 'http' }

test('rows are added, edited and removed as names only, and the last removal sends [] to clear', () => {
  let ep = addHeaderRow(endpoint)
  assert.deepEqual(ep.headerEnvRefs, [{ headerName: '', envRefName: '' }])
  ep = updateHeaderRow(ep, 0, { headerName: 'X-Example-Key' })
  ep = updateHeaderRow(ep, 0, { envRefName: 'EXAMPLE_KEY' })
  ep = updateHeaderRow(addHeaderRow(ep), 1, { headerName: 'X-Example-Secret', envRefName: 'EXAMPLE_SECRET' })
  assert.deepEqual(ep.headerEnvRefs, [
    { headerName: 'X-Example-Key', envRefName: 'EXAMPLE_KEY' },
    { headerName: 'X-Example-Secret', envRefName: 'EXAMPLE_SECRET' },
  ])
  ep = removeHeaderRow(removeHeaderRow(ep, 0), 0)
  assert.deepEqual(ep.headerEnvRefs, [])
  // The other fields are untouched throughout.
  assert.deepEqual({ ...ep, headerEnvRefs: undefined }, { ...endpoint, headerEnvRefs: undefined })
})

test('the reference selector offers the agent\'s own Env Refs and keeps a dangling one visible by name', () => {
  assert.deepEqual(envRefChoices(['EXAMPLE_KEY', 'EXAMPLE_SECRET'], 'EXAMPLE_KEY').map(c => c.value), ['EXAMPLE_KEY', 'EXAMPLE_SECRET'])
  const choices = envRefChoices(['EXAMPLE_KEY'], 'REMOVED_KEY')
  assert.deepEqual(choices.at(-1), { value: 'REMOVED_KEY', label: 'REMOVED_KEY (not an Env Ref of this agent)', dangling: true })
  assert.equal(envRefChoices(['EXAMPLE_KEY'], '').length, 1)

  const ep = { ...endpoint, headerEnvRefs: [{ headerName: 'X-Example-Key', envRefName: 'REMOVED_KEY' }] }
  assert.deepEqual(danglingMappings(ep, ['EXAMPLE_KEY']), ep.headerEnvRefs)
  assert.deepEqual(danglingMappings(ep, ['REMOVED_KEY']), [])
})

test('a save sends the mappings with the endpoint, and an endpoint loaded without them sends none', () => {
  const edits = {
    model: 'sonnet', memoryLimitMb: '4096', isEnabled: true, image: '', permissionMode: 'acceptEdits', maxTurns: '50',
    workDir: '/workspace', proactiveIntervalMinutes: '0', groupListenMode: 'mention', groupDebounceSeconds: '15',
    warmupTimeoutSeconds: '60', shortName: 'a', showStats: false, prefixMessages: false, formattingMode: 0,
    suppressToolMessages: false, effort: '', jsonSchema: '', agentsJson: '', autoMemoryEnabled: true,
    journalEnabled: false, journalCrossChatEnabled: false, chatEnabled: true, hostPort: '', telegramSendOnly: false,
    provider: 'claude', codexSandboxMode: '', tools: '', projects: [], networks: '', envRefs: 'EXAMPLE_KEY',
    mcpEndpoints: [
      { ...endpoint, headerEnvRefs: [{ headerName: 'X-Example-Key', envRefName: 'EXAMPLE_KEY' }] },
      { mcpName: 'legacy', url: 'http://legacy:3000', transportType: 'http' },
    ],
    telegramUsers: '', telegramGroups: '', canReceiveChatRequests: false, requestReceivedMessage: '', mountDockerSock: false,
    outputStyle: '', localMode: false, localBaseUrl: '', localServer: 'ollama', contextWindow: '', harnessExtra: '',
    instructions: [],
  } satisfies ConfigEdits
  const save = configSaveBody(edits, null, true)
  assert.ok(save.ok)
  const sent = save.body.mcpEndpoints as McpEndpointEntry[]
  assert.deepEqual(sent[0].headerEnvRefs, [{ headerName: 'X-Example-Key', envRefName: 'EXAMPLE_KEY' }])
  // Omitted, not []: an older orchestrator, or one this dialog did not edit, keeps what it stores.
  assert.equal('headerEnvRefs' in sent[1], false)
})

test('a refused mapping is recognised by its fixed code', () => {
  assert.ok(isMcpHeaderFault('mcp_header_ref_missing: MCP endpoint \'example\' maps X-Example-Key to EXAMPLE_KEY …'))
  assert.ok(isMcpHeaderFault('mcp_headers_unsupported: provider \'gemini\' does not send MCP headers'))
  assert.ok(!isMcpHeaderFault('Memory must be ≥ 128 MB'))
})
