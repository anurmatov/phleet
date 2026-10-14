// Run with `npm test` (node --test, Node ≥ 22.18 strips the types). Type-checked by `tsc -b`.
// #460 Part 3a: the read-only effective-settings block (AC-X5, AC-X6 display).
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { fileURLToPath } from 'node:url'
import { createElement } from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import { createServer } from 'vite'
import { claudeLines, codexLines, driftState, harnessExtraPlaceholder, shortSha } from './effectiveHarness.ts'
import type { EffectiveHarness } from './types'

const SHA_A = 'a'.repeat(64)
const SHA_B = 'b'.repeat(64)

// Shapes as GET /api/agents/{name}/effective-harness serves them (credentials already masked).
const claudeLocal: EffectiveHarness = {
  agent: 'agent1', provider: 'claude', mode: 'local', codex: null,
  claude: {
    args: ['--tools', 'Agent,Task,ToolSearch,Read', '--disallowedTools', 'WebSearch', '--strict-mcp-config'],
    env: [
      { name: 'ANTHROPIC_BASE_URL', value: 'http://inference-host:11434' },
      { name: 'ANTHROPIC_AUTH_TOKEN', value: '***' },
      { name: 'CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC', value: '1' },
      { name: 'MAX_MCP_OUTPUT_TOKENS', value: '12000' },
    ],
  },
  extra: { state: 'applied', reason: null, replaced: ['MAX_MCP_OUTPUT_TOKENS', '--exclude-dynamic-system-prompt-sections'], notes: [] },
  planSha256: SHA_A, computedPlanSha256: SHA_A, startedPlanSha256: SHA_B, pendingReprovision: true,
}

const codexCloud: EffectiveHarness = {
  agent: 'agent1', provider: 'codex', mode: 'cloud', claude: null,
  codex: {
    configToml: 'check_for_update_on_startup = false\n\n[analytics]\nenabled = false\n\n[history]\npersistence = "save-all"\n',
    threadConfig: { 'features.apps': false, web_search: 'disabled', tool_output_token_limit: 12000 },
  },
  extra: { state: 'applied', reason: null, replaced: ['history.persistence', 'tool_output_token_limit'] },
  planSha256: SHA_A, computedPlanSha256: SHA_A, startedPlanSha256: SHA_A, pendingReprovision: false,
}

test('drift: pending, running, not reported', () => {
  assert.equal(driftState(claudeLocal), 'pending')
  assert.equal(driftState(codexCloud), 'running')
  assert.equal(driftState({ ...codexCloud, startedPlanSha256: null }), 'not-reported')
  assert.equal(shortSha(SHA_A), 'aaaaaaaaaaaa')
  assert.equal(shortSha(null), '—')
})

test('codex lines: the replaced table key and thread key are marked; others are not', () => {
  const { root, thread } = codexLines(codexCloud)
  assert.deepEqual(root.filter(l => l.override).map(l => l.text), ['persistence = "save-all"'])
  assert.deepEqual(thread, [
    { text: 'features.apps = false', override: false },
    { text: 'web_search = "disabled"', override: false },
    { text: 'tool_output_token_limit = 12000', override: true },
  ])
})

test('claude lines: flags with their values, a removed default shown, env overrides marked', () => {
  const { args, env } = claudeLines(claudeLocal)
  assert.deepEqual(args.map(a => a.text), [
    '--tools Agent,Task,ToolSearch,Read', '--disallowedTools WebSearch', '--strict-mcp-config',
    '!--exclude-dynamic-system-prompt-sections',
  ])
  assert.ok(args.at(-1)!.override)
  assert.deepEqual(env.filter(e => e.override).map(e => e.text), ['MAX_MCP_OUTPUT_TOKENS=12000'])
})

test('placeholders are provider-specific', () => {
  assert.match(harnessExtraPlaceholder('codex'), /tool_output_token_limit = 12000/)
  assert.match(harnessExtraPlaceholder('claude'), /--disallowedTools WebSearch/)
})

test('rendered block: masked credentials, overrides, both shas, the badge and the ignored reason', async () => {
  const server = await createServer({ root: fileURLToPath(new URL('..', import.meta.url)), configFile: false,
    server: { middlewareMode: true, hmr: false, watch: null }, appType: 'custom' })
  try {
    const { EffectiveHarnessBlock } = await server.ssrLoadModule('/src/components/EffectiveHarnessView.tsx')
    const render = (harness: EffectiveHarness) => renderToStaticMarkup(createElement(EffectiveHarnessBlock, { harness }))

    const pending = render(claudeLocal)
    assert.ok(pending.includes('ANTHROPIC_AUTH_TOKEN=***'))
    assert.equal(pending.includes('phleet-local-no-auth'), false)
    assert.match(pending, /class="effective-harness-override">MAX_MCP_OUTPUT_TOKENS=12000<span class="effective-harness-override-tag"> override<\/span>/)
    assert.match(pending, /title="a{64}">plan <code>a{12}<\/code>/)
    assert.match(pending, /title="b{64}">running <code>b{12}<\/code>/)
    assert.match(pending, /effective-harness-drift-pending">pending reprovision</)

    const running = render(codexCloud)
    assert.match(running, />running this plan</)
    assert.match(running, /class="effective-harness-override">persistence = &quot;save-all&quot;/)

    const notReported = render({ ...codexCloud, startedPlanSha256: null })
    assert.match(notReported, />not reported</)

    const ignored = render({ ...codexCloud, extra: { state: 'ignored', reason: 'harnessExtra line 1: \'notify\' is not an allowed setting for codex.', replaced: [] } })
    assert.ok(ignored.includes('Extra harness settings ignored: harnessExtra line 1:'))
    assert.match(ignored, /extra <strong>ignored<\/strong>/)
  } finally { await server.close() }
})
