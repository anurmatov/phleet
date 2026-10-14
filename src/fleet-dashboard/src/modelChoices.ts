import { ALIAS_ONLY_PROVIDERS, PROVIDER_MODELS } from './constants.ts'

// The cloud model picker's choices (#460, owner direction). Claude and Codex offer family aliases only
// and no free text; a stored value outside them (a dated, hosted or other id) is shown as the current
// choice until someone picks an alias, and is never rewritten. Gemini keeps its list and free text.
// Kept free of JSX so `node --test` can run it.

export interface ModelChoices {
  /** The aliases (or Gemini's model list), in display order. */
  options: string[]
  /** The stored value when it is not one of `options`, shown as-is; else null. */
  current: string | null
  /** Whether a free-text model may be typed. */
  freeText: boolean
}

export function modelChoices(provider: string, value: string): ModelChoices {
  const options = PROVIDER_MODELS[provider] ?? []
  return {
    options,
    current: value !== '' && !options.includes(value) ? value : null,
    freeText: !ALIAS_ONLY_PROVIDERS.includes(provider),
  }
}
