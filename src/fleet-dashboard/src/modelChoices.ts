import { PROVIDER_MODELS } from './constants.ts'

// The cloud model picker's choices. Claude and Codex list their family aliases (#460, owner direction),
// Gemini its model list; every provider also takes a custom model name (#465), so a dated or hosted
// (zai/) id can be entered. A stored value outside the list opens in the custom input, as is, and is
// never rewritten. Kept free of JSX so `node --test` can run it.

export interface ModelChoices {
  /** The aliases (or Gemini's model list), in display order. */
  options: string[]
  /** The stored value when it is not one of `options`, shown as-is in the custom input; else null. */
  current: string | null
  /** Whether a free-text model may be typed. */
  freeText: boolean
}

export function modelChoices(provider: string, value: string): ModelChoices {
  const options = PROVIDER_MODELS[provider] ?? []
  return {
    options,
    current: value !== '' && !options.includes(value) ? value : null,
    freeText: true,
  }
}

/**
 * Whether the picker shows the custom input for `value`: false for a listed model, true for any other
 * non-empty id (a stored one, or one a create-modal copy-from fills in), null for an empty value, which
 * leaves the mode as it is so clearing the input does not switch back to the select.
 */
export function customModeFor(value: string, options: string[]): boolean | null {
  if (options.includes(value)) return false
  return value !== '' ? true : null
}
