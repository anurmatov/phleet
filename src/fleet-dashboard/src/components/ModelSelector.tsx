import { useState, useEffect } from 'react'
import { customModeFor, modelChoices } from '../modelChoices'

// The names `agy models` prints beside the Gemini slugs.
const PILL_LABELS: Record<string, string> = {
  'gemini-4-neon-high': 'Gemini 4 Neon (High)',
  'gemini-4-neon-xhigh': 'Gemini 4 Neon (XHigh)',
  'gemini-4-neon-medium': 'Gemini 4 Neon (Medium)',
  'gemini-4-neon-low': 'Gemini 4 Neon (Low)',
  'gemini-4-carbon-high': 'Gemini 4 Carbon (High)',
  'gemini-4-carbon-medium': 'Gemini 4 Carbon (Medium)',
  'gemini-4-carbon-low': 'Gemini 4 Carbon (Low)',
  'gemini-4-hydrogen-high': 'Gemini 4 Hydrogen (High)',
  'gemini-4-hydrogen-xhigh': 'Gemini 4 Hydrogen (XHigh)',
  'gemini-4-hydrogen-medium': 'Gemini 4 Hydrogen (Medium)',
  'gemini-4-hydrogen-low': 'Gemini 4 Hydrogen (Low)',
  'gemini-4-argon-high': 'Gemini 4 Argon (High)',
  'gemini-4-argon-medium': 'Gemini 4 Argon (Medium)',
  'gemini-4-argon-low': 'Gemini 4 Argon (Low)',
  'gemini-3.8-flash-high': 'Gemini 3.8 Flash (High)',
  'gemini-3.8-flash-medium': 'Gemini 3.8 Flash (Medium)',
  'gemini-3.8-flash-low': 'Gemini 3.8 Flash (Low)',
  'gemini-3.7-flash-high': 'Gemini 3.7 Flash (High)',
  'gemini-3.7-flash-medium': 'Gemini 3.7 Flash (Medium)',
  'gemini-3.7-flash-low': 'Gemini 3.7 Flash (Low)',
  'gemini-3.6-flash-high': 'Gemini 3.6 Flash (High)',
  'gemini-3.6-flash-medium': 'Gemini 3.6 Flash (Medium)',
  'gemini-3.6-flash-low': 'Gemini 3.6 Flash (Low)',
  'gemini-3.1-pro-high': 'Gemini 3.1 Pro (High)',
  'gemini-3.1-pro-low': 'Gemini 3.1 Pro (Low)',
}

interface ModelSelectorProps {
  provider: string
  value: string
  onChange: (model: string) => void
  className?: string
}

export default function ModelSelector({ provider, value, onChange, className }: ModelSelectorProps) {
  const choices = modelChoices(provider, value)
  const models = choices.options
  const isKnown = models.includes(value)

  // #465: a stored value outside the list (a dated or zai/ id), or none, opens in the custom input, as is.
  const [customMode, setCustomMode] = useState(customModeFor(value, models) ?? true)

  // Follow value changes: a listed model (e.g. provider reset) shows the select; any other id (e.g. a
  // create-modal copy-from of a dated or zai/ model) shows the custom input.
  useEffect(() => {
    const next = customModeFor(value, models)
    if (next !== null) setCustomMode(next)
  }, [value, models])

  if (customMode) {
    return (
      <div className="model-selector">
        <input
          className={`config-input ${className ?? ''}`}
          value={value}
          onChange={e => onChange(e.target.value)}
          placeholder="custom model name"
        />
        <div className="model-pills">
          {models.map(m => (
            <button
              key={m}
              type="button"
              className="model-pill"
              onClick={() => { onChange(m); setCustomMode(false) }}
            >
              {PILL_LABELS[m] ?? m}
            </button>
          ))}
        </div>
      </div>
    )
  }

  return (
    <div className="model-selector">
      <select
        className={`config-input ${className ?? ''}`}
        value={isKnown ? value : ''}
        onChange={e => {
          if (e.target.value === '') {
            setCustomMode(true)
          } else {
            onChange(e.target.value)
          }
        }}
      >
        {models.map(m => (
          <option key={m} value={m}>{m}</option>
        ))}
        <option value="">custom…</option>
      </select>
      <div className="model-pills">
        {models.map(m => (
          <button
            key={m}
            type="button"
            className={`model-pill${value === m ? ' active' : ''}`}
            onClick={() => onChange(m)}
          >
            {PILL_LABELS[m] ?? m}
          </button>
        ))}
      </div>
    </div>
  )
}
