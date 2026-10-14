import { useState, useEffect } from 'react'
import { customModeFor, modelChoices } from '../modelChoices'

const PILL_LABELS: Record<string, string> = {
  'gemini-3-pro-preview': '3 pro preview',
  'gemini-3-flash-preview': '3 flash preview',
  'gemini-2.5-pro': '2.5 pro',
  'gemini-2.5-flash': '2.5 flash',
  'gemini-2.5-flash-lite': '2.5 flash-lite',
  'gemini-2.0-flash': '2.0 flash',
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
