// Light, dark, or whatever the system says.
//
// The choice is this browser's and is kept in its local storage: it is a preference about a
// screen, not about the fleet. "System" stores nothing and sets no attribute, which leaves the
// stylesheet's own `prefers-color-scheme` rule in charge (styles/tokens.css).
// Kept free of JSX so `node --test` can run it.

export type ThemeChoice = 'system' | 'light' | 'dark'

export const THEME_KEY = 'fleet.theme'
export const THEME_CHOICES: readonly ThemeChoice[] = ['system', 'light', 'dark']

export function parseTheme(stored: string | null | undefined): ThemeChoice {
  return stored === 'light' || stored === 'dark' ? stored : 'system'
}

/** The choice after this one, in the order the toggle steps through them. */
export function nextTheme(current: ThemeChoice): ThemeChoice {
  return THEME_CHOICES[(THEME_CHOICES.indexOf(current) + 1) % THEME_CHOICES.length]
}

export function themeLabel(choice: ThemeChoice): string {
  return choice === 'system' ? 'Theme: follows the system' : choice === 'light' ? 'Theme: light' : 'Theme: dark'
}

interface Root { setAttribute(name: string, value: string): void; removeAttribute(name: string): void }
interface Store { getItem(key: string): string | null; setItem(key: string, value: string): void; removeItem(key: string): void }

export function readTheme(store: Store | undefined): ThemeChoice {
  // Storage can be refused outright (a private window, a blocked site); that is "no choice made".
  try { return parseTheme(store?.getItem(THEME_KEY)) } catch { return 'system' }
}

export function applyTheme(choice: ThemeChoice, root: Root, store?: Store): void {
  if (choice === 'system') root.removeAttribute('data-theme')
  else root.setAttribute('data-theme', choice)
  try {
    if (choice === 'system') store?.removeItem(THEME_KEY)
    else store?.setItem(THEME_KEY, choice)
  } catch { /* the choice still holds for this page */ }
}
