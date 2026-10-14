// Run with `npm test` (node --test, Node ≥ 22.18 strips the types). Type-checked by `tsc -b`.
// #460 owner direction: the cloud model picker lists family aliases; #465: plus a custom model name.
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { fileURLToPath } from 'node:url'
import { createElement, type ReactElement } from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import { createServer } from 'vite'
import { PROVIDER_DEFAULT_MODEL, PROVIDER_MODELS } from './constants.ts'
import { modelChoices } from './modelChoices.ts'

test('Claude and Codex list the family aliases and take free text, like Gemini', () => {
  assert.deepEqual(PROVIDER_MODELS.claude, ['opus', 'sonnet', 'haiku'])
  assert.deepEqual(PROVIDER_MODELS.codex, ['sol', 'terra', 'luna'])
  assert.equal(PROVIDER_DEFAULT_MODEL.claude, 'sonnet')
  assert.equal(PROVIDER_DEFAULT_MODEL.codex, 'sol')
  for (const provider of ['claude', 'codex', 'gemini']) {
    const choices = modelChoices(provider, PROVIDER_MODELS[provider][0])
    assert.equal(choices.freeText, true, provider)
    assert.equal(choices.current, null, provider)
  }
})

test('a stored dated or hosted id is kept as the current value, never rewritten', () => {
  assert.deepEqual(modelChoices('claude', 'claude-opus-4-7'), { options: ['opus', 'sonnet', 'haiku'], current: 'claude-opus-4-7', freeText: true })
  assert.equal(modelChoices('codex', 'gpt-5.6-sol').current, 'gpt-5.6-sol')
  assert.equal(modelChoices('codex', 'zai/glm-5.3').current, 'zai/glm-5.3')
  assert.equal(modelChoices('codex', '').current, null)
  assert.equal(modelChoices('gemini', 'gemini-custom').current, 'gemini-custom')
})

test('rendered picker: aliases plus a custom entry; a stored non-alias id opens in the custom input', async () => {
  const server = await createServer({ root: fileURLToPath(new URL('..', import.meta.url)), configFile: false,
    server: { middlewareMode: true, hmr: false, watch: null }, appType: 'custom' })
  try {
    const { default: ModelSelector } = await server.ssrLoadModule('/src/components/ModelSelector.tsx')
    const render = (provider: string, value: string) =>
      renderToStaticMarkup(createElement(ModelSelector, { provider, value, onChange: () => {} }))
    // The element tree ModelSelector returns, with its handlers; its hooks run inside the probe's render.
    const tree = (provider: string, value: string, onChange: (model: string) => void) => {
      let out: ReactElement | null = null
      const Probe = () => { out = ModelSelector({ provider, value, onChange }); return null }
      renderToStaticMarkup(createElement(Probe))
      return out as unknown as ReactElement<{ children: ReactElement<Record<string, unknown>>[] }>
    }

    for (const [provider, value] of [['claude', 'opus'], ['codex', 'sol']]) {
      const html = render(provider, value)
      const [a, b, c] = PROVIDER_MODELS[provider]
      assert.match(html, new RegExp(`<option value="${a}" selected="">${a}</option><option value="${b}">${b}</option><option value="${c}">${c}</option><option value="">custom…</option>`), provider)
      assert.match(html, new RegExp(`class="model-pill active"[^>]*>${a}<`), provider)
      assert.equal(html.includes('<input'), false, provider)
    }

    for (const [provider, value] of [['claude', 'claude-opus-4-7'], ['codex', 'zai/glm-5.3'], ['codex', 'gpt-5.6-sol']]) {
      const html = render(provider, value)
      assert.ok(html.includes(`<input class="config-input " placeholder="custom model name" value="${value}"/>`), value)
      assert.equal(html.includes('<select'), false, value)
      for (const alias of PROVIDER_MODELS[provider]) assert.match(html, new RegExp(`class="model-pill"[^>]*>${alias}<`), value)
    }

    assert.ok(render('gemini', 'gemini-2.5-pro').includes('custom…'))

    // Typed text reaches onChange exactly; an alias pill hands over the alias.
    const seen: string[] = []
    const [input, pills] = tree('claude', 'claude-opus-4-7', m => seen.push(m)).props.children
    ;(input.props.onChange as (e: { target: { value: string } }) => void)({ target: { value: ' Claude-Opus-4-7 ' } })
    ;(input.props.onChange as (e: { target: { value: string } }) => void)({ target: { value: 'zai/GLM-5.3' } })
    ;((pills.props.children as ReactElement<{ onClick: () => void }>[])[0].props.onClick)()
    assert.deepEqual(seen, [' Claude-Opus-4-7 ', 'zai/GLM-5.3', 'opus'])
  } finally { await server.close() }
})
