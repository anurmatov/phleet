// Run with `npm test` (node --test, Node ≥ 22.18 strips the types). Type-checked by `tsc -b`.
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import { createRequire } from 'node:module'
import ts from 'typescript'
import { createElement } from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import * as constants from './constants.ts'
import { LOCAL_MODEL_SUGGESTIONS, PROVIDER_DEFAULT_MODEL, PROVIDER_MODELS, PROVIDER_OPTIONS } from './constants.ts'
import * as contextWindowModule from './contextWindow.ts'
import * as journalCrossChat from './journalCrossChat.ts'
import * as localModel from './localModel.ts'
import {
  LEGACY_CODEX_NOTICE, LOCAL_TAG_REQUIRED, LOCAL_URL_REQUIRED, PI1_TEXT, PI3_TEXT, PI_MIN_CONTEXT_WINDOW,
  PI_RUNS_ON_NOTE, V1_PATH_HINT, applySuggestion, composeCodexLocalModel, createFormProviderPatch, effortChoices,
  effortOptions, isLocalHarness, isLocalMode, localModelEditValues, localModelSaveBody, localModelSuggestions,
  localModelTag, runsOnLocked, setLocalServer, setLocalTag, showLegacyCodexNotice, splitCodexLocalModel,
  suggestionNotes, switchProvider, switchRunsOn, v1PathHint,
} from './localModel.ts'
import type { LocalModelEdits, LocalModelPatch } from './localModel.ts'
import type { AgentConfig, ConfigEdits, CreateForm } from './types'

const ORIGIN = 'http://inference-host:11434'

function edits(overrides: Partial<LocalModelEdits> = {}): LocalModelEdits {
  return {
    provider: 'codex', localMode: true, model: 'ollama/qwen3.8:27b', localServer: 'ollama',
    localBaseUrl: ORIGIN, contextWindow: '131072', effort: 'none', envRefs: '', ...overrides,
  }
}

const apply = (e: LocalModelEdits, patch: LocalModelPatch): LocalModelEdits => ({ ...e, ...patch })

test('split agrees with CodexExecutor.SplitLocalModel', () => {
  const cases: Array<[string, { server: string; tag: string } | null]> = [
    // CodexExecutorTests.SplitLocalModel_RecognisesOnlyBuiltInProviderPrefixes
    ['ollama/gpt-oss:20b', { server: 'ollama', tag: 'gpt-oss:20b' }],
    ['OLLAMA/gpt-oss:20b', { server: 'ollama', tag: 'gpt-oss:20b' }],
    ['lmstudio/qwen3-coder', { server: 'lmstudio', tag: 'qwen3-coder' }],
    ['gpt-5', null],
    ['owl/t-lite', null],
    ['/gpt-oss:20b', null],
    ['ollama/', null],
    // #382 cases
    ['ollama/qwen3.8:27b', { server: 'ollama', tag: 'qwen3.8:27b' }],
    ['lmstudio/x', { server: 'lmstudio', tag: 'x' }],
    ['OLLAMA/x', { server: 'ollama', tag: 'x' }],
    ['LMStudio/x', { server: 'lmstudio', tag: 'x' }],
    ['ollama/hf.co/org/model:q4', { server: 'ollama', tag: 'hf.co/org/model:q4' }],
    ['zai/glm-5.3', null],
    ['/x', null],
    ['ollama', null],
    ['', null],
  ]
  for (const [model, expected] of cases) assert.deepEqual(splitCodexLocalModel(model), expected, JSON.stringify(model))
})

test('compose is the inverse of split', () => {
  assert.equal(composeCodexLocalModel('ollama', 'qwen3.8:27b'), 'ollama/qwen3.8:27b')
  assert.equal(composeCodexLocalModel('lmstudio', 'hf.co/org/model:q4'), 'lmstudio/hf.co/org/model:q4')
  assert.deepEqual(splitCodexLocalModel(composeCodexLocalModel('lmstudio', 'a/b')), { server: 'lmstudio', tag: 'a/b' })
})

test('mode: local when claude or codex has a URL, or codex has a local model (legacy); gemini never', () => {
  assert.equal(isLocalMode('claude', ORIGIN, 'qwen3.8:27b'), true)
  assert.equal(isLocalMode('claude', '', 'claude-sonnet-4-6'), false)
  assert.equal(isLocalMode('claude', '   ', 'qwen3.8:27b'), false)
  assert.equal(isLocalMode('codex', ORIGIN, 'ollama/qwen3.8:27b'), true)
  assert.equal(isLocalMode('codex', '', 'ollama/qwen3.8:27b'), true, 'legacy Env Ref row')
  assert.equal(isLocalMode('codex', '', 'gpt-5'), false)
  assert.equal(isLocalMode('codex', '', 'zai/glm-5.3'), false)
  assert.equal(isLocalMode('gemini', ORIGIN, 'gemini-2.5-flash'), false)
  assert.equal(isLocalMode('gemini', '', 'ollama/x'), false)
})

test('load: localBaseUrl wins, anthropicBaseUrl is the fallback for an older orchestrator', () => {
  assert.deepEqual(
    localModelEditValues({ provider: 'codex', model: 'lmstudio/x', localBaseUrl: ORIGIN, anthropicBaseUrl: ORIGIN }),
    { localMode: true, localBaseUrl: ORIGIN, localServer: 'lmstudio' })
  assert.deepEqual(
    localModelEditValues({ provider: 'claude', model: 'qwen3.8:27b', anthropicBaseUrl: ORIGIN }),
    { localMode: true, localBaseUrl: ORIGIN, localServer: 'ollama' })
  assert.deepEqual(
    localModelEditValues({ provider: 'codex', model: 'ollama/x', localBaseUrl: null, anthropicBaseUrl: null }),
    { localMode: true, localBaseUrl: '', localServer: 'ollama' })
  assert.deepEqual(
    localModelEditValues({ provider: 'claude', model: 'claude-sonnet-4-6' }),
    { localMode: false, localBaseUrl: '', localServer: 'ollama' })
})

test('Cloud → Local: tag and URL start empty', () => {
  const cloud = edits({ provider: 'claude', localMode: false, model: 'claude-sonnet-4-6', localBaseUrl: '', contextWindow: '' })
  const local = apply(cloud, switchRunsOn(cloud, true))
  assert.equal(local.localMode, true)
  assert.equal(local.model, '')
  assert.equal(local.localBaseUrl, '')
})

test('Local → Cloud: URL and context cleared (sent as 0), model reset, effort kept', () => {
  const local = edits({ effort: 'none' })
  const patch = switchRunsOn(local, false)
  assert.equal('effort' in patch, false)
  const cloud = apply(local, patch)
  assert.equal(cloud.localMode, false)
  assert.equal(cloud.localBaseUrl, '')
  assert.equal(cloud.contextWindow, '')
  assert.equal(cloud.model, PROVIDER_DEFAULT_MODEL.codex)
  assert.equal(cloud.effort, 'none')
  const saved = localModelSaveBody(cloud)
  assert.ok(saved.ok)
  if (saved.ok) assert.deepEqual(saved.body, { model: 'gpt-5', localBaseUrl: '', contextWindow: 0 })
})

test('Codex → Claude while Local strips the server prefix; URL and context unchanged', () => {
  for (const model of ['ollama/qwen3.8:27b', 'lmstudio/qwen3.8:27b']) {
    const codex = edits({ model, localServer: splitCodexLocalModel(model)!.server })
    const claude = apply(codex, switchProvider(codex, 'claude'))
    assert.equal(claude.provider, 'claude')
    assert.equal(claude.localMode, true)
    assert.equal(claude.model, 'qwen3.8:27b', model)
    assert.equal(claude.localBaseUrl, ORIGIN)
    assert.equal(claude.contextWindow, '131072')
    assert.equal(claude.effort, codex.effort, 'effort is not touched; the select marks it if invalid')
  }
})

test('Claude → Codex while Local composes ollama/<tag>; URL and context unchanged', () => {
  const claude = edits({ provider: 'claude', model: 'qwen3.8:27b', localServer: 'lmstudio', effort: 'off' })
  const codex = apply(claude, switchProvider(claude, 'codex'))
  assert.equal(codex.model, 'ollama/qwen3.8:27b')
  assert.equal(codex.localServer, 'ollama')
  assert.equal(codex.localMode, true)
  assert.equal(codex.localBaseUrl, ORIGIN)
  assert.equal(codex.contextWindow, '131072')
  assert.equal(codex.effort, 'off')
})

test('switching to Gemini clears URL and context; a cloud switch resets the model', () => {
  const gemini = apply(edits(), switchProvider(edits(), 'gemini'))
  assert.equal(gemini.localMode, false)
  assert.equal(gemini.localBaseUrl, '')
  assert.equal(gemini.contextWindow, '')
  assert.equal(gemini.model, PROVIDER_DEFAULT_MODEL.gemini)

  const cloud = edits({ provider: 'claude', localMode: false, model: 'claude-opus-4-8', localBaseUrl: '' })
  const codex = apply(cloud, switchProvider(cloud, 'codex'))
  assert.equal(codex.localMode, false)
  assert.equal(codex.model, PROVIDER_DEFAULT_MODEL.codex)
  assert.deepEqual(switchProvider(cloud, 'claude'), {})
})

test('Codex tag and server edit the stored <server>/<tag> form; an empty tag keeps the server', () => {
  let e = edits({ model: '', localServer: 'ollama' })
  e = apply(e, setLocalServer(e, 'lmstudio'))
  assert.equal(e.model, '')
  assert.equal(e.localServer, 'lmstudio')
  e = apply(e, setLocalTag(e, 'qwen3.8:27b'))
  assert.equal(e.model, 'lmstudio/qwen3.8:27b')
  assert.equal(localModelTag('codex', e.model), 'qwen3.8:27b')
  e = apply(e, setLocalServer(e, 'ollama'))
  assert.equal(e.model, 'ollama/qwen3.8:27b')
  const claude = edits({ provider: 'claude', model: '' })
  assert.equal(apply(claude, setLocalTag(claude, 'qwen3.8:27b')).model, 'qwen3.8:27b')
})

test('empty Codex URL: blocked unless Env Refs hold exactly CODEX_OSS_BASE_URL (legacy notice)', () => {
  const noUrl = (envRefs: string) => edits({ localBaseUrl: '', envRefs })

  assert.deepEqual(localModelSaveBody(noUrl('GITHUB_APP_ID')), { ok: false, error: LOCAL_URL_REQUIRED })
  assert.equal(showLegacyCodexNotice(noUrl('GITHUB_APP_ID')), false)

  const legacy = noUrl('GITHUB_APP_ID, CODEX_OSS_BASE_URL')
  assert.deepEqual(localModelSaveBody(legacy),
    { ok: true, body: { model: 'ollama/qwen3.8:27b', localBaseUrl: '', contextWindow: 131072 } })
  assert.equal(showLegacyCodexNotice(legacy), true)
  assert.match(LEGACY_CODEX_NOTICE, /CODEX_OSS_BASE_URL Env Ref \(legacy\)/)

  for (const envRefs of ['codex_oss_base_url', 'CODEX_OSS_BASE_URL_X', 'GITHUB_APP_ID CODEX_OSS_BASE_URL']) {
    assert.deepEqual(localModelSaveBody(noUrl(envRefs)), { ok: false, error: LOCAL_URL_REQUIRED }, envRefs)
    assert.equal(showLegacyCodexNotice(noUrl(envRefs)), false, envRefs)
  }
  // Parsed as Save parses it: split on ',', trim, drop empties.
  assert.equal(localModelSaveBody(noUrl(' , CODEX_OSS_BASE_URL ,')).ok, true)
  // The notice is only for an empty ORIGIN.
  assert.equal(showLegacyCodexNotice(edits({ envRefs: 'CODEX_OSS_BASE_URL' })), false)
})

test('empty Claude local URL is always blocked, Env Ref or not', () => {
  for (const envRefs of ['', 'CODEX_OSS_BASE_URL']) {
    const claude = edits({ provider: 'claude', model: 'qwen3.8:27b', localBaseUrl: '  ', envRefs })
    assert.deepEqual(localModelSaveBody(claude), { ok: false, error: LOCAL_URL_REQUIRED }, envRefs)
    assert.equal(showLegacyCodexNotice(claude), false)
  }
})

test('local mode needs a model tag', () => {
  assert.deepEqual(localModelSaveBody(edits({ model: '' })), { ok: false, error: LOCAL_TAG_REQUIRED })
  assert.deepEqual(localModelSaveBody(edits({ model: 'ollama/  ' })), { ok: false, error: LOCAL_TAG_REQUIRED })
  assert.deepEqual(localModelSaveBody(edits({ provider: 'claude', model: ' ' })), { ok: false, error: LOCAL_TAG_REQUIRED })
})

test('Save sends localBaseUrl only, never the anthropicBaseUrl alias', () => {
  const cases = [
    edits(),
    edits({ provider: 'claude', model: 'qwen3.8:27b' }),
    edits({ provider: 'claude', localMode: false, model: '', localBaseUrl: '', contextWindow: '' }),
    edits({ provider: 'gemini', localMode: false, model: 'gemini-2.5-pro', localBaseUrl: '' }),
  ]
  for (const e of cases) {
    const saved = localModelSaveBody(e)
    assert.ok(saved.ok, JSON.stringify(e))
    if (!saved.ok) continue
    assert.deepEqual(Object.keys(saved.body).sort(), ['contextWindow', 'localBaseUrl', 'model'])
    assert.equal(JSON.stringify(saved.body).includes('anthropicBaseUrl'), false)
  }
  const local = localModelSaveBody(edits({ localBaseUrl: ` ${ORIGIN} ` }))
  assert.deepEqual(local, { ok: true, body: { model: 'ollama/qwen3.8:27b', localBaseUrl: ORIGIN, contextWindow: 131072 } })
  const cloud = localModelSaveBody(cases[2])
  assert.deepEqual(cloud, { ok: true, body: { model: PROVIDER_DEFAULT_MODEL.claude, localBaseUrl: '', contextWindow: 0 } })
  assert.equal(localModelSaveBody(edits({ contextWindow: '4095' })).ok, false)
})

test('chips per harness: Qwen3.8 on Claude, Codex and Pi, gpt-oss on Codex only, none on Gemini', () => {
  assert.deepEqual(localModelSuggestions('claude').map(s => s.tag), ['qwen3.8:27b', 'qwen3.8-flash-next:125b-a6b-q4_K_M'])
  assert.deepEqual(localModelSuggestions('codex').map(s => s.tag),
    ['qwen3.8:27b', 'qwen3.8-flash-next:125b-a6b-q4_K_M', 'gpt-oss:20b'])
  assert.deepEqual(localModelSuggestions('pi').map(s => s.tag), ['qwen3.8:27b', 'qwen3.8-flash-next:125b-a6b-q4_K_M'])
  assert.deepEqual(localModelSuggestions('gemini'), [])
  // gpt-oss moved out of the Codex cloud list; no cloud entry is a local model.
  for (const model of PROVIDER_MODELS.codex) assert.equal(splitCodexLocalModel(model), null, model)
})

test('a chip fills the tag (and Codex effort for Qwen3.8), never the URL or context window', () => {
  const [qwen, , gptOss] = LOCAL_MODEL_SUGGESTIONS

  const codex = edits({ model: '', localServer: 'lmstudio', effort: '', localBaseUrl: '', contextWindow: '' })
  const qwenPatch = applySuggestion(codex, qwen)
  assert.deepEqual(qwenPatch, { model: 'lmstudio/qwen3.8:27b', effort: 'none' })
  assert.deepEqual(suggestionNotes(apply(codex, qwenPatch)), {
    effortNote: 'Effort set to none — recommended for Qwen3.8 on Codex',
    contextHint: 'Qwen3.8 supports up to 262144; enter the context your server is configured with.',
  })
  // The note is gone once the operator picks another effort.
  assert.equal(suggestionNotes(apply(codex, { ...qwenPatch, effort: 'low' })).effortNote, null)

  assert.deepEqual(applySuggestion(codex, gptOss), { model: 'lmstudio/gpt-oss:20b' })
  assert.deepEqual(suggestionNotes(apply(codex, applySuggestion(codex, gptOss))), { effortNote: null, contextHint: null })

  const claude = edits({ provider: 'claude', model: '', effort: '', localBaseUrl: '', contextWindow: '' })
  const claudePatch = applySuggestion(claude, qwen)
  assert.deepEqual(claudePatch, { model: 'qwen3.8:27b' })
  assert.equal(suggestionNotes(apply(claude, claudePatch)).effortNote, null)
  assert.match(suggestionNotes(apply(claude, claudePatch)).contextHint ?? '', /262144/)
})

test('/v1 hint for a URL ending in /v1 or /v1/, nothing else', () => {
  assert.equal(v1PathHint(`${ORIGIN}/v1`), V1_PATH_HINT)
  assert.equal(v1PathHint(`${ORIGIN}/v1/`), V1_PATH_HINT)
  assert.equal(V1_PATH_HINT, 'Remove /v1 — Fleet adds the right path for each harness')
  for (const url of [ORIGIN, `${ORIGIN}/`, `${ORIGIN}/v1beta`, `${ORIGIN}/v2`, '']) assert.equal(v1PathHint(url), null, url)
})

test('effort: Claude local keeps its own vocabulary; Codex local uses the Codex list', () => {
  assert.deepEqual(effortChoices('claude', true), ['', 'off', 'low', 'medium', 'xhigh'])
  assert.deepEqual(effortChoices('claude', false), ['', 'low', 'medium', 'high', 'xhigh', 'max'])
  assert.deepEqual(effortChoices('codex', true), effortChoices('codex', false))
  assert.deepEqual(effortChoices('codex', true), ['', 'none', 'minimal', 'low', 'medium', 'high', 'xhigh', 'max'])
})

// ── Pi (#386, A24) ────────────────────────────────────────────────────────────

const PI_ORIGIN = 'http://inference-host:8000'
const PI_TAG = 'qwen3.8-flash-next'

function piEdits(overrides: Partial<LocalModelEdits> = {}): LocalModelEdits {
  return edits({ provider: 'pi', model: PI_TAG, localBaseUrl: PI_ORIGIN, contextWindow: '131072', effort: '', ...overrides })
}

test('provider options: both modals list Pi (local model) from the one shared list', () => {
  assert.deepEqual(PROVIDER_OPTIONS.map(p => p.value), ['claude', 'codex', 'gemini', 'pi'])
  assert.deepEqual(PROVIDER_OPTIONS.find(p => p.value === 'pi'), { value: 'pi', label: 'Pi (local model)' })
  for (const file of ['./components/AgentConfigModal.tsx', './components/CreateAgentModal.tsx']) {
    const text = readFileSync(new URL(file, import.meta.url), 'utf8')
    assert.match(text, /PROVIDER_OPTIONS\.map\(/, file)
    assert.doesNotMatch(text, /<option value="(claude|codex|gemini|pi)"/, `${file} hard-codes a provider option`)
  }
  // Pi has no cloud list and no default: its tag is always the operator's.
  assert.equal(PROVIDER_MODELS.pi, undefined)
  assert.equal(PROVIDER_DEFAULT_MODEL.pi, undefined)
})

test('pi is a local harness whose Runs on is locked to Local server', () => {
  assert.equal(isLocalHarness('pi'), true)
  assert.equal(isLocalHarness('gemini'), false)
  assert.equal(runsOnLocked('pi'), true)
  for (const p of ['claude', 'codex', 'gemini']) assert.equal(runsOnLocked(p), false, p)
  // Local with or without a URL: an empty URL is PI1 at Save, never Cloud.
  assert.equal(isLocalMode('pi', PI_ORIGIN, PI_TAG), true)
  assert.equal(isLocalMode('pi', '', PI_TAG), true)
  assert.deepEqual(localModelEditValues({ provider: 'pi', model: PI_TAG, localBaseUrl: null }),
    { localMode: true, localBaseUrl: '', localServer: 'ollama' })
  // The Cloud pill does nothing; neither does Local.
  assert.deepEqual(switchRunsOn(piEdits(), false), {})
  assert.deepEqual(switchRunsOn(piEdits(), true), {})
  assert.deepEqual(switchRunsOn(piEdits({ localMode: false }), false), {})
  assert.equal(PI_RUNS_ON_NOTE, 'Pi runs only on a local server')
})

test('pi Save: context below 65536 (or unset) is blocked with the PI3 text', () => {
  assert.equal(PI3_TEXT, "Pi needs the server's context size, at least 65536; set the context window to the server's --ctx.")
  assert.equal(PI_MIN_CONTEXT_WINDOW, 65536)
  for (const contextWindow of ['', '  ', '0', '1000', '4095', '4096', '32768', '65535'])
    assert.deepEqual(localModelSaveBody(piEdits({ contextWindow })), { ok: false, error: PI3_TEXT }, JSON.stringify(contextWindow))
  for (const contextWindow of ['65536', '131072', ' 262144 ', '1048576']) {
    const saved = localModelSaveBody(piEdits({ contextWindow }))
    assert.ok(saved.ok, contextWindow)
    if (saved.ok) assert.equal(saved.body.contextWindow, Number(contextWindow))
  }
  // Malformed values keep the shared context-window errors.
  assert.deepEqual(localModelSaveBody(piEdits({ contextWindow: '65536.5' })),
    { ok: false, error: 'Context window must be a whole number of tokens' })
  assert.deepEqual(localModelSaveBody(piEdits({ contextWindow: '2000000' })),
    { ok: false, error: 'Context window must be 4096–1048576 tokens, or empty' })
})

test('pi Save: URL (PI1), then tag, then context — the server order; no Env Ref escape', () => {
  assert.equal(PI1_TEXT, 'Pi runs only on a local server; set the local server URL.')
  for (const envRefs of ['', 'CODEX_OSS_BASE_URL'])
    assert.deepEqual(localModelSaveBody(piEdits({ localBaseUrl: ' ', envRefs, contextWindow: '' })), { ok: false, error: PI1_TEXT })
  assert.deepEqual(localModelSaveBody(piEdits({ model: '  ', contextWindow: '' })), { ok: false, error: LOCAL_TAG_REQUIRED })
  // localMode is ignored for pi: there is no cloud body to fall back to.
  assert.deepEqual(localModelSaveBody(piEdits({ localMode: false, localBaseUrl: '' })), { ok: false, error: PI1_TEXT })
})

test('pi Save body: model, localBaseUrl and contextWindow only — never anthropicBaseUrl', () => {
  const saved = localModelSaveBody(piEdits({ localBaseUrl: ` ${PI_ORIGIN} `, model: ` ${PI_TAG} ` }))
  assert.deepEqual(saved, { ok: true, body: { model: PI_TAG, localBaseUrl: PI_ORIGIN, contextWindow: 131072 } })
  if (saved.ok) {
    assert.deepEqual(Object.keys(saved.body).sort(), ['contextWindow', 'localBaseUrl', 'model'])
    assert.equal(JSON.stringify(saved.body).includes('anthropicBaseUrl'), false)
  }
})

test('pi effort: Default (on), Off and On', () => {
  assert.deepEqual(effortChoices('pi', true), ['', 'off', 'on'])
  assert.deepEqual(effortChoices('pi', false), ['', 'off', 'on'])
  assert.deepEqual(effortOptions('pi', true),
    [{ value: '', label: 'Default (on)' }, { value: 'off', label: 'Off' }, { value: 'on', label: 'On' }])
  // The other harnesses keep their labels.
  assert.deepEqual(effortOptions('claude', true).map(o => o.label), ['default (xhigh)', 'off', 'low', 'medium', 'xhigh'])
  assert.equal(effortOptions('codex', true)[0].label, 'default')
})

test('pi chips fill the bare tag only: no Codex effort, the Qwen context hint', () => {
  const pi = piEdits({ model: '', effort: '', localBaseUrl: '', contextWindow: '' })
  const [qwen, flash] = localModelSuggestions('pi')
  assert.deepEqual(applySuggestion(pi, qwen), { model: 'qwen3.8:27b' })
  assert.deepEqual(applySuggestion(pi, flash), { model: 'qwen3.8-flash-next:125b-a6b-q4_K_M' })
  assert.deepEqual(setLocalTag(pi, PI_TAG), { model: PI_TAG })
  const notes = suggestionNotes(apply(pi, applySuggestion(pi, qwen)))
  assert.equal(notes.effortNote, null)
  assert.match(notes.contextHint ?? '', /262144/)
})

// §4.7, every row. URL and context are kept in each.
const CODEX_EFFORTS = ['', 'none', 'minimal', 'low', 'medium', 'high', 'xhigh', 'max']

test('§4.7 Codex local → Pi: <server>/<tag> → <tag>; none/minimal → off, anything else → ""', () => {
  for (const model of ['ollama/qwen3.8:27b', 'lmstudio/qwen3.8:27b', 'OLLAMA/qwen3.8:27b']) {
    for (const effort of CODEX_EFFORTS) {
      const codex = edits({ model, localServer: splitCodexLocalModel(model)!.server, effort })
      const pi = apply(codex, switchProvider(codex, 'pi'))
      const label = `${model} ${effort}`
      assert.equal(pi.provider, 'pi', label)
      assert.equal(pi.localMode, true, label)
      assert.equal(pi.model, 'qwen3.8:27b', label)
      assert.equal(pi.effort, effort === 'none' || effort === 'minimal' ? 'off' : '', label)
      assert.equal(pi.localBaseUrl, ORIGIN, label)
      assert.equal(pi.contextWindow, '131072', label)
    }
  }
  // A6's PUT is exactly what the dashboard sends after the switch.
  const codex = edits({ model: 'ollama/qwen3.8:27b', effort: 'none' })
  const pi = apply(codex, switchProvider(codex, 'pi'))
  assert.deepEqual({ provider: pi.provider, model: pi.model, effort: pi.effort }, { provider: 'pi', model: 'qwen3.8:27b', effort: 'off' })
  assert.deepEqual(localModelSaveBody(pi), { ok: true, body: { model: 'qwen3.8:27b', localBaseUrl: ORIGIN, contextWindow: 131072 } })
  // A legacy Env Ref row has no URL: Pi needs one entered first.
  const legacy = edits({ localBaseUrl: '', envRefs: 'CODEX_OSS_BASE_URL' })
  assert.deepEqual(localModelSaveBody(apply(legacy, switchProvider(legacy, 'pi'))), { ok: false, error: PI1_TEXT })
})

test('§4.7 Claude local → Pi: model unchanged; off → off, anything else → ""', () => {
  for (const effort of ['', 'off', 'low', 'medium', 'xhigh']) {
    const claude = edits({ provider: 'claude', model: 'qwen3.8:27b', effort })
    const pi = apply(claude, switchProvider(claude, 'pi'))
    assert.equal(pi.provider, 'pi', effort)
    assert.equal(pi.localMode, true, effort)
    assert.equal(pi.model, 'qwen3.8:27b', effort)
    assert.equal(pi.effort, effort === 'off' ? 'off' : '', effort)
    assert.equal(pi.localBaseUrl, ORIGIN, effort)
    assert.equal(pi.contextWindow, '131072', effort)
  }
})

test('§4.7 Pi → Claude local: model unchanged; off → off; on/"" → ""', () => {
  for (const [effort, expected] of [['off', 'off'], ['on', ''], ['', '']]) {
    const pi = piEdits({ effort })
    const claude = apply(pi, switchProvider(pi, 'claude'))
    assert.equal(claude.provider, 'claude', effort)
    assert.equal(claude.localMode, true, effort)
    assert.equal(claude.model, PI_TAG, effort)
    assert.equal(claude.effort, expected, effort)
    assert.equal(claude.localBaseUrl, PI_ORIGIN, effort)
    assert.equal(claude.contextWindow, '131072', effort)
    assert.ok(localModelSaveBody(claude).ok, effort)
  }
})

test('§4.7 Pi → Codex local: <tag> → ollama/<tag>; off → none; on/"" → ""', () => {
  for (const [effort, expected] of [['off', 'none'], ['on', ''], ['', '']]) {
    const pi = piEdits({ effort, localServer: 'lmstudio' })
    const codex = apply(pi, switchProvider(pi, 'codex'))
    assert.equal(codex.provider, 'codex', effort)
    assert.equal(codex.localMode, true, effort)
    assert.equal(codex.model, `ollama/${PI_TAG}`, effort)
    assert.equal(codex.localServer, 'ollama', effort)
    assert.equal(codex.effort, expected, effort)
    assert.equal(codex.localBaseUrl, PI_ORIGIN, effort)
    assert.equal(codex.contextWindow, '131072', effort)
  }
  // An empty tag stays empty so Save asks for one.
  const empty = piEdits({ model: '' })
  assert.equal(apply(empty, switchProvider(empty, 'codex')).model, '')
})

test('§4.7 round trips: Codex → Pi → Codex drops only the server choice; Claude → Pi → Claude is lossless for off', () => {
  const codex = edits({ model: 'lmstudio/qwen3.8:27b', localServer: 'lmstudio', effort: 'minimal' })
  const viaPi = apply(codex, switchProvider(codex, 'pi'))
  const back = apply(viaPi, switchProvider(viaPi, 'codex'))
  assert.deepEqual([back.model, back.effort, back.localBaseUrl, back.contextWindow], ['ollama/qwen3.8:27b', 'none', ORIGIN, '131072'])

  const claude = edits({ provider: 'claude', model: 'qwen3.8:27b', effort: 'off' })
  const claudeViaPi = apply(claude, switchProvider(claude, 'pi'))
  const claudeBack = apply(claudeViaPi, switchProvider(claudeViaPi, 'claude'))
  assert.deepEqual([claudeBack.model, claudeBack.effort, claudeBack.localBaseUrl], ['qwen3.8:27b', 'off', ORIGIN])
})

test('Cloud → Pi starts like Cloud → Local; Pi → Gemini clears the URL and context', () => {
  const cloud = edits({ provider: 'codex', localMode: false, model: 'gpt-5', localBaseUrl: '', contextWindow: '', effort: 'high' })
  const pi = apply(cloud, switchProvider(cloud, 'pi'))
  assert.deepEqual([pi.provider, pi.localMode, pi.model, pi.localBaseUrl, pi.effort], ['pi', true, '', '', ''])
  assert.deepEqual(localModelSaveBody(pi), { ok: false, error: PI1_TEXT })
  const gemini = edits({ provider: 'gemini', localMode: false, model: 'gemini-2.5-flash', localBaseUrl: '', contextWindow: '', effort: '' })
  assert.deepEqual(switchProvider(gemini, 'pi'),
    { provider: 'pi', localMode: true, model: '', localBaseUrl: '', localServer: 'ollama', effort: '' })

  const fromPi = apply(piEdits({ effort: 'on' }), switchProvider(piEdits({ effort: 'on' }), 'gemini'))
  assert.deepEqual([fromPi.provider, fromPi.localMode, fromPi.model, fromPi.localBaseUrl, fromPi.contextWindow, fromPi.effort],
    ['gemini', false, PROVIDER_DEFAULT_MODEL.gemini, '', '', ''])
  // The stale on does not resurface on a later gemini → claude switch.
  assert.equal(apply(fromPi, switchProvider(fromPi, 'claude')).effort, '')
  assert.deepEqual(switchProvider(piEdits(), 'pi'), {})
})

test('create form: Pi starts with an empty tag and a §4.7 effort; leaving Pi drops off/on, URL and context', () => {
  assert.deepEqual(createFormProviderPatch({ provider: 'codex', effort: 'none' }, 'pi'), { provider: 'pi', model: '', effort: 'off' })
  assert.deepEqual(createFormProviderPatch({ provider: 'claude', effort: 'high' }, 'pi'), { provider: 'pi', model: '', effort: '' })
  assert.deepEqual(createFormProviderPatch({ provider: 'pi', effort: 'off' }, 'claude'),
    { provider: 'claude', model: PROVIDER_DEFAULT_MODEL.claude, effort: '', localBaseUrl: '', contextWindow: '' })
  assert.deepEqual(createFormProviderPatch({ provider: 'claude', effort: 'high' }, 'codex'), { provider: 'codex', model: PROVIDER_DEFAULT_MODEL.codex })
})

// ── The production modals, rendered (as commsJournal.test.ts does) ────────────

type Modules = Record<string, unknown>

/** Compiles a production component file to CommonJS and runs it, resolving its imports from `modules`. */
function loadComponent(file: string, modules: Modules): Record<string, unknown> {
  const text = readFileSync(new URL(file, import.meta.url), 'utf8')
  const compiled = ts.transpileModule(text, {
    compilerOptions: { jsx: ts.JsxEmit.ReactJSX, module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2020 },
  }).outputText
  const nodeRequire = createRequire(import.meta.url)
  const exports: Record<string, unknown> = {}
  new Function('require', 'exports', compiled)((id: string) => (id in modules ? modules[id] : nodeRequire(id)), exports)
  return exports
}

const stub = (marker: string) => ({ default: () => createElement('div', { 'data-stub': marker }) })
const FieldHintModule = loadComponent('./components/FieldHint.tsx', {})
const LocalModelFieldsModule = loadComponent('./components/LocalModelFields.tsx', {
  '../contextWindow': contextWindowModule, '../localModel': localModel, './FieldHint': FieldHintModule,
})
const AgentConfigModal = loadComponent('./components/AgentConfigModal.tsx', {
  '../constants': constants, '../journalCrossChat': journalCrossChat, '../localModel': localModel,
  '../utils': { apiFetch: () => new Promise(() => {}) }, './ModelSelector': stub('model-selector'),
  './FieldHint': FieldHintModule, './LocalModelFields': LocalModelFieldsModule,
  './InstructionPicker': stub('instruction-picker'), './ProjectAssignmentTable': stub('project-table'),
}).default as React.ComponentType<Record<string, unknown>>
const CreateAgentModal = loadComponent('./components/CreateAgentModal.tsx', {
  '../constants': constants, '../localModel': localModel, './ModelSelector': stub('model-selector'),
  './FieldHint': FieldHintModule, './InstructionPicker': stub('instruction-picker'),
}).default as React.ComponentType<Record<string, unknown>>

function configEdits(overrides: Partial<ConfigEdits>): ConfigEdits {
  return {
    model: PI_TAG, memoryLimitMb: '4096', isEnabled: true, image: '', permissionMode: 'acceptEdits', maxTurns: '50',
    workDir: '/workspace', proactiveIntervalMinutes: '0', groupListenMode: 'mention', groupDebounceSeconds: '15',
    warmupTimeoutSeconds: '60', shortName: 'pi-dev', displayName: 'pi-dev', showStats: true, prefixMessages: false,
    formattingMode: 0, suppressToolMessages: false, effort: '', jsonSchema: '', agentsJson: '', autoMemoryEnabled: true,
    journalEnabled: false, journalCrossChatEnabled: false, hostPort: '', telegramSendOnly: false, provider: 'pi',
    codexSandboxMode: '', tools: '', projects: [], networks: '', envRefs: '', mcpEndpoints: [], telegramUsers: '',
    telegramGroups: '', canReceiveChatRequests: false, requestReceivedMessage: '', mountDockerSock: false,
    outputStyle: '', localMode: true, localBaseUrl: PI_ORIGIN, localServer: 'ollama', contextWindow: '131072',
    instructions: [], ...overrides,
  }
}

/** The config modal with advanced settings shown (they hold Effort and the Claude/Codex-only fields). */
function renderConfig(e: ConfigEdits): string {
  const global = globalThis as unknown as { localStorage?: unknown }
  global.localStorage = { getItem: () => 'true', setItem: () => {} }
  try {
    const noop = () => {}
    return renderToStaticMarkup(createElement(AgentConfigModal, {
      agentName: 'pi-dev', configData: { provider: e.provider, tools: [] } as unknown as AgentConfig, configEdits: e,
      configSaveState: 'idle', configSaveMsg: '', configLoading: false, configReprovisionConfirm: false,
      allInstructions: [], outputStyles: [], projectContexts: [], promptSizeLimits: null, projectAccess: null,
      projectAccessLoading: false, onEditsChange: noop, onSave: noop, onReprovisionConfirmToggle: noop,
      onProjectAccessAdd: noop, onProjectAccessRemove: noop, onProjectAccessToggleWildcard: noop, onClose: noop,
    }))
  } finally {
    delete global.localStorage
  }
}

// React escapes the apostrophes in "server's".
const PI3_WARNING = `⚠ ${PI3_TEXT.replace(/'/g, '&#x27;')}`

const CLAUDE_OR_CODEX_ONLY = ['Permission Mode', 'Max Turns', 'JSON Schema', 'Agents JSON', 'Auto Memory', 'Sandbox Mode', 'LM Studio']

test('config modal, pi: option listed, Runs on locked to Local, Claude/Codex-only fields hidden, Pi effort', () => {
  const html = renderConfig(configEdits({}))
  assert.match(html, /<option value="pi" selected="">Pi \(local model\)<\/option>/)
  assert.match(html, new RegExp(`<button[^>]*aria-pressed="false"[^>]*disabled=""[^>]*title="${PI_RUNS_ON_NOTE}"[^>]*>Cloud</button>`))
  assert.match(html, /<button[^>]*class="model-pill active"[^>]*aria-pressed="true"[^>]*>Local server<\/button>/)
  assert.ok(html.includes(`<p class="field-hint">${PI_RUNS_ON_NOTE}</p>`))
  assert.ok(html.includes(`value="${PI_ORIGIN}"`))
  assert.ok(html.includes(`value="${PI_TAG}"`))
  for (const label of CLAUDE_OR_CODEX_ONLY) assert.equal(html.includes(label), false, label)
  assert.equal(html.includes('data-stub="model-selector"'), false, 'no cloud model picker')
  assert.match(html, /<option value="" selected="">Default \(on\)<\/option><option value="off">Off<\/option><option value="on">On<\/option><\/select>/)
  assert.equal(html.includes('not valid in this mode'), false)
  assert.equal(html.includes(PI3_WARNING), false, 'no PI3 warning at 131072')
})

test('config modal, pi: a context below 65536 shows the PI3 text', () => {
  for (const contextWindow of ['', '65535']) assert.ok(renderConfig(configEdits({ contextWindow })).includes(PI3_WARNING), contextWindow)
  assert.equal(renderConfig(configEdits({ contextWindow: '65536' })).includes(PI3_WARNING), false)
})

test('config modal, Codex local → Pi switch renders the normalized tag and effort', () => {
  const codex = configEdits({ provider: 'codex', model: 'ollama/qwen3.8:27b', effort: 'none', localServer: 'ollama' })
  const codexHtml = renderConfig(codex)
  assert.ok(codexHtml.includes('LM Studio'), 'Codex shows its server select')
  assert.ok(codexHtml.includes('Sandbox Mode'))
  const pi = { ...codex, ...switchProvider({ ...codex, provider: 'codex' }, 'pi') }
  const html = renderConfig(pi)
  assert.ok(html.includes('value="qwen3.8:27b"'))
  assert.match(html, /<option value="off" selected="">Off<\/option>/)
  assert.ok(html.includes(`value="${PI_ORIGIN}"`))
  assert.equal(html.includes('LM Studio'), false)
  assert.equal(html.includes('Sandbox Mode'), false)
})

function createForm(overrides: Partial<CreateForm>): CreateForm {
  return {
    name: 'pi-dev', displayName: '', role: 'developer', model: '', containerName: '', memoryLimitMb: '4096',
    isEnabled: true, image: '', permissionMode: 'acceptEdits', maxTurns: '50', workDir: '/workspace',
    proactiveIntervalMinutes: '0', groupListenMode: 'mention', groupDebounceSeconds: '15', warmupTimeoutSeconds: '60',
    shortName: 'pi-dev', effort: '', jsonSchema: '', agentsJson: '', autoMemoryEnabled: true, showStats: true,
    prefixMessages: false, formattingMode: 0, suppressToolMessages: false, telegramSendOnly: false, provider: 'pi',
    codexSandboxMode: '', tools: '', projects: '', networks: '', envRefs: '', mcpEndpoints: [], telegramUsers: '',
    telegramGroups: '', instructions: [], localBaseUrl: '', contextWindow: '', ...overrides,
  }
}

function renderCreate(form: CreateForm): string {
  const noop = () => {}
  return renderToStaticMarkup(createElement(CreateAgentModal, {
    createForm: form, createState: 'idle', createMsg: '', agentNames: [], copyFrom: '', copyFromLoading: false,
    allInstructions: [], onCopyFrom: noop, onFormChange: noop, onSubmit: noop, onClose: noop,
  }))
}

test('create modal: pi option listed; pi takes a bare tag with the Qwen3.8 chips and the local-only note', () => {
  const claude = renderCreate(createForm({ provider: 'claude', model: PROVIDER_DEFAULT_MODEL.claude }))
  assert.match(claude, /<option value="pi">Pi \(local model\)<\/option>/)
  assert.ok(claude.includes('data-stub="model-selector"'))

  const html = renderCreate(createForm({ model: PI_TAG }))
  assert.match(html, /<option value="pi" selected="">Pi \(local model\)<\/option>/)
  assert.equal(html.includes('data-stub="model-selector"'), false)
  assert.ok(html.includes('Model tag'))
  assert.ok(html.includes(PI_RUNS_ON_NOTE))
  assert.ok(html.includes(`value="${PI_TAG}"`))
  assert.ok(html.includes('>Qwen3.8 27B</button>'))
  assert.ok(html.includes('>Qwen3.8 Flash Next</button>'))
  assert.equal(html.includes('gpt-oss'), false)
})

// ── #386 H1: a Pi agent is created complete — URL, tag and context, checked before the request ──

test('create body: Pi needs the URL (PI1), the tag, then a context of at least 65536 (PI3)', () => {
  const create = localModel.createFormLocalModel
  const pi = { provider: 'pi', model: PI_TAG, localBaseUrl: PI_ORIGIN, contextWindow: '131072' }
  assert.deepEqual(create({ ...pi, localBaseUrl: '' }), { ok: false, error: PI1_TEXT })
  assert.deepEqual(create({ ...pi, model: '' }), { ok: false, error: localModel.LOCAL_TAG_REQUIRED })
  assert.deepEqual(create({ ...pi, contextWindow: '' }), { ok: false, error: localModel.PI3_TEXT })
  assert.deepEqual(create({ ...pi, contextWindow: '65535' }), { ok: false, error: localModel.PI3_TEXT })
  assert.deepEqual(create(pi), { ok: true, body: { localBaseUrl: PI_ORIGIN, contextWindow: 131072 } })
  // Every other provider's create request is unchanged.
  assert.deepEqual(create({ ...pi, provider: 'claude', localBaseUrl: '', contextWindow: '' }), { ok: true, body: {} })
})

test('create modal: Pi asks for the server URL and context; Create is blocked with the rule text until complete', () => {
  const incomplete = renderCreate(createForm({ model: PI_TAG }))
  assert.ok(incomplete.includes('Server URL'))
  assert.ok(incomplete.includes('Context window'))
  assert.ok(incomplete.includes(PI1_TEXT))
  assert.match(incomplete, /<button class="config-save-btn" disabled="">Create<\/button>/)

  const noContext = renderCreate(createForm({ model: PI_TAG, localBaseUrl: PI_ORIGIN, contextWindow: '65535' }))
  const pi3Html = localModel.PI3_TEXT.split("'").join('&#x27;') // renderToStaticMarkup escapes the apostrophe
  assert.ok(noContext.includes(pi3Html))
  assert.match(noContext, /<button class="config-save-btn" disabled="">Create<\/button>/)

  const complete = renderCreate(createForm({ model: PI_TAG, localBaseUrl: PI_ORIGIN, contextWindow: '131072' }))
  assert.equal(complete.includes(PI1_TEXT), false)
  assert.equal(complete.includes(pi3Html), false)
  assert.match(complete, /<button class="config-save-btn">Create<\/button>/)

  // Other providers never show the Pi fields or the block.
  const claude = renderCreate(createForm({ provider: 'claude', model: PROVIDER_DEFAULT_MODEL.claude }))
  assert.equal(claude.includes('Server URL'), false)
  assert.match(claude, /<button class="config-save-btn">Create<\/button>/)
})
