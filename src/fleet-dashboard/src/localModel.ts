import type { AgentConfig, CodexLocalServer, ConfigEdits, LocalModelSuggestion } from './types'
import { LOCAL_MODEL_SUGGESTIONS, PROVIDER_DEFAULT_MODEL } from './constants.ts'
import { parseContextWindow } from './contextWindow.ts'

// Local model mode for the Claude and Codex harnesses (#382). "Runs on: Local server" is a
// where-it-runs choice inside the chosen harness: one server URL, a model tag and a context window.
// A Codex local model is stored as `<server>/<tag>`; a Claude local model is the bare tag.
// Kept free of JSX and `import.meta.env` so `node --test` can run it.

export const CODEX_LOCAL_SERVERS: CodexLocalServer[] = ['ollama', 'lmstudio']

/** The Env Ref a legacy Codex local agent reaches its server through. */
export const CODEX_OSS_ENV_REF = 'CODEX_OSS_BASE_URL'

export const LOCAL_URL_REQUIRED = 'Enter the local server URL'
export const LOCAL_TAG_REQUIRED = 'Enter the model tag'
export const LEGACY_CODEX_NOTICE =
  'Reaching the server through the CODEX_OSS_BASE_URL Env Ref (legacy). Enter the server URL here to manage it in Fleet; the Env Ref is then ignored for this agent and can be removed.'
export const V1_PATH_HINT = 'Remove /v1 — Fleet adds the right path for each harness'

/** The edit fields local mode reads and writes, with the provider already resolved. */
export type LocalModelEdits =
  Pick<ConfigEdits, 'model' | 'effort' | 'localMode' | 'localServer' | 'localBaseUrl' | 'contextWindow' | 'envRefs'>
  & { provider: string }

export type LocalModelPatch = Partial<LocalModelEdits>

export function isLocalHarness(provider: string): boolean {
  return provider === 'claude' || provider === 'codex'
}

/**
 * Mirrors CodexExecutor.SplitLocalModel exactly: split at the first `/`; local only when that
 * slash is neither the first nor the last character and the prefix is `ollama` or `lmstudio`
 * (any case). The tag keeps any later slashes. Null means a cloud or hosted model.
 */
export function splitCodexLocalModel(model: string): { server: CodexLocalServer; tag: string } | null {
  const slash = model.indexOf('/')
  if (slash <= 0 || slash === model.length - 1) return null
  const prefix = model.slice(0, slash).toLowerCase()
  const server = CODEX_LOCAL_SERVERS.find(s => s === prefix)
  return server ? { server, tag: model.slice(slash + 1) } : null
}

export function composeCodexLocalModel(server: CodexLocalServer, tag: string): string {
  return `${server}/${tag}`
}

/** Local when claude or codex has a URL, or codex has a local model (the legacy Env Ref row). Gemini never. */
export function isLocalMode(provider: string, localBaseUrl: string, model: string): boolean {
  if (!isLocalHarness(provider)) return false
  return localBaseUrl.trim() !== '' || (provider === 'codex' && splitCodexLocalModel(model) !== null)
}

/** Edit-form values from an API row. An older orchestrator returns only `anthropicBaseUrl`. */
export function localModelEditValues(
  cfg: Pick<AgentConfig, 'provider' | 'model' | 'localBaseUrl' | 'anthropicBaseUrl'>,
): Pick<ConfigEdits, 'localMode' | 'localBaseUrl' | 'localServer'> {
  const localBaseUrl = cfg.localBaseUrl ?? cfg.anthropicBaseUrl ?? ''
  return {
    localMode: isLocalMode(cfg.provider ?? 'claude', localBaseUrl, cfg.model),
    localBaseUrl,
    localServer: splitCodexLocalModel(cfg.model)?.server ?? 'ollama',
  }
}

/** The model tag the form shows: the stored model for Claude, the part after `<server>/` for Codex. */
export function localModelTag(provider: string, model: string): string {
  return provider === 'codex' ? splitCodexLocalModel(model)?.tag ?? '' : model
}

/** The stored model for a tag on this harness; an empty tag stays empty so Save can ask for one. */
function modelForTag(provider: string, server: CodexLocalServer, tag: string): string {
  return provider === 'codex' && tag !== '' ? composeCodexLocalModel(server, tag) : tag
}

export function setLocalTag(edits: LocalModelEdits, tag: string): LocalModelPatch {
  return { model: modelForTag(edits.provider, edits.localServer, tag) }
}

export function setLocalServer(edits: LocalModelEdits, server: CodexLocalServer): LocalModelPatch {
  return { localServer: server, model: modelForTag(edits.provider, server, localModelTag(edits.provider, edits.model)) }
}

/**
 * The "Runs on" toggle. Cloud → Local starts with an empty tag and URL. Local → Cloud clears the
 * URL and the context window and resets the model; effort is left alone (#349 marker rule).
 */
export function switchRunsOn(edits: LocalModelEdits, local: boolean): LocalModelPatch {
  if (local === edits.localMode) return {}
  return local
    ? { localMode: true, model: '', localBaseUrl: '', localServer: 'ollama' }
    : { localMode: false, model: PROVIDER_DEFAULT_MODEL[edits.provider] ?? '', localBaseUrl: '', contextWindow: '' }
}

/**
 * Provider change. Claude ↔ Codex while Local keeps the URL and context and normalizes the stored
 * model (Codex → Claude strips `<server>/`; Claude → Codex composes `ollama/<tag>`). Gemini has no
 * local mode, so switching to it clears the URL and context. Otherwise the model resets as before.
 */
export function switchProvider(edits: LocalModelEdits, next: string): LocalModelPatch {
  if (next === edits.provider) return {}
  if (edits.localMode && isLocalHarness(next)) {
    const tag = localModelTag(edits.provider, edits.model)
    return { provider: next, localServer: 'ollama', model: modelForTag(next, 'ollama', tag) }
  }
  const patch: LocalModelPatch = { provider: next, localMode: false, model: PROVIDER_DEFAULT_MODEL[next] ?? '' }
  return next === 'gemini' ? { ...patch, localBaseUrl: '', contextWindow: '' } : patch
}

export function localModelSuggestions(provider: string): LocalModelSuggestion[] {
  return LOCAL_MODEL_SUGGESTIONS.filter(s => (s.harnesses as string[]).includes(provider))
}

/** A chip fills the tag (and on Codex the recommended effort) — never the URL or context window. */
export function applySuggestion(edits: LocalModelEdits, suggestion: LocalModelSuggestion): LocalModelPatch {
  const patch = setLocalTag(edits, suggestion.tag)
  return edits.provider === 'codex' && suggestion.codexEffort
    ? { ...patch, effort: suggestion.codexEffort.value }
    : patch
}

/** Notes for the suggestion matching the current tag: its context hint, and the effort note while it holds. */
export function suggestionNotes(edits: LocalModelEdits): { effortNote: string | null; contextHint: string | null } {
  const tag = localModelTag(edits.provider, edits.model).trim()
  const match = localModelSuggestions(edits.provider).find(s => s.tag === tag)
  const effort = edits.provider === 'codex' ? match?.codexEffort : undefined
  return {
    effortNote: effort && edits.effort === effort.value ? effort.note : null,
    contextHint: match?.contextHint ?? null,
  }
}

/** Env Refs parsed exactly as Save parses them: split on `,`, trim, drop empties. */
export function parseEnvRefs(raw: string): string[] {
  return raw.split(',').map(r => r.trim()).filter(Boolean)
}

/** Exact, case-sensitive match on the legacy Env Ref. */
export function hasCodexOssEnvRef(envRefs: string): boolean {
  return parseEnvRefs(envRefs).includes(CODEX_OSS_ENV_REF)
}

/** Footnote 1: a Codex local agent with no URL still reaching its server through the legacy Env Ref. */
export function showLegacyCodexNotice(edits: LocalModelEdits): boolean {
  return edits.provider === 'codex' && edits.localMode && edits.localBaseUrl.trim() === ''
    && hasCodexOssEnvRef(edits.envRefs)
}

/** Shown for a URL ending in `/v1` or `/v1/`. Nothing is rewritten. */
export function v1PathHint(url: string): string | null {
  return /\/v1\/?$/.test(url.trim()) ? V1_PATH_HINT : null
}

/** Effort values for the select; '' is the harness default. Codex uses one list, local or cloud. */
export function effortChoices(provider: string, localMode: boolean): string[] {
  if (provider === 'codex') return ['', 'none', 'minimal', 'low', 'medium', 'high', 'xhigh', 'max']
  if (provider === 'claude' && localMode) return ['', 'off', 'low', 'medium', 'xhigh']
  return ['', 'low', 'medium', 'high', 'xhigh', 'max']
}

export type LocalModelSave =
  | { ok: true; body: { model: string; localBaseUrl: string; contextWindow: number } }
  | { ok: false; error: string }

/**
 * The model, URL and context-window part of the config PUT. Sends `localBaseUrl` only — never the
 * `anthropicBaseUrl` alias. Claude local always needs a URL (empty means cloud); Codex local may
 * leave it empty only while the legacy Env Ref is present.
 */
export function localModelSaveBody(edits: LocalModelEdits): LocalModelSave {
  const contextWindow = parseContextWindow(edits.contextWindow)
  if (!contextWindow.ok) return contextWindow
  if (!edits.localMode || !isLocalHarness(edits.provider)) {
    const model = edits.model.trim() || PROVIDER_DEFAULT_MODEL[edits.provider] || ''
    if (!model) return { ok: false, error: 'Model is required' }
    return { ok: true, body: { model, localBaseUrl: '', contextWindow: contextWindow.value } }
  }
  const localBaseUrl = edits.localBaseUrl.trim()
  if (localBaseUrl === '' && !(edits.provider === 'codex' && hasCodexOssEnvRef(edits.envRefs)))
    return { ok: false, error: LOCAL_URL_REQUIRED }
  // The edit state already holds the stored form (setLocalTag / switchProvider), so it is sent as is.
  const model = edits.model.trim()
  if (localModelTag(edits.provider, model).trim() === '') return { ok: false, error: LOCAL_TAG_REQUIRED }
  return { ok: true, body: { model, localBaseUrl, contextWindow: contextWindow.value } }
}
