import { useState, useEffect } from 'react'
import { PROVIDER_MODELS } from '../constants'
import { modelChoices } from '../modelChoices'

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
  const models = PROVIDER_MODELS[provider] ?? []
  const isKnown = models.includes(value)
  const choices = modelChoices(provider, value)

  const [customMode, setCustomMode] = useState(!isKnown && models.length > 0)

  // When the model value changes to a known model (e.g. provider reset), exit custom mode
  useEffect(() => {
    if (models.includes(value)) setCustomMode(false)
  }, [value, models])

  // #460: Claude and Codex offer family aliases only. A stored dated or hosted id stays selected, as is,
  // until an alias is picked.
  if (!choices.freeText) {
    return (
      <div className="model-selector">
        <select className={`config-input ${className ?? ''}`} value={value} onChange={e => onChange(e.target.value)}>
          {choices.options.map(m => <option key={m} value={m}>{m}</option>)}
          {choices.current !== null && <option value={choices.current}>{choices.current} (current)</option>}
        </select>
        <div className="model-pills">
          {choices.options.map(m => (
            <button key={m} type="button" className={`model-pill${value === m ? ' active' : ''}`} onClick={() => onChange(m)}>
              {m}
            </button>
          ))}
        </div>
      </div>
    )
  }

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
        <option value="">— custom —</option>
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
