// Run with `npm test` (node --test, Node ≥ 22.18 strips the types). Type-checked by `tsc -b`.
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { LOCAL_MODEL_SUGGESTIONS, PROVIDER_DEFAULT_MODEL, PROVIDER_MODELS } from './constants.ts'
import {
  LEGACY_CODEX_NOTICE, LOCAL_TAG_REQUIRED, LOCAL_URL_REQUIRED, V1_PATH_HINT, applySuggestion,
  composeCodexLocalModel, effortChoices, isLocalMode, localModelEditValues, localModelSaveBody,
  localModelSuggestions, localModelTag, setLocalServer, setLocalTag, showLegacyCodexNotice,
  splitCodexLocalModel, suggestionNotes, switchProvider, switchRunsOn, v1PathHint,
} from './localModel.ts'
import type { LocalModelEdits, LocalModelPatch } from './localModel.ts'

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

test('chips per harness: Qwen3.8 on both, gpt-oss on Codex only, none on Gemini', () => {
  assert.deepEqual(localModelSuggestions('claude').map(s => s.tag), ['qwen3.8:27b', 'qwen3.8-flash-next:125b-a6b-q4_K_M'])
  assert.deepEqual(localModelSuggestions('codex').map(s => s.tag),
    ['qwen3.8:27b', 'qwen3.8-flash-next:125b-a6b-q4_K_M', 'gpt-oss:20b'])
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
