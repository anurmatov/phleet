import type { AgentConfig, CodexLocalServer, ConfigEdits, LocalModelSuggestion } from './types'
import { LOCAL_MODEL_SUGGESTIONS, PROVIDER_DEFAULT_MODEL } from './constants.ts'
import { parseContextWindow } from './contextWindow.ts'

// Local model mode for the Claude and Codex harnesses (#382) and the Pi harness (#386). "Runs on:
// Local server" is a where-it-runs choice inside the chosen harness: one server URL, a model tag and
// a context window. A Codex local model is stored as `<server>/<tag>`; a Claude or Pi local model is
// the bare tag. Pi has no cloud: it is always local.
// Kept free of JSX and `import.meta.env` so `node --test` can run it.

export const CODEX_LOCAL_SERVERS: CodexLocalServer[] = ['ollama', 'lmstudio']

/** PI3's minimum context window; mirrors `PiLocalModel.MinContextWindow`. */
export const PI_MIN_CONTEXT_WINDOW = 65536

/** The "Runs on" note for a Pi agent, whose Runs on is locked to Local server. */
export const PI_RUNS_ON_NOTE = 'Pi runs only on a local server'

// The server's fault texts (`PiLocalModel.Pi1Text`, `Pi3Text`), shown before Save sends anything.
export const PI1_TEXT = 'Pi runs only on a local server; set the local server URL.'
export const PI3_TEXT = "Pi needs the server's context size, at least 65536; set the context window to the server's --ctx."

/** PI4: '' (the default) and `on` run thinking on; `off` runs it off. */
export const PI_EFFORT_CHOICES = ['', 'off', 'on']

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
  return provider === 'claude' || provider === 'codex' || provider === 'pi'
}

export function isPi(provider: string): boolean {
  return provider === 'pi'
}

/** Pi has no cloud mode, so its "Runs on" is locked to Local server. */
export function runsOnLocked(provider: string): boolean {
  return isPi(provider)
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

/**
 * Local when claude or codex has a URL, or codex has a local model (the legacy Env Ref row). Pi
 * always, URL or not (an empty URL is PI1 at Save). Gemini never.
 */
export function isLocalMode(provider: string, localBaseUrl: string, model: string): boolean {
  if (runsOnLocked(provider)) return true
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

/** The model tag the form shows: the stored model for Claude and Pi, the part after `<server>/` for Codex. */
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
 * URL and the context window and resets the model; effort is left alone (#349 marker rule). Pi is
 * locked to Local server: nothing changes.
 */
export function switchRunsOn(edits: LocalModelEdits, local: boolean): LocalModelPatch {
  if (runsOnLocked(edits.provider) || local === edits.localMode) return {}
  return local
    ? { localMode: true, model: '', localBaseUrl: '', localServer: 'ollama' }
    : { localMode: false, model: PROVIDER_DEFAULT_MODEL[edits.provider] ?? '', localBaseUrl: '', contextWindow: '' }
}

/**
 * §4.7: the Pi effort for a move to Pi. From Codex, `none`/`minimal` → `off`; from Claude (or any
 * other harness), `off` → `off`. Anything else → '' (the default, thinking on).
 */
export function effortToPi(from: string, effort: string): string {
  if (from === 'codex') return effort === 'none' || effort === 'minimal' ? 'off' : ''
  return effort === 'off' ? 'off' : ''
}

/** §4.7: the effort for a move from Pi. `off` → `off` on Claude, `none` on Codex; `on`/'' → ''. */
export function effortFromPi(next: string, effort: string): string {
  if (effort !== 'off') return ''
  return next === 'codex' ? 'none' : 'off'
}

/**
 * Provider change. Between local harnesses (Claude, Codex, Pi) the URL and context are kept and the
 * stored model is normalized: `<server>/` is stripped leaving Codex, `ollama/<tag>` is composed
 * entering it, and Claude and Pi keep the bare tag. A move to or from Pi also maps effort (§4.7);
 * Claude ↔ Codex leaves it to the select's marker (#349). Cloud → Pi starts like Cloud → Local.
 * Gemini has no local mode, so switching to it clears the URL and context. Otherwise the model
 * resets as before.
 */
export function switchProvider(edits: LocalModelEdits, next: string): LocalModelPatch {
  if (next === edits.provider) return {}
  if ((edits.localMode || runsOnLocked(edits.provider)) && isLocalHarness(next)) {
    const tag = localModelTag(edits.provider, edits.model)
    const patch: LocalModelPatch = { provider: next, localMode: true, localServer: 'ollama', model: modelForTag(next, 'ollama', tag) }
    if (isPi(next)) return { ...patch, effort: effortToPi(edits.provider, edits.effort) }
    if (isPi(edits.provider)) return { ...patch, effort: effortFromPi(next, edits.effort) }
    return patch
  }
  if (isPi(next)) {
    return {
      provider: next, localMode: true, model: '', localBaseUrl: '', localServer: 'ollama',
      effort: effortToPi(edits.provider, edits.effort),
    }
  }
  const patch: LocalModelPatch = { provider: next, localMode: false, model: PROVIDER_DEFAULT_MODEL[next] ?? '' }
  return next === 'gemini' ? { ...patch, localBaseUrl: '', contextWindow: '' } : patch
}

/**
 * The create form's provider change: the provider's default model. Pi has none, so it starts with
 * an empty tag, and an effort copied from another agent is mapped as in §4.7. The create form has
 * no local mode, so leaving Pi (for a cloud model) drops its off/on effort.
 */
export function createFormProviderPatch(
  form: { provider: string; effort: string }, next: string,
): { provider: string; model: string; effort?: string; localBaseUrl?: string; contextWindow?: string } {
  const patch = { provider: next, model: PROVIDER_DEFAULT_MODEL[next] ?? '' }
  if (next === form.provider) return patch
  if (isPi(next)) return { ...patch, effort: effortToPi(form.provider, form.effort) }
  return isPi(form.provider) ? { ...patch, effort: '', localBaseUrl: '', contextWindow: '' } : patch
}

export type CreateLocalModel =
  | { ok: true; body: { localBaseUrl?: string; contextWindow?: number } }
  | { ok: false; error: string }

/**
 * The local-model part of the create request (#386 H1). A Pi agent is created complete: URL
 * (PI1), tag, then context (PI3), checked as the config modal's Save checks them, so it can
 * provision at once. Every other provider sends nothing here, as before.
 */
export function createFormLocalModel(
  form: { provider: string; model: string; localBaseUrl: string; contextWindow: string },
): CreateLocalModel {
  if (!isPi(form.provider)) return { ok: true, body: {} }
  const save = piSaveBody({
    provider: form.provider, localMode: true, model: form.model, localBaseUrl: form.localBaseUrl,
    contextWindow: form.contextWindow, localServer: 'ollama', effort: '', envRefs: '',
  })
  return save.ok
    ? { ok: true, body: { localBaseUrl: save.body.localBaseUrl, contextWindow: save.body.contextWindow } }
    : save
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
  if (isPi(provider)) return [...PI_EFFORT_CHOICES]
  if (provider === 'codex') return ['', 'none', 'minimal', 'low', 'medium', 'high', 'xhigh', 'max']
  if (provider === 'claude' && localMode) return ['', 'off', 'low', 'medium', 'xhigh']
  return ['', 'low', 'medium', 'high', 'xhigh', 'max']
}

/** The effort select's options: `effortChoices` with labels; the default names what it runs as. */
export function effortOptions(provider: string, localMode: boolean): { value: string; label: string }[] {
  if (isPi(provider)) return [{ value: '', label: 'Default (on)' }, { value: 'off', label: 'Off' }, { value: 'on', label: 'On' }]
  const defaultLabel = provider === 'claude' && localMode ? 'default (xhigh)' : 'default'
  return effortChoices(provider, localMode).map(value => ({ value, label: value === '' ? defaultLabel : value }))
}

/**
 * PI3 as the form sees it: the text for an empty context window or a whole number below 65536, else
 * null. A value that is not a whole number, or is over the maximum, is `parseContextWindow`'s.
 */
export function piContextFault(contextWindow: string): string | null {
  const raw = contextWindow.trim()
  if (raw === '') return PI3_TEXT
  const value = Number(raw)
  return Number.isInteger(value) && value < PI_MIN_CONTEXT_WINDOW ? PI3_TEXT : null
}

export type LocalModelSave =
  | { ok: true; body: { model: string; localBaseUrl: string; contextWindow: number } }
  | { ok: false; error: string }

/** Pi is always local: URL (PI1), tag, then context (PI3) — the server's order. */
function piSaveBody(edits: LocalModelEdits): LocalModelSave {
  const localBaseUrl = edits.localBaseUrl.trim()
  if (localBaseUrl === '') return { ok: false, error: PI1_TEXT }
  const model = edits.model.trim()
  if (model === '') return { ok: false, error: LOCAL_TAG_REQUIRED }
  const contextFault = piContextFault(edits.contextWindow)
  if (contextFault) return { ok: false, error: contextFault }
  const contextWindow = parseContextWindow(edits.contextWindow)
  if (!contextWindow.ok) return contextWindow
  return { ok: true, body: { model, localBaseUrl, contextWindow: contextWindow.value } }
}

/**
 * The model, URL and context-window part of the config PUT. Sends `localBaseUrl` only — never the
 * `anthropicBaseUrl` alias. Claude local always needs a URL (empty means cloud); Codex local may
 * leave it empty only while the legacy Env Ref is present. Pi always needs a URL and a context
 * window of at least 65536.
 */
export function localModelSaveBody(edits: LocalModelEdits): LocalModelSave {
  if (isPi(edits.provider)) return piSaveBody(edits)
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
