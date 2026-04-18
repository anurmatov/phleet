import { useState, useEffect } from 'react'
import { PROVIDER_MODELS } from '../constants'

const PILL_LABELS: Record<string, string> = {
  'claude-sonnet-4-6': 'sonnet 4.6',
  'claude-opus-4-7': 'opus 4.7',
  'claude-opus-4-6': 'opus 4.6',
  'claude-haiku-4-5-20251001': 'haiku 4.5',
  'gpt-5': 'gpt-5',
  'gpt-5.4': 'gpt-5.4',
  'gpt-5.4-mini': 'gpt-5.4-mini',
  'gpt-5.3-codex': 'gpt-5.3-codex',
  'codex-mini-latest': 'codex-mini',
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

  const [customMode, setCustomMode] = useState(!isKnown && models.length > 0)

  // When the model value changes to a known model (e.g. provider reset), exit custom mode
  useEffect(() => {
    if (models.includes(value)) setCustomMode(false)
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
