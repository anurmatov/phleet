// Run with `npm test` (node --test, Node ≥ 22.18 strips the types). Type-checked by `tsc -b`.
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { DEFAULT_CREATE_FORM, createAgentBody, createFormFromConfig, createFormLocalEdits } from './agentCreate.ts'
import { ACTIVE_VIEWS, parseViewHash } from './activeViews.ts'
import type { AgentConfig, CreateForm } from './types'

/** `GET /api/agents/{name}/config` for a cloud Claude agent. No role and no display name: the route sends neither. */
const cloudConfig: AgentConfig = {
  name: 'source', model: 'claude-opus-5-5', memoryLimitMb: 2048, isEnabled: true, image: '',
  permissionMode: 'acceptEdits', maxTurns: 80, workDir: '/workspace', proactiveIntervalMinutes: 0,
  groupListenMode: 'mention', groupDebounceSeconds: 15, warmupTimeoutSeconds: 60, shortName: 'src',
  showStats: true, prefixMessages: false, formattingMode: 2, suppressToolMessages: false, effort: 'high',
  jsonSchema: '', agentsJson: '', autoMemoryEnabled: true, journalEnabled: false,
  journalCrossChatEnabled: false, hostPort: 18080, telegramSendOnly: false, provider: 'claude',
  codexSandboxMode: null, outputStyle: null, localBaseUrl: null, contextWindow: null,
  tools: [{ toolName: 'Read', isEnabled: true }, { toolName: 'Bash', isEnabled: false }],
  projects: ['fleet'], mcpEndpoints: [{ mcpName: 'fleet-memory', url: 'http://fleet-memory:3100/mcp', transportType: 'http' }],
  networks: ['fleet-net'], envRefs: ['GH_TOKEN', 'OTHER'], telegramUsers: [11, 22], telegramGroups: [-100],
  canReceiveChatRequests: false, requestReceivedMessage: null, mountDockerSock: false, instructions: [],
}

const named = (form: CreateForm): CreateForm => ({ ...form, name: 'copy' })
const body = (form: CreateForm) => {
  const create = createAgentBody(form, false)
  assert.ok(create.ok, create.ok ? '' : create.error)
  return create.body
}

test('a cloud create sends exactly the body it always did', () => {
  const form: CreateForm = { ...DEFAULT_CREATE_FORM, name: ' a1 ', role: ' developer ', tools: 'Read\n\n Bash ', telegramUsers: '7\nx\n8' }
  assert.deepEqual(body(form), {
    name: 'a1', displayName: 'a1', role: 'developer', model: 'sonnet', containerName: 'fleet-a1',
    memoryLimitMb: 4096, isEnabled: true, image: null, permissionMode: 'acceptEdits', maxTurns: 50,
    workDir: '/workspace', proactiveIntervalMinutes: 0, groupListenMode: 'mention', groupDebounceSeconds: 15,
    warmupTimeoutSeconds: 60, shortName: '', showStats: true, prefixMessages: false, formattingMode: 0,
    suppressToolMessages: false, telegramSendOnly: false, effort: null, jsonSchema: null, agentsJson: null,
    autoMemoryEnabled: true, provider: 'claude', codexSandboxMode: null, tools: ['Read', 'Bash'], projects: [],
    networks: [], envRefs: [], mcpEndpoints: [], telegramUsers: [7, 8], telegramGroups: [], provision: false,
  })
  // No local-model key reaches an orchestrator that predates them.
  assert.equal('localBaseUrl' in body(form) || 'contextWindow' in body(form), false)
  const provisioned = createAgentBody(form, true)
  assert.ok(provisioned.ok)
  assert.equal(provisioned.body.provision, true)
})

test('copying takes the role from the agent list, since the config route has none', () => {
  const before: CreateForm = { ...DEFAULT_CREATE_FORM, name: 'copy', displayName: 'Copy', role: 'typed-first' }
  const copied = createFormFromConfig(before, cloudConfig, 'developer')
  assert.equal(copied.role, 'developer')
  // A source the list knows no role for leaves what was typed.
  for (const none of [null, undefined, '']) assert.equal(createFormFromConfig(before, cloudConfig, none).role, 'typed-first')
  // Identity is never copied; settings are, and a disabled tool is not carried over as enabled.
  assert.deepEqual([copied.name, copied.displayName, copied.shortName], ['copy', 'Copy', ''])
  assert.deepEqual([copied.model, copied.effort, copied.memoryLimitMb, copied.maxTurns], ['claude-opus-5-5', 'high', '2048', '80'])
  assert.equal(copied.tools, 'Read')
  assert.equal(copied.envRefs, 'GH_TOKEN\nOTHER')
  assert.equal(copied.telegramUsers, '11\n22')
  assert.deepEqual([copied.localMode, copied.localBaseUrl, copied.contextWindow], [false, '', ''])
})

test('a copy of a local-model Claude agent is created as a local-model agent', () => {
  const local: AgentConfig = { ...cloudConfig, model: 'qwen3.6:27b', effort: 'off', localBaseUrl: 'http://10.0.0.5:11434', contextWindow: 131072 }
  const copied = named(createFormFromConfig(DEFAULT_CREATE_FORM, local, 'developer'))
  assert.deepEqual([copied.localMode, copied.localBaseUrl, copied.contextWindow], [true, 'http://10.0.0.5:11434', '131072'])
  const sent = body(copied)
  // Before: no localBaseUrl in the body, so a cloud agent holding a local model tag.
  assert.equal(sent.localBaseUrl, 'http://10.0.0.5:11434')
  assert.equal(sent.contextWindow, 131072)
  assert.equal(sent.model, 'qwen3.6:27b')
  assert.equal(sent.effort, 'off')
})

test('an older orchestrator names the local server anthropicBaseUrl; the copy still reads it', () => {
  const legacy: AgentConfig = { ...cloudConfig, model: 'qwen3.6:27b', localBaseUrl: undefined, anthropicBaseUrl: 'http://10.0.0.5:11434' }
  assert.equal(body(named(createFormFromConfig(DEFAULT_CREATE_FORM, legacy, 'developer'))).localBaseUrl, 'http://10.0.0.5:11434')
})

test('a copy of a Codex local agent keeps its server prefix, URL or legacy Env Ref', () => {
  const codex: AgentConfig = { ...cloudConfig, provider: 'codex', model: 'lmstudio/gpt-oss:20b', effort: '', localBaseUrl: 'http://10.0.0.5:1234', contextWindow: null }
  const copied = named(createFormFromConfig(DEFAULT_CREATE_FORM, codex, 'developer'))
  assert.deepEqual([copied.localMode, copied.localServer], [true, 'lmstudio'])
  const sent = body(copied)
  assert.deepEqual([sent.model, sent.localBaseUrl, 'contextWindow' in sent], ['lmstudio/gpt-oss:20b', 'http://10.0.0.5:1234', false])

  // Legacy: no URL, the server reached through the Env Ref. Local by its model prefix; no URL is sent.
  const legacy: AgentConfig = { ...codex, model: 'ollama/gpt-oss:20b', localBaseUrl: null, envRefs: ['GH_TOKEN', 'CODEX_OSS_BASE_URL'] }
  const legacyForm = named(createFormFromConfig(DEFAULT_CREATE_FORM, legacy, 'developer'))
  assert.equal(legacyForm.localMode, true)
  // The form lists Env Refs one per line; the shared local rules read them comma-separated.
  assert.equal(createFormLocalEdits(legacyForm).envRefs, 'GH_TOKEN,CODEX_OSS_BASE_URL')
  const legacySent = body(legacyForm)
  assert.deepEqual([legacySent.model, 'localBaseUrl' in legacySent], ['ollama/gpt-oss:20b', false])
})

test('what is missing is said before anything is sent', () => {
  const ready: CreateForm = { ...DEFAULT_CREATE_FORM, name: 'a1', role: 'developer' }
  const problem = (form: CreateForm) => { const c = createAgentBody(form, false); return c.ok ? null : c.error }
  assert.equal(problem(ready), null)
  assert.equal(problem({ ...ready, name: '  ' }), 'Name is required')
  assert.equal(problem({ ...ready, role: '' }), 'Role is required')
  // An empty model falls back to the provider's default, as before.
  assert.equal(body({ ...ready, model: '' }).model, 'sonnet')
  assert.equal(problem({ ...ready, localMode: true, model: 'qwen3.6:27b' }), 'Enter the local server URL')
  assert.equal(problem({ ...ready, localMode: true, model: '', localBaseUrl: 'http://10.0.0.5:11434' }), 'Enter the model tag')
  assert.match(problem({ ...ready, localMode: true, model: 'm', localBaseUrl: 'http://10.0.0.5:11434', contextWindow: '12' }) ?? '', /Context window must be/)
  // Local mode means nothing for a harness without it.
  assert.equal('localBaseUrl' in body({ ...ready, provider: 'gemini', model: 'gemini-x', localMode: true, localBaseUrl: 'http://10.0.0.5:11434' }), false)
})

test('every view has a hash, #memory included, and a bad hash never throws', () => {
  for (const view of ACTIVE_VIEWS) assert.deepEqual(parseViewHash(`#${view}`), { view, param: '' })
  // The one the hand-kept list had dropped: reloading on it landed on agents.
  assert.deepEqual(parseViewHash('#memory'), { view: 'memory', param: '' })
  assert.deepEqual(parseViewHash('#output-styles/alpha%20beta'), { view: 'output-styles', param: 'alpha beta' })
  assert.deepEqual(parseViewHash('memory/a/b'), { view: 'memory', param: 'a/b' })
  for (const unknown of ['', '#', '#nope', '#Agents', '#agents-x/1']) assert.equal(parseViewHash(unknown), null)
  // Runs while the page first renders: malformed percent-encoding is kept as written, not thrown.
  assert.deepEqual(parseViewHash('#output-styles/%E0%A4%A'), { view: 'output-styles', param: '%E0%A4%A' })
})
