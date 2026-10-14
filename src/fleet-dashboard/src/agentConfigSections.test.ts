// Run with `npm test` (node --test, Node ≥ 22.18 strips the types). Type-checked by `tsc -b`.
// #460 Part 2: the sectioned agent config dialog is a layout change only.
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import { createElement } from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import { createServer } from 'vite'
import { ADVANCED_DEFAULTS, countCustomized } from './constants.ts'
import { CONFIG_SECTIONS, DEFAULT_SECTION, sectionCustomizedCount, visibleFields } from './components/AgentConfigSections.ts'
import { configSaveBody } from './agentConfigSave.ts'
import type { AgentConfig, ConfigEdits } from './types'

// Bodies recorded by evaluating the pre-#460 inline save code of App.tsx (main at 09eb3f1) on these
// edits, before the dialog was sectioned.
const recorded = JSON.parse(readFileSync(new URL('./fixtures/agentConfigSaveBodies.json', import.meta.url), 'utf8')) as
  Record<string, { edits: Omit<ConfigEdits, 'harnessExtra'>; body: Record<string, unknown> }>

const edits = (name: string, harnessExtra = ''): ConfigEdits => ({ ...recorded[name].edits, harnessExtra })

// Every labelled field of the pre-#460 modal, with the section the design puts it in (#460 Part 2 table).
const TODAY: Record<string, { section: string; key: string }> = {
  'Enabled': { section: 'general', key: 'isEnabled' },
  'Short Name': { section: 'general', key: 'shortName' },
  'Provider': { section: 'model', key: 'provider' },
  'Model': { section: 'model', key: 'model' },
  'Runs on': { section: 'model', key: 'localMode' },
  'Server URL': { section: 'model', key: 'localBaseUrl' },
  'Server': { section: 'model', key: 'localServer' },
  'Context window (tokens)': { section: 'model', key: 'contextWindow' },
  'Image input': { section: 'model', key: 'piImageInput' },
  'Effort': { section: 'model', key: 'effort' },
  'Sandbox Mode': { section: 'model', key: 'codexSandboxMode' },
  'Auto Memory': { section: 'model', key: 'autoMemoryEnabled' },
  'Instructions': { section: 'instructions', key: 'instructions' },
  'Projects': { section: 'instructions', key: 'projects' },
  'Output Style': { section: 'instructions', key: 'outputStyle' },
  'Memory Project Access': { section: 'instructions', key: 'memoryProjectAccess' },
  'Tools': { section: 'tools', key: 'tools' },
  'MCP Endpoints': { section: 'tools', key: 'mcpEndpoints' },
  'Telegram Users': { section: 'telegram', key: 'telegramUsers' },
  'Telegram Groups': { section: 'telegram', key: 'telegramGroups' },
  'Group Listen Mode': { section: 'telegram', key: 'groupListenMode' },
  'Group Debounce (s)': { section: 'telegram', key: 'groupDebounceSeconds' },
  'Proactive Interval (min)': { section: 'telegram', key: 'proactiveIntervalMinutes' },
  'Show Stats': { section: 'telegram', key: 'showStats' },
  'Prefix Messages': { section: 'telegram', key: 'prefixMessages' },
  'Formatting Mode': { section: 'telegram', key: 'formattingMode' },
  'Suppress Tool Messages': { section: 'telegram', key: 'suppressToolMessages' },
  'Telegram Send-Only (no polling)': { section: 'telegram', key: 'telegramSendOnly' },
  'Journal human Telegram messages': { section: 'telegram', key: 'journalEnabled' },
  'Cross-chat attachments': { section: 'telegram', key: 'journalCrossChatEnabled' },
  'Accept Chat Requests': { section: 'telegram', key: 'canReceiveChatRequests' },
  'Request Received Message': { section: 'telegram', key: 'requestReceivedMessage' },
  'Image': { section: 'advanced', key: 'image' },
  'Memory (MB)': { section: 'advanced', key: 'memoryLimitMb' },
  'Work Dir': { section: 'advanced', key: 'workDir' },
  'Permission Mode': { section: 'advanced', key: 'permissionMode' },
  'Max Turns': { section: 'advanced', key: 'maxTurns' },
  'Warmup Timeout (s)': { section: 'advanced', key: 'warmupTimeoutSeconds' },
  'JSON Schema': { section: 'advanced', key: 'jsonSchema' },
  'Agents JSON': { section: 'advanced', key: 'agentsJson' },
  'Networks': { section: 'advanced', key: 'networks' },
  'Env Refs': { section: 'advanced', key: 'envRefs' },
  'Mount Docker socket': { section: 'advanced', key: 'mountDockerSock' },
}

test('AC-D1: every field of the old modal is in exactly one section, the one the design names', () => {
  const placed = CONFIG_SECTIONS.flatMap(s => s.fields.map(f => ({ section: s.id, key: f.key })))
  for (const [label, { section, key }] of Object.entries(TODAY)) {
    const homes = placed.filter(p => p.key === key)
    assert.equal(homes.length, 1, `${label} (${key}) is in ${homes.length} sections`)
    assert.equal(homes[0].section, section, label)
  }
  // The only additions are the two #460 Part 3 fields.
  const added = placed.map(p => p.key).filter(k => !Object.values(TODAY).some(t => t.key === k))
  assert.deepEqual(added, ['effectiveHarness', 'harnessExtra'])
  assert.deepEqual(CONFIG_SECTIONS.map(s => s.label), ['General', 'Model & runtime', 'Instructions & projects', 'Tools & MCP', 'Telegram', 'Advanced'])
})

test('AC-D3: each section counts today\'s customized fields in that section; together they are the old total', () => {
  for (const name of Object.keys(recorded)) {
    const e = edits(name) as unknown as Record<string, unknown>
    const perSection = CONFIG_SECTIONS.map(s => sectionCustomizedCount(s, e))
    assert.equal(perSection.reduce((a, b) => a + b, 0), countCustomized(e), name)
    for (const s of CONFIG_SECTIONS)
      assert.equal(sectionCustomizedCount(s, e), countCustomized(e, s.fields.map(f => f.key)), `${name}/${s.id}`)
  }
  // Every counted field sits in exactly one section, so nothing is counted twice or dropped.
  for (const key of Object.keys(ADVANCED_DEFAULTS))
    assert.equal(CONFIG_SECTIONS.filter(s => s.fields.some(f => f.key === key)).length, 1, key)
})

test('AC-D2: the save body equals the body recorded from the old modal; harnessExtra is sent only when edited', () => {
  for (const [name, { body }] of Object.entries(recorded)) {
    const save = configSaveBody(edits(name), null)
    assert.ok(save.ok)
    assert.deepEqual(JSON.parse(JSON.stringify(save.body)), body, name)
    // Loaded and unchanged: still not sent.
    const kept = configSaveBody(edits(name, '--disallowedTools WebSearch'), '--disallowedTools WebSearch')
    assert.ok(kept.ok && !('harnessExtra' in kept.body), name)
  }
  const set = configSaveBody(edits('claude-cloud', 'MAX_MCP_OUTPUT_TOKENS=12000'), null)
  assert.ok(set.ok)
  assert.deepEqual(JSON.parse(JSON.stringify(set.body)), { ...recorded['claude-cloud'].body, harnessExtra: 'MAX_MCP_OUTPUT_TOKENS=12000' })
  const cleared = configSaveBody(edits('claude-cloud', ''), 'web_search = "disabled"')
  assert.ok(cleared.ok)
  assert.equal(cleared.body.harnessExtra, '')
})

test('sections: a provider sees only its fields; no section is ever empty for a real provider', () => {
  const advanced = CONFIG_SECTIONS.find(s => s.id === 'advanced')!
  assert.ok(visibleFields(advanced, 'claude').some(f => f.key === 'harnessExtra'))
  assert.ok(visibleFields(advanced, 'codex').some(f => f.key === 'harnessExtra'))
  for (const provider of ['pi', 'gemini']) {
    assert.equal(visibleFields(advanced, provider).some(f => f.key === 'harnessExtra'), false, provider)
    assert.equal(visibleFields(CONFIG_SECTIONS[1], provider).some(f => f.key === 'effectiveHarness'), false, provider)
  }
  for (const provider of ['claude', 'codex', 'gemini', 'pi'])
    for (const s of CONFIG_SECTIONS) assert.ok(visibleFields(s, provider).length > 0, `${provider}/${s.id}`)
})

// ── the real modal, rendered ─────────────────────────────────────────────────

function panels(html: string): Record<string, string> {
  const out: Record<string, string> = {}
  const parts = html.split('<section class="config-section-panel" data-section="')
  for (const part of parts.slice(1)) out[part.slice(0, part.indexOf('"'))] = part.slice(0, part.indexOf('</section>'))
  return out
}

const label = (text: string) => new RegExp(`>${text.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}(<| <span)`)

test('rendered dialog: six tabs with counts, one visible panel, every label in its own panel only, footer outside', async () => {
  const server = await createServer({ root: fileURLToPath(new URL('..', import.meta.url)), configFile: false,
    server: { middlewareMode: true, hmr: false, watch: null }, appType: 'custom' })
  try {
    const { default: Modal } = await server.ssrLoadModule('/src/components/AgentConfigModal.tsx')
    const render = (e: ConfigEdits, configSaveState = 'idle', configSaveMsg = '') => renderToStaticMarkup(createElement(Modal, {
      agentName: 'agent1', configData: { provider: e.provider, tools: [], harnessExtra: null } as unknown as AgentConfig, configEdits: e,
      configSaveState, configSaveMsg, configLoading: false, configReprovisionConfirm: false,
      allInstructions: [], outputStyles: [], projectContexts: [], promptSizeLimits: null, projectAccess: [],
      projectAccessLoading: false, onEditsChange: () => {}, onSave: () => {}, onReprovisionConfirmToggle: () => {},
      onProjectAccessAdd: () => {}, onProjectAccessRemove: () => {}, onProjectAccessToggleWildcard: () => {}, onClose: () => {},
    }))
    const claude = render(edits('claude-cloud'))
    const codex = render(edits('codex-local'))

    assert.equal((claude.match(/role="tab"/g) ?? []).length, 6)
    assert.match(claude, new RegExp(`role="tab" aria-selected="true" class="config-section-tab active">Model &amp; runtime`))
    assert.equal(DEFAULT_SECTION, 'model')
    const p = panels(claude)
    assert.deepEqual(Object.keys(p), CONFIG_SECTIONS.map(s => s.id))
    for (const [id, text] of Object.entries(p)) assert.equal(text.includes('hidden=""'), id !== 'model', id)
    // The customized counts appear on the tabs, e.g. Telegram has groupDebounceSeconds and formattingMode.
    assert.match(claude, /Telegram<span class="config-section-count" title="2 customized">2<\/span>/)

    // Fields each render must show; the rest are provider- or mode-specific.
    const absent: Record<string, string[]> = {
      claude: ['Server URL', 'Server', 'Context window (tokens)', 'Image input', 'Sandbox Mode'],
      codex: ['Model', 'Image input', 'Auto Memory', 'Permission Mode', 'Max Turns', 'JSON Schema', 'Agents JSON'],
    }
    for (const [html, provider] of [[claude, 'claude'], [codex, 'codex']] as const) {
      const byPanel = panels(html)
      for (const [text, { section }] of Object.entries(TODAY)) {
        const homes = Object.entries(byPanel).filter(([, h]) => label(text).test(h)).map(([id]) => id)
        assert.deepEqual(homes, absent[provider].includes(text) ? [] : [section], `${provider}: ${text}`)
      }
      assert.ok(byPanel.model.includes('Effective settings'), provider)
      assert.ok(label('Extra harness settings').test(byPanel.advanced), provider)
      assert.ok(html.indexOf('<div class="config-modal-footer">') > html.lastIndexOf('</section>'), 'footer after the panels')
    }
    assert.ok(codex.includes('tool_output_token_limit = 12000'), 'Codex placeholder')
    assert.ok(claude.includes('MAX_MCP_OUTPUT_TOKENS=12000'), 'Claude placeholder')

    const pi = render(edits('pi'))
    assert.equal(pi.includes('Extra harness settings'), false)
    assert.equal(pi.includes('Effective settings'), false)

    // A refused harnessExtra shows its line and reason at the field too.
    const refused = 'harnessExtra line 2: \'notify\' is not an allowed setting for codex.'
    const error = panels(render(edits('codex-local', 'notify = 1'), 'error', refused)).advanced
    assert.ok(error.includes('harnessExtra line 2: &#x27;notify&#x27; is not an allowed setting for codex.'))
  } finally { await server.close() }
})
