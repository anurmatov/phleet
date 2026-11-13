// Run with `npm test` (node --test, Node ≥ 22.18 strips the types). Type-checked by `tsc -b`.
//
// The dashboard's types are written by hand against C# the compiler here never sees, and they
// drifted: the workflow editor read `name` from a list keyed by `agentName`; the config type
// declared an `agentName` and a `displayName` the route does not send. These tests read the
// orchestrator's source and hold the dashboard's request bodies and response types against it.
//
// They are a stopgap for generated types (which need the server to have typed DTOs first), and
// they fail, rather than skip, when the C# they read has moved: a guard that cannot find what it
// guards must say so.
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync, readdirSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import { join } from 'node:path'
import { configSaveBody } from './agentConfigSave.ts'
import { DEFAULT_CREATE_FORM, createAgentBody } from './agentCreate.ts'
import { agentNamesFromList } from './agentList.ts'
import type { AgentConfig, AgentState, ConfigEdits, CreateForm } from './types'

const ORCHESTRATOR = fileURLToPath(new URL('../../Fleet.Orchestrator/', import.meta.url))

function orchestratorSources(): { path: string; text: string }[] {
  const out: { path: string; text: string }[] = []
  const walk = (dir: string) => {
    for (const entry of readdirSync(dir, { withFileTypes: true })) {
      if (entry.name === 'bin' || entry.name === 'obj' || entry.name === 'Migrations') continue
      const full = join(dir, entry.name)
      if (entry.isDirectory()) walk(full)
      else if (entry.name.endsWith('.cs')) out.push({ path: full, text: readFileSync(full, 'utf8') })
    }
  }
  walk(ORCHESTRATOR)
  return out
}

const sources = orchestratorSources()

/** The one source file containing `marker`; the test fails if there is none or more than one. */
function sourceWith(marker: string): string {
  const hits = sources.filter(s => s.text.includes(marker))
  assert.equal(hits.length, 1, `expected exactly one orchestrator file containing ${JSON.stringify(marker)}, found ${hits.length}`)
  return hits[0].text
}

const camel = (name: string) => name.charAt(0).toLowerCase() + name.slice(1)

/** Splits on commas that are not inside (), <> or []. */
function splitTopLevel(text: string): string[] {
  const parts: string[] = []
  let depth = 0, start = 0
  for (let i = 0; i < text.length; i++) {
    const c = text[i]
    if (c === '(' || c === '<' || c === '[') depth++
    else if (c === ')' || c === '>' || c === ']') depth--
    else if (c === ',' && depth === 0) { parts.push(text.slice(start, i)); start = i + 1 }
  }
  parts.push(text.slice(start))
  return parts.map(p => p.trim()).filter(Boolean)
}

/** The JSON names a positional C# record binds: its parameter names, camelCased. */
function recordJsonNames(record: string): Set<string> {
  const marker = `record ${record}(`
  const text = sourceWith(marker)
  const start = text.indexOf(marker) + marker.length
  let depth = 1, end = start
  while (depth > 0) {
    const c = text[end++]
    if (c === '(') depth++
    else if (c === ')') depth--
    assert.ok(end <= text.length, `unterminated record ${record}`)
  }
  const names = splitTopLevel(text.slice(start, end - 1)).map(param => {
    const declaration = param.split('=')[0].trim()        // drop a default value
    const name = declaration.split(/\s+/).pop()!
    assert.match(name, /^[A-Z]\w*$/, `could not read a parameter name from ${JSON.stringify(param)}`)
    return camel(name)
  })
  assert.ok(names.length > 5, `record ${record} parsed to too few parameters`)
  return new Set(names)
}

/** The JSON names of the anonymous object `GET /api/agents/{name}/config` answers with. */
function configResponseJsonNames(): Set<string> {
  const route = 'app.MapGet("/api/agents/{name}/config"'
  const text = sourceWith(route)
  const from = text.indexOf('return Results.Ok(new', text.indexOf(route))
  assert.ok(from > 0, 'the config route no longer answers with an anonymous object')
  const open = text.indexOf('{', from)
  let depth = 1, end = open + 1
  while (depth > 0) {
    const c = text[end++]
    if (c === '{') depth++
    else if (c === '}') depth--
  }
  const names = new Set<string>()
  // Comments go first: one with a comma in it would otherwise split a member in two.
  const members = text.slice(open + 1, end - 1).replace(/\/\/[^\n]*/g, '')
  for (const raw of splitTopLevelBraces(members)) {
    const member = raw.trim()
    if (!member) continue
    const assigned = /^(\w+)\s*=/.exec(member)
    const projected = /^agent\.(\w+)$/.exec(member)
    const name = assigned?.[1] ?? projected?.[1]
    assert.ok(name, `could not read a member name from ${JSON.stringify(member)}`)
    names.add(camel(name))
  }
  assert.ok(names.size > 20, 'the config response parsed to too few members')
  return names
}

/** As {@link splitTopLevel}, also treating {} as nesting. */
function splitTopLevelBraces(text: string): string[] {
  const parts: string[] = []
  let depth = 0, start = 0
  for (let i = 0; i < text.length; i++) {
    const c = text[i]
    if ('({['.includes(c)) depth++
    else if (')}]'.includes(c)) depth--
    else if (c === ',' && depth === 0) { parts.push(text.slice(start, i)); start = i + 1 }
  }
  parts.push(text.slice(start))
  return parts
}

/** The JSON names of the public properties of the orchestrator's `AgentState`. */
function agentStateJsonNames(): Set<string> {
  // The line end keeps AgentStateMonitorService and the like out.
  const marker = 'public sealed class AgentState\n'
  const text = sourceWith(marker)
  const open = text.indexOf('{', text.indexOf(marker))
  let depth = 1, end = open + 1
  while (depth > 0) {
    const c = text[end++]
    if (c === '{') depth++
    else if (c === '}') depth--
  }
  const body = text.slice(open + 1, end - 1)
  assert.equal(/\[JsonIgnore|\[JsonPropertyName/.test(body), false, 'AgentState now renames or hides a property; teach this test')
  const names = new Set<string>()
  for (const match of body.matchAll(/^\s*public\s+(?:required\s+)?[\w<>[\]?,. ]+?\s+(\w+)\s*(?:\{|=>)/gm))
    names.add(camel(match[1]))
  assert.ok(names.has('agentName') && names.has('effectiveStatus'), 'AgentState parsed without its known properties')
  return names
}

const unknownKeys = (sent: Iterable<string>, accepted: Set<string>) => [...sent].filter(k => !accepted.has(k)).sort()

test('the agent list is keyed by agentName, and that is what the workflow editor reads', () => {
  assert.ok(agentStateJsonNames().has('agentName'))
  assert.equal(agentStateJsonNames().has('name'), false)

  assert.deepEqual(agentNamesFromList([{ agentName: 'dev' }, { agentName: 'cto', role: 'co-cto' }]), ['dev', 'cto'])
  // The shape the editor used to read yields nothing, as do rows that are not agents.
  assert.deepEqual(agentNamesFromList([{ name: 'dev' }]), [])
  assert.deepEqual(agentNamesFromList([{ agentName: '' }, { agentName: 7 }, null, 'dev', { agentName: 'dev' }, { agentName: 'dev' }]), ['dev'])
  for (const notAList of [null, undefined, { error: 'Sign in to use this fleet.' }, 'x']) assert.deepEqual(agentNamesFromList(notAList), [])
})

test('every field the dashboard declares on an agent row is one the orchestrator sends', () => {
  // `Required<>` makes tsc refuse this object when a field is added to the type and not here.
  const row: Required<AgentState> = {
    agentName: 'a', displayName: null, shortName: null, model: null, role: null, currentTask: null,
    currentTaskId: null, reportedStatus: 'idle', effectiveStatus: 'idle', lastSeen: '', version: null,
    queuedCount: 0, priorityQueuedCount: 0, queuedMessages: null, backgroundTasks: null,
    containerName: null, containerStartedAt: null, hostPort: null, journal: null,
  }
  // Declared optional because the orchestrator does not send them; listed so a third one is noticed.
  const declaredButNotSent = ['displayName', 'shortName']
  assert.deepEqual(unknownKeys(Object.keys(row), agentStateJsonNames()), declaredButNotSent)
})

test('every field the dashboard declares on an agent config is one the config route sends', () => {
  const config: Required<AgentConfig> = {
    name: 'a', model: 'm', memoryLimitMb: 1, isEnabled: true, image: '', permissionMode: '', maxTurns: 1,
    workDir: '', proactiveIntervalMinutes: 0, groupListenMode: '', groupDebounceSeconds: 0,
    warmupTimeoutSeconds: 0, shortName: '', showStats: true, prefixMessages: false, formattingMode: 0,
    suppressToolMessages: false, effort: '', jsonSchema: '', agentsJson: '', autoMemoryEnabled: true,
    journalEnabled: false, journalCrossChatEnabled: false, chatEnabled: true, hostPort: null,
    telegramSendOnly: false, provider: 'claude', codexSandboxMode: null, outputStyle: null,
    localBaseUrl: null, anthropicBaseUrl: null, contextWindow: null, harnessExtra: null, tools: [],
    projects: [], mcpEndpoints: [], networks: [], envRefs: [], telegramUsers: [], telegramGroups: [],
    canReceiveChatRequests: false, requestReceivedMessage: null, mountDockerSock: false, instructions: [],
  }
  const sent = configResponseJsonNames()
  assert.deepEqual(unknownKeys(Object.keys(config), sent), [])
  // The two the type used to declare, and the copy-from code used to read through a cast.
  for (const absent of ['agentName', 'displayName', 'role']) assert.equal(sent.has(absent), false, absent)
  assert.ok(sent.has('name'))
})

test('the config dialog sends only fields the config route accepts', () => {
  const recorded = JSON.parse(readFileSync(new URL('./fixtures/agentConfigSaveBodies.json', import.meta.url), 'utf8')) as
    Record<string, { edits: Omit<ConfigEdits, 'harnessExtra'> }>
  const accepted = recordJsonNames('AgentConfigUpdateRequest')
  const cases = Object.keys(recorded)
  assert.ok(cases.length > 0)
  for (const name of cases) {
    // An edited harnessExtra and a moved chat switch, so the two conditional keys are sent too.
    const edits: ConfigEdits = { ...recorded[name].edits, harnessExtra: 'edited', chatEnabled: true }
    const save = configSaveBody(edits, null, false)
    assert.ok(save.ok, name)
    // `hostPort` is sent and not bound: the dialog has no field for it and the route ignores the
    // key. Listed, not removed, because the body is pinned by the #460 fixture; a second such
    // key fails here.
    assert.deepEqual(unknownKeys(Object.keys(save.body), accepted), ['hostPort'], name)
    assert.ok('harnessExtra' in save.body && 'chatEnabled' in save.body, name)
  }
  // The route takes no display name; the dialog used to send the key (always undefined).
  assert.equal(accepted.has('displayName'), false)
})

test('the New Agent dialog sends only fields the create route accepts', () => {
  const accepted = recordJsonNames('CreateAgentRequest')
  const base: CreateForm = { ...DEFAULT_CREATE_FORM, name: 'a1', role: 'developer' }
  const forms: Record<string, CreateForm> = {
    cloud: base,
    'claude local': { ...base, localMode: true, model: 'qwen3.6:27b', localBaseUrl: 'http://10.0.0.5:11434', contextWindow: '131072' },
    'codex local': { ...base, provider: 'codex', localMode: true, model: 'ollama/gpt-oss:20b', localBaseUrl: 'http://10.0.0.5:11434', contextWindow: '65536' },
  }
  for (const [name, form] of Object.entries(forms)) {
    const create = createAgentBody(form, true)
    assert.ok(create.ok, name)
    assert.deepEqual(unknownKeys(Object.keys(create.body), accepted), [], name)
  }
  assert.ok(accepted.has('localBaseUrl') && accepted.has('contextWindow'))
})
