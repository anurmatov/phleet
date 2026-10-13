import type { CodexLocalServer } from '../types'
import { CONTEXT_WINDOW_MAX, CONTEXT_WINDOW_MIN } from '../contextWindow'
import {
  LEGACY_CODEX_NOTICE, PI_MIN_CONTEXT_WINDOW, PI_RUNS_ON_NOTE, applySuggestion, localModelSuggestions, localModelTag,
  piContextFault, runsOnLocked, setLocalServer, setLocalTag, showLegacyCodexNotice, suggestionNotes, switchRunsOn,
  v1PathHint,
} from '../localModel'
import type { LocalModelEdits, LocalModelPatch } from '../localModel'
import FieldHint from './FieldHint'

const SERVER_LABELS: Record<CodexLocalServer, string> = { ollama: 'Ollama', lmstudio: 'LM Studio' }

interface LocalModelFieldsProps {
  /** Config edits with the provider resolved; only claude, codex and pi render this. */
  edits: LocalModelEdits
  onEditsChange: (patch: LocalModelPatch) => void
}

/**
 * "Runs on" toggle and, in Local server mode, the server URL, server software (Codex), model tag
 * with suggestion chips, and context window (#382). Pi (#386) is locked to Local server and needs a
 * context window of at least 65536. The cloud model picker is the caller's.
 */
export default function LocalModelFields({ edits, onEditsChange }: LocalModelFieldsProps) {
  const isCodex = edits.provider === 'codex'
  const locked = runsOnLocked(edits.provider)
  const localMode = locked || edits.localMode
  const tag = localModelTag(edits.provider, edits.model)
  const v1Hint = v1PathHint(edits.localBaseUrl)
  const { effortNote, contextHint } = suggestionNotes(edits)
  const piContext = locked ? piContextFault(edits.contextWindow) : null

  return (
    <>
      <div className="config-field">
        <label className="config-label">Runs on</label>
        {locked && <FieldHint>{PI_RUNS_ON_NOTE}</FieldHint>}
        <div className="model-pills">
          {([false, true] as const).map(local => (
            <button
              key={String(local)}
              type="button"
              className={`model-pill${localMode === local ? ' active' : ''}`}
              aria-pressed={localMode === local}
              disabled={locked && !local}
              title={locked && !local ? PI_RUNS_ON_NOTE : undefined}
              onClick={() => onEditsChange(switchRunsOn(edits, local))}
            >
              {local ? 'Local server' : 'Cloud'}
            </button>
          ))}
        </div>
      </div>
      {localMode && (
        <>
          <div className="config-field">
            <label className="config-label">Server URL</label>
            <FieldHint>Origin only; Fleet adds the API path.</FieldHint>
            <input
              className="config-input"
              value={edits.localBaseUrl}
              onChange={e => onEditsChange({ localBaseUrl: e.target.value })}
              placeholder="http://<server-address>:11434"
            />
            {v1Hint && <div className="size-warning-notice">⚠ {v1Hint}</div>}
            {showLegacyCodexNotice(edits) && <div className="size-warning-notice">{LEGACY_CODEX_NOTICE}</div>}
          </div>
          {isCodex && (
            <div className="config-field">
              <label className="config-label">Server</label>
              <select
                className="config-input"
                value={edits.localServer}
                onChange={e => onEditsChange(setLocalServer(edits, e.target.value as CodexLocalServer))}
              >
                {(Object.keys(SERVER_LABELS) as CodexLocalServer[]).map(s => (
                  <option key={s} value={s}>{SERVER_LABELS[s]}</option>
                ))}
              </select>
            </div>
          )}
          <div className="config-field">
            <label className="config-label">Model tag</label>
            {locked && <FieldHint>The bare tag the server serves, with no ollama/ or lmstudio/ prefix.</FieldHint>}
            <div className="model-selector">
              <input
                className="config-input"
                value={tag}
                onChange={e => onEditsChange(setLocalTag(edits, e.target.value))}
                placeholder="the server's model tag"
              />
              <div className="model-pills">
                {localModelSuggestions(edits.provider).map(s => (
                  <button
                    key={s.tag}
                    type="button"
                    className={`model-pill${tag.trim() === s.tag ? ' active' : ''}`}
                    title={s.tag}
                    onClick={() => onEditsChange(applySuggestion(edits, s))}
                  >
                    {s.label}
                  </button>
                ))}
              </div>
              {effortNote && <div className="model-selector-note">{effortNote}</div>}
            </div>
          </div>
          <div className="config-field">
            <label className="config-label">Context window (tokens)</label>
            <FieldHint>
              {locked
                ? `Your server's --ctx, at least ${PI_MIN_CONTEXT_WINDOW}. Takes effect on reprovision.`
                : "Your server's context size (e.g. num_ctx / --ctx). Takes effect on reprovision."}
            </FieldHint>
            {contextHint && <FieldHint>{contextHint}</FieldHint>}
            <input
              className="config-input config-input-short"
              type="number"
              min={locked ? PI_MIN_CONTEXT_WINDOW : CONTEXT_WINDOW_MIN}
              max={CONTEXT_WINDOW_MAX}
              step={1}
              value={edits.contextWindow}
              onChange={e => onEditsChange({ contextWindow: e.target.value })}
              placeholder="e.g. 131072"
            />
            {piContext && <div className="size-warning-notice">⚠ {piContext}</div>}
            {!locked && edits.contextWindow.trim() === '' && (
              <div className="size-warning-notice">⚠ Not set: the agent may not compact before the server rejects the prompt.</div>
            )}
          </div>
        </>
      )}
    </>
  )
}
