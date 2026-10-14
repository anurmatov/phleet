// Run with `npm test` (node --test, Node ≥ 22.18 strips the types). Type-checked by `tsc -b`.
// #460 owner direction: the cloud model picker offers family aliases only.
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { fileURLToPath } from 'node:url'
import { createElement } from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import { createServer } from 'vite'
import { PROVIDER_DEFAULT_MODEL, PROVIDER_MODELS } from './constants.ts'
import { modelChoices } from './modelChoices.ts'

test('Claude and Codex offer the family aliases only, with no free text', () => {
  assert.deepEqual(PROVIDER_MODELS.claude, ['opus', 'sonnet', 'haiku'])
  assert.deepEqual(PROVIDER_MODELS.codex, ['sol', 'terra', 'luna'])
  assert.equal(PROVIDER_DEFAULT_MODEL.claude, 'sonnet')
  assert.equal(PROVIDER_DEFAULT_MODEL.codex, 'sol')
  for (const provider of ['claude', 'codex']) {
    const choices = modelChoices(provider, PROVIDER_MODELS[provider][0])
    assert.equal(choices.freeText, false, provider)
    assert.equal(choices.current, null, provider)
  }
})

test('a stored dated or hosted id is kept as the current choice, never rewritten', () => {
  assert.deepEqual(modelChoices('claude', 'claude-opus-4-7'), { options: ['opus', 'sonnet', 'haiku'], current: 'claude-opus-4-7', freeText: false })
  assert.equal(modelChoices('codex', 'gpt-5.6-sol').current, 'gpt-5.6-sol')
  assert.equal(modelChoices('codex', 'zai/glm-5.3').current, 'zai/glm-5.3')
  assert.equal(modelChoices('codex', '').current, null)
})

test('Gemini keeps its list and free text', () => {
  assert.equal(modelChoices('gemini', 'gemini-custom').freeText, true)
})

test('rendered picker: aliases, the stored dated id selected as current, no custom entry', async () => {
  const server = await createServer({ root: fileURLToPath(new URL('..', import.meta.url)), configFile: false,
    server: { middlewareMode: true, hmr: false, watch: null }, appType: 'custom' })
  try {
    const { default: ModelSelector } = await server.ssrLoadModule('/src/components/ModelSelector.tsx')
    const render = (provider: string, value: string) =>
      renderToStaticMarkup(createElement(ModelSelector, { provider, value, onChange: () => {} }))

    const dated = render('codex', 'gpt-5.6-sol')
    assert.match(dated, /<option value="sol">sol<\/option><option value="terra">terra<\/option><option value="luna">luna<\/option><option value="gpt-5.6-sol" selected="">gpt-5.6-sol \(current\)<\/option>/)
    assert.equal(dated.includes('custom'), false)

    const alias = render('claude', 'opus')
    assert.match(alias, /<option value="opus" selected="">opus<\/option>/)
    assert.equal(alias.includes('(current)'), false)
    assert.match(alias, /class="model-pill active"[^>]*>opus</)

    assert.ok(render('gemini', 'gemini-2.5-pro').includes('— custom —'))
  } finally { await server.close() }
})
